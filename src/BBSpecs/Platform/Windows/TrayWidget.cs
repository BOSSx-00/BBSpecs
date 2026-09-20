using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace BBSpecs.Platform.Windows;

/// <summary>
/// The small panel that appears above the tray when the icon is clicked: six
/// numbers and nothing else.
///
/// Drawn with GDI rather than being a second webview. Photino's extra windows
/// run a nested message loop, which would freeze the main window for as long as
/// the panel stayed open, and a whole browser engine is a heavy way to print
/// six numbers. This shares the main message loop, costs nothing while hidden,
/// and appears instantly.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class TrayWidget : IDisposable
{
    public readonly record struct Readings(
        double? CpuLoad, double? CpuTemp,
        double? GpuLoad, double? GpuTemp,
        double? DownKbps, double? UpKbps);

    /// <summary>Raised when the user clicks the panel, which opens the full app.</summary>
    public event Action? Clicked;

    private const int Width = 268;
    private const int Height = 178;
    private const int Pad = 16;

    private readonly WndProc _proc;
    private readonly IntPtr _window;
    private readonly uint _classAtom;

    private IntPtr _titleFont, _labelFont, _valueFont;
    private IntPtr _background, _border;

    private Readings _readings;
    private string _version = "";
    private bool _visible;
    private bool _disposed;

    /// <summary>When the panel was last dismissed by a click outside it.</summary>
    private long _dismissedAt;

    private readonly MouseProc _mouseProc;   // held so the GC cannot collect it
    private IntPtr _hook;

    public TrayWidget()
    {
        _proc = HandleMessage;
        _mouseProc = OnMouseEvent;

        var wc = new WNDCLASSEX
        {
            cbSize = Marshal.SizeOf<WNDCLASSEX>(),
            style = CS_DROPSHADOW,
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_proc),
            hInstance = GetModuleHandle(null),
            hCursor = LoadCursor(IntPtr.Zero, (IntPtr)32512),   // IDC_ARROW
            lpszClassName = "BBSpecsTrayWidget",
        };

        _classAtom = RegisterClassEx(ref wc);
        if (_classAtom == 0) throw new InvalidOperationException("The widget window could not be registered.");

        _window = CreateWindowEx(
            WS_EX_TOOLWINDOW | WS_EX_TOPMOST | WS_EX_NOACTIVATE,
            "BBSpecsTrayWidget", "", WS_POPUP,
            0, 0, Width, Height,
            IntPtr.Zero, IntPtr.Zero, wc.hInstance, IntPtr.Zero);

        if (_window == IntPtr.Zero) throw new InvalidOperationException("The widget window could not be created.");

        CreateDrawingTools();
    }

    public bool IsVisible => _visible;

    /// <summary>
    /// Colours follow the theme the app is set to, so the two match. Kept in
    /// step with the [data-theme] blocks in styles.css by hand: there is no
    /// stylesheet on this side of the app to read them from.
    /// </summary>
    public void SetTheme(string theme)
    {
        (int back, int brand, int text, int muted) = theme switch
        {
            "akbu" => (Rgb(0x2C, 0x2C, 0x2C), Rgb(0xFF, 0x2D, 0x95), Rgb(0xFF, 0xFF, 0xFF), Rgb(0xB4, 0xB4, 0xB4)),
            "astro" => (Rgb(0xFF, 0xFD, 0xF8), Rgb(0x0F, 0x8F, 0x88), Rgb(0x3A, 0x2F, 0x26), Rgb(0x6B, 0x5A, 0x49)),
            _ => (Rgb(0x12, 0x13, 0x14), Rgb(0xFF, 0x1F, 0x3D), Rgb(0xFF, 0xFF, 0xFF), Rgb(0x9B, 0x9D, 0xA4)),
        };

        if (_background != IntPtr.Zero) DeleteObject(_background);
        if (_border != IntPtr.Zero) DeleteObject(_border);

        _background = CreateSolidBrush(back);
        _border = CreateSolidBrush(brand);
        _brand = brand;
        _text = text;
        _muted = muted;

        if (_visible) Invalidate();
    }

    public void Update(Readings readings, string version)
    {
        _readings = readings;
        _version = version;
        if (_visible) Invalidate();
    }

    /// <summary>Shows the panel just above the notification area, or hides it.</summary>
    public void Toggle()
    {
        if (_visible) { Hide(); return; }

        // The same click that is about to open it may have just closed it: the
        // watcher sees the press on the tray icon before the shell delivers the
        // release to us. Without this the panel would reopen instead of closing.
        if (Environment.TickCount64 - _dismissedAt < 350) return;

        GetCursorPos(out POINT cursor);

        // Kept inside whichever monitor the tray is on, and clear of the taskbar.
        RECT work = WorkArea(cursor);
        int x = Math.Clamp(cursor.X - Width / 2, work.Left + 8, work.Right - Width - 8);
        int y = cursor.Y > (work.Top + work.Bottom) / 2
            ? work.Bottom - Height - 8      // taskbar at the bottom: sit above it
            : work.Top + 8;                 // taskbar at the top: sit below it

        SetWindowPos(_window, HWND_TOPMOST, x, y, Width, Height, SWP_SHOWWINDOW | SWP_NOACTIVATE);
        _visible = true;

        // Only hooked while the panel is on screen, so there is no global hook
        // sitting in the system for the rest of the session.
        _hook = SetWindowsHookEx(WH_MOUSE_LL, _mouseProc, GetModuleHandle(null), 0);

        Invalidate();
    }

    public void Hide()
    {
        if (!_visible) return;

        if (_hook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_hook);
            _hook = IntPtr.Zero;
        }

        ShowWindow(_window, SW_HIDE);
        _visible = false;
    }

    /// <summary>
    /// Closes the panel when a mouse button goes down anywhere outside it.
    ///
    /// This took three attempts, so the two that failed are worth recording.
    ///
    /// Taking the keyboard focus and closing on losing it is the obvious answer
    /// and does not survive contact with Windows: SetForegroundWindow is
    /// refused to a process that does not already own the foreground window,
    /// and a click on a tray icon is input to the shell rather than to us, so
    /// the call fails exactly when it is needed. Forcing it through with
    /// AttachThreadInput made the panel dismiss itself during its own
    /// appearance, so the tray icon looked like it did nothing at all.
    ///
    /// Polling the mouse on a timer failed differently. A click is often
    /// shorter than the gap between two polls, so most of them were missed
    /// entirely, and GetAsyncKeyState's "pressed since you last asked" bit is
    /// documented as unreliable when anything else on the machine is polling
    /// it too, which on Windows is always.
    ///
    /// A low-level mouse hook is the mechanism actually meant for this. It sees
    /// every click however brief, it is installed only while the panel is on
    /// screen, and passing the event straight on means the click still lands
    /// wherever it was aimed. The panel never asks for focus at all, so it can
    /// appear over your work without stealing what you were typing into.
    /// </summary>
    private IntPtr OnMouseEvent(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0 && IsButtonDown(wParam.ToInt64()))
        {
            var hookData = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
            GetWindowRect(_window, out RECT bounds);

            bool inside = hookData.pt.X >= bounds.Left && hookData.pt.X < bounds.Right
                       && hookData.pt.Y >= bounds.Top && hookData.pt.Y < bounds.Bottom;

            if (!inside)
            {
                // Recorded before hiding: clicking the tray icon to close the
                // panel arrives here first, and the shell's own notification
                // follows a moment later. Without this it would reopen.
                _dismissedAt = Environment.TickCount64;
                Hide();
            }
        }

        return CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
    }

    private static bool IsButtonDown(long message) =>
        message is WM_LBUTTONDOWN or WM_RBUTTONDOWN or WM_MBUTTONDOWN or WM_NCLBUTTONDOWN;

    // ---- painting --------------------------------------------------------------

    private IntPtr HandleMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam)
    {
        switch (message)
        {
            case WM_PAINT:
                Paint();
                return IntPtr.Zero;

            case WM_LBUTTONUP:
                Hide();
                Clicked?.Invoke();
                return IntPtr.Zero;


        }

        return DefWindowProc(window, message, wParam, lParam);
    }

    private void Paint()
    {
        IntPtr dc = BeginPaint(_window, out PAINTSTRUCT ps);
        try
        {
            var full = new RECT { Left = 0, Top = 0, Right = Width, Bottom = Height };

            FillRect(dc, ref full, _border);                     // one-pixel frame
            var inner = new RECT { Left = 1, Top = 2, Right = Width - 1, Bottom = Height - 1 };
            FillRect(dc, ref inner, _background);

            SetBkMode(dc, TRANSPARENT);

            int y = Pad - 4;

            SelectObject(dc, _titleFont);
            SetTextColor(dc, _brand);
            Draw(dc, "BBSPECS", Pad, y);

            SelectObject(dc, _labelFont);
            SetTextColor(dc, _muted);
            DrawRight(dc, _version, Width - Pad, y + 2);

            y += 30;

            Row(dc, ref y, "PROCESSOR", Percent(_readings.CpuLoad), Degrees(_readings.CpuTemp));
            Row(dc, ref y, "GRAPHICS", Percent(_readings.GpuLoad), Degrees(_readings.GpuTemp));
            Row(dc, ref y, "NETWORK", Speed(_readings.DownKbps, "down"), Speed(_readings.UpKbps, "up"));
        }
        finally
        {
            EndPaint(_window, ref ps);
        }
    }

    private void Row(IntPtr dc, ref int y, string label, string left, string right)
    {
        SelectObject(dc, _labelFont);
        SetTextColor(dc, _muted);
        Draw(dc, label, Pad, y);

        SelectObject(dc, _valueFont);
        SetTextColor(dc, _text);
        Draw(dc, left, Pad, y + 15);
        DrawRight(dc, right, Width - Pad, y + 15);

        y += 44;
    }

    private static string Percent(double? value) => value is double v ? $"{v:0}%" : "--";
    private static string Degrees(double? value) => value is double v ? $"{v:0}°C" : "--";

    private static string Speed(double? kbps, string direction)
    {
        if (kbps is not double v) return $"-- {direction}";
        return v >= 1000 ? $"{v / 1000:0.0} Mb/s {direction}" : $"{v:0} Kb/s {direction}";
    }

    private void Draw(IntPtr dc, string text, int x, int y) =>
        TextOut(dc, x, y, text, text.Length);

    private void DrawRight(IntPtr dc, string text, int right, int y)
    {
        GetTextExtentPoint32(dc, text, text.Length, out SIZE size);
        TextOut(dc, right - size.cx, y, text, text.Length);
    }

    private void Invalidate() => InvalidateRect(_window, IntPtr.Zero, false);

    private void CreateDrawingTools()
    {
        // Consolas, because it is on every Windows install and its digits are
        // fixed width: numbers that change every second should not make the
        // lines around them jump about.
        _titleFont = CreateFont(15, "Consolas", bold: true);
        _labelFont = CreateFont(12, "Consolas", bold: false);
        _valueFont = CreateFont(17, "Consolas", bold: false);
        SetTheme("bossx");
    }

    private static IntPtr CreateFont(int height, string face, bool bold) =>
        CreateFontW(-height, 0, 0, 0, bold ? 700 : 400, 0, 0, 0,
                    1 /* DEFAULT_CHARSET */, 0, 0, 5 /* CLEARTYPE_QUALITY */, 0, face);

    private static RECT WorkArea(POINT near)
    {
        IntPtr monitor = MonitorFromPoint(near, MONITOR_DEFAULTTONEAREST);
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };

        if (monitor != IntPtr.Zero && GetMonitorInfo(monitor, ref info)) return info.rcWork;

        return new RECT { Left = 0, Top = 0, Right = 1920, Bottom = 1040 };
    }

    private static int Rgb(int r, int g, int b) => r | (g << 8) | (b << 16);
    private int _brand = Rgb(0xFF, 0x1F, 0x3D);
    private int _text = Rgb(0xFF, 0xFF, 0xFF);
    private int _muted = Rgb(0x9B, 0x9D, 0xA4);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        foreach (IntPtr font in new[] { _titleFont, _labelFont, _valueFont })
            if (font != IntPtr.Zero) DeleteObject(font);

        if (_background != IntPtr.Zero) DeleteObject(_background);
        if (_border != IntPtr.Zero) DeleteObject(_border);
        if (_window != IntPtr.Zero) DestroyWindow(_window);
        if (_classAtom != 0) UnregisterClass("BBSpecsTrayWidget", GetModuleHandle(null));
    }

    // ---- interop ---------------------------------------------------------------

    private const int WS_POPUP = unchecked((int)0x80000000);
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_TOPMOST = 0x00000008;
    private const int CS_DROPSHADOW = 0x00020000;

    private const int WM_PAINT = 0x000F;
    private const int WM_LBUTTONUP = 0x0202;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int SWP_NOACTIVATE = 0x0010;

    private const int WH_MOUSE_LL = 14;
    private const int WM_LBUTTONDOWN = 0x0201, WM_RBUTTONDOWN = 0x0204;
    private const int WM_MBUTTONDOWN = 0x0207, WM_NCLBUTTONDOWN = 0x00A1;

    private delegate IntPtr MouseProc(int code, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct MSLLHOOKSTRUCT
    {
        public POINT pt;
        public uint mouseData;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    private const int SW_HIDE = 0;
    private const int SWP_SHOWWINDOW = 0x0040;
    private const int TRANSPARENT = 1;
    private const int MONITOR_DEFAULTTONEAREST = 2;

    private static readonly IntPtr HWND_TOPMOST = new(-1);

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

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct SIZE { public int cx, cy; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO { public int cbSize; public RECT rcMonitor, rcWork; public int dwFlags; }

    [StructLayout(LayoutKind.Sequential)]
    private struct PAINTSTRUCT
    {
        public IntPtr hdc;
        public int fErase;
        public RECT rcPaint;
        public int fRestore, fIncUpdate;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] public byte[] rgbReserved;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint RegisterClassEx(ref WNDCLASSEX wc);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool UnregisterClass(string className, IntPtr instance);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(int exStyle, string className, string windowName,
        int style, int x, int y, int width, int height, IntPtr parent, IntPtr menu,
        IntPtr instance, IntPtr param);

    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr window);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr DefWindowProc(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr window, int command);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int cx, int cy, int flags);
    [DllImport("user32.dll")] private static extern bool InvalidateRect(IntPtr window, IntPtr rect, bool erase);
    [DllImport("user32.dll")] private static extern IntPtr BeginPaint(IntPtr window, out PAINTSTRUCT ps);
    [DllImport("user32.dll")] private static extern bool EndPaint(IntPtr window, ref PAINTSTRUCT ps);
    [DllImport("user32.dll")] private static extern int FillRect(IntPtr dc, ref RECT rect, IntPtr brush);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT point);
    [DllImport("user32.dll")] private static extern IntPtr LoadCursor(IntPtr instance, IntPtr name);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromPoint(POINT point, int flags);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, out RECT bounds);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int type, MouseProc callback, IntPtr module, uint thread);

    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hook);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);

    [DllImport("gdi32.dll")] private static extern IntPtr CreateSolidBrush(int color);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr handle);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr handle);
    [DllImport("gdi32.dll")] private static extern int SetTextColor(IntPtr dc, int color);
    [DllImport("gdi32.dll")] private static extern int SetBkMode(IntPtr dc, int mode);

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode, EntryPoint = "TextOutW")]
    private static extern bool TextOut(IntPtr dc, int x, int y, string text, int length);

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetTextExtentPoint32W")]
    private static extern bool GetTextExtentPoint32(IntPtr dc, string text, int length, out SIZE size);

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode, EntryPoint = "CreateFontW")]
    private static extern IntPtr CreateFontW(int height, int width, int escapement, int orientation,
        int weight, int italic, int underline, int strikeOut, int charSet, int outPrecision,
        int clipPrecision, int quality, int pitchAndFamily, string face);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? name);
}
