using System.Runtime.InteropServices;
using EndpointAgent.Core.SessionNotice;

namespace EndpointAgent.SessionNotice;

/// <summary>
/// The window a signed-in user sees: a restart countdown in fixed words, or,
/// when no restart wants the screen, an administrator's announcement.
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
/// before the restart is delivered. A hidden restart notice comes back for the
/// final minute, so a user who dismissed it early is still warned before the
/// machine goes down. A closed announcement, by contrast, is dismissed for good
/// BY ITS ID: however often the same announcement is delivered again -- a replay,
/// a re-queued task, each with a fresh timestamp -- it stays closed, and only an
/// announcement with a different id shows.
/// </para>
/// <para>
/// <b>One window, two faces, restart first.</b> When both a restart notice and an
/// announcement are live, the restart is shown: a reboot countdown must never sit
/// hidden behind a message that can wait. The announcement gets the screen only
/// while no restart wants it, and goes away on its own after
/// <see cref="AnnouncementView.LingerMinutes"/> minutes. With no announcement
/// around, the restart behaviour is exactly what it always was.
/// </para>
/// <para>
/// <b>Every word of chrome is a constant</b> from <see cref="RestartNoticeView"/>
/// and <see cref="AnnouncementView"/>. A restart's only variable is the countdown,
/// computed here from the notice's time and this machine's clock. An
/// announcement's title and message are an administrator's words, bounded by the
/// protocol and drawn as plain text with <c>DrawTextW</c> -- never parsed, never a
/// link, nothing to follow or execute (ADR-0005, announcements amendment).
/// </para>
/// <para>
/// <b>It closes when asked.</b> When the caller's exit condition becomes true --
/// the service has gone away -- and when Windows sends <c>WM_ENDSESSION</c>,
/// which is sign-out, shutdown, and also an installer's Restart Manager asking
/// applications holding files to close so an upgrade can replace them. Closing
/// is all it does then; the restart Windows is counting down is untouched.
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
    private static Func<AnnouncementNotice?> _latestAnnouncement = () => null;
    private static Func<bool> _exitRequested = () => false;
    private static RestartNotice? _lastSeen;
    private static AnnouncementNotice? _lastAnnouncementSeen;
    private static AnnouncementNotice? _measuredAnnouncement;

    // Ids the user closed. Grows only by a click per message; never reshown,
    // however often the same id is delivered again.
    private static readonly HashSet<Guid> _dismissedAnnouncementIds = [];
    private static RestartNoticeView _view = RestartNoticeView.For(null, DateTimeOffset.UtcNow);
    private static AnnouncementView _announcementView = AnnouncementView.For(null, DateTimeOffset.UtcNow);
    private static bool _shown;
    private static bool _userHid;
    private static bool _showingAnnouncement;
    private static bool _announcementCapped;
    private static string _currentTitle = RestartNoticeView.Title;
    private static float _scale = 1f;
    private static int _width;
    private static int _restartHeight;
    private static int _announcementHeight;
    private static int _announcementTitleHeight;
    private static int _announcementBodyHeight;
    private static int _currentHeight;
    private static IntPtr _okButton;
    private static IntPtr _titleFont;
    private static IntPtr _bodyFont;
    private static IntPtr _countdownFont;
    private static IntPtr _announcementTitleFont;

    // Held in a static so the delegate is not collected while Windows holds a pointer to it.
    private static readonly WndProc Procedure = WindowProcedure;

    public static int Run(Func<RestartNotice?> latest, Func<AnnouncementNotice?> latestAnnouncement, Func<bool> exitRequested)
    {
        _latest = latest;
        _latestAnnouncement = latestAnnouncement;
        _exitRequested = exitRequested;

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

        // Two sizes for two faces: the restart notice keeps its dimensions
        // exactly; an announcement's height is measured from its own text
        // (MeasureAnnouncement), so the window grows to fit the message and back
        // for the restart. Until something is measured, a generous default.
        _width = Scale(460);
        _restartHeight = Scale(290);
        _announcementHeight = Scale(500);
        _announcementTitleHeight = Scale(52);
        _announcementBodyHeight = Scale(264);
        _currentHeight = _restartHeight;

        int width = _width, height = _restartHeight;
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
        _announcementTitleFont = CreateFont(Scale(18), FW_BOLD);

        _okButton = CreateWindowExW(
            0, "BUTTON", "OK", WS_CHILD | WS_VISIBLE | WS_TABSTOP | BS_DEFPUSHBUTTON,
            width - Scale(110), height - Scale(90), Scale(80), Scale(30),
            window, new IntPtr(OkButtonId), instance, IntPtr.Zero);
        _ = SendMessageW(_okButton, WM_SETFONT, _bodyFont, new IntPtr(1));

        _ = SetTimer(window, new IntPtr(TimerId), TickMilliseconds, IntPtr.Zero);

        while (GetMessageW(out var message, IntPtr.Zero, 0, 0) > 0)
        {
            _ = TranslateMessage(ref message);
            _ = DispatchMessageW(ref message);
        }

        _ = DeleteObject(_titleFont);
        _ = DeleteObject(_bodyFont);
        _ = DeleteObject(_countdownFont);
        _ = DeleteObject(_announcementTitleFont);
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
                // Closing an announcement dismisses that announcement's ID for
                // good: a re-delivery of the same id -- replay, re-queued task --
                // stays closed, and only a different id shows.
                if (_showingAnnouncement)
                {
                    if (_lastAnnouncementSeen is { } shown)
                    {
                        _dismissedAnnouncementIds.Add(shown.Id);
                    }
                }
                else
                {
                    _userHid = true;
                }

                Hide(window);
                return IntPtr.Zero;

            case WM_PAINT:
                Paint(window);
                return IntPtr.Zero;

            case WM_QUERYENDSESSION:
                // Sign-out, shutdown, or Restart Manager on an installer's behalf:
                // there is never a reason to object.
                return new IntPtr(1);

            case WM_ENDSESSION when wParam != IntPtr.Zero:
                _ = DestroyWindow(window);
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
        if (_exitRequested())
        {
            _ = DestroyWindow(window);
            return;
        }

        var notice = _latest();
        var announcement = _latestAnnouncement();
        var now = DateTimeOffset.UtcNow;

        // A different restart is a new warning, even if the last one was hidden.
        if (notice != _lastSeen)
        {
            _lastSeen = notice;
            _userHid = false;
        }

        // The announcement needs no reset here: the view checks the notice's id
        // against the ones the user dismissed, so a new one shows by its new id.
        _lastAnnouncementSeen = announcement;

        _view = RestartNoticeView.For(notice, now);

        // A cancellation is shown once and can be dismissed for good: there is no
        // final minute to come back for. A pending restart comes back for its
        // last minute even if the user hid it.
        var remaining = notice is null || notice.Cancelled ? TimeSpan.MaxValue : notice.RestartAt - now;
        var showRestart = _view.Visible && (!_userHid || _view.Restarting || remaining <= FinalMinute);

        // The restart face always wins the window: a reboot countdown must never
        // sit hidden behind a message that can wait half an hour.
        _announcementView = AnnouncementView.For(announcement, now, _dismissedAnnouncementIds);
        _showingAnnouncement = !showRestart && _announcementView.Visible;

        var shouldShow = showRestart || _showingAnnouncement;

        // Size the window to the message it is about to show, once per message.
        if (_showingAnnouncement && !Equals(_lastAnnouncementSeen, _measuredAnnouncement))
        {
            MeasureAnnouncement(window);
        }

        var title = _showingAnnouncement
            ? AnnouncementView.WindowTitle
            : _view.Cancelled ? RestartNoticeView.CancelledTitle : RestartNoticeView.Title;
        if (!string.Equals(title, _currentTitle, StringComparison.Ordinal))
        {
            _currentTitle = title;
            _ = SetWindowTextW(window, title);
        }

        ResizeForFace(window, _showingAnnouncement);

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

    /// <summary>
    /// Grows the window to the measured height of the announcement it is about to
    /// show and shrinks it back for a restart, keeping it centred and the OK
    /// button in its corner. Idempotent per height, so the restart face is never
    /// touched while no announcement is being shown.
    /// </summary>
    private static void ResizeForFace(IntPtr window, bool announcement)
    {
        var height = announcement ? _announcementHeight : _restartHeight;
        if (height == _currentHeight)
        {
            return;
        }

        _currentHeight = height;
        var x = (GetSystemMetrics(SM_CXSCREEN) - _width) / 2;
        var y = (GetSystemMetrics(SM_CYSCREEN) - height) / 2;
        _ = SetWindowPos(window, IntPtr.Zero, x, y, _width, height, SWP_NOACTIVATE | SWP_NOZORDER);
        _ = MoveWindow(_okButton, _width - Scale(110), height - Scale(90), Scale(80), Scale(30), true);
        _ = InvalidateRect(window, IntPtr.Zero, true);
    }

    /// <summary>
    /// Measures the announcement's title and word-wrapped message at the window's
    /// text width (<c>DT_CALCRECT</c>) and sets the window height that shows ALL
    /// of it -- a 500-character message written as twenty short lines needs far
    /// more room than one paragraph. Capped at seven tenths of the work area so
    /// the window never swallows the desktop; past the cap the body is drawn with
    /// an end ellipsis, so truncation is visible instead of silent.
    /// </summary>
    private static void MeasureAnnouncement(IntPtr window)
    {
        _measuredAnnouncement = _lastAnnouncementSeen;
        _announcementTitleHeight = Scale(52);
        _announcementBodyHeight = Scale(264);
        _announcementCapped = false;
        _announcementHeight = Scale(500);

        if (_measuredAnnouncement is not { } notice)
        {
            return;
        }

        _ = GetClientRect(window, out var client);
        var margin = Scale(22);
        var textWidth = client.Right - 2 * margin;
        if (textWidth <= 0)
        {
            return;
        }

        var hdc = GetDC(window);
        if (hdc == IntPtr.Zero)
        {
            // No DC, no measurement; the generous defaults above still apply.
            return;
        }

        try
        {
            _announcementTitleHeight = MeasureText(hdc, _announcementTitleFont, notice.Title, textWidth, DT_WORDBREAK) + Scale(8);
            _announcementBodyHeight = MeasureText(hdc, _bodyFont, notice.Message, textWidth, DT_WORDBREAK | DT_EDITCONTROL) + Scale(12);
        }
        finally
        {
            _ = ReleaseDC(window, hdc);
        }

        // Everything around the two texts: top margin, fixed headline, fixed
        // footer, the button zone -- plus the caption and borders, taken from the
        // window as it really is rather than guessed.
        var chrome = margin + Scale(34) + Scale(26) + Scale(90);
        _ = GetWindowRect(window, out var bounds);
        var frame = bounds.Bottom - bounds.Top - client.Bottom;

        var desired = chrome + frame + _announcementTitleHeight + _announcementBodyHeight;

        var cap = GetSystemMetrics(SM_CYSCREEN) * 7 / 10;
        var workArea = default(RECT);
        if (SystemParametersInfoW(SPI_GETWORKAREA, 0, ref workArea, 0))
        {
            cap = (workArea.Bottom - workArea.Top) * 7 / 10;
        }

        if (desired > cap)
        {
            _announcementCapped = true;
            _announcementBodyHeight = Math.Max(Scale(24), cap - chrome - frame - _announcementTitleHeight);
            desired = cap;
        }

        _announcementHeight = Math.Max(_restartHeight, desired);
    }

    /// <summary>The height the text needs at the given width, in pixels.</summary>
    private static int MeasureText(IntPtr hdc, IntPtr font, string text, int width, uint format)
    {
        var rect = new RECT { Left = 0, Top = 0, Right = width, Bottom = 0 };
        var previous = SelectObject(hdc, font);
        _ = DrawTextW(hdc, text.Length == 0 ? " " : text, -1, ref rect, format | DT_CALCRECT | DT_NOPREFIX);
        _ = SelectObject(hdc, previous);
        return rect.Bottom;
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

            if (_showingAnnouncement)
            {
                // The chrome is fixed words; the administrator's title and
                // message are DATA, drawn as plain text and nothing else --
                // DT_NOPREFIX (added in Draw) keeps even an ampersand inert. The
                // heights come from MeasureAnnouncement, so the whole message is
                // on screen; a message past the window cap ends in a visible
                // ellipsis (the ellipsis flags and DT_EDITCONTROL exclude each
                // other, so the uncapped path keeps EDITCONTROL's mid-word wrap).
                var bodyFormat = _announcementCapped
                    ? DT_LEFT | DT_WORDBREAK | DT_END_ELLIPSIS
                    : DT_LEFT | DT_WORDBREAK | DT_EDITCONTROL;
                Draw(hdc, _titleFont, AnnouncementView.Headline, client, ref top, Scale(34), DT_LEFT);
                Draw(hdc, _announcementTitleFont, _announcementView.Title, client, ref top, _announcementTitleHeight, DT_LEFT | DT_WORDBREAK);
                Draw(hdc, _bodyFont, _announcementView.Message, client, ref top, _announcementBodyHeight, bodyFormat);
                Draw(hdc, _bodyFont, AnnouncementView.Footer, client, ref top, Scale(26), DT_LEFT);
                return;
            }

            if (_view.Cancelled)
            {
                // Every word is still a constant; only which constants changes.
                Draw(hdc, _titleFont, RestartNoticeView.CancelledTitle, client, ref top, Scale(34), DT_LEFT);
                Draw(hdc, _bodyFont, RestartNoticeView.CancelledHeadline, client, ref top, Scale(46), DT_LEFT | DT_WORDBREAK);
                Draw(hdc, _countdownFont, RestartNoticeView.CancelledBody, client, ref top, Scale(76), DT_CENTER);
                Draw(hdc, _bodyFont, RestartNoticeView.CancelledFooter, client, ref top, Scale(26), DT_LEFT);
                return;
            }

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
    private const uint WM_DESTROY = 0x0002, WM_PAINT = 0x000F, WM_CLOSE = 0x0010, WM_QUERYENDSESSION = 0x0011;
    private const uint WM_ENDSESSION = 0x0016, WM_SETFONT = 0x0030;
    private const uint WM_COMMAND = 0x0111, WM_TIMER = 0x0113;
    private const int SW_HIDE = 0, SW_SHOWNOACTIVATE = 4;
    private const uint SWP_NOSIZE = 0x0001, SWP_NOMOVE = 0x0002, SWP_NOACTIVATE = 0x0010, SWP_SHOWWINDOW = 0x0040;
    private const uint DT_LEFT = 0x0000, DT_CENTER = 0x0001, DT_WORDBREAK = 0x0010, DT_NOPREFIX = 0x0800, DT_EDITCONTROL = 0x2000;
    private const uint DT_CALCRECT = 0x0400, DT_END_ELLIPSIS = 0x8000;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SPI_GETWORKAREA = 0x0030;
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
    private static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr hWnd, int command);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowTextW(IntPtr hWnd, string text);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool MoveWindow(IntPtr hWnd, int x, int y, int width, int height, [MarshalAs(UnmanagedType.Bool)] bool repaint);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hdc);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SystemParametersInfoW(uint action, uint param, ref RECT value, uint winIni);

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
