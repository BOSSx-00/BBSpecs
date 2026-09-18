using System.Diagnostics;

namespace BBSpecs.Services;

/// <summary>
/// Runs a command-line tool and captures its output.
///
/// The Linux readings lean on a handful of standard utilities (nvidia-smi,
/// dmidecode, iw, nmcli, smartctl). None of them are guaranteed to be installed,
/// so a missing binary has to be an ordinary "no data" answer rather than a
/// crash — every call here returns null instead of throwing.
/// </summary>
public static class Shell
{
    private static readonly Dictionary<string, bool> Available = [];
    private static readonly Lock AvailabilityLock = new();

    /// <summary>Runs <paramref name="file"/> and returns stdout, or null on any failure.</summary>
    public static string? Run(string file, string arguments, int timeoutMs = 4000)
    {
        if (!Exists(file)) return null;

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

            if (!process.Start()) return null;

            // Read before waiting: a full pipe buffer would otherwise deadlock us.
            Task<string> stdout = process.StandardOutput.ReadToEndAsync();
            Task<string> stderr = process.StandardError.ReadToEndAsync();

            if (!process.WaitForExit(timeoutMs))
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                return null;
            }

            Task.WaitAll([stdout, stderr], 1000);
            string output = stdout.IsCompletedSuccessfully ? stdout.Result : "";
            return string.IsNullOrWhiteSpace(output) ? null : output;
        }
        catch
        {
            return null;
        }
    }

    public static string[] RunLines(string file, string arguments, int timeoutMs = 4000) =>
        Run(file, arguments, timeoutMs)
            ?.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        ?? [];

    /// <summary>Is this tool on PATH? Probed once per name and remembered.</summary>
    public static bool Exists(string file)
    {
        lock (AvailabilityLock)
        {
            if (Available.TryGetValue(file, out bool known)) return known;
        }

        bool found = Probe(file);

        lock (AvailabilityLock) { Available[file] = found; }
        return found;
    }

    private static bool Probe(string file)
    {
        try
        {
            if (Path.IsPathRooted(file)) return File.Exists(file);

            string? path = Environment.GetEnvironmentVariable("PATH");
            if (string.IsNullOrEmpty(path)) return false;

            bool windows = OperatingSystem.IsWindows();
            string[] extensions = windows ? [".exe", ".cmd", ".bat", ""] : [""];

            foreach (string dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                foreach (string ext in extensions)
                {
                    try
                    {
                        if (File.Exists(Path.Combine(dir.Trim('"'), file + ext))) return true;
                    }
                    catch { /* unreadable PATH entry */ }
                }
            }

            // Several of the tools we want live in sbin, which isn't on a normal
            // user's PATH even though the binary is present.
            if (!windows)
            {
                foreach (string dir in new[] { "/usr/sbin", "/sbin", "/usr/local/sbin", "/usr/bin", "/bin" })
                {
                    try { if (File.Exists(Path.Combine(dir, file))) return true; } catch { }
                }
            }
        }
        catch { /* treat as missing */ }

        return false;
    }

    /// <summary>Full path for a tool that may live in sbin, or the bare name.</summary>
    public static string Resolve(string file)
    {
        if (OperatingSystem.IsWindows() || Path.IsPathRooted(file)) return file;

        foreach (string dir in new[] { "/usr/sbin", "/sbin", "/usr/local/sbin" })
        {
            try
            {
                string candidate = Path.Combine(dir, file);
                if (File.Exists(candidate)) return candidate;
            }
            catch { }
        }
        return file;
    }
}
