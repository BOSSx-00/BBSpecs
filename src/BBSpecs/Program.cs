using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.Json;
using BBSpecs.Models;
using BBSpecs.Services;
using Photino.NET;

namespace BBSpecs;

/// <summary>
/// The whole app is one window: a native frame hosting a webview that renders
/// everything in web/. Photino gives us that window on both Windows (WebView2)
/// and Linux (WebKitGTK), so the entire front-end is shared.
/// </summary>
public static class Program
{
    /// <summary>Read from the assembly so the .csproj stays the one place a version lives.</summary>
    public static string Version { get; } = ReadVersion();

    private static PhotinoWindow? _window;
    private static SnapshotProvider? _provider;
    private static readonly CancellationTokenSource Stopping = new();

    /// <summary>
    /// The most recent snapshot, already serialised, published by the collector
    /// thread and picked up by the page.
    ///
    /// The page asks for it rather than the collector pushing it, because the
    /// webview may only be written to from the thread that owns the window.
    /// Marshalling every tick across to that thread proved unreliable, whereas
    /// replying to the page's own request already runs there.
    /// </summary>
    private static volatile string? _latest;
    private static readonly Settings _settings = Settings.Load();
    private static string? _pendingUpdate;
    private static Updates.Release? _offered;

    /// <summary>
    /// A downloaded update waiting to be run. Kept here rather than handed to
    /// the page and passed back: the page has no business naming a file for the
    /// app to execute, and for an installed copy it is not even the same file.
    /// </summary>
    private static Updates.Staged? _staged;
    private static readonly Alerts _alerts = new();
    private static volatile Snapshot? _forTray;
    private static readonly System.Collections.Concurrent.ConcurrentQueue<Alerts.Alert> _queuedAlerts = new();
#if BBSPECS_WINDOWS
    private static Platform.Windows.TrayIcon? _tray;
    private static Platform.Windows.TrayWidget? _widget;

    /// <summary>The last tooltip sent, so an unchanged one is not sent again.</summary>
    private static string? _tooltip;
#endif

    /// <summary>
    /// Started by the logon task, so it belongs in the notification area with
    /// no window. Kept as a field because the window is hidden on creation and
    /// shown later, from the tray.
    /// </summary>
    private static readonly bool _startInTray =
        Environment.GetCommandLineArgs().Contains(Autostart.TrayArgument, StringComparer.OrdinalIgnoreCase);

    private static IntPtr _windowHandle;

    /// <summary>
    /// What the collector is doing, shown on the opening screen. Opening the
    /// sensor layer can take a few seconds on the first run: that is worth
    /// saying out loud rather than leaving the window looking stuck.
    /// </summary>
    private static volatile string _phase = "Starting up…";

    /// <summary>
    /// A finished public-IP lookup waiting to be handed to the page. Set from a
    /// background task, taken by the next poll on the window's own thread.
    /// </summary>
    private static string? _pendingPublicIp;

    /// <summary>The outcome of a driver install, waiting for the next poll.</summary>
    private static string? _pendingDriverResult;

    /// <summary>
    /// Volumes the page is allowed to ask the file manager to open.
    ///
    /// The page is ours, but it is still the untrusted side of this boundary:
    /// handing whatever string arrives to a shell would be a hole. Only paths
    /// BBSpecs itself just reported are openable.
    /// </summary>
    private static volatile string[] _openableVolumes = [];

