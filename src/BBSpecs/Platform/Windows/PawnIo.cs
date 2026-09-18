using System.Runtime.Versioning;

namespace BBSpecs.Platform.Windows;

/// <summary>
/// Detects PawnIO, the driver that actually reads processor temperature.
///
/// Modern LibreHardwareMonitor no longer ships its own kernel driver. It carries
/// small sandboxed bytecode modules (IntelMSR, LpcIO, RyzenSMU …) and runs them
/// inside PawnIO, a separate signed driver installed once per machine. The old
/// approach, WinRing0, let any program read and write arbitrary physical memory,
/// so Microsoft added it to the Vulnerable Driver Blocklist; PawnIO exists to do
/// the same job safely and is not blocked.
///
/// The practical consequence is that without PawnIO the temperature sensors are
/// all present and all read null, which looks exactly like a permissions problem
/// and isn't one. Detecting it is the difference between an unsolvable mystery
/// and a one-step fix.
/// </summary>
[SupportedOSPlatform("windows")]
public static class PawnIo
{
    /// <summary>Where the project publishes the installer.</summary>
    public const string DownloadUrl = "https://pawnio.eu";

    /// <summary>The official installer, carried inside BBSpecs so there is nothing to download.</summary>
    private static string BundledInstaller =>
        Path.Combine(AppContext.BaseDirectory, "Assets", "PawnIO", "PawnIO_setup.exe");

    public static bool IsBundled
    {
        get { try { return File.Exists(BundledInstaller); } catch { return false; } }
    }

    // Detection is cached, but installing invalidates it, so the cache is
    // replaceable rather than a one-shot Lazy.
    public static bool IsInstalled => _detect.Value;

    private static Lazy<bool> _detect = new(Detect);

    private static void Forget() => _detect = new Lazy<bool>(Detect);

    /// <summary>
    /// Runs the bundled installer. Only ever called after the user presses the
    /// button in the banner: BBSpecs never installs a driver on its own.
    /// </summary>
    public static (bool Ok, string Message) Install()
    {
        if (IsInstalled) return (true, "PawnIO is already installed.");

        if (!IsBundled)
        {
            return (false, "The bundled installer is missing from this copy of BBSpecs. " +
                           "You can install PawnIO yourself from " + DownloadUrl + ".");
        }

        try
        {
            // -install -silent are the switches the publisher documents, and the
            // ones Microsoft's own winget package uses.
            var start = new System.Diagnostics.ProcessStartInfo
            {
                FileName = BundledInstaller,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            start.ArgumentList.Add("-install");
            start.ArgumentList.Add("-silent");

            using System.Diagnostics.Process? process = System.Diagnostics.Process.Start(start);
            if (process is null) return (false, "Windows wouldn't start the installer.");

            if (!process.WaitForExit(120_000))
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                return (false, "The installer took too long and was stopped.");
            }

            Forget();

            if (process.ExitCode != 0 && !IsInstalled)
                return (false, $"The installer stopped with code {process.ExitCode}.");

            return IsInstalled
                ? (true, "PawnIO installed. Temperatures should appear in a moment.")
                : (false, "The installer finished but PawnIO still isn't detected. " +
                          "A restart may be needed.");
        }
        catch (Exception ex)
        {
            return (false, "Couldn't run the installer: " + ex.Message);
        }
    }

    private static bool Detect()
    {
        // The user-mode library is what the sensor code actually links against,
        // so it is the most direct evidence the package is present.
        foreach (string library in new[] { "PawnIOLib.dll", "PawnIO.sys" })
        {
            foreach (Environment.SpecialFolder folder in
                     new[] { Environment.SpecialFolder.System, Environment.SpecialFolder.SystemX86 })
            {
                try
                {
                    string path = Path.Combine(Environment.GetFolderPath(folder), library);
                    if (File.Exists(path)) return true;

                    string driver = Path.Combine(Environment.GetFolderPath(folder), "drivers", library);
                    if (File.Exists(driver)) return true;
                }
                catch { /* unreadable, keep looking */ }
            }
        }

        // Installed but not yet started still counts: it will start on demand.
        try
        {
            if (Reg.Read(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\PawnIO", "ImagePath") is not null)
                return true;
        }
        catch { }

        try
        {
            if (Reg.Read(@"HKEY_LOCAL_MACHINE\SOFTWARE\PawnIO", "Install_Dir") is not null) return true;
        }
        catch { }

        return false;
    }
}
