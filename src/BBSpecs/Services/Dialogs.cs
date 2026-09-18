using System.Diagnostics;
using System.Runtime.InteropServices;

namespace BBSpecs.Services;

/// <summary>
/// Native message boxes for the handful of failures that happen before there is
/// an app window to show them in — chiefly a missing webview, which is the one
/// thing that stops BBSpecs from drawing anything at all.
///
/// Photino's own ShowMessage needs a window, so it is no use here.
/// </summary>
public static class Dialogs
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);

    private const uint MB_OK = 0x0;
    private const uint MB_YESNO = 0x4;
    private const uint MB_ICONERROR = 0x10;
    private const uint MB_ICONQUESTION = 0x20;
    private const uint MB_SETFOREGROUND = 0x0001_0000;
    private const uint MB_TOPMOST = 0x0004_0000;
    private const int IDYES = 6;

    /// <summary>Shows a message and waits for the user to dismiss it.</summary>
    public static void Error(string title, string message)
    {
        if (OperatingSystem.IsWindows())
        {
            try
            {
                MessageBoxW(IntPtr.Zero, message, title,
                    MB_OK | MB_ICONERROR | MB_SETFOREGROUND | MB_TOPMOST);
                return;
            }
            catch { /* fall through to the console */ }
        }
        else if (TryLinuxDialog(["--error", "--title", title, "--text", message], out _))
        {
            return;
        }

        // A windowed app launched from a menu has nowhere to print, but someone
        // running it from a terminal deserves to see why it gave up.
        Console.Error.WriteLine($"{title}\n\n{message}");
    }

    /// <summary>Asks a yes/no question. Returns false if no dialog could be shown.</summary>
    public static bool Ask(string title, string message)
    {
        if (OperatingSystem.IsWindows())
        {
            try
            {
                return MessageBoxW(IntPtr.Zero, message, title,
                    MB_YESNO | MB_ICONQUESTION | MB_SETFOREGROUND | MB_TOPMOST) == IDYES;
            }
            catch { return false; }
        }

        if (TryLinuxDialog(["--question", "--title", title, "--text", message], out bool yes)) return yes;

        Console.Error.WriteLine($"{title}\n\n{message}");
        return false;
    }

    /// <summary>
    /// zenity ships with GNOME-based desktops and kdialog with KDE; between them
    /// they cover most installs. kdialog uses the same flag names for these two.
    /// </summary>
    private static bool TryLinuxDialog(string[] arguments, out bool accepted)
    {
        accepted = false;

        foreach (string tool in new[] { "zenity", "kdialog" })
        {
            if (!Shell.Exists(tool)) continue;

            try
            {
                using var process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = tool,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                    },
                };
                foreach (string argument in arguments) process.StartInfo.ArgumentList.Add(argument);

                if (!process.Start()) continue;
                if (!process.WaitForExit(120_000))
                {
                    try { process.Kill(entireProcessTree: true); } catch { }
                    continue;
                }

                accepted = process.ExitCode == 0;
                return true;
            }
            catch { /* try the next tool */ }
        }

        return false;
    }

    /// <summary>Opens a page in the user's browser, at their explicit request.</summary>
    public static void OpenUrl(string url)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
            }
            else
            {
                Process.Start(new ProcessStartInfo { FileName = "xdg-open", ArgumentList = { url } });
            }
        }
        catch
        {
            Console.Error.WriteLine("Open this page manually: " + url);
        }
    }
}
