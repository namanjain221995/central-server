using System.Runtime.InteropServices;
using EndpointAgent.Core.ScreenLock;
using EndpointAgent.Windows.ScreenLock;

namespace EndpointAgent.LockScreen;

/// <summary>
/// Draws the full-screen lock on every monitor, blocks the keyboard and mouse,
/// and talks to the LocalSystem service: polling what to show and whether the
/// device is still locked, and -- on Ctrl+Alt+U -- sending a typed code for the
/// service to verify.
/// </summary>
/// <remarks>
/// <para>
/// Thin on purpose, like the notifier's window. Every decision that could be got
/// wrong lives in tested Core helpers -- which monitors to cover
/// (<see cref="LockOverlayLayout"/>), what the fixed words are
/// (<see cref="LockScreenView"/>), which keys to swallow
/// (<see cref="LockInputFilter"/>), and how to read the service's verify answer
/// (<see cref="LockScreenPrompt"/>). This file is the raw Win32 and WinForms glue
/// around them, and is not unit-tested.
/// </para>
/// <para>
/// <b>Honest limits of a user-mode lock.</b> The low-level hooks cannot intercept
/// Ctrl+Alt+Del: the Secure Attention Sequence is handled by Winlogon on a desktop
/// no user process can touch. When the user returns from that screen the overlay
/// is still topmost and the watchdog re-launches it if it was closed, so it covers
/// the desktop again, but it cannot prevent the SAS menu itself. Nor can it stop a
/// hard power-off; that is covered by re-locking on the next boot. And a local
/// administrator can always end this process -- the service relaunches it, but the
/// lock is not a defence against the machine's own administrator. The
/// always-available second way out is a server <c>ReleaseScreenLock</c>, which does
/// not depend on anything here.
/// </para>
/// <para>
/// <b>Nothing received is executed.</b> The message and contact line are set as
/// plain <see cref="Label.Text"/> with mnemonics off; they are never parsed, never
/// a link or a path. The only thing sent to the service is the code the operator
/// typed.
/// </para>
/// </remarks>
internal sealed class LockOverlayController : IDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);

    private readonly ScreenLockPipeClient _client = new();
    private readonly CancellationTokenSource _closing = new();
    private readonly List<Form> _forms = [];
    private readonly HookProc _keyboardProc;
    private readonly HookProc _mouseProc;

    private Image? _logo;
    private Form? _contentForm;
    private Label? _messageLabel;
    private Label? _contactLabel;
    private Label? _statusLabel;
    private Panel? _promptPanel;
    private TextBox? _codeBox;
    private System.Windows.Forms.Timer? _poll;

    private IntPtr _keyboardHook;
    private IntPtr _mouseHook;
    private bool _prompting;
    private bool _dismissing;
    private bool _polling;

    public LockOverlayController()
    {
        _keyboardProc = KeyboardHook;
        _mouseProc = MouseHook;
    }

    public void Run()
    {
        LoadLogo();
        BuildForms();

        // Block input first, then install the hooks, then show: there must be no
        // instant in which the desktop is both visible and live.
        _ = NativeMethods.BlockInput(true);
        InstallHooks();

        foreach (var form in _forms)
        {
            form.Show();
        }

        TakeForeground();

        _poll = new System.Windows.Forms.Timer { Interval = (int)PollInterval.TotalMilliseconds };
        _poll.Tick += (_, _) => Tick();
        _poll.Start();

        Application.Run();
    }

    // ----------------------------------------------------------------- building

    private void LoadLogo()
    {
        try
        {
            using var stream = typeof(LockOverlayController).Assembly
                .GetManifestResourceStream("EndpointAgent.LockScreen.techsara-logo.png");
            if (stream is not null)
            {
                _logo = Image.FromStream(stream);
            }
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or OutOfMemoryException)
        {
            // No logo is not worth failing a lock over; the words still show.
            _logo = null;
        }
    }

    private void BuildForms()
    {
        var monitors = Screen.AllScreens
            .Select(s => new MonitorRect(s.Bounds.Left, s.Bounds.Top, s.Bounds.Width, s.Bounds.Height, s.Primary))
            .ToList();

        foreach (var overlay in LockOverlayLayout.For(monitors))
        {
            var form = NewOverlayForm(overlay.Bounds);
            if (overlay.ShowsContent)
            {
                _contentForm = form;
                BuildContent(form);
            }

            _forms.Add(form);
        }
    }

    private Form NewOverlayForm(MonitorRect bounds)
    {
        var form = new Form
        {
            FormBorderStyle = FormBorderStyle.None,
            StartPosition = FormStartPosition.Manual,
            ShowInTaskbar = false,
            ControlBox = false,
            MinimizeBox = false,
            MaximizeBox = false,
            TopMost = true,
            BackColor = Color.Black,
            Cursor = Cursors.Default,
            Bounds = new Rectangle(bounds.Left, bounds.Top, Math.Max(bounds.Width, 1), Math.Max(bounds.Height, 1)),
        };

        // Alt+F4 and the like are swallowed by the hook, but refuse a close we did
        // not start, as a last guard.
        form.FormClosing += (_, e) =>
        {
            if (!_dismissing)
            {
                e.Cancel = true;
            }
        };

        return form;
    }

    private void BuildContent(Form form)
    {
        var stack = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            BackColor = Color.Black,
            Anchor = AnchorStyles.None,
        };

        if (_logo is not null)
        {
            stack.Controls.Add(new PictureBox
            {
                Image = _logo,
                SizeMode = PictureBoxSizeMode.AutoSize,
                Margin = new Padding(0, 0, 0, 24),
                Anchor = AnchorStyles.None,
            });
        }

        stack.Controls.Add(Text(LockScreenView.Headline, 20f, FontStyle.Bold, Color.White));
        _messageLabel = Text(string.Empty, 14f, FontStyle.Regular, Color.Gainsboro, topMargin: 18);
        stack.Controls.Add(_messageLabel);
        _contactLabel = Text(string.Empty, 12f, FontStyle.Regular, Color.Silver, topMargin: 10);
        stack.Controls.Add(_contactLabel);

        stack.Controls.Add(Text(LockScreenView.UnlockHint, 10f, FontStyle.Italic, Color.DimGray, topMargin: 40));

        _promptPanel = BuildPrompt();
        stack.Controls.Add(_promptPanel);

        form.Controls.Add(stack);
        form.Resize += (_, _) => Centre(form, stack);
        form.Shown += (_, _) => Centre(form, stack);
        Centre(form, stack);
    }

    private Panel BuildPrompt()
    {
        var panel = new Panel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            BackColor = Color.Black,
            Visible = false,
            Margin = new Padding(0, 24, 0, 0),
        };

        var caption = Text(LockScreenView.PromptCaption, 12f, FontStyle.Bold, Color.White);
        caption.Location = new Point(0, 0);
        panel.Controls.Add(caption);

        _codeBox = new TextBox
        {
            UseSystemPasswordChar = true,
            Width = 320,
            Font = new Font("Segoe UI", 14f),
            Location = new Point(0, 34),
        };
        _codeBox.KeyDown += CodeBoxKeyDown;
        panel.Controls.Add(_codeBox);

        _statusLabel = Text(string.Empty, 11f, FontStyle.Regular, Color.Khaki);
        _statusLabel.Location = new Point(0, 74);
        panel.Controls.Add(_statusLabel);

        return panel;
    }

    private static Label Text(string text, float size, FontStyle style, Color colour, int topMargin = 0) => new()
    {
        Text = text,
        AutoSize = true,
        // Mnemonics off, so an '&' in the administrator's text is drawn, not eaten,
        // and never becomes an accelerator -- the plain-text guarantee.
        UseMnemonic = false,
        MaximumSize = new Size(820, 0),
        ForeColor = colour,
        BackColor = Color.Transparent,
        Font = new Font("Segoe UI", size, style),
        Margin = new Padding(0, topMargin, 0, 0),
        Anchor = AnchorStyles.None,
    };

    private static void Centre(Form form, Control content) =>
        content.Location = new Point(
            Math.Max(0, (form.ClientSize.Width - content.Width) / 2),
            Math.Max(0, (form.ClientSize.Height - content.Height) / 2));

    // ------------------------------------------------------------------- polling

    private async void Tick()
    {
        // Fight anything that steals the foreground: a locked screen that sits
        // behind another window is no lock at all.
        TakeForeground();

        if (_polling || _closing.IsCancellationRequested)
        {
            return;
        }

        _polling = true;
        try
        {
            var response = await _client.ShowAsync(_closing.Token);
            if (_closing.IsCancellationRequested)
            {
                return;
            }

            // Only a clear "not locked" dismisses the overlay. An unreachable
            // service (null) keeps the lock up: the device is not freed by a
            // service that cannot be asked.
            if (response is { Locked: false })
            {
                Dismiss();
                return;
            }

            if (response is { Locked: true })
            {
                ShowWords(response.Message, response.Contact);
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            _polling = false;
        }
    }

    private void ShowWords(string? message, string? contact)
    {
        if (_messageLabel is { } messageLabel)
        {
            messageLabel.Text = message ?? string.Empty;
        }

        if (_contactLabel is { } contactLabel)
        {
            contactLabel.Text = contact ?? string.Empty;
            contactLabel.Visible = !string.IsNullOrEmpty(contact);
        }
    }

    // -------------------------------------------------------------------- prompt

    private void OpenPrompt()
    {
        if (_prompting)
        {
            return;
        }

        _prompting = true;

        // The keyboard must now reach the passphrase box, so lift the belt-and-
        // braces block; the hook still swallows every key that is not for the box.
        _ = NativeMethods.BlockInput(false);

        if (_promptPanel is { } panel)
        {
            panel.Visible = true;
        }

        if (_statusLabel is { } status)
        {
            status.Text = string.Empty;
        }

        if (_codeBox is { } box)
        {
            box.Enabled = true;
            box.Clear();
            TakeForeground();
            box.Focus();
        }
    }

    private void CancelPrompt()
    {
        _prompting = false;
        if (_promptPanel is { } panel)
        {
            panel.Visible = false;
        }

        _codeBox?.Clear();
        _ = NativeMethods.BlockInput(true);
        TakeForeground();
    }

    private async void CodeBoxKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Escape)
        {
            e.SuppressKeyPress = true;
            CancelPrompt();
            return;
        }

        if (e.KeyCode != Keys.Enter)
        {
            return;
        }

        e.SuppressKeyPress = true;
        var code = _codeBox?.Text ?? string.Empty;
        if (code.Length == 0)
        {
            return;
        }

        await SubmitAsync(code);
    }

    private async Task SubmitAsync(string code)
    {
        if (_statusLabel is { } verifying)
        {
            verifying.Text = LockScreenView.Verifying;
        }

        if (_codeBox is { } box)
        {
            box.Enabled = false;
        }

        bool? unlocked;
        try
        {
            unlocked = await _client.SubmitAsync(code, _closing.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        switch (LockScreenPrompt.Interpret(unlocked))
        {
            case LockPromptOutcome.Dismiss:
                Dismiss();
                return;

            case LockPromptOutcome.ShowIncorrect:
                ShowPromptStatus(LockScreenView.Incorrect);
                return;

            case LockPromptOutcome.ShowUnavailable:
                ShowPromptStatus(LockScreenView.Unavailable);
                return;
        }
    }

    private void ShowPromptStatus(string status)
    {
        if (_statusLabel is { } label)
        {
            label.Text = status;
        }

        if (_codeBox is { } box)
        {
            box.Enabled = true;
            box.Clear();
            box.Focus();
        }
    }

    // ------------------------------------------------------------------ dismiss

    private void Dismiss()
    {
        if (_dismissing)
        {
            return;
        }

        _dismissing = true;
        _poll?.Stop();
        _closing.Cancel();
        RemoveHooks();
        _ = NativeMethods.BlockInput(false);

        foreach (var form in _forms)
        {
            form.Close();
        }

        Application.ExitThread();
    }

    // -------------------------------------------------------------- foreground

    private void TakeForeground()
    {
        if (_contentForm is not { IsDisposed: false } form)
        {
            return;
        }

        try
        {
            form.TopMost = true;
            form.BringToFront();
            _ = NativeMethods.SetForegroundWindow(form.Handle);
            form.Activate();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    // ------------------------------------------------------------------- hooks

    private void InstallHooks()
    {
        var module = NativeMethods.GetModuleHandleW(null);
        _keyboardHook = NativeMethods.SetWindowsHookExW(NativeMethods.WH_KEYBOARD_LL, _keyboardProc, module, 0);
        _mouseHook = NativeMethods.SetWindowsHookExW(NativeMethods.WH_MOUSE_LL, _mouseProc, module, 0);
    }

    private void RemoveHooks()
    {
        if (_keyboardHook != IntPtr.Zero)
        {
            _ = NativeMethods.UnhookWindowsHookEx(_keyboardHook);
            _keyboardHook = IntPtr.Zero;
        }

        if (_mouseHook != IntPtr.Zero)
        {
            _ = NativeMethods.UnhookWindowsHookEx(_mouseHook);
            _mouseHook = IntPtr.Zero;
        }
    }

    private IntPtr KeyboardHook(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0)
        {
            var message = wParam.ToInt64();
            var keyDown = message is NativeMethods.WM_KEYDOWN or NativeMethods.WM_SYSKEYDOWN;
            var virtualKey = Marshal.ReadInt32(lParam); // KBDLLHOOKSTRUCT.vkCode is the first field.
            var ctrl = IsDown(NativeMethods.VK_CONTROL);
            var alt = IsDown(NativeMethods.VK_MENU);

            switch (LockInputFilter.DecideKey(_prompting, ctrl, alt, virtualKey, keyDown))
            {
                case LockKeyAction.OpenPrompt:
                    OpenPrompt();
                    return new IntPtr(1); // swallow the chord's final key

                case LockKeyAction.PassToPrompt:
                    break; // let it through to the focused passphrase box

                case LockKeyAction.Swallow:
                default:
                    return new IntPtr(1);
            }
        }

        return NativeMethods.CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
    }

    private IntPtr MouseHook(int code, IntPtr wParam, IntPtr lParam)
    {
        // Every mouse event is swallowed: the overlay covers every monitor and the
        // prompt is keyboard-only, so nothing on the desktop is ever clickable.
        if (code >= 0 && LockInputFilter.SwallowMouse())
        {
            return new IntPtr(1);
        }

        return NativeMethods.CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
    }

    private static bool IsDown(int virtualKey) => (NativeMethods.GetAsyncKeyState(virtualKey) & 0x8000) != 0;

    public void Dispose()
    {
        RemoveHooks();
        _ = NativeMethods.BlockInput(false);
        _poll?.Dispose();
        _closing.Dispose();
        foreach (var form in _forms)
        {
            form.Dispose();
        }

        _logo?.Dispose();
    }

    private delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);

    private static class NativeMethods
    {
        internal const int WH_KEYBOARD_LL = 13;
        internal const int WH_MOUSE_LL = 14;
        internal const long WM_KEYDOWN = 0x0100;
        internal const long WM_SYSKEYDOWN = 0x0104;
        internal const int VK_CONTROL = 0x11;
        internal const int VK_MENU = 0x12;

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern IntPtr SetWindowsHookExW(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll")]
        internal static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool BlockInput([MarshalAs(UnmanagedType.Bool)] bool fBlockIt);

        [DllImport("user32.dll")]
        internal static extern short GetAsyncKeyState(int vKey);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        internal static extern IntPtr GetModuleHandleW(string? moduleName);
    }
}
