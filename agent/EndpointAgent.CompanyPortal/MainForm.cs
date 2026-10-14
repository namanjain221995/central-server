using EndpointAgent.Core.CompanyPortal;

namespace EndpointAgent.CompanyPortal;

/// <summary>
/// The portal window: a searchable catalog of tiles grouped by category, a
/// Refresh button, and a status line that says how the agent is doing.
/// </summary>
/// <remarks>
/// <para>
/// Every exchange with the agent is one awaited call on <see cref="PortalPipeClient"/>;
/// the window thread is never blocked, and every continuation checks that the
/// form is still alive before touching it. Closing the window cancels whatever
/// is in flight.
/// </para>
/// <para>
/// The catalog is re-read on a timer: every ten seconds while any tile is
/// installing, so the user sees it finish, and every minute otherwise. Tiles
/// are kept across refreshes and updated in place, so icons stay put and the
/// list does not flicker or lose its scroll position.
/// </para>
/// <para>
/// Every word in the status line, on a button or in a message box is a constant
/// from <see cref="PortalText"/>, chosen by which branch ran. An outcome or
/// error word from the wire selects a branch; it is never shown.
/// </para>
/// </remarks>
internal sealed class MainForm : Form
{
    private static readonly TimeSpan IdleRefresh = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan InstallingRefresh = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan SearchSettle = TimeSpan.FromMilliseconds(200);

    /// <summary>The install outcomes the protocol documents. Matched, never displayed.</summary>
    private const string OutcomeQueued = "Queued";
    private const string OutcomeAlreadyInstalled = "AlreadyInstalled";
    private const string OutcomeAlreadyInProgress = "AlreadyInProgress";
    private const string OutcomeRefused = "Refused";

    private readonly PortalPipeClient _client = new();
    private readonly IconCache _icons = new();
    private readonly CancellationTokenSource _closing = new();
    private readonly Dictionary<Guid, AppTile> _tiles = [];
    private readonly List<Control> _groupControls = [];
    private IReadOnlyList<PortalApp> _apps = [];

    private readonly Font _titleFont;
    private readonly Font _headerFont;
    private readonly TextBox _search;
    private readonly Button _refresh;
    private readonly Panel _body;
    private readonly TableLayoutPanel _groups;
    private readonly Label _message;
    private readonly ToolStripStatusLabel _status;
    private readonly System.Windows.Forms.Timer _autoRefresh;
    private readonly System.Windows.Forms.Timer _searchTimer;

    private bool _refreshing;
    private bool _fetchingIcons;
    private bool _loadedOnce;

    public MainForm()
    {
        Text = PortalText.Title;
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96F, 96F);
        ClientSize = new Size(900, 640);
        MinimumSize = new Size(640, 480);
        StartPosition = FormStartPosition.CenterScreen;

        _titleFont = new Font(Font.FontFamily, 16f, FontStyle.Bold);
        _headerFont = new Font(Font.FontFamily, 11f, FontStyle.Bold);

        // --- timers: created first, because the controls below wire up to them ----
        _autoRefresh = new System.Windows.Forms.Timer { Interval = (int)IdleRefresh.TotalMilliseconds };
        _autoRefresh.Tick += (_, _) => RefreshCatalog();

        _searchTimer = new System.Windows.Forms.Timer { Interval = (int)SearchSettle.TotalMilliseconds };
        _searchTimer.Tick += (_, _) =>
        {
            _searchTimer.Stop();
            if (_loadedOnce)
            {
                Relayout();
            }
        };

        // --- top bar: title, subtitle, search, refresh ---------------------------
        var title = new Label
        {
            Text = PortalText.Title,
            Font = _titleFont,
            AutoSize = true,
            UseMnemonic = false,
            Margin = new Padding(0, 0, 0, 2),
        };

        // The machine name is this computer's, from the environment, not the wire.
        var subtitle = new Label
        {
            Text = PortalText.Subtitle + " · " + Environment.MachineName,
            ForeColor = SystemColors.GrayText,
            AutoSize = true,
            UseMnemonic = false,
            Margin = Padding.Empty,
        };

        _search = new TextBox
        {
            Width = 240,
            PlaceholderText = PortalText.SearchPlaceholder,
            Anchor = AnchorStyles.None,
            Margin = new Padding(0, 0, 8, 0),
        };
        _search.TextChanged += (_, _) =>
        {
            _searchTimer.Stop();
            _searchTimer.Start();
        };

