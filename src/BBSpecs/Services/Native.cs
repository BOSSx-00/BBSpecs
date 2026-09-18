using System.Runtime.InteropServices;
using System.Text;

namespace BBSpecs.Services;

/// <summary>Window handling that Photino doesn't expose.</summary>
internal static class Native
{
    public const int SW_RESTORE = 9;

    private delegate bool EnumWindowsProc(IntPtr window, IntPtr parameter);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextW(IntPtr window, StringBuilder text, int capacity);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr window);

    [DllImport("user32.dll")]
    public static extern bool ShowWindow(IntPtr window, int command);

    [DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(IntPtr window);

    /// <summary>
    /// The first visible top-level window with exactly this title, from any
    /// process. Title rather than process name, because a released build is
    /// renamed per version and the user may rename it again.
    /// </summary>
    public static IntPtr FindMainWindow(string title)
    {
        IntPtr found = IntPtr.Zero;

        EnumWindows((window, _) =>
        {
            if (!IsWindowVisible(window)) return true;

            var text = new StringBuilder(256);
            if (GetWindowTextW(window, text, text.Capacity) <= 0) return true;
            if (!string.Equals(text.ToString(), title, StringComparison.Ordinal)) return true;

            found = window;
            return false;   // stop enumerating
        }, IntPtr.Zero);

        return found;
    }
}