    [STAThread]
    public static int Main()
    {
        // One instance at a time: two copies both polling hardware sensors can
        // fight over the same driver handle.
        using Mutex single = CreateInstanceMutex(out bool isFirst);
        if (!isFirst)
        {
            // Exiting quietly here looks exactly like the app failing to launch.
            // Bring the window that IS running to the front instead.
            ShowExistingWindow();
            return 0;
        }

        AppDomain.CurrentDomain.UnhandledException += (_, e) => LogCrash(e.ExceptionObject as Exception);

        // If the last run replaced itself, the file it was replaced from is
        // still sitting there: this is the first moment nothing has it open.
        Updates.CleanUpPreviousVersion();

        // A downloaded release has a new filename, which would leave the logon
        // task aimed at a file that no longer exists. Off the launch path: it
        // shells out to the task scheduler, and nothing here should make the
        // window take longer to appear.
        _ = Task.Run(Autostart.KeepPathCurrent);

        // The webview is the only thing BBSpecs can't carry inside itself. Say so
        // properly rather than vanishing: a windowed app has no console, so a
        // silent exit looks to the user like nothing happened at all.
        if (Prerequisites.Check() is Prerequisites.Problem missing)
        {
            ReportMissingPrerequisite(missing);
            return 1;
        }

        try
        {
            _provider = CreateProvider();
            RunWindow();
            return 0;
        }
        catch (Exception ex)
        {
            LogCrash(ex);
            Dialogs.Error("BBSpecs couldn't start",
                $"{ex.Message}\n\nDetails were saved to:\n{CrashLogPath()}");
            return 1;
        }
        finally
        {
            Stopping.Cancel();
            _provider?.Dispose();
            Stopping.Dispose();
        }
    }

    /// <summary>
    /// Raises the already-running copy. Matched on window title rather than
    /// process name, because the executable is renamed per release
    /// (BBSpecs-1.2.3-win-x64.exe) and whatever the user saved it as.
    /// </summary>
    private static void ShowExistingWindow()
    {
        if (!OperatingSystem.IsWindows()) return;

        try
        {
            // Hidden ones count: the running copy may be sitting in the tray.
            IntPtr window = Native.FindMainWindow("BBSpecs", includeHidden: true);
            if (window == IntPtr.Zero)
            {
                Dialogs.Error("BBSpecs is already running",
                    "Another copy of BBSpecs is already open. Look for it on your taskbar "
                    + "or in the notification area.");
                return;
            }

            // The running copy may be parked off the desktop for the tray, and
            // showing a parked window leaves it exactly where it cannot be seen.
            // SetWindowPos works across processes, so this reaches it fine.
            Native.Unpark(window, 120, 90);
            Native.ShowWindow(window, Native.SW_SHOW);
            Native.ShowWindow(window, Native.SW_RESTORE);
            Native.SetForegroundWindow(window);
        }
        catch { /* the other copy is closing; nothing useful to do */ }
    }

    private static void ReportMissingPrerequisite(Prerequisites.Problem missing)
    {
        if (missing.HelpUrl is null || missing.HelpPrompt is null)
        {
            Dialogs.Error(missing.Title, missing.Message);
            return;
        }

        // Opening a browser is the user's call, so it only happens if they say yes.
        if (Dialogs.Ask(missing.Title, $"{missing.Message}\n\n{missing.HelpPrompt}"))
            Dialogs.OpenUrl(missing.HelpUrl);
    }

