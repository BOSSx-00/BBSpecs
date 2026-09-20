using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text.Json;

namespace BBSpecs.Services;

/// <summary>
/// Checks GitHub for a newer release and, if the user asks for it, swaps the
/// running program for the new one.
///
/// The whole point of BBSpecs is that it is one file with nothing to install,
/// so updating has to keep that promise: no installer, no package manager, no
/// second copy left behind. The trick that makes it work on Windows is that a
/// running executable cannot be overwritten but it can be renamed, so the old
/// file is moved aside, the new one takes its place, and the next launch clears
/// up the leftover.
/// </summary>
public static class Updates
{
    private const string Api = "https://api.github.com/repos/BOSSx-00/BBSpecs/releases/latest";

    /// <summary>What the running file is renamed to while it is replaced.</summary>
    private const string RetiredSuffix = ".old";

    public sealed record Release(string Version, string Notes, string DownloadUrl, long SizeBytes);

    /// <summary>
    /// Why there is nothing to offer. "Nothing newer exists" and "the question
    /// could not be asked" look identical from here unless they are kept apart,
    /// and reporting the second as the first is how a monitor tells somebody
    /// they are current while they sit two releases behind.
    /// </summary>
    public enum Outcome { UpToDate, Available, Unreachable, NotSupported }

    public readonly record struct CheckResult(Outcome Outcome, Release? Release);

    /// <summary>Something downloaded and ready to run, with how to run it.</summary>
    public readonly record struct Staged(string Path, string Arguments);

    /// <summary>
    /// Whether this copy was put here by the installer.
    ///
    /// Told apart by the uninstaller sitting beside it, which the installer
    /// always writes and a portable download never has. It decides how updating
    /// works: a loose file replaces itself, while an installed one fetches the
    /// new installer and lets that do the work, so the entry in Add or Remove
    /// Programs keeps saying the truth about which version is on the machine.
    /// </summary>
    public static bool IsInstalled
    {
        get
        {
            try
            {
                if (Environment.ProcessPath is not string path) return false;
                if (Path.GetDirectoryName(path) is not string folder) return false;

                return File.Exists(Path.Combine(folder, "unins000.exe"));
            }
            catch { return false; }
        }
    }

