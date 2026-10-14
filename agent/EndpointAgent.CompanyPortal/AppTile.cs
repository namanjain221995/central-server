using EndpointAgent.Core.CompanyPortal;

namespace EndpointAgent.CompanyPortal;

/// <summary>What a tile's button does, decided by the state word the catalog gave the app.</summary>
internal enum TileState
{
    /// <summary>Not on this device; the button offers to install it.</summary>
    Available,

    /// <summary>On this device at an older version; the button offers the update.</summary>
    UpdateAvailable,

    /// <summary>Queued or running; the button waits.</summary>
    Installing,

    /// <summary>On this device at the catalog's version; nothing to do.</summary>
    Installed,

    /// <summary>A word this build does not know. Shown, never actionable.</summary>
    Unknown,
}

/// <summary>
/// One app in the catalog: icon, name, publisher and version, a short
/// description, and the one button whose label and enabled state follow
/// <see cref="TileState"/>.
/// </summary>
/// <remarks>
/// <para>
/// The only text from the wire that reaches the screen passes through here, and
/// it is only ever the <c>Text</c> of a label with mnemonics off, so an ampersand
/// in a product name is an ampersand. The button's words are constants. The
/// state word is matched exactly against the four the protocol documents; any
/// other is <see cref="TileState.Unknown"/> and gets a button that does nothing.
/// </para>
/// <para>
/// Sized in device pixels for the DPI it is created at, because a control added
/// to a form after the form has scaled itself is not scaled again. The form
/// passes its own DPI and font, so tiles match the chrome around them.
/// </para>
/// </remarks>
internal sealed class AppTile : UserControl
{
    /// <summary>Two tiles fit the default window side by side; one fits the minimum.</summary>
    public const int LogicalWidth = 400;

    private const int LogicalIcon = 48;
    private const int LogicalPadding = 10;
    private const int LogicalGap = 12;
    private const int LogicalButtonWidth = 96;
    private const int LogicalButtonHeight = 30;

    private readonly PictureBox _icon;
    private readonly Label _name;
    private readonly Label _meta;
    private readonly Label _description;
    private readonly Button _action;
    private readonly Font _nameFont;
    private readonly int _iconPixels;
    private Bitmap? _placeholder;
    private string _placeholderFor = string.Empty;

    /// <summary>The user pressed Install or Update. The form decides what to send.</summary>
    public event EventHandler? InstallRequested;

    public AppTile(PortalApp app, int dpi, Font font)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(font);

        int S(int logical) => (int)Math.Round(logical * dpi / 96.0);

        Font = font;
        _nameFont = new Font(font, FontStyle.Bold);
        _iconPixels = S(LogicalIcon);

        var lineHeight = (int)Math.Ceiling(font.GetHeight(dpi));
        var nameHeight = (int)Math.Ceiling(_nameFont.GetHeight(dpi)) + S(2);
        var metaHeight = lineHeight + S(2);
        var descriptionHeight = lineHeight * 2 + S(2);

        AutoScaleMode = AutoScaleMode.None;
        BorderStyle = BorderStyle.FixedSingle;
        BackColor = SystemColors.Window;
        Padding = new Padding(S(LogicalPadding));
        Margin = new Padding(S(6));
        Size = new Size(S(LogicalWidth), S(LogicalPadding) * 2 + 2 + nameHeight + metaHeight + descriptionHeight);

        _icon = new PictureBox
        {
            Size = new Size(_iconPixels, _iconPixels),
            SizeMode = PictureBoxSizeMode.Zoom,
            Margin = new Padding(0, 0, S(LogicalGap), 0),
            Anchor = AnchorStyles.Top | AnchorStyles.Left,
        };

        _name = new Label
        {
            AutoSize = false,
            Dock = DockStyle.Fill,
            Font = _nameFont,
            AutoEllipsis = true,
            UseMnemonic = false,
            TextAlign = ContentAlignment.MiddleLeft,
            Margin = Padding.Empty,
        };

        _meta = new Label
        {
            AutoSize = false,
            Dock = DockStyle.Fill,
            ForeColor = SystemColors.GrayText,
            AutoEllipsis = true,
            UseMnemonic = false,
            TextAlign = ContentAlignment.MiddleLeft,
            Margin = Padding.Empty,
        };