    /// <summary>Opens a volume in Explorer, or the desktop's file manager on Linux.</summary>
    private static void OpenInFileManager(string volume)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                // Explorer needs a trailing separator on a bare drive letter:
                // "C:" means "the working directory on C", "C:\" means the root.
                string path = volume.EndsWith(':') ? volume + Path.DirectorySeparatorChar : volume;
                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    ArgumentList = { path },
                    UseShellExecute = false,
                });
            }
            else
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "xdg-open",
                    ArgumentList = { volume },
                    UseShellExecute = false,
                });
            }
        }
        catch (Exception ex)
        {
            LogCrash(ex);
        }
    }

    private static SnapshotProvider CreateProvider()
    {
#if BBSPECS_WINDOWS
        return new Platform.Windows.WindowsHardware();
#else
        return new Platform.Linux.LinuxHardware();
#endif
    }

    private static void RunWindow()
    {
        string page = Path.Combine(AppContext.BaseDirectory, "web", "index.html");
        if (!File.Exists(page))
            throw new FileNotFoundException(
                "BBSpecs couldn't find its display files. The app folder looks incomplete: " +
                "download it again.", page);

        _window = new PhotinoWindow()
            .SetTitle("BBSpecs")
            .SetUseOsDefaultSize(false)
            .SetSize(1180, 820)
            .SetMinSize(900, 620)
            .SetResizable(true)
            .SetContextMenuEnabled(false)
            .SetDevToolsEnabled(true)
            .SetSmoothScrollingEnabled(true)
            .SetJavascriptClipboardAccessEnabled(true)
            .Center();

        string? icon = FindIcon();
        if (icon is not null)
        {
            // A bad icon path is fatal inside the native layer on some desktops,
            // so only set it once we know the file is really there.
            try { _window.SetIconFile(icon); } catch { }
        }

        _window.RegisterWebMessageReceivedHandler(OnWebMessage);

        // The window has to exist before it can be hidden, and this is the
        // first moment it does. Hiding it only when the tray icon is really
        // there, because a hidden window with nothing to restore it from is an
        // app the user cannot reach at all.
        _window.RegisterWindowCreatedHandler((_, _) =>
        {
            _windowHandle = Native.FindMainWindow("BBSpecs", includeHidden: true);

            if (!_startInTray) return;

#if BBSPECS_WINDOWS
            if (_tray?.IsPresent == true && _windowHandle != IntPtr.Zero)
            {
                Native.ParkOffScreen(_windowHandle);
                return;
            }
#endif
            // No tray to restore it from, so the next best thing is out of the way.
            try { _window?.SetMinimized(true); } catch { }
        });
        _window.RegisterWindowClosingHandler((_, _) => { Stopping.Cancel(); return false; });

        // The Uri overload specifically: Load(string) treats its argument as a
        // filesystem path and quietly ignores anything that already looks like a
        // file:// URL, which leaves the window with nothing to show.
        _window.Load(new Uri(page));

#if BBSPECS_WINDOWS
        StartTray();
#endif

        StartCollector();
        _window.WaitForClose();

#if BBSPECS_WINDOWS
        _widget?.Dispose();
        _tray?.Dispose();
#endif
    }

#if BBSPECS_WINDOWS
    /// <summary>
    /// Puts BBSpecs in the notification area. Created here rather than at
    /// startup because it must belong to the thread running the message loop,
    /// and a failure to create it is never a reason to refuse to launch: the
    /// app works perfectly well without a tray icon.
    /// </summary>
    private static void StartTray()
    {
        try
        {
            _tray = new Platform.Windows.TrayIcon($"BBSpecs v{Version}");
        }
        catch (Exception ex)
        {
            LogCrash(ex);
            return;
        }

        try
        {
            _widget = new Platform.Windows.TrayWidget();
            _widget.SetTheme(_settings.Theme);
            _widget.Clicked += ShowMainWindow;
        }
        catch (Exception ex) { LogCrash(ex); }

        _tray.Activated += () => _widget?.Toggle();
        _tray.OpenRequested += ShowMainWindow;
        _tray.UpdateRequested += () => CheckForUpdate(announceWhenCurrent: true);
        _tray.ExitRequested += () =>
        {
            Stopping.Cancel();
            _window?.Close();
        };
    }

    /// <summary>
    /// Pushes the latest readings into the tray icon and its widget, and sends
    /// out at most one notification per second so a burst never stacks up into
    /// a wall of popups.
    /// </summary>
    private static void RefreshTray()
    {
        if (_tray is null || _forTray is not Snapshot s) return;

        GpuInfo? gpu = s.Gpus.FirstOrDefault();

        _widget?.Update(new Platform.Windows.TrayWidget.Readings(
            s.Cpu.LoadPercent, s.Cpu.TempC,
            gpu?.LoadPercent, gpu?.TempC,
            s.Network.DownloadKbps, s.Network.UploadKbps), "v" + Version);

        string line = Environment.NewLine;
        string tooltip =
            $"BBSpecs v{Version}{line}" +
            $"CPU {Describe(s.Cpu.LoadPercent, "%")}  {Describe(s.Cpu.TempC, "°C")}{line}" +
            $"GPU {Describe(gpu?.LoadPercent, "%")}  {Describe(gpu?.TempC, "°C")}";

        if (tooltip != _tooltip)
        {
            _tooltip = tooltip;
            _tray.SetTooltip(tooltip);
        }

        if (_settings.Notifications && _queuedAlerts.TryDequeue(out Alerts.Alert alert))
            _tray.Notify(alert.Title, alert.Message, alert.Warning);
    }

    private static string Describe(double? value, string unit) =>
        value is double v ? $"{v:0}{unit}" : "--";

    private static void ShowMainWindow()
    {
        try
        {
            IntPtr window = _windowHandle != IntPtr.Zero
                ? _windowHandle
                : Native.FindMainWindow("BBSpecs", includeHidden: true);

            if (window == IntPtr.Zero) return;

            // A copy started for the tray is parked off the desktop rather than
            // hidden, so bring it back before showing it.
            Native.Unpark(window, 120, 90);
            Native.ShowWindow(window, Native.SW_SHOW);
            Native.ShowWindow(window, Native.SW_RESTORE);
            Native.SetForegroundWindow(window);
        }
        catch { }
    }
