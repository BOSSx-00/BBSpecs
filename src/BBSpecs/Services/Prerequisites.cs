namespace BBSpecs.Services;

/// <summary>
/// Checks the one thing BBSpecs can't supply for itself: the system webview it
/// draws into. Everything else (the .NET runtime, the sensor library, the
/// fonts, the interface) ships inside the executable.
///
/// Without this check a machine missing the webview would launch, fail inside
/// the native layer, and close again without a word, because a windowed app has
/// no console to complain to. That is the worst failure a download can have.
/// </summary>
public static class Prerequisites
{
    public sealed record Problem(string Title, string Message, string? HelpUrl = null, string? HelpPrompt = null);

#if BBSPECS_WINDOWS
    public static Problem? Check() => CheckWebView2();
#else
    public static Problem? Check() => CheckWebKitGtk();
#endif

#if BBSPECS_WINDOWS
    // ---- Windows ---------------------------------------------------------------

    /// <summary>Microsoft's fixed GUID for the Evergreen WebView2 Runtime.</summary>
    private const string WebView2Client = "{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}";

    private static Problem? CheckWebView2()
    {
        if (IsWebView2Present()) return null;

        return new Problem(
            "BBSpecs needs one Microsoft component",
            "BBSpecs draws its display using the Microsoft Edge WebView2 Runtime, and it " +
            "isn't installed on this PC.\n\n" +
            "It's a free Microsoft component that comes with Windows 11 and with Microsoft " +
            "Edge on Windows 10: this PC is one of the rare ones without it.\n\n" +
            "Install it once and BBSpecs will work from then on.",
            "https://go.microsoft.com/fwlink/p/?LinkId=2124703",
            "Open the Microsoft download page now?");
    }

    private static bool IsWebView2Present()
    {
        // An organisation can deploy a fixed-version copy and point apps at it.
        string? pinned = Environment.GetEnvironmentVariable("WEBVIEW2_BROWSER_EXECUTABLE_FOLDER");
        if (!string.IsNullOrWhiteSpace(pinned))
        {
            try { if (Directory.Exists(pinned)) return true; } catch { }
        }

        // The runtime records itself in one of three places depending on whether
        // it was installed per-machine on 64-bit, per-machine on 32-bit, or
        // per-user. Any of them counts.
        string[] keys =
        [
            $@"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{WebView2Client}",
            $@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\EdgeUpdate\Clients\{WebView2Client}",
            $@"HKEY_CURRENT_USER\SOFTWARE\Microsoft\EdgeUpdate\Clients\{WebView2Client}",
        ];

        foreach (string key in keys)
        {
            string? version = Platform.Windows.Reg.Read(key, "pv");
            // An uninstalled-but-not-cleaned-up entry leaves "0.0.0.0" behind.
            if (!string.IsNullOrWhiteSpace(version) && version != "0.0.0.0") return true;
        }

        // Last resort: the runtime's own folder. Registry access can be blocked by
        // policy on locked-down machines where the component is nonetheless there.
        foreach (Environment.SpecialFolder root in
                 new[] { Environment.SpecialFolder.ProgramFilesX86, Environment.SpecialFolder.ProgramFiles })
        {
            try
            {
                string path = Path.Combine(
                    Environment.GetFolderPath(root), "Microsoft", "EdgeWebView", "Application");
                if (Directory.Exists(path) && Directory.GetDirectories(path).Length > 0) return true;
            }
            catch { /* unreadable, keep looking */ }
        }

        return false;
    }
#else
    // ---- Linux -----------------------------------------------------------------

    private static Problem? CheckWebKitGtk()
    {
        if (IsWebKitGtkPresent()) return null;

        return new Problem(
            "BBSpecs needs the system webview",
            "BBSpecs draws its display using WebKitGTK, which isn't installed on this system.\n\n" +
            "Install it with whichever of these matches your distribution, then start " +
            "BBSpecs again:\n\n" +
            "  Debian / Ubuntu   sudo apt install libwebkit2gtk-4.1-0\n" +
            "  Fedora            sudo dnf install webkit2gtk4.1\n" +
            "  Arch              sudo pacman -S webkit2gtk-4.1\n" +
            "  openSUSE          sudo zypper install libwebkit2gtk-4_1-0");
    }

    private static bool IsWebKitGtkPresent()
    {
        // The linker cache is the authoritative answer and covers every layout.
        string? cache = Shell.Run(Shell.Resolve("ldconfig"), "-p", 4000);
        if (cache is not null && cache.Contains("libwebkit2gtk", StringComparison.OrdinalIgnoreCase))
            return true;

        // ldconfig isn't always available (some containers, some immutable
        // distributions), so fall back to looking where libraries normally live.
        string[] directories =
        [
            "/usr/lib/x86_64-linux-gnu", "/usr/lib/aarch64-linux-gnu",
            "/usr/lib64", "/usr/lib", "/lib/x86_64-linux-gnu", "/lib64", "/usr/local/lib",
        ];

        foreach (string directory in directories)
        {
            try
            {
                if (Directory.Exists(directory)
                    && Directory.EnumerateFiles(directory, "libwebkit2gtk-*.so*").Any())
                    return true;
            }
            catch { /* unreadable, keep looking */ }
        }

        return false;
    }
#endif
}
