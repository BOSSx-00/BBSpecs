using System.Runtime.InteropServices;
using System.Text;

namespace BBSpecs.Services;

/// <summary>Window handling that Photino doesn't expose.</summary>
internal static class Native
{
    public const int SW_RESTORE = 9;
    public const int SW_HIDE = 0;
    public const int SW_SHOW = 5;

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

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr window, IntPtr after,
        int x, int y, int width, int height, uint flags);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr window, out RECT bounds);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    private const uint SWP_NOSIZE = 0x0001, SWP_NOZORDER = 0x0004, SWP_NOACTIVATE = 0x0010;

    /// <summary>
    /// Moves a window far off the desktop instead of hiding it.
    ///
    /// Hiding is the obvious way to start without a window on screen, and it
    /// does not work: a webview given a hidden window never initialises, so the
    /// window comes back empty when it is finally shown. Parked off-screen the
    /// window is a real, visible, paintable window as far as the webview is
    /// concerned, and nobody can see it.
    /// </summary>
    public static void ParkOffScreen(IntPtr window)
    {
        SetWindowPos(window, IntPtr.Zero, -32000, -32000, 0, 0,
                     SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);

        // Off the desktop but still a normal window, so the taskbar gives it a
        // button and it looks like an open app. The shell's own interface takes
        // the button away without touching the window styles, which is the
        // alternative and needs a hide and show to take effect: the very thing
        // that stops the webview initialising.
        SetTaskbarButton(window, present: false);
    }

    /// <summary>Brings a parked window back to the middle of the screen.</summary>
    public static void Unpark(IntPtr window, int x, int y)
    {
        if (!GetWindowRect(window, out RECT bounds)) return;
        if (bounds.Left > -30000) return;      // already on the desktop

        SetWindowPos(window, IntPtr.Zero, x, y, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
        SetTaskbarButton(window, present: true);
    }

    /// <summary>
    /// Adds or removes a window's taskbar button through the shell, leaving the
    /// window itself untouched. Best effort: a missing button is cosmetic, and
    /// nothing here is worth failing a launch over.
    /// </summary>
    private static void SetTaskbarButton(IntPtr window, bool present)
    {
        try
        {
            var taskbar = (ITaskbarList)new TaskbarList();
            taskbar.HrInit();

            if (present) taskbar.AddTab(window);
            else taskbar.DeleteTab(window);

            Marshal.ReleaseComObject(taskbar);
        }
        catch { }
    }

    [ComImport]
    [Guid("56FDF342-FD6D-11D0-958A-006097C9A090")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ITaskbarList
    {
        void HrInit();
        void AddTab(IntPtr window);
        void DeleteTab(IntPtr window);
        void ActivateTab(IntPtr window);
        void SetActiveAlt(IntPtr window);
    }

    [ComImport]
    [Guid("56FDF344-FD6D-11D0-958A-006097C9A090")]
    [ClassInterface(ClassInterfaceType.None)]
    private class TaskbarList { }

    /// <summary>
    /// The first top-level window with exactly this title, from any process.
    /// Title rather than process name, because a released build is renamed per
    /// version and the user may rename it again.
    ///
    /// <paramref name="includeHidden"/> matters when a copy is sitting in the
    /// tray with no window on screen. Launching the program again should bring
    /// that copy up, and it cannot be found by a search that skips windows
    /// nobody can currently see.
    /// </summary>
    public static IntPtr FindMainWindow(string title, bool includeHidden = false)
    {
        IntPtr found = IntPtr.Zero;

        EnumWindows((window, _) =>
        {
            if (!includeHidden && !IsWindowVisible(window)) return true;

            var text = new StringBuilder(256);
            if (GetWindowTextW(window, text, text.Capacity) <= 0) return true;
            if (!string.Equals(text.ToString(), title, StringComparison.Ordinal)) return true;

            found = window;
            return false;   // stop enumerating
        }, IntPtr.Zero);

        return found;
    }
}