#endif

    /// <summary>Windows wants a .ico for the title bar; Linux desktops want a .png.</summary>
    private static string? FindIcon()
    {
        string[] candidates = OperatingSystem.IsWindows()
            ? [Path.Combine(AppContext.BaseDirectory, "Assets", "bbspecs.ico"),
               Path.Combine(AppContext.BaseDirectory, "web", "logo.png")]
            : [Path.Combine(AppContext.BaseDirectory, "web", "logo.png"),
               Path.Combine(AppContext.BaseDirectory, "Assets", "bbspecs.ico")];

        foreach (string candidate in candidates)
        {
            try { if (File.Exists(candidate)) return candidate; } catch { }
        }

        return null;
    }

    /// <summary>
    /// Sensors are polled on one dedicated thread. The Windows sensor library
    /// keeps per-thread state, so it must always be the same thread: and never
    /// the UI thread, because a slow WMI query would freeze the window.
    /// </summary>
    private static void StartCollector()
    {
        var thread = new Thread(() =>
        {
            _phase = "Waking up the sensors…";
            try { _provider!.Start(); }
            catch (Exception ex) { LogCrash(ex); }

            _phase = "Reading your hardware…";
            CancellationToken token = Stopping.Token;
            bool reported = false;

            while (!token.IsCancellationRequested)
            {
                try
                {
                    Snapshot snapshot = _provider!.Build(Version);
                    _latest = Json.Serialize(new { type = "snapshot", data = snapshot });

                    _forTray = snapshot;
                    if (_settings.Notifications)
                    {
                        foreach (Alerts.Alert alert in _alerts.Check(snapshot))
                            _queuedAlerts.Enqueue(alert);
                    }

                    _openableVolumes = snapshot.Drives
                        .SelectMany(d => d.Volumes)
                        .Select(v => v.Letter)
                        .Where(v => !string.IsNullOrWhiteSpace(v))
                        .Distinct()
                        .ToArray();
                }
                catch (Exception ex)
                {
                    // A single bad read shouldn't stop the stream, but a reading
                    // that fails every time needs to be traceable, so record the
                    // first one and stay quiet about the repeats.
                    if (!reported) { reported = true; LogCrash(ex); }
                }

                // One second is responsive enough to feel live without the poll
                // itself showing up as CPU load.
                if (token.WaitHandle.WaitOne(1000)) break;
            }
        })
        {
            IsBackground = true,
            Name = "BBSpecs.Sensors",
            Priority = ThreadPriority.BelowNormal,
        };

        thread.Start();
    }

    /// <summary>
    /// Asks GitHub whether there is something newer, off the window thread. The
    /// answer is parked for the next poll rather than posted, for the same
    /// reason the public address lookup is: the webview only tolerates writes
    /// from the thread that owns it.
    ///
    /// Staying quiet when the app is already current is the default, because
    /// this also runs unprompted at startup and nobody wants to dismiss a box
    /// telling them nothing happened. Pressing the menu item is a question, so
    /// that one gets an answer either way.
    /// </summary>
    private static void CheckForUpdate(bool announceWhenCurrent)
    {
        _ = Task.Run(async () =>
        {
            Updates.CheckResult result = await Updates.CheckAsync(Stopping.Token);
            _offered = result.Release;

            if (result is { Outcome: Updates.Outcome.Available, Release: Updates.Release release })
            {
                _pendingUpdate = Json.Serialize(new
                {
                    type = "updateAvailable",
                    version = release.Version,
                    notes = release.Notes,
                    sizeBytes = release.SizeBytes,
                });
            }
            else if (announceWhenCurrent)
            {
                _pendingUpdate = Json.Serialize(new
                {
                    type = "updateNone",
                    version = Version,
                    reachable = result.Outcome != Updates.Outcome.Unreachable,
                    supported = result.Outcome != Updates.Outcome.NotSupported,
                });
            }
        });
    }

    /// <summary>Sends a payload to the page. Only call this from the window's thread.</summary>
    private static void Post<T>(T payload) => PostRaw(Json.Serialize(payload));

    private static void PostRaw(string json)
    {
        try { _window?.SendWebMessage(json); }
        catch { /* the window is closing */ }
    }

    private static void OnWebMessage(object? sender, string message)
    {
        string command;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(message);
            command = doc.RootElement.TryGetProperty("cmd", out JsonElement c) ? c.GetString() ?? "" : "";
        }
        catch { return; }

        switch (command)
        {
            case "poll":
                // Anything a background job finished since the last poll goes
                // first, because the page is about to re-render either way.
                if (Interlocked.Exchange(ref _pendingPublicIp, null) is string publicIp)
                    PostRaw(publicIp);

                if (Interlocked.Exchange(ref _pendingDriverResult, null) is string driverResult)
                    PostRaw(driverResult);

                if (Interlocked.Exchange(ref _pendingUpdate, null) is string update)
                    PostRaw(update);

#if BBSPECS_WINDOWS
                // The tray and its widget are window-thread property, and this
                // is the once-a-second beat that already runs there.
                RefreshTray();
#else
                // No tray on Linux: desktops disagree too much about how one is
                // registered for it to be worth a dependency. Notifications
                // still work, through the interface every desktop does agree on.
                if (_settings.Notifications && _queuedAlerts.TryDequeue(out Alerts.Alert alert))
                {
                    Shell.Run("notify-send",
                        $"--app-name=BBSpecs --icon=dialog-{(alert.Warning ? "warning" : "information")} " +
                        $"\"{alert.Title.Replace("\"", "")}\" \"{alert.Message.Replace("\"", "")}\"");
                }
