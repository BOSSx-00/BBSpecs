using System.Diagnostics;
using System.Text.RegularExpressions;

namespace BBSpecs.Services;

/// <summary>
/// Starting BBSpecs with the computer, in the notification area only.
///
/// On Windows this is a scheduled task rather than the usual Run registry key,
/// and that is not a stylistic choice. BBSpecs asks for Administrator so it can
/// read temperature sensors, and Windows silently refuses to start anything
/// from the Run key that needs elevation: there is no prompt at sign-in, it
/// simply never appears. A logon task created with the highest privileges runs
/// without a prompt and without that failure, which is the only arrangement
/// that actually works for an elevated app.
///
/// Creating such a task needs Administrator itself, which BBSpecs already has
/// whenever sensors are working. When it does not, the option says so rather
/// than failing quietly.
///
/// On Linux it is an ordinary autostart desktop entry.
/// </summary>
public static class Autostart
{
    private const string TaskName = "BBSpecs";

    /// <summary>Tells a launched copy to stay in the tray and not open a window.</summary>
    public const string TrayArgument = "--tray";

    /// <summary>
    /// Whether this copy is in a position to change the setting at all. A
    /// Windows logon task with elevated rights can only be created by an
    /// elevated process.
    /// </summary>
    public static bool Supported =>
        !OperatingSystem.IsWindows() || Elevation.IsElevated;

    private static bool? _cached;

    /// <summary>
    /// Cached after the first look. Asking costs a process on Windows, and the
    /// answer cannot change while the app runs except through Set below.
    /// </summary>
    public static bool IsEnabled()
    {
        if (_cached is bool known) return known;

        bool enabled;
        try
        {
            enabled = OperatingSystem.IsWindows()
                ? Run("schtasks", $"/Query /TN \"{TaskName}\"").ExitCode == 0
                : File.Exists(DesktopEntryPath);
        }
        catch { enabled = false; }

        _cached = enabled;
        return enabled;
    }

    /// <summary>Turns it on or off and reports what the setting ended up as.</summary>
    public static bool Set(bool enabled)
    {
        try
        {
            if (OperatingSystem.IsWindows()) SetWindows(enabled);
            else SetLinux(enabled);
        }
        catch { /* fall through to reporting whatever actually happened */ }

        _cached = null;
        return IsEnabled();
    }

    /// <summary>
    /// Points an existing entry at wherever the program is now.
    ///
    /// Updating in place keeps the same filename, so it usually stays correct.
    /// Downloading a new release by hand does not: that leaves a task aimed at
    /// a file that is gone, and an autostart that silently stops working is
    /// worse than one that was never set up. Checked on every launch, and
    /// rewritten only when the path has actually changed.
    /// </summary>
    public static void KeepPathCurrent()
    {
        try
        {
            if (Environment.ProcessPath is not string current) return;
            if (!IsEnabled() || !Supported) return;

            if (RegisteredPath() is string registered
                && !string.Equals(registered, current, StringComparison.OrdinalIgnoreCase))
            {
                Set(true);
            }
        }
        catch { }
    }

    // ---- Windows ---------------------------------------------------------------

    private static void SetWindows(bool enabled)
    {
        if (!enabled)
        {
            Run("schtasks", $"/Delete /TN \"{TaskName}\" /F");
            return;
        }

        if (Environment.ProcessPath is not string exe) return;

        // /RL HIGHEST is what avoids the sign-in prompt for an elevated app.
        // /F overwrites an existing task, which is also how the path is fixed.
        Run("schtasks",
            $"/Create /TN \"{TaskName}\" /TR \"\\\"{exe}\\\" {TrayArgument}\" /SC ONLOGON /RL HIGHEST /F");
    }

    /// <summary>The executable an existing task points at, or null.</summary>
    private static string? RegisteredPath()
    {
        if (!OperatingSystem.IsWindows()) return LinuxRegisteredPath();

        Result result = Run("schtasks", $"/Query /TN \"{TaskName}\" /XML ONE");
        if (result.ExitCode != 0) return null;

        Match command = Regex.Match(result.Output, "<Command>(.*?)</Command>", RegexOptions.Singleline);
        if (!command.Success) return null;

        return command.Groups[1].Value.Trim().Trim('"');
    }

    // ---- Linux -----------------------------------------------------------------

    private static string DesktopEntryPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".config", "autostart", "bbspecs.desktop");

    private static void SetLinux(bool enabled)
    {
        if (!enabled)
        {
            if (File.Exists(DesktopEntryPath)) File.Delete(DesktopEntryPath);
            return;
        }

        if (Environment.ProcessPath is not string exe) return;

        Directory.CreateDirectory(Path.GetDirectoryName(DesktopEntryPath)!);
        File.WriteAllText(DesktopEntryPath, string.Join('\n',
        [
            "[Desktop Entry]",
            "Type=Application",
            "Name=BBSpecs",
            "Comment=Hardware readings, started with the session",
            $"Exec=\"{exe}\" {TrayArgument}",
            "Terminal=false",
            "X-GNOME-Autostart-enabled=true",
            "",
        ]));
    }

    private static string? LinuxRegisteredPath()
    {
        try
        {
            foreach (string line in File.ReadAllLines(DesktopEntryPath))
            {
                if (!line.StartsWith("Exec=", StringComparison.Ordinal)) continue;

                string value = line["Exec=".Length..].Trim();
                if (value.StartsWith('"') && value.IndexOf('"', 1) is int end and > 0)
                    return value[1..end];

                return value.Split(' ')[0];
            }
        }
        catch { }

        return null;
    }

    // ---- running a tool ---------------------------------------------------------

    private readonly record struct Result(int ExitCode, string Output);

    /// <summary>
    /// Like <see cref="Shell.Run"/>, but the exit code is the answer here: an
    /// absent scheduled task is reported by a non-zero code and nothing else.
    /// </summary>
    private static Result Run(string file, string arguments)
    {
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = file,
                    Arguments = arguments,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                },
            };

            if (!process.Start()) return new Result(-1, "");

            Task<string> stdout = process.StandardOutput.ReadToEndAsync();
            process.StandardError.ReadToEnd();

            if (!process.WaitForExit(8000))
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                return new Result(-1, "");
            }

            Task.WaitAll([stdout], 1000);
            return new Result(process.ExitCode, stdout.IsCompletedSuccessfully ? stdout.Result : "");
        }
        catch
        {
            return new Result(-1, "");
        }
    }
}
