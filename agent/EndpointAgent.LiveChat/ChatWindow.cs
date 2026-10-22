using EndpointAgent.Windows.LiveChat;
using EndpointPlatform.Contracts.Agent;

namespace EndpointAgent.LiveChat;

/// <summary>
/// The live-chat window: a right-docked panel on the signed-in person's desktop
/// with a Techsara-branded header, a scrolling transcript, and a box to reply.
/// </summary>
/// <remarks>
/// <para>
/// Thin on purpose, like the portal and the overlay. Every exchange with the agent
/// is one awaited call on <see cref="LiveChatPipeClient"/>; the window thread is
/// never blocked, and every continuation checks the form is still alive before
/// touching it. The transcript is polled about once a second: new messages are
/// appended, never redrawn, so the list does not flicker or lose its place.
/// </para>
/// <para>
/// <b>Nothing received is executed.</b> The administrator's name and every message
/// body are set as plain <see cref="Label.Text"/> with mnemonics off; they are
/// never parsed, never a link or a path. The only thing sent to the service is the
/// text the person typed.
/// </para>
/// <para>
/// <b>No orphan window.</b> The window closes itself when the service says the chat
/// has ended, and when the service it reads from goes away (a run of unreachable
/// polls) -- the same "exit when the service disappears" rule the notifier and
/// overlay follow.
/// </para>
/// </remarks>
internal sealed class ChatWindow : Form
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);

    /// <summary>Consecutive unreachable polls before the window gives up and closes, so a brief blip does not.</summary>
    private const int MaxConsecutiveFailures = 6;

    private const int WindowWidth = 380;
    private const int MaxBubbleWidth = 250;

    private static readonly Color BrandColor = Color.FromArgb(0x11, 0x5E, 0x67);
    private static readonly Color AdminBubble = Color.FromArgb(0xEC, 0xEF, 0xF1);
    private static readonly Color AdminText = Color.FromArgb(0x20, 0x20, 0x20);
    private static readonly Color EmployeeBubble = Color.FromArgb(0x1C, 0x7C, 0x88);
    private static readonly Color EmployeeText = Color.White;
    private static readonly Color SystemText = Color.Gray;
    private static readonly Color MetaText = Color.FromArgb(0x70, 0x70, 0x70);

    private readonly LiveChatPipeClient _client = new();
    private readonly CancellationTokenSource _closing = new();

    private readonly Label _adminLabel;
    private readonly Panel _body;
    private readonly TableLayoutPanel _log;
    private readonly TextBox _input;
    private readonly Button _send;
    private readonly ToolStripStatusLabel _status;
    private readonly System.Windows.Forms.Timer _poll;
    private readonly Font _bodyFont;
    private readonly Font _metaFont;
    private Image? _logo;

    private int _displayedSeq;
    private int _consecutiveFailures;
    private bool _polling;
    private bool _sending;
    private bool _ending;
    private bool _reportedClosed;

    public ChatWindow()
    {
        Text = ChatUiText.Title;
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96F, 96F);
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        ShowInTaskbar = true;

        // Always on top. Windows refuses foreground focus to a window the person
        // did not start themselves (focus-stealing prevention), so without this the
        // chat appears only as a taskbar button behind whatever they are working
        // in -- and IT's opening message goes unseen. A narrow right-docked strip
        // that stays visible is the whole point of the window; the person can
        // still minimise or close it (a close is a dismissal, and a new message
        // from IT brings it back).
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        MinimumSize = new Size(WindowWidth, 300);
        ClientSize = new Size(WindowWidth, 600);

        _bodyFont = new Font("Segoe UI", 10f);
        _metaFont = new Font("Segoe UI", 7.5f);

        LoadLogo();

        // --- header: logo, "IT Support", and the admin's name -------------------
        var header = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 2,
            RowCount = 2,
            BackColor = BrandColor,
            Padding = new Padding(12, 10, 12, 10),
        };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        header.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        header.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        if (_logo is not null)
        {
            var logo = new PictureBox
            {
                Image = _logo,
                SizeMode = PictureBoxSizeMode.Zoom,
                Size = new Size(34, 34),
                Margin = new Padding(0, 0, 10, 0),
            };
            header.Controls.Add(logo, 0, 0);
            header.SetRowSpan(logo, 2);
        }

        var title = new Label
        {
            Text = ChatUiText.Title,
            Font = new Font("Segoe UI", 13f, FontStyle.Bold),
            ForeColor = Color.White,
            AutoSize = true,
            UseMnemonic = false,
            Margin = new Padding(0, 0, 0, 0),
        };
        _adminLabel = new Label
        {
            Text = ChatUiText.Subtitle,
            Font = new Font("Segoe UI", 9f),
            ForeColor = Color.Gainsboro,
            AutoSize = true,
            UseMnemonic = false,
            Margin = new Padding(0, 1, 0, 0),
        };
        header.Controls.Add(title, 1, 0);
        header.Controls.Add(_adminLabel, 1, 1);

        // --- transcript -----------------------------------------------------------
        _log = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1,
            Padding = new Padding(8, 8, 8, 8),
            BackColor = SystemColors.Window,
        };
        _log.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        _body = new Panel { Dock = DockStyle.Fill, AutoScroll = true, BackColor = SystemColors.Window };
        _body.HorizontalScroll.Enabled = false;
        _body.HorizontalScroll.Visible = false;
        _body.Controls.Add(_log);

        // --- input bar ------------------------------------------------------------
        _input = new TextBox
        {
            Dock = DockStyle.Fill,
            Font = _bodyFont,
            PlaceholderText = ChatUiText.InputPlaceholder,
            Multiline = false,
            Margin = new Padding(0, 0, 8, 0),
        };
        _input.KeyDown += InputKeyDown;

        _send = new Button
        {
            Text = ChatUiText.Send,
            AutoSize = true,
            MinimumSize = new Size(72, 28),
            Margin = Padding.Empty,
            UseMnemonic = false,
        };
        _send.Click += (_, _) => BeginSend();

        var inputBar = new TableLayoutPanel
        {
            Dock = DockStyle.Bottom,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 2,
            RowCount = 1,
            Padding = new Padding(10, 8, 10, 8),
            BackColor = SystemColors.Control,
        };
        inputBar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        inputBar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        inputBar.Controls.Add(_input, 0, 0);
        inputBar.Controls.Add(_send, 1, 0);

        // --- status strip ---------------------------------------------------------
        _status = new ToolStripStatusLabel { Text = ChatUiText.Connecting };
        var strip = new StatusStrip { SizingGrip = false };
        strip.Items.Add(_status);

        // Fill first (docked last, takes the centre), then the edges.
        Controls.Add(_body);
        Controls.Add(header);
        Controls.Add(inputBar);
        Controls.Add(strip);

        _poll = new System.Windows.Forms.Timer { Interval = (int)PollInterval.TotalMilliseconds };
        _poll.Tick += (_, _) => PollTick();
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        DockRight();
    }

    /// <summary>
    /// Shown WITHOUT requesting activation, the way the announcement notice is
    /// (SW_SHOWNOACTIVATE). A service-started process has no foreground rights, so
    /// asking for activation makes Windows park the window as a flashing taskbar
    /// button instead of showing it -- exactly the "it only appears in the taskbar"
    /// failure. Not asking at all, combined with TopMost, puts the window visibly
    /// in front of whatever the person is working in; their first click into the
    /// input box activates it normally.
    /// </summary>
    protected override bool ShowWithoutActivation => true;

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);

        // Belt-and-braces: whatever show-state the launch handed us, the chat is
        // only useful restored and visible.
        if (WindowState != FormWindowState.Normal)
        {
            WindowState = FormWindowState.Normal;
        }

        _poll.Start();
        PollTick();
    }

    private void DockRight()
    {
        var workingArea = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1024, 768);
        var width = Math.Min(WindowWidth, workingArea.Width);
        var height = workingArea.Height;
        Bounds = new Rectangle(workingArea.Right - width, workingArea.Top, width, height);
    }

    private void LoadLogo()
    {
        try
        {
            using var stream = typeof(ChatWindow).Assembly
                .GetManifestResourceStream("EndpointAgent.LiveChat.techsara-logo.png");
            if (stream is not null)
            {
                _logo = Image.FromStream(stream);
            }
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or OutOfMemoryException)
        {
            _logo = null;
        }
    }

    // --- polling ------------------------------------------------------------------

    private async void PollTick()
    {
        if (_polling || _closing.IsCancellationRequested)
        {
            return;
        }

        _polling = true;
        try
        {
            var response = await _client.PollAsync(_displayedSeq, _closing.Token);
            if (IsDisposed || _closing.IsCancellationRequested)
            {
                return;
            }

            if (response is null)
            {
                // Could not reach or trust the service. Tolerate a blip; give up on a run.
                _consecutiveFailures++;
                if (_consecutiveFailures >= MaxConsecutiveFailures)
                {
                    _status.Text = ChatUiText.Disconnected;
                    CloseWindow();
                }

                return;
            }

            _consecutiveFailures = 0;

            if (response.Active != true)
            {
                _status.Text = ChatUiText.Ended;
                CloseWindow();
                return;
            }

            if (!string.IsNullOrEmpty(response.AdminDisplay))
            {
                _adminLabel.Text = response.AdminDisplay;
            }

            if (response.Messages is { Count: > 0 } messages)
            {
                AppendMessages(messages);
            }

            _status.Text = ChatUiText.Connected;
        }
        catch (OperationCanceledException)
        {
            // Closing.
        }
        finally
        {
            _polling = false;
        }
    }

    private void AppendMessages(IReadOnlyList<ChatMessageDto> messages)
    {
        // A new line while the window is minimised would otherwise sit unread in
        // the taskbar; restore it so the conversation is actually seen.
        if (WindowState == FormWindowState.Minimized)
        {
            WindowState = FormWindowState.Normal;
        }

        var atBottom = IsScrolledToBottom();

        _log.SuspendLayout();
        foreach (var message in messages.OrderBy(m => m.Seq))
        {
            if (message.Seq <= _displayedSeq)
            {
                continue;
            }

            _log.Controls.Add(BuildRow(message), 0, _log.RowCount);
            _log.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            _log.RowCount++;
            _displayedSeq = message.Seq;
        }

        _log.ResumeLayout(true);

        if (atBottom)
        {
            ScrollToBottom();
        }
    }

    /// <summary>One message as a bubble: admin left and light, employee right and branded, system centred and muted.</summary>
    private Control BuildRow(ChatMessageDto message)
    {
        var isEmployee = string.Equals(message.Sender, ChatUiText.SenderEmployee, StringComparison.Ordinal);
        var isSystem = string.Equals(message.Sender, ChatUiText.SenderSystem, StringComparison.Ordinal);

        if (isSystem)
        {
            var systemLabel = new Label
            {
                Text = message.Body,
                Font = new Font(_bodyFont, FontStyle.Italic),
                ForeColor = SystemText,
                AutoSize = true,
                UseMnemonic = false,
                TextAlign = ContentAlignment.MiddleCenter,
                Anchor = AnchorStyles.None,
                MaximumSize = new Size(MaxBubbleWidth + 60, 0),
                Margin = new Padding(0, 6, 0, 6),
            };

            var systemRow = new TableLayoutPanel
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                ColumnCount = 1,
                RowCount = 1,
                Margin = Padding.Empty,
            };
            systemRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            systemRow.Controls.Add(systemLabel, 0, 0);
            return systemRow;
        }

        var bubble = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            BackColor = isEmployee ? EmployeeBubble : AdminBubble,
            Padding = new Padding(10, 7, 10, 7),
            Margin = new Padding(0, 4, 0, 4),
        };

        var bodyLabel = new Label
        {
            Text = message.Body,
            Font = _bodyFont,
            ForeColor = isEmployee ? EmployeeText : AdminText,
            AutoSize = true,
            UseMnemonic = false,
            MaximumSize = new Size(MaxBubbleWidth, 0),
            Margin = Padding.Empty,
        };
        bubble.Controls.Add(bodyLabel);

        var meta = new Label
        {
            Text = $"{message.SenderDisplay} · {message.SentAt.ToLocalTime():t}",
            Font = _metaFont,
            ForeColor = isEmployee ? Color.Gainsboro : MetaText,
            AutoSize = true,
            UseMnemonic = false,
            MaximumSize = new Size(MaxBubbleWidth, 0),
            Margin = new Padding(0, 3, 0, 0),
        };
        bubble.Controls.Add(meta);

        var row = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 2,
            RowCount = 1,
            Margin = Padding.Empty,
        };

        if (isEmployee)
        {
            // Spacer on the left, bubble hugs the right.
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            row.Controls.Add(new Panel { Margin = Padding.Empty }, 0, 0);
            row.Controls.Add(bubble, 1, 0);
        }
        else
        {
            // Bubble hugs the left, spacer on the right.
            row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            row.Controls.Add(bubble, 0, 0);
            row.Controls.Add(new Panel { Margin = Padding.Empty }, 1, 0);
        }

        return row;
    }

    private bool IsScrolledToBottom()
    {
        var offset = _body.VerticalScroll.Value + _body.ClientSize.Height;
        return offset >= _body.VerticalScroll.Maximum - 40;
    }

    private void ScrollToBottom()
    {
        if (_log.Controls.Count == 0)
        {
            return;
        }

        var last = _log.Controls[^1];
        _body.ScrollControlIntoView(last);
    }

    // --- sending ------------------------------------------------------------------

    private async void BeginSend()
    {
        if (_sending || _closing.IsCancellationRequested)
        {
            return;
        }

        var text = _input.Text.Trim();
        if (text.Length == 0)
        {
            return;
        }

        if (text.Length > EndpointAgent.Core.Chat.ChatText.MaxBodyChars)
        {
            text = text[..EndpointAgent.Core.Chat.ChatText.MaxBodyChars];
        }

        _sending = true;
        _send.Enabled = false;
        _input.Enabled = false;
        try
        {
            var accepted = await _client.SendAsync(text, _closing.Token);
            if (IsDisposed || _closing.IsCancellationRequested)
            {
                return;
            }

            if (accepted == true)
            {
                _input.Clear();
                // The server's copy returns on the next poll as an Employee message.
            }
            else if (accepted == false)
            {
                _status.Text = ChatUiText.Ended;
            }
            else
            {
                _status.Text = ChatUiText.Disconnected;
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            _sending = false;
            if (!IsDisposed && !_closing.IsCancellationRequested)
            {
                _send.Enabled = true;
                _input.Enabled = true;
                _input.Focus();
            }
        }
    }

    private void InputKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Enter && !e.Shift)
        {
            e.SuppressKeyPress = true;
            BeginSend();
        }
    }

    private void CloseWindow()
    {
        if (_ending)
        {
            return;
        }

        _ending = true;
        _poll.Stop();
        Close();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        _poll.Stop();

        // Only a close the PERSON chose is reported. When the service drove this
        // close (the chat ended, or the service went away), it already knows -- and
        // a late "closed" report could race a brand-new chat on the service and
        // dismiss it before its window ever appeared.
        if (!_ending)
        {
            ReportClosedBestEffort();
        }

        _closing.Cancel();
        base.OnFormClosing(e);
    }

    /// <summary>
    /// Tells the service the person closed the window. The chat stays open on the
    /// device -- the service re-shows the window when the administrator's next
    /// message arrives. Bounded and best-effort: a slow or missing service never
    /// holds the close.
    /// </summary>
    private void ReportClosedBestEffort()
    {
        if (_reportedClosed)
        {
            return;
        }

        _reportedClosed = true;
        try
        {
            using var brief = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            _client.ReportClosedAsync(brief.Token).GetAwaiter().GetResult();
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or InvalidOperationException)
        {
            // Closing anyway; the service also learns the window is gone on its own.
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !IsDisposed)
        {
            _closing.Cancel();
            _poll.Dispose();
            _closing.Dispose();
            _bodyFont.Dispose();
            _metaFont.Dispose();
            _logo?.Dispose();
        }

        base.Dispose(disposing);
    }
}