#endif

                // Whatever the collector published last. Until the first reading
                // lands, say what it's busy with so the opening screen is honest.
                if (_latest is string latest) PostRaw(latest);
                else Post(new { type = "starting", phase = _phase });
                break;

            case "ready":
                Post(new
                {
                    type = "hello",
                    version = Version,
                    platform = OperatingSystem.IsWindows() ? "windows" : "linux",
                    roleName = Elevation.RoleName,
                    canRelaunch = OperatingSystem.IsWindows() || Shell.Exists("pkexec"),
#if BBSPECS_WINDOWS
                    canInstallDriver = Platform.Windows.PawnIo.IsBundled,
#else
                    canInstallDriver = false,
#endif
                    // The page starts on the defaults in its own state object,
                    // so whatever was chosen last time has to be handed back
                    // before the first render rather than after it.
                    settings = _settings,

                    // Read from the system rather than from our own settings
                    // file: the task can be removed from outside the app, and
                    // a switch that disagrees with reality is worse than none.
                    runOnStart = Autostart.IsEnabled(),
                    canRunOnStart = Autostart.Supported,
                });

                // Now that something is there to show the answer to.
                CheckForUpdate(announceWhenCurrent: false);
                break;

            case "setting":
                try
                {
                    using JsonDocument doc = JsonDocument.Parse(message);
                    if (doc.RootElement.TryGetProperty("key", out JsonElement k)
                        && k.GetString() is string key
                        && doc.RootElement.TryGetProperty("value", out JsonElement v))
                    {
                        _settings.Apply(key, v);
#if BBSPECS_WINDOWS
                        if (key == "theme") _widget?.SetTheme(_settings.Theme);
#endif
                        // Turning alerts on should not immediately fire for
                        // something that has been quietly true for an hour.
                        if (key == "notifications")
                        {
                            _alerts.Reset();
                            _queuedAlerts.Clear();
                        }
                    }
                }
                catch { }
                break;

            case "publicIp":
                // Deliberately not awaited here: the continuation would resume on
                // a thread-pool thread, and the webview may only be written to
                // from the window's own thread. The result is parked instead and
                // handed over on the next poll, which already runs there.
                _ = Task.Run(async () =>
                {
                    PublicIpInfo result = await _provider!.Network.FetchPublicIpAsync();
                    _pendingPublicIp = Json.Serialize(new { type = "publicIp", data = result });
                });
                break;

            case "uiError":
                // The page reports its own script errors so a blank or frozen
                // panel leaves a trail instead of just looking broken.
                try
                {
                    using JsonDocument doc = JsonDocument.Parse(message);
                    string detail = doc.RootElement.TryGetProperty("detail", out JsonElement d)
                        ? d.GetString() ?? "" : "";
                    LogCrash(new InvalidOperationException("Display error: " + detail));
                }
                catch { }
                break;

            case "installDriver":
