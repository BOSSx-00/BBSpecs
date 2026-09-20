using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace BBSpecs.Platform.Windows;

/// <summary>
/// The notification-area icon: a left click for the readings widget, a right
/// click for the menu, and the channel every desktop alert goes out through.
///
/// Written straight against Shell_NotifyIcon rather than pulling in WinForms.
/// The whole app is one downloadable file with nothing to install, and a tray
/// icon is not worth tens of megabytes of framework to draw.
///
/// Everything here has to happen on the thread that owns the window, because
/// the callbacks arrive as window messages and are only delivered to the
/// thread that created the receiving window.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class TrayIcon : IDisposable
{
    // ---- the four things the tray can ask for ----------------------------------

    public event Action? Activated;
    public event Action? OpenRequested;
    public event Action? UpdateRequested;
    public event Action? ExitRequested;

    private const int WM_APP = 0x8000;
    private const int TrayCallback = WM_APP + 1;

    private const int WM_LBUTTONUP = 0x0202;
    private const int WM_RBUTTONUP = 0x0205;
    private const int WM_DESTROY = 0x0002;
    private const int WM_COMMAND = 0x0111;

    private const int IdOpen = 1;
    private const int IdUpdate = 2;
    private const int IdExit = 3;

    private readonly WndProc _proc;          // held so the GC cannot collect it
    private readonly IntPtr _window;
    private readonly IntPtr _icon;
    private readonly uint _classAtom;
    private bool _added;
    private bool _disposed;

    public TrayIcon(string tooltip)
    {
        _proc = HandleMessage;

        // A message-only window: never shown, never in the taskbar, and exists
        // purely to be somewhere for the shell to send the click callbacks.
        var wc = new WNDCLASSEX
        {
            cbSize = Marshal.SizeOf<WNDCLASSEX>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_proc),
            hInstance = GetModuleHandle(null),
            lpszClassName = "BBSpecsTraySink",
        };

        _classAtom = RegisterClassEx(ref wc);
        if (_classAtom == 0) throw new InvalidOperationException("The tray message window could not be registered.");

        _window = CreateWindowEx(0, "BBSpecsTraySink", "", 0, 0, 0, 0, 0,
                                 HWND_MESSAGE, IntPtr.Zero, wc.hInstance, IntPtr.Zero);
        if (_window == IntPtr.Zero) throw new InvalidOperationException("The tray message window could not be created.");

        _icon = LoadOwnIcon();

        var data = NewData();
        data.uFlags = NIF_ICON | NIF_MESSAGE | NIF_TIP;
        data.uCallbackMessage = TrayCallback;
        data.hIcon = _icon;
        data.szTip = Trim(tooltip, 127);

        _added = Shell_NotifyIcon(NIM_ADD, ref data);
        if (_added)
        {
            // Opt into the modern behaviour, which is what makes balloons appear
            // as proper notifications rather than the old speech bubble.
            data.uVersion = NOTIFYICON_VERSION_4;
            Shell_NotifyIcon(NIM_SETVERSION, ref data);
        }
    }

    public bool IsPresent => _added;

    /// <summary>Updates the text shown when the pointer rests on the icon.</summary>
    public void SetTooltip(string tooltip)
    {
        if (!_added) return;

        var data = NewData();
        data.uFlags = NIF_TIP;
        data.szTip = Trim(tooltip, 127);
        Shell_NotifyIcon(NIM_MODIFY, ref data);
    }

    /// <summary>
    /// Raises a desktop notification. Windows takes it from here: it lands in
    /// the Action Center and obeys Focus Assist, so a notification sent while
    /// someone is playing a game waits rather than interrupting them.
    /// </summary>
    public void Notify(string title, string message, bool warning)
    {
        if (!_added) return;

        var data = NewData();
        data.uFlags = NIF_INFO;
        data.szInfoTitle = Trim(title, 63);
        data.szInfo = Trim(message, 255);
        data.dwInfoFlags = warning ? NIIF_WARNING : NIIF_INFO;
        Shell_NotifyIcon(NIM_MODIFY, ref data);
    }

    // ---- message handling ------------------------------------------------------

    private IntPtr HandleMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam)
    {
        switch (message)
        {
            case TrayCallback:
                // Under version 4 the mouse message is in the low word of lParam.
                switch ((int)(lParam.ToInt64() & 0xFFFF))
                {
                    case WM_LBUTTONUP: Activated?.Invoke(); return IntPtr.Zero;
                    case WM_RBUTTONUP: ShowMenu(); return IntPtr.Zero;
                }
                break;

            case WM_COMMAND:
                switch ((int)(wParam.ToInt64() & 0xFFFF))
                {
                    case IdOpen: OpenRequested?.Invoke(); return IntPtr.Zero;
                    case IdUpdate: UpdateRequested?.Invoke(); return IntPtr.Zero;
                    case IdExit: ExitRequested?.Invoke(); return IntPtr.Zero;
                }
                break;

            case WM_DESTROY:
                return IntPtr.Zero;
        }

        return DefWindowProc(window, message, wParam, lParam);
    }

    private void ShowMenu()
    {
        IntPtr menu = CreatePopupMenu();
        if (menu == IntPtr.Zero) return;

        try
        {
            AppendMenu(menu, MF_STRING, IdOpen, "Open BBSpecs");
            AppendMenu(menu, MF_STRING, IdUpdate, "Check for updates");
            AppendMenu(menu, MF_SEPARATOR, 0, null);
            AppendMenu(menu, MF_STRING, IdExit, "Exit");

            // Required, and genuinely load-bearing: without it the menu refuses
            // to close when the user clicks somewhere else and sits there until
            // they pick something.
            SetForegroundWindow(_window);

            GetCursorPos(out POINT cursor);
            TrackPopupMenuEx(menu, TPM_RIGHTBUTTON, cursor.X, cursor.Y, _window, IntPtr.Zero);
            PostMessage(_window, 0, IntPtr.Zero, IntPtr.Zero);
        }
        finally
        {
            DestroyMenu(menu);
        }
    }

    /// <summary>
    /// Pulls the icon out of the running executable, which is the one file
    /// guaranteed to be there whatever else the folder is missing.
    /// </summary>
    private static IntPtr LoadOwnIcon()
    {
        try
        {
            if (Environment.ProcessPath is string exe
                && ExtractIconEx(exe, 0, out IntPtr large, out IntPtr small, 1) > 0)
            {
                if (small != IntPtr.Zero)
                {
                    if (large != IntPtr.Zero) DestroyIcon(large);
                    return small;
                }

                if (large != IntPtr.Zero) return large;
            }
        }
        catch { }

        return LoadIcon(IntPtr.Zero, (IntPtr)32512);   // IDI_APPLICATION
    }

    private NOTIFYICONDATA NewData() => new()
    {
        cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
        hWnd = _window,
        uID = 1,
    };

    /// <summary>The struct fields are fixed-length buffers, so text has to fit.</summary>
    private static string Trim(string text, int max) =>
        text.Length <= max ? text : text[..max];

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_added)
        {
            var data = NewData();
            Shell_NotifyIcon(NIM_DELETE, ref data);
            _added = false;
        }

        if (_icon != IntPtr.Zero) DestroyIcon(_icon);
        if (_window != IntPtr.Zero) DestroyWindow(_window);
        if (_classAtom != 0) UnregisterClass("BBSpecsTraySink", GetModuleHandle(null));
    }

    // ---- interop ---------------------------------------------------------------

    private const int NIM_ADD = 0, NIM_MODIFY = 1, NIM_DELETE = 2, NIM_SETVERSION = 4;
    private const int NIF_MESSAGE = 0x01, NIF_ICON = 0x02, NIF_TIP = 0x04, NIF_INFO = 0x10;
    private const int NIIF_INFO = 0x01, NIIF_WARNING = 0x02;
    private const int NOTIFYICON_VERSION_4 = 4;

    private const int MF_STRING = 0x0000, MF_SEPARATOR = 0x0800;
    private const int TPM_RIGHTBUTTON = 0x0002;

    private static readonly IntPtr HWND_MESSAGE = new(-3);

    private delegate IntPtr WndProc(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEX
    {
        public int cbSize;
        public int style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszMenuName;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public int uID;
        public int uFlags;
        public int uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public int dwState;
        public int dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public int uVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public int dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIcon(int message, ref NOTIFYICONDATA data);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int ExtractIconEx(string file, int index, out IntPtr large, out IntPtr small, int count);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint RegisterClassEx(ref WNDCLASSEX wc);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool UnregisterClass(string className, IntPtr instance);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(int exStyle, string className, string windowName,
        int style, int x, int y, int width, int height, IntPtr parent, IntPtr menu,
        IntPtr instance, IntPtr param);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr window);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr DefWindowProc(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool AppendMenu(IntPtr menu, int flags, int id, string? item);

    [DllImport("user32.dll")]
    private static extern bool DestroyMenu(IntPtr menu);

    [DllImport("user32.dll")]
    private static extern bool TrackPopupMenuEx(IntPtr menu, int flags, int x, int y, IntPtr window, IntPtr parameters);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT point);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr window);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr LoadIcon(IntPtr instance, IntPtr name);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr icon);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? name);
}
