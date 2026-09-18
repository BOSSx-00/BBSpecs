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

    /// <summary>
    /// What the collector is doing, shown on the opening screen. Opening the
    /// sensor layer can take a few seconds on the first run — that is worth
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
    /// The page is ours, but it is still the untrusted side of this boundary —
    /// handing whatever string arrives to a shell would be a hole. Only paths
    /// BBSpecs itself just reported are openable.
    /// </summary>
    private static volatile string[] _openableVolumes = [];

    [STAThread]
    public static int Main()
    {
        // One instance at a time — two copies both polling hardware sensors can
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
            IntPtr window = Native.FindMainWindow("BBSpecs");
            if (window == IntPtr.Zero)
            {
                Dialogs.Error("BBSpecs is already running",
                    "Another copy of BBSpecs is already open. Look for it on your taskbar.");
                return;
            }

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
                "BBSpecs couldn't find its display files. The app folder looks incomplete — " +
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
        _window.RegisterWindowClosingHandler((_, _) => { Stopping.Cancel(); return false; });

        // The Uri overload specifically: Load(string) treats its argument as a
        // filesystem path and quietly ignores anything that already looks like a
        // file:// URL, which leaves the window with nothing to show.
        _window.Load(new Uri(page));

        StartCollector();
        _window.WaitForClose();
    }

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
    /// keeps per-thread state, so it must always be the same thread — and never
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
                });
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
    /// silent — the collector calls this once per failing read, and a dialog per
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