    private static HttpClient Client()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        // GitHub rejects anonymous API calls that do not identify themselves.
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("BBSpecs", Program.Version));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return client;
    }

    /// <summary>
    /// Returns the newest release when it is newer than what is running, or null
    /// when it is not, when the network is unavailable, or when this copy was
    /// not built as a single file (a developer build should never self-replace).
    /// </summary>
    public static async Task<CheckResult> CheckAsync(CancellationToken token = default)
    {
        if (!CanSelfUpdate()) return new CheckResult(Outcome.NotSupported, null);

        try
        {
            using HttpClient client = Client();
            using var response = await client.GetAsync(Api, token);

            // 404 is the honest answer from a repository with no releases yet,
            // so it means up to date. Anything else means the question failed.
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                return new CheckResult(Outcome.UpToDate, null);

            if (!response.IsSuccessStatusCode)
                return new CheckResult(Outcome.Unreachable, null);

            using JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
            JsonElement root = doc.RootElement;

            if (root.TryGetProperty("draft", out JsonElement draft) && draft.GetBoolean())
                return new CheckResult(Outcome.UpToDate, null);
            if (root.TryGetProperty("prerelease", out JsonElement pre) && pre.GetBoolean())
                return new CheckResult(Outcome.UpToDate, null);

            string tag = root.TryGetProperty("tag_name", out JsonElement t) ? t.GetString() ?? "" : "";
            string latest = tag.TrimStart('v', 'V');
            if (!IsNewer(latest, Program.Version))
                return new CheckResult(Outcome.UpToDate, null);

            if (!root.TryGetProperty("assets", out JsonElement assets))
                return new CheckResult(Outcome.UpToDate, null);

            // An installed copy wants the installer; a loose one wants the
            // single file it can put in its own place.
            string wanted = OperatingSystem.IsWindows()
                ? (IsInstalled ? "-Setup-" + latest + ".exe" : "win-x64.exe")
                : "linux-x64";
            foreach (JsonElement asset in assets.EnumerateArray())
            {
                string name = asset.TryGetProperty("name", out JsonElement n) ? n.GetString() ?? "" : "";
                if (!name.EndsWith(wanted, StringComparison.OrdinalIgnoreCase)) continue;

                // "BBSpecs-1.2.3-win-x64.exe" also ends with the portable
                // suffix, so an installed copy has to rule it out explicitly.
                if (!IsInstalled && name.Contains("-Setup-", StringComparison.OrdinalIgnoreCase)) continue;

                string url = asset.TryGetProperty("browser_download_url", out JsonElement u) ? u.GetString() ?? "" : "";
                if (url.Length == 0) continue;

                long size = asset.TryGetProperty("size", out JsonElement sz) ? sz.GetInt64() : 0;
                string notes = root.TryGetProperty("body", out JsonElement b) ? b.GetString() ?? "" : "";

                return new CheckResult(Outcome.Available, new Release(latest, Summarise(notes), url, size));
            }

            // A release exists but carries nothing for this platform.
            return new CheckResult(Outcome.UpToDate, null);
        }
        catch
        {
            // Offline, rate limited, or GitHub is having a day.
            return new CheckResult(Outcome.Unreachable, null);
        }
    }

    /// <summary>
    /// Downloads the release and gets it ready to run. Returns what to launch,
    /// or null if anything went wrong, in which case nothing has changed.
    /// </summary>
    public static async Task<Staged?> DownloadAndStageAsync(Release release, CancellationToken token = default)
    {
        if (Environment.ProcessPath is not string current) return null;

        return IsInstalled
            ? await StageInstallerAsync(release, token)
            : await ReplaceSelfAsync(release, current, token);
    }

    /// <summary>
    /// The installed case: fetch the new installer into a temporary folder and
    /// hand it back to be run silently. It closes this copy through the Restart
    /// Manager, replaces the file and starts the new one itself, which keeps the
    /// uninstall entry and the version it reports correct.
    /// </summary>
    private static async Task<Staged?> StageInstallerAsync(Release release, CancellationToken token)
    {
        string path = Path.Combine(Path.GetTempPath(), $"BBSpecs-Setup-{release.Version}.exe");

        if (!await DownloadAsync(release, path, token)) return null;

        return new Staged(path, "/SILENT /SUPPRESSMSGBOXES /NORESTART");
    }

    /// <summary>
    /// The portable case: a running executable cannot be overwritten, but it
    /// can be renamed, so the old file is moved aside and the new one takes its
    /// place. The leftover is cleared up by the next launch.
    /// </summary>
    private static async Task<Staged?> ReplaceSelfAsync(Release release, string current, CancellationToken token)
    {
        string staged = current + ".new";
        string retired = current + RetiredSuffix;

        try
        {
            if (!await DownloadAsync(release, staged, token)) return null;

            TryDelete(retired);
            File.Move(current, retired);
            File.Move(staged, current);

            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(current,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                    UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                    UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            }

            return new Staged(current, "");
        }
        catch
        {
            // Put things back exactly as they were before giving up.
            TryDelete(staged);
            try
            {
                if (!File.Exists(current) && File.Exists(retired)) File.Move(retired, current);
            }
            catch { }
            return null;
        }
    }

    /// <summary>
    /// Downloads to a neighbouring file and only then checks the size. A
    /// half-finished download sitting where the real thing belongs is the one
    /// outcome worth going to some trouble to avoid.
    /// </summary>
    private static async Task<bool> DownloadAsync(Release release, string path, CancellationToken token)
    {
        try
        {
            using (HttpClient client = Client())
            using (var response = await client.GetAsync(release.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, token))
            {
                if (!response.IsSuccessStatusCode) return false;

                using FileStream file = File.Create(path);
                await response.Content.CopyToAsync(file, token);
            }

            long written = new FileInfo(path).Length;
            if (written == 0 || (release.SizeBytes > 0 && written != release.SizeBytes))
            {
                TryDelete(path);
                return false;
            }

            return true;
        }
        catch
        {
            TryDelete(path);
            return false;
        }
    }

    /// <summary>Starts what was staged. The caller closes this copy straight after.</summary>
    public static bool Launch(Staged staged)
    {
        try
        {
            var start = new ProcessStartInfo(staged.Path) { UseShellExecute = true };
            if (staged.Arguments.Length > 0) start.Arguments = staged.Arguments;

            Process.Start(start);
            return true;
        }
        catch { return false; }
    }

    /// <summary>
    /// Removes the file the previous version was renamed to. Called on startup,
    /// which is the first moment it is no longer locked by a running process.
    /// </summary>
    public static void CleanUpPreviousVersion()
    {
        if (Environment.ProcessPath is not string current) return;
        TryDelete(current + RetiredSuffix);
    }

    // ---- helpers ---------------------------------------------------------------

    /// <summary>
    /// A framework-dependent build or a "dotnet run" has no single file to swap,
    /// and replacing one would break the developer's working copy.
    /// </summary>
    private static bool CanSelfUpdate()
    {
        if (Environment.ProcessPath is not string path) return false;

        string name = Path.GetFileNameWithoutExtension(path);
        if (name.Equals("dotnet", StringComparison.OrdinalIgnoreCase)) return false;

        // A published single file carries everything beside it inside itself.
        // A build tree has the runtime sitting next to the executable, and
        // replacing that executable with a self-contained download would leave
        // somebody's working copy in a state they did not ask for.
        string? folder = Path.GetDirectoryName(path);
        return folder is not null
            && !File.Exists(Path.Combine(folder, "Photino.NET.dll"));
    }

    /// <summary>Compares dotted version numbers a part at a time.</summary>
    internal static bool IsNewer(string candidate, string running)
    {
        int[] a = Parse(candidate);
        int[] b = Parse(running);
        if (a.Length == 0) return false;

        for (int i = 0; i < Math.Max(a.Length, b.Length); i++)
        {
            int left = i < a.Length ? a[i] : 0;
            int right = i < b.Length ? b[i] : 0;
            if (left != right) return left > right;
        }

        return false;
    }

    private static int[] Parse(string version) =>
        version.Split('.', '-', '+')
               .TakeWhile(part => part.Length > 0 && part.All(char.IsAsciiDigit))
               .Select(int.Parse)
               .ToArray();

    /// <summary>
    /// The release notes are written for the GitHub page, complete with download
    /// links and headings. A popup has room for the first real sentence only.
    /// </summary>
    private static string Summarise(string body)
    {
        char[] decoration = ['#', '*', '-', ' '];

        foreach (string raw in body.Split('\n'))
        {
            string line = raw.Trim().TrimStart(decoration).Trim();
            if (line.Length < 12) continue;
            if (line.Length > 0 && (line[0] == '[' || line[0] == '|' || line[0] == '`')) continue;
            if (line.Contains("http", StringComparison.OrdinalIgnoreCase)) continue;

            return line.Length > 220 ? line[..217] + "..." : line;
        }

        return "";
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}