        _description = new Label
        {
            AutoSize = false,
            Dock = DockStyle.Fill,
            AutoEllipsis = true,
            UseMnemonic = false,
            TextAlign = ContentAlignment.TopLeft,
            Margin = Padding.Empty,
        };

        _action = new Button
        {
            AutoSize = true,
            MinimumSize = new Size(S(LogicalButtonWidth), S(LogicalButtonHeight)),
            Margin = new Padding(S(LogicalGap), 0, 0, 0),
            Anchor = AnchorStyles.None,
            UseMnemonic = false,
        };
        _action.Click += (_, _) => InstallRequested?.Invoke(this, EventArgs.Empty);

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            RowCount = 3,
            Margin = Padding.Empty,
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, nameHeight));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, metaHeight));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, descriptionHeight));

        layout.Controls.Add(_icon, 0, 0);
        layout.SetRowSpan(_icon, 3);
        layout.Controls.Add(_name, 1, 0);
        layout.Controls.Add(_meta, 1, 1);
        layout.Controls.Add(_description, 1, 2);
        layout.Controls.Add(_action, 2, 0);
        layout.SetRowSpan(_action, 3);

        Controls.Add(layout);

        App = app;
        Show(app);
    }

    /// <summary>The catalog entry this tile currently shows.</summary>
    public PortalApp App { get; private set; }

    public TileState State { get; private set; }

    /// <summary>True while an install request for this tile is on the wire.</summary>
    public bool Busy { get; private set; }

    public Guid Id => App.Id;

    public string DisplayName => App.DisplayName;

    public string? Category => App.Category;

    /// <summary>The protocol's four state words, matched exactly; anything else is unknown.</summary>
    public static TileState Parse(string state) => state switch
    {
        "Available" => TileState.Available,
        "UpdateAvailable" => TileState.UpdateAvailable,
        "Installing" => TileState.Installing,
        "Installed" => TileState.Installed,
        _ => TileState.Unknown,
    };

    /// <summary>Shows a (possibly newer) catalog entry for the same app.</summary>
    public void Show(PortalApp app)
    {
        ArgumentNullException.ThrowIfNull(app);

        App = app;
        State = Parse(app.State);

        _name.Text = app.DisplayName;
        _meta.Text = string.IsNullOrWhiteSpace(app.Publisher)
            ? app.Version
            : app.Publisher + " · " + app.Version;
        _description.Text = app.Description ?? string.Empty;

        EnsurePlaceholder(app.DisplayName);
        if (_icon.Image is null)
        {
            _icon.Image = _placeholder;
        }

        ApplyState();
    }

    /// <summary>The icon the service sent, or null to fall back to the placeholder.</summary>
    public void SetIcon(Image? image)
    {
        _icon.Image = image ?? _placeholder;
    }

    /// <summary>The button is pressed; hold it until the answer comes.</summary>
    public void BeginInstall()
    {
        Busy = true;
        ApplyState();
    }

    /// <summary>The answer came. A null state keeps the one the catalog gave, for a refusal or an error.</summary>
    public void EndInstall(TileState? state)
    {
        Busy = false;
        if (state is not null)
        {
            State = state.Value;
        }

        ApplyState();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            // The icon may belong to the cache; only the placeholder is this tile's to dispose.
            _icon.Image = null;
            _placeholder?.Dispose();
            _nameFont.Dispose();
        }

        base.Dispose(disposing);
    }

    private void ApplyState()
    {
        var (text, enabled) = State switch
        {
            TileState.Available => (PortalText.Install, true),
            TileState.UpdateAvailable => (PortalText.Update, true),
            TileState.Installing => (PortalText.Installing, false),
            TileState.Installed => (PortalText.Installed, false),
            _ => (PortalText.Unavailable, false),
        };

        _action.Text = text;
        _action.Enabled = enabled && !Busy;
        _action.ForeColor = State == TileState.Installed ? SystemColors.GrayText : SystemColors.ControlText;
    }

    private void EnsurePlaceholder(string displayName)
    {
        if (_placeholder is not null && _placeholderFor == displayName)
        {
            return;
        }

        var replaced = _placeholder;
        _placeholder = PlaceholderIcon.Create(displayName, _iconPixels, Font);
        _placeholderFor = displayName;

        if (replaced is not null)
        {
            if (ReferenceEquals(_icon.Image, replaced))
            {
                _icon.Image = _placeholder;
            }

            replaced.Dispose();
        }
    }
}
