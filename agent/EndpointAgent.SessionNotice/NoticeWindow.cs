using System.Runtime.InteropServices;
using EndpointAgent.Core.SessionNotice;

namespace EndpointAgent.SessionNotice;

/// <summary>
/// The window a signed-in user sees: a fixed message and a live countdown.
/// </summary>
/// <remarks>
/// <para>
/// Plain Win32 through P/Invoke rather than WinForms or WPF, so the notifier
/// shares the self-contained runtime the service already ships instead of adding
/// the desktop runtime for one window.
/// </para>
/// <para>
/// <b>Informational only.</b> It has an OK button that hides it, and a close box
/// that does the same. It has no control that cancels anything, because nothing on
/// the endpoint can: cancellation belongs to an administrator, in the console,
/// before the restart is delivered. A hidden notice comes back for the final
/// minute, so a user who dismissed it early is still warned before the machine
/// goes down.
/// </para>
/// <para>
/// <b>Every word is a constant</b> from <see cref="RestartNoticeView"/>. The only
/// thing that varies is the countdown, computed here from the notice's time and
/// this machine's clock -- the same clock the service used to compute it.
/// </para>
/// </remarks>
internal static class NoticeWindow
{
    private const string ClassName = "EndpointPlatformAgent.SessionNotice";
    private const int OkButtonId = 1;
    private const uint TimerId = 1;
    private const uint TickMilliseconds = 500;

    /// <summary>A user who hid the notice sees it again for the last minute.</summary>
    private static readonly TimeSpan FinalMinute = TimeSpan.FromMinutes(1);

    private static Func<RestartNotice?> _latest = () => null;
    private static RestartNotice? _lastSeen;
    private static RestartNoticeView _view = RestartNoticeView.For(null, DateTimeOffset.UtcNow);
    private static bool _shown;
    private static bool _userHid;
    private static float _scale = 1f;
    private static IntPtr _titleFont;
    private static IntPtr _bodyFont;
    private static IntPtr _countdownFont;

    // Held in a static so the delegate is not collected while Windows holds a pointer to it.
    private static readonly WndProc Procedure = WindowProcedure;

    public static int Run(Func<RestartNotice?> latest)
    {
        _latest = latest;

        // Crisp text on scaled displays; failure just means blurrier text.
        _ = SetProcessDpiAwarenessContext(new IntPtr(-4));
        _scale = GetDpiForSystem() / 96f;

        var instance = GetModuleHandleW(null);
        var windowClass = new WNDCLASSEX
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(),
            style = CS_HREDRAW | CS_VREDRAW,
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(Procedure),
            hInstance = instance,
            hCursor = LoadCursorW(IntPtr.Zero, new IntPtr(IDC_ARROW)),
            hbrBackground = new IntPtr(COLOR_WINDOW + 1),
            lpszClassName = ClassName,
        };

        if (RegisterClassExW(ref windowClass) == 0)
        {
            return 1;
        }

        int width = Scale(460), height = Scale(290);
        var x = (GetSystemMetrics(SM_CXSCREEN) - width) / 2;
        var y = (GetSystemMetrics(SM_CYSCREEN) - height) / 2;

        var window = CreateWindowExW(
            WS_EX_TOPMOST, ClassName, RestartNoticeView.Title,
            WS_POPUP | WS_CAPTION | WS_SYSMENU,
            x, y, width, height, IntPtr.Zero, IntPtr.Zero, instance, IntPtr.Zero);
        if (window == IntPtr.Zero)
        {
            return 1;
        }

        _titleFont = CreateFont(Scale(22), FW_BOLD);
        _bodyFont = CreateFont(Scale(16), FW_NORMAL);
        _countdownFont = CreateFont(Scale(40), FW_BOLD);

        var button = CreateWindowExW(
            0, "BUTTON", "OK", WS_CHILD | WS_VISIBLE | WS_TABSTOP | BS_DEFPUSHBUTTON,
            width - Scale(110), height - Scale(90), Scale(80), Scale(30),
            window, new IntPtr(OkButtonId), instance, IntPtr.Zero);
        _ = SendMessageW(button, WM_SETFONT, _bodyFont, new IntPtr(1));