        _refresh = new Button
        {
            Text = PortalText.Refresh,
            AutoSize = true,
            MinimumSize = new Size(90, 28),
            Anchor = AnchorStyles.None,
            Margin = Padding.Empty,
            UseMnemonic = false,
        };
        _refresh.Click += (_, _) => RefreshCatalog();

        var tools = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Anchor = AnchorStyles.Right,
            Margin = Padding.Empty,
        };
        tools.Controls.Add(_search);
        tools.Controls.Add(_refresh);

        var header = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 2,
            RowCount = 2,
            Padding = new Padding(16, 14, 16, 12),
            BackColor = SystemColors.Window,
        };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        header.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        header.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        header.Controls.Add(title, 0, 0);
        header.Controls.Add(subtitle, 0, 1);
        header.Controls.Add(tools, 1, 0);
        header.SetRowSpan(tools, 2);

        // --- status strip ----------------------------------------------------------
        // Not Spring: on the current desktop runtime a springing status label is
        // laid out past the strip's right edge and its text never appears. The
        // label sizes to its text and sits at the left on its own.
        _status = new ToolStripStatusLabel { Text = PortalText.Loading };
        var strip = new StatusStrip { SizingGrip = true };
        strip.Items.Add(_status);

        // --- body: category headers and rows of tiles, or one centred message -----
        _groups = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1,
            Padding = new Padding(10, 6, 10, 10),
            Visible = false,
        };
        _groups.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        _message = new Label
        {
            Dock = DockStyle.Fill,
            Text = PortalText.Loading,
            TextAlign = ContentAlignment.MiddleCenter,
            ForeColor = SystemColors.GrayText,
            UseMnemonic = false,
        };

        _body = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
        // Tiles wrap to the width they have; nothing is ever wider than the window.
        _body.HorizontalScroll.Enabled = false;
        _body.HorizontalScroll.Visible = false;
        _body.Controls.Add(_message);
        _body.Controls.Add(_groups);

        Controls.Add(_body);
        Controls.Add(header);
        Controls.Add(strip);
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        RefreshCatalog();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        _autoRefresh.Stop();
        _searchTimer.Stop();
        _closing.Cancel();
        base.OnFormClosing(e);
    }

    protected override void Dispose(bool disposing)
    {
        // Disposed twice on every close -- once by Application.Run when the form
        // closes, once by Main's using -- so the second call must find nothing to do.
        if (disposing && !IsDisposed)
        {
            _closing.Cancel();
            _autoRefresh.Dispose();
            _searchTimer.Dispose();
            _icons.Dispose();
            _closing.Dispose();
            _titleFont.Dispose();
            _headerFont.Dispose();
        }

        base.Dispose(disposing);
    }

    private bool AnyInstalling => _tiles.Values.Any(t => t.State == TileState.Installing);

    // --- the catalog ---------------------------------------------------------------

    /// <summary>Asks the agent for the list. One at a time: a second request while one is out is ignored.</summary>
    private async void RefreshCatalog()
    {
        if (_refreshing || _closing.IsCancellationRequested)
        {
            return;
        }

        // The wait can be long -- the service gives the server up to 30 s -- so
        // the user sees that something is happening: the status line says so,
        // Refresh is disabled, and the timer is held until this answer is in.
        _refreshing = true;
        _refresh.Enabled = false;
        _autoRefresh.Stop();
        SetStatus(PortalText.Loading);
        if (!_loadedOnce)
        {
            ShowMessage(PortalText.Loading);
        }

        try
        {
            var response = await _client.SendAsync(PortalRequest.ListApps, _closing.Token);
            if (IsDisposed)
            {
                return;
            }

            if (!response.Ok)
            {
                Fail(TextFor(response.Error ?? PortalError.BadRequest));
            }
            else if (response.Apps is null)
            {
                // A well-formed answer to a different question is still not an answer to this one.
                Fail(PortalText.AgentUnexpected);
            }
            else
            {
                ApplyCatalog(response.Apps);
                SetStatus(PortalText.Connected);
                FetchMissingIcons();
            }
        }
        catch (PortalUnavailableException ex)
        {
            if (!IsDisposed)
            {
                Fail(TextFor(ex.Reason));
            }
        }
        catch (OperationCanceledException)
        {
            // Closing.
        }
        finally
        {
            _refreshing = false;
            if (!IsDisposed && !_closing.IsCancellationRequested)
            {
                _refresh.Enabled = true;
                ScheduleNextRefresh();
            }
        }
    }

    /// <summary>Updates tiles in place, adds new ones, drops the ones that went away, then regroups.</summary>
    private void ApplyCatalog(IReadOnlyList<PortalApp> apps)
    {
        _apps = apps;
        _loadedOnce = true;

        var seen = new HashSet<Guid>();
        foreach (var app in apps)
        {
            if (!seen.Add(app.Id))
            {
                // A catalog that lists an app twice gets one tile for it.
                continue;
            }

            if (_tiles.TryGetValue(app.Id, out var tile))
            {
                // A tile with an install on the wire keeps its button until the answer comes.
                if (!tile.Busy)
                {
                    tile.Show(app);
                }
            }
            else
            {
                tile = new AppTile(app, DeviceDpi, Font);
                tile.InstallRequested += OnInstallRequested;
                _tiles[app.Id] = tile;
            }

            tile.SetIcon(_icons.TryGet(app.Id, app.UpdatedAt, out var icon) ? icon : null);
        }

        foreach (var gone in _tiles.Keys.Where(id => !seen.Contains(id)).ToList())
        {
            var tile = _tiles[gone];
            _tiles.Remove(gone);
            tile.InstallRequested -= OnInstallRequested;
            tile.Parent?.Controls.Remove(tile);
            tile.Dispose();
        }

        Relayout();
    }

    /// <summary>
    /// Rebuilds the category rows from the tiles that match the search. Tiles
    /// are re-parented, not re-created, so their icons and state carry over.
    /// </summary>
    private void Relayout()
    {
        var scroll = _body.AutoScrollPosition;

        _body.SuspendLayout();
        _groups.SuspendLayout();

        // Detach the tiles first: disposing a panel disposes its children.
        foreach (var control in _groupControls)
        {
            if (control is FlowLayoutPanel row)
            {
                row.Controls.Clear();
            }

            control.Dispose();
        }

        _groupControls.Clear();
        _groups.Controls.Clear();
        _groups.RowStyles.Clear();
        _groups.RowCount = 0;

        var query = _search.Text.Trim();
        var visible = _tiles.Values
            .Where(t => query.Length == 0 || t.DisplayName.Contains(query, StringComparison.CurrentCultureIgnoreCase))
            .OrderBy(t => t.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        if (visible.Count == 0)
        {
            ShowMessage(_apps.Count == 0 ? PortalText.Empty : PortalText.NoMatches);
        }
        else
        {
            // Named categories alphabetically; apps with none last, under a fixed heading.
            var groups = visible
                .GroupBy(t => string.IsNullOrWhiteSpace(t.Category) ? null : t.Category.Trim(), StringComparer.CurrentCultureIgnoreCase)
                .OrderBy(g => g.Key is null ? 1 : 0)
                .ThenBy(g => g.Key, StringComparer.CurrentCultureIgnoreCase);

            foreach (var group in groups)
            {
                var heading = new Label
                {
                    Text = group.Key ?? PortalText.OtherCategory,
                    Font = _headerFont,
                    AutoSize = true,
                    UseMnemonic = false,
                    Anchor = AnchorStyles.Left,
                    Margin = new Padding(6, 12, 6, 4),
                };
                AddGroupRow(heading);

                var row = new FlowLayoutPanel
                {
                    FlowDirection = FlowDirection.LeftToRight,
                    WrapContents = true,
                    AutoSize = true,
                    AutoSizeMode = AutoSizeMode.GrowAndShrink,
                    Anchor = AnchorStyles.Left | AnchorStyles.Right,
                    Margin = Padding.Empty,
                };
                foreach (var tile in group)
                {
                    row.Controls.Add(tile);
                }

                AddGroupRow(row);
            }

            _message.Visible = false;
            _groups.Visible = true;
        }

        _groups.ResumeLayout(true);
        _body.ResumeLayout(true);
        _body.AutoScrollPosition = new Point(-scroll.X, -scroll.Y);
    }

    private void AddGroupRow(Control control)
    {
        _groupControls.Add(control);
        _groups.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _groups.RowCount = _groups.RowStyles.Count;
        _groups.Controls.Add(control, 0, _groups.RowCount - 1);
    }

    // --- icons -----------------------------------------------------------------------

    /// <summary>
    /// Fetches, one at a time, the icons the catalog says exist and the cache does
    /// not have. One at a time because the service rates the session, and a
    /// burst of a hundred requests would be refused and shown as an error.
    /// </summary>
    private async void FetchMissingIcons()
    {
        if (_fetchingIcons)
        {
            return;
        }

        _fetchingIcons = true;
        try
        {
            foreach (var app in _apps.Where(a => a.HasIcon).ToList())
            {
                if (_closing.IsCancellationRequested || _icons.TryGet(app.Id, app.UpdatedAt, out _))
                {
                    continue;
                }

                PortalResponse response;
                try
                {
                    response = await _client.SendAsync(PortalRequest.IconOf(app.Id), _closing.Token);
                }
                catch (PortalUnavailableException)
                {
                    // The next list refresh will say what is wrong, in the status line.
                    return;
                }

                if (IsDisposed)
                {
                    return;
                }

                if (!response.Ok)
                {
                    if (response.Error == PortalError.BadRequest)
                    {
                        // This icon, specifically, will not come; do not ask again for this version.
                        _icons.Put(app.Id, app.UpdatedAt, null);
                        continue;
                    }

                    // Rate limit, enrolment or server trouble: stop for now, try again next refresh.
                    return;
                }

                var image = response.Icon is null ? null : IconCache.Decode(response.Icon);
                _icons.Put(app.Id, app.UpdatedAt, image);

                if (_tiles.TryGetValue(app.Id, out var tile) && tile.App.UpdatedAt == app.UpdatedAt)
                {
                    tile.SetIcon(image);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Closing.
        }
        finally
        {
            _fetchingIcons = false;
        }
    }

    // --- installing ------------------------------------------------------------------

    private async void OnInstallRequested(object? sender, EventArgs e)
    {
        if (sender is not AppTile tile || tile.Busy || _closing.IsCancellationRequested)
        {
            return;
        }

        tile.BeginInstall();
        try
        {
            var response = await _client.SendAsync(PortalRequest.InstallOf(tile.Id), _closing.Token);
            if (IsDisposed)
            {
                return;
            }

            if (!response.Ok)
            {
                tile.EndInstall(null);
                SetStatus(TextFor(response.Error ?? PortalError.BadRequest));
                return;
            }

            switch (response.Outcome)
            {
                case OutcomeQueued:
                    tile.EndInstall(TileState.Installing);
                    SetStatus(PortalText.InstallStarted);
                    break;

                case OutcomeAlreadyInstalled:
                    tile.EndInstall(TileState.Installed);
                    SetStatus(PortalText.Connected);
                    break;

                case OutcomeAlreadyInProgress:
                    tile.EndInstall(TileState.Installing);
                    SetStatus(PortalText.InstallInProgress);
                    break;

                case OutcomeRefused:
                    tile.EndInstall(null);
                    SetStatus(PortalText.Connected);
                    MessageBox.Show(this, PortalText.Refused, PortalText.Title, MessageBoxButtons.OK, MessageBoxIcon.Information);
                    break;

                default:
                    tile.EndInstall(null);
                    SetStatus(PortalText.AgentUnexpected);
                    break;
            }
        }
        catch (PortalUnavailableException ex)
        {
            if (!IsDisposed)
            {
                tile.EndInstall(null);
                SetStatus(TextFor(ex.Reason));
            }
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (!IsDisposed && !_closing.IsCancellationRequested && !_refreshing)
        {
            ScheduleNextRefresh();
        }
    }

    // --- status and messages -------------------------------------------------------

    private void ScheduleNextRefresh()
    {
        _autoRefresh.Stop();
        _autoRefresh.Interval = (int)(AnyInstalling ? InstallingRefresh : IdleRefresh).TotalMilliseconds;
        _autoRefresh.Start();
    }

    private void SetStatus(string text) => _status.Text = text;

    /// <summary>An error from the agent: in the status line always, and in the body too until a catalog has been shown.</summary>
    private void Fail(string text)
    {
        SetStatus(text);
        if (!_loadedOnce)
        {
            ShowMessage(text);
        }
    }

    private void ShowMessage(string text)
    {
        _message.Text = text;
        _groups.Visible = false;
        _message.Visible = true;
    }

    private static string TextFor(PortalError error) => error switch
    {
        PortalError.NotEnrolled => PortalText.NotEnrolled,
        PortalError.ServerUnavailable => PortalText.ServerUnavailable,
        PortalError.TooManyRequests => PortalText.TooManyRequests,
        _ => PortalText.AgentUnexpected,
    };

    private static string TextFor(PortalUnavailableReason reason) => reason switch
    {
        PortalUnavailableReason.AgentNotRunning => PortalText.AgentNotRunning,
        PortalUnavailableReason.Untrusted => PortalText.AgentUntrusted,
        PortalUnavailableReason.Timeout => PortalText.AgentTimeout,
        _ => PortalText.AgentUnexpected,
    };
}