#if BBSPECS_WINDOWS
                // Runs only because the user pressed the button. Kicked onto a
                // background thread so the window keeps drawing while it works.
                Post(new { type = "driverInstall", state = "working" });
                _ = Task.Run(() =>
                {
                    (bool ok, string detail) = Platform.Windows.PawnIo.Install();
                    if (ok) _provider?.RequestSensorReload();
                    _pendingDriverResult = Json.Serialize(
                        new { type = "driverInstall", state = ok ? "done" : "failed", detail });
                });
#endif
                break;

            case "openVolume":
                try
                {
                    using JsonDocument doc = JsonDocument.Parse(message);
                    if (doc.RootElement.TryGetProperty("path", out JsonElement p)
                        && p.GetString() is string requested
                        && _openableVolumes.Contains(requested, StringComparer.Ordinal))
                    {
                        OpenInFileManager(requested);
                    }
                }
                catch { }
                break;

            case "runOnStart":
                try
                {
                    using JsonDocument doc = JsonDocument.Parse(message);
                    bool turnOn = doc.RootElement.TryGetProperty("value", out JsonElement v)
                                  && v.ValueKind == JsonValueKind.True;

                    // schtasks takes a moment, and the window thread should not
                    // be the one waiting for it.
                    _ = Task.Run(() =>
                    {
                        bool actual = Autostart.Set(turnOn);
                        _pendingUpdate = Json.Serialize(new { type = "runOnStart", enabled = actual });
                    });
                }
                catch { }
                break;

            case "checkUpdates":
                CheckForUpdate(announceWhenCurrent: true);
                break;

            case "installUpdate":
                if (_offered is Updates.Release wanted)
                {
                    _ = Task.Run(async () =>
                    {
                        Updates.Staged? staged = await Updates.DownloadAndStageAsync(wanted, Stopping.Token);
                        _staged = staged;

                        _pendingUpdate = staged is null
                            ? Json.Serialize(new { type = "updateFailed" })
                            : Json.Serialize(new { type = "updateReady" });
                    });
                }
                break;

            case "restartForUpdate":
                if (_staged is Updates.Staged ready && Updates.Launch(ready))
                {
                    Stopping.Cancel();
                    _window?.Close();
                }
                break;

            case "diskManagement":
                // The one place BBSpecs points somewhere else: partitioning a
                // disk is a job for the tool that owns it, and doing it here
                // would mean shipping a way to destroy data by accident.
                try
                {
                    if (OperatingSystem.IsWindows())
                    {
                        Process.Start(new ProcessStartInfo("diskmgmt.msc") { UseShellExecute = true });
                    }
                    else
                    {
                        // Whichever of these the desktop happens to have.
                        foreach (string tool in new[] { "gnome-disks", "gparted", "kde-partitionmanager" })
                        {
                            if (!Shell.Exists(tool)) continue;
                            Process.Start(new ProcessStartInfo(tool) { UseShellExecute = true });
                            break;
                        }
                    }
                }
                catch { }
                break;

            case "openHelp":
                // Only ever reached by the user pressing the button in the banner.
                try
                {
                    using JsonDocument doc = JsonDocument.Parse(message);
                    if (doc.RootElement.TryGetProperty("url", out JsonElement u)
                        && u.GetString() is string url
                        && Uri.TryCreate(url, UriKind.Absolute, out Uri? parsed)
                        && parsed.Scheme is "https" or "http")
                    {
                        Dialogs.OpenUrl(parsed.ToString());
                    }
                }
                catch { }
                break;

            case "relaunchElevated":
                if (Elevation.TryRelaunchElevated())
                {
                    Stopping.Cancel();
                    _window?.Close();
                }
                else
                {
                    Post(new { type = "relaunchFailed" });
                }
                break;
        }
    }

    // ---- housekeeping ----------------------------------------------------------

    private static Mutex CreateInstanceMutex(out bool isFirst)
    {
        // "Global\" is a Windows kernel-namespace prefix; on Unix, .NET maps named
        // mutexes onto files and rejects the backslash.
        string name = OperatingSystem.IsWindows()
            ? @"Global\BBSpecs.SingleInstance"
            : "BBSpecs.SingleInstance";

        try
        {
            return new Mutex(true, name, out isFirst);
        }
        catch
        {
            // Some sandboxes forbid named mutexes entirely; running is better than
            // refusing to start.
            isFirst = true;
            return new Mutex(true);
        }
    }

    private static string ReadVersion()
    {
        try
        {
            string? informational = Assembly.GetExecutingAssembly()
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

            if (!string.IsNullOrWhiteSpace(informational))
            {
                // Strip the "+<commit sha>" the SDK appends when building from a repo.
                int plus = informational.IndexOf('+');
                return plus > 0 ? informational[..plus] : informational;
            }

            Version? v = Assembly.GetExecutingAssembly().GetName().Version;
            return v is null ? "0.0.0" : $"{v.Major}.{v.Minor}.{v.Build}";
        }
        catch { return "0.0.0"; }
    }

    private static string CrashLogPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "BBSpecs", "crash.log");

    /// <summary>
    /// Appends to a log next to the user's data, and never throws. Deliberately
    /// silent: the collector calls this once per failing read, and a dialog per
    /// tick would be unusable.
    /// </summary>
    private static void LogCrash(Exception? ex)
    {
        try
        {
            string path = CrashLogPath();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            File.AppendAllText(path, new StringBuilder()
                .AppendLine($"--- {DateTime.Now:u}  BBSpecs v{Version} ---")
                .AppendLine(ex?.ToString() ?? "Unknown error")
                .AppendLine()
                .ToString());

            Console.Error.WriteLine($"BBSpecs error: {ex?.Message}  (details in {path})");
        }
        catch { /* nothing more we can do */ }
    }
}
