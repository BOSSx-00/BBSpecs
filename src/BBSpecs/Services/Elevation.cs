using System.Diagnostics;
using System.Runtime.InteropServices;

namespace BBSpecs.Services;

/// <summary>
/// Administrator (Windows) / root (Linux) checks.
///
/// Reading hardware sensors needs elevation on both platforms: Windows keeps the
/// sensor driver behind UAC, and on Linux the interesting bits (dmidecode for
/// RAM slots, smartctl for drive health) only answer to root.
/// </summary>
public static class Elevation
{
    [DllImport("libc", EntryPoint = "geteuid")]
    private static extern uint GetEffectiveUid();

    private static readonly Lazy<bool> Cached = new(Detect);

    public static bool IsElevated => Cached.Value;

    /// <summary>The word to show the user for this platform.</summary>
    public static string RoleName => OperatingSystem.IsWindows() ? "Administrator" : "root";

    private static bool Detect()
    {
        try
        {
            if (OperatingSystem.IsWindows()) return DetectWindows();
            return GetEffectiveUid() == 0;
        }
        catch
        {
            return false;
        }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static bool DetectWindows()
    {
        using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
        return new System.Security.Principal.WindowsPrincipal(identity)
            .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
    }

    /// <summary>
    /// Restarts this app elevated. Returns true if the elevated copy started, in
    /// which case the caller should exit; false if the user declined or no
    /// elevation helper is available.
    /// </summary>
    public static bool TryRelaunchElevated()
    {
        string? exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe)) return false;

        try
        {
            if (OperatingSystem.IsWindows())
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = exe,
                    UseShellExecute = true,
                    Verb = "runas",
                    WorkingDirectory = AppContext.BaseDirectory,
                });
                return true;
            }

            // pkexec shows the desktop's own password prompt and, unlike sudo,
            // works without a terminal attached.
            if (!Shell.Exists("pkexec")) return false;

            Process.Start(new ProcessStartInfo
            {
                FileName = "pkexec",
                UseShellExecute = false,
                WorkingDirectory = AppContext.BaseDirectory,
                ArgumentList =
                {
                    "env",
                    // pkexec scrubs the environment, and the webview needs these
                    // back or it cannot reach the user's display session.
                    "DISPLAY=" + (Environment.GetEnvironmentVariable("DISPLAY") ?? ""),
                    "WAYLAND_DISPLAY=" + (Environment.GetEnvironmentVariable("WAYLAND_DISPLAY") ?? ""),
                    "XAUTHORITY=" + (Environment.GetEnvironmentVariable("XAUTHORITY") ?? ""),
                    "XDG_RUNTIME_DIR=" + (Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR") ?? ""),
                    exe,
                },
            });
            return true;
        }
        catch
        {
            // User dismissed the prompt, or policy blocks elevation.
            return false;
        }
    }
}