        _ = SetTimer(window, new IntPtr(TimerId), TickMilliseconds, IntPtr.Zero);

        while (GetMessageW(out var message, IntPtr.Zero, 0, 0) > 0)
        {
            _ = TranslateMessage(ref message);
            _ = DispatchMessageW(ref message);
        }

        _ = DeleteObject(_titleFont);
        _ = DeleteObject(_bodyFont);
        _ = DeleteObject(_countdownFont);
        return 0;
    }

    private static IntPtr WindowProcedure(IntPtr window, uint message, IntPtr wParam, IntPtr lParam)
    {
        switch (message)
        {
            case WM_TIMER:
                Tick(window);
                return IntPtr.Zero;

            case WM_COMMAND when (wParam.ToInt64() & 0xFFFF) == OkButtonId:
            case WM_CLOSE:
                // Hide, never close: the notifier stays running for the session, and
                // hiding is all a user may do. Nothing here reaches the restart.
                _userHid = true;
                Hide(window);
                return IntPtr.Zero;

            case WM_PAINT:
                Paint(window);
                return IntPtr.Zero;

            case WM_DESTROY:
                PostQuitMessage(0);
                return IntPtr.Zero;

            default:
                return DefWindowProcW(window, message, wParam, lParam);
        }
    }

    private static void Tick(IntPtr window)
    {
        var notice = _latest();
        var now = DateTimeOffset.UtcNow;

        // A different restart is a new warning, even if the last one was hidden.
        if (notice != _lastSeen)
        {
            _lastSeen = notice;
            _userHid = false;
        }

        _view = RestartNoticeView.For(notice, now);

        var remaining = notice is null ? TimeSpan.MaxValue : notice.RestartAt - now;
        var shouldShow = _view.Visible && (!_userHid || _view.Restarting || remaining <= FinalMinute);

        if (shouldShow && !_shown)
        {
            // Shown without taking focus: the user may be typing, and a window that
            // grabs the keyboard invites an accidental Enter to dismiss it unread.
            _ = SetWindowPos(window, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_SHOWWINDOW);
            _ = ShowWindow(window, SW_SHOWNOACTIVATE);
            _shown = true;
        }
        else if (!shouldShow && _shown)
        {
            Hide(window);
        }

        if (_shown)
        {
            _ = InvalidateRect(window, IntPtr.Zero, true);
        }
    }

    private static void Hide(IntPtr window)
    {
        _ = ShowWindow(window, SW_HIDE);
        _shown = false;
    }

    private static void Paint(IntPtr window)
    {
        var hdc = BeginPaint(window, out var paint);
        try
        {
            _ = GetClientRect(window, out var client);
            _ = SetBkMode(hdc, TRANSPARENT);

            var margin = Scale(22);
            var top = margin;

            Draw(hdc, _titleFont, RestartNoticeView.Title, client, ref top, Scale(34), DT_LEFT);
            Draw(hdc, _bodyFont, RestartNoticeView.Headline, client, ref top, Scale(46), DT_LEFT | DT_WORDBREAK);

            if (_view.Restarting)
            {
                Draw(hdc, _countdownFont, RestartNoticeView.RestartingNow, client, ref top, Scale(64), DT_CENTER);
            }
            else
            {
                Draw(hdc, _bodyFont, RestartNoticeView.CountdownLabel, client, ref top, Scale(24), DT_CENTER);
                Draw(hdc, _countdownFont, _view.Countdown ?? string.Empty, client, ref top, Scale(52), DT_CENTER);
            }

            Draw(hdc, _bodyFont, RestartNoticeView.Footer, client, ref top, Scale(26), DT_LEFT);
        }
        finally
        {
            _ = EndPaint(window, ref paint);
        }
    }

    private static void Draw(IntPtr hdc, IntPtr font, string text, RECT client, ref int top, int height, uint format)
    {
        var margin = Scale(22);
        var rect = new RECT { Left = margin, Top = top, Right = client.Right - margin, Bottom = top + height };
        var previous = SelectObject(hdc, font);
        _ = DrawTextW(hdc, text, -1, ref rect, format | DT_NOPREFIX);
        _ = SelectObject(hdc, previous);
        top += height;
    }

    private static int Scale(int value) => (int)Math.Round(value * _scale);

    private static IntPtr CreateFont(int height, int weight) =>
        CreateFontW(-height, 0, 0, 0, weight, 0, 0, 0, DEFAULT_CHARSET, 0, 0, CLEARTYPE_QUALITY, 0, "Segoe UI");

    // ------------------------------------------------------------------ Win32

    private delegate IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    private const uint CS_VREDRAW = 0x0001, CS_HREDRAW = 0x0002;
    private const uint WS_POPUP = 0x80000000, WS_CAPTION = 0x00C00000, WS_SYSMENU = 0x00080000;
    private const uint WS_CHILD = 0x40000000, WS_VISIBLE = 0x10000000, WS_TABSTOP = 0x00010000;
    private const uint WS_EX_TOPMOST = 0x00000008;
    private const uint BS_DEFPUSHBUTTON = 0x00000001;
    private const uint WM_DESTROY = 0x0002, WM_PAINT = 0x000F, WM_CLOSE = 0x0010, WM_SETFONT = 0x0030;
    private const uint WM_COMMAND = 0x0111, WM_TIMER = 0x0113;
    private const int SW_HIDE = 0, SW_SHOWNOACTIVATE = 4;
    private const uint SWP_NOSIZE = 0x0001, SWP_NOMOVE = 0x0002, SWP_NOACTIVATE = 0x0010, SWP_SHOWWINDOW = 0x0040;
    private const uint DT_LEFT = 0x0000, DT_CENTER = 0x0001, DT_WORDBREAK = 0x0010, DT_NOPREFIX = 0x0800;
    private const int TRANSPARENT = 1, COLOR_WINDOW = 5, IDC_ARROW = 32512;
    private const int SM_CXSCREEN = 0, SM_CYSCREEN = 1;
    private const int FW_NORMAL = 400, FW_BOLD = 700;
    private const uint DEFAULT_CHARSET = 1, CLEARTYPE_QUALITY = 5;
    private static readonly IntPtr HWND_TOPMOST = new(-1);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEX
    {
        public uint cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X, Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public POINT pt;
        public uint lPrivate;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PAINTSTRUCT
    {
        public IntPtr hdc;
        [MarshalAs(UnmanagedType.Bool)] public bool fErase;
        public RECT rcPaint;
        [MarshalAs(UnmanagedType.Bool)] public bool fRestore;
        [MarshalAs(UnmanagedType.Bool)] public bool fIncUpdate;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] public byte[] rgbReserved;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassExW(ref WNDCLASSEX windowClass);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowExW(
        uint exStyle, string className, string windowName, uint style,
        int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr DefWindowProcW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetMessageW(out MSG msg, IntPtr hWnd, uint min, uint max);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TranslateMessage(ref MSG msg);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr DispatchMessageW(ref MSG msg);

    [DllImport("user32.dll")]
    private static extern void PostQuitMessage(int exitCode);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr hWnd, int command);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    private static extern IntPtr SetTimer(IntPtr hWnd, IntPtr id, uint elapse, IntPtr timerProc);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool InvalidateRect(IntPtr hWnd, IntPtr rect, [MarshalAs(UnmanagedType.Bool)] bool erase);

    [DllImport("user32.dll")]
    private static extern IntPtr BeginPaint(IntPtr hWnd, out PAINTSTRUCT paint);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EndPaint(IntPtr hWnd, ref PAINTSTRUCT paint);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetClientRect(IntPtr hWnd, out RECT rect);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int DrawTextW(IntPtr hdc, string text, int count, ref RECT rect, uint format);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessageW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadCursorW(IntPtr instance, IntPtr cursorName);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForSystem();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetProcessDpiAwarenessContext(IntPtr context);

    [DllImport("gdi32.dll")]
    private static extern int SetBkMode(IntPtr hdc, int mode);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr obj);

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateFontW(
        int height, int width, int escapement, int orientation, int weight, uint italic, uint underline,
        uint strikeOut, uint charSet, uint outPrecision, uint clipPrecision, uint quality, uint pitchAndFamily, string faceName);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandleW(string? moduleName);
}
