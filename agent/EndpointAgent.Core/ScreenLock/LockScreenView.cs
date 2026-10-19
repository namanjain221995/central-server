namespace EndpointAgent.Core.ScreenLock;

/// <summary>
/// The fixed words framing the screen-lock overlay. Everything here is a
/// compile-time constant; the only variable text on the overlay is the
/// administrator's message and contact line, which are DATA, drawn as plain text
/// and never parsed (ADR-0005, screen-lock amendment).
/// </summary>
public static class LockScreenView
{
    /// <summary>Fixed. The headline, which says who locked the device.</summary>
    public const string Headline = "This device has been locked by your IT administrator.";

    /// <summary>Fixed. The label above the administrator's message.</summary>
    public const string MessageLabel = "Message from your IT administrator";

    /// <summary>Fixed. The hint that tells IT staff how to unlock on the device.</summary>
    public const string UnlockHint = "IT staff: press Ctrl+Alt+U to enter the unlock code.";

    /// <summary>Fixed. The caption on the passphrase prompt.</summary>
    public const string PromptCaption = "Enter the IT unlock code";

    /// <summary>Fixed. Shown while the service verifies a submitted code.</summary>
    public const string Verifying = "Verifying…";

    /// <summary>Fixed. Shown when the service rejects a submitted code.</summary>
    public const string Incorrect = "That code is not correct.";

    /// <summary>Fixed. Shown when the service cannot be reached to verify a code.</summary>
    public const string Unavailable = "The unlock service is not reachable right now. Try again in a moment.";
}

/// <summary>A monitor's bounds in virtual-desktop coordinates. One overlay window covers each.</summary>
public readonly record struct MonitorRect(int Left, int Top, int Width, int Height, bool Primary)
{
    public int Right => Left + Width;
    public int Bottom => Top + Height;
}

/// <summary>One overlay window: the monitor it covers and whether it carries the logo and text.</summary>
/// <param name="Bounds">The monitor to cover, edge to edge.</param>
/// <param name="ShowsContent">True for exactly one overlay -- the one that shows the logo, message and prompt.</param>
public sealed record LockOverlay(MonitorRect Bounds, bool ShowsContent);

/// <summary>
/// Which overlay windows to create for a set of monitors, and which one carries
/// the content. Pure, so the decision is tested without a display.
/// </summary>
public static class LockOverlayLayout
{
    /// <summary>
    /// One overlay per monitor, each covering its monitor edge to edge so no part
    /// of any desktop is ever exposed. Exactly one overlay carries the logo,
    /// message and prompt: the primary monitor when one is marked, otherwise the
    /// first. With no monitors reported, a single content overlay is still
    /// returned so a lock is never silently invisible.
    /// </summary>
    public static IReadOnlyList<LockOverlay> For(IReadOnlyList<MonitorRect> monitors)
    {
        ArgumentNullException.ThrowIfNull(monitors);

        if (monitors.Count == 0)
        {
            // No monitor information: still cover something, so a lock is never a
            // no-op that leaves the desktop live.
            return [new LockOverlay(new MonitorRect(0, 0, 0, 0, Primary: true), ShowsContent: true)];
        }

        var contentIndex = IndexOfContentMonitor(monitors);
        var overlays = new LockOverlay[monitors.Count];
        for (var i = 0; i < monitors.Count; i++)
        {
            overlays[i] = new LockOverlay(monitors[i], ShowsContent: i == contentIndex);
        }

        return overlays;
    }

    /// <summary>The primary monitor when one is marked, else the first.</summary>
    public static int IndexOfContentMonitor(IReadOnlyList<MonitorRect> monitors)
    {
        ArgumentNullException.ThrowIfNull(monitors);
        for (var i = 0; i < monitors.Count; i++)
        {
            if (monitors[i].Primary)
            {
                return i;
            }
        }

        return 0;
    }
}

/// <summary>What the overlay should do with the service's answer to a submitted code.</summary>
public enum LockPromptOutcome
{
    /// <summary>The code was correct: the lock is released, so the overlay closes.</summary>
    Dismiss,

    /// <summary>The code was wrong: stay locked and say so.</summary>
    ShowIncorrect,

    /// <summary>The service could not be reached: stay locked and say so. Never unlock on an unreachable service.</summary>
    ShowUnavailable,
}

/// <summary>
/// How the overlay reads the service's verification answer. Pure, so the
/// fail-safe -- an unreachable service never unlocks -- is tested without a pipe.
/// </summary>
public static class LockScreenPrompt
{
    /// <summary>
    /// <paramref name="unlocked"/> is true/false from the service, or null when
    /// the service could not be reached or did not answer. Only a clear true
    /// dismisses the overlay; everything else keeps it up.
    /// </summary>
    public static LockPromptOutcome Interpret(bool? unlocked) => unlocked switch
    {
        true => LockPromptOutcome.Dismiss,
        false => LockPromptOutcome.ShowIncorrect,
        null => LockPromptOutcome.ShowUnavailable,
    };
}

/// <summary>What the overlay's low-level keyboard hook should do with one key event.</summary>
public enum LockKeyAction
{
    /// <summary>Drop the key: it never reaches the desktop or any application.</summary>
    Swallow,

    /// <summary>Let the key reach the passphrase box (only while the prompt is open, and only for editing keys).</summary>
    PassToPrompt,

    /// <summary>The Ctrl+Alt+U chord: open the passphrase prompt.</summary>
    OpenPrompt,
}

/// <summary>
/// The pure decision behind the overlay's input hooks: with the lock up, every
/// key is swallowed except the Ctrl+Alt+U chord that opens the prompt; while the
/// prompt is open, the keys needed to type and submit a code reach the box and
/// the rest are still swallowed. Separated from the raw hook so every branch is
/// testable, and so the honest limit is explicit in one place.
/// </summary>
/// <remarks>
/// <b>Honest limit.</b> This is user-mode input filtering. It cannot intercept
/// Ctrl+Alt+Del (the Secure Attention Sequence, handled by Winlogon on a desktop
/// no user process can touch) and it cannot stop a hard power-off. The first is
/// covered by the lock's chrome -- the overlay simply reappears over the
/// Ctrl+Alt+Del menu's return -- and the second by relocking on the next boot.
/// </remarks>
public static class LockInputFilter
{
    /// <summary>Virtual-key code for the 'U' in the unlock chord.</summary>
    public const int VkU = 0x55;

    public static LockKeyAction DecideKey(bool prompting, bool ctrlDown, bool altDown, int virtualKey, bool keyDown)
    {
        if (!prompting)
        {
            // The one way in: Ctrl+Alt+U, on key-down. Everything else is dropped.
            if (keyDown && ctrlDown && altDown && virtualKey == VkU)
            {
                return LockKeyAction.OpenPrompt;
            }

            return LockKeyAction.Swallow;
        }

        // The prompt is open. Only the keys that type or edit a code, or submit or
        // cancel it, reach the box; shell chords (Win, Tab, function keys, ...) are
        // still swallowed so the prompt cannot be used to escape to the desktop.
        return IsPromptEditingKey(virtualKey) ? LockKeyAction.PassToPrompt : LockKeyAction.Swallow;
    }

    /// <summary>The mouse is always swallowed: the overlay covers every monitor and the prompt is keyboard-only.</summary>
    public static bool SwallowMouse() => true;

    /// <summary>Keys that may reach the passphrase box: letters, digits, common symbols, editing, Shift, Enter and Escape.</summary>
    public static bool IsPromptEditingKey(int virtualKey) =>
        virtualKey is (>= 0x41 and <= 0x5A)      // A-Z
            or (>= 0x30 and <= 0x39)             // 0-9
            or (>= 0x60 and <= 0x69)             // numpad 0-9
            or (>= 0x6A and <= 0x6F)             // numpad * + - . /
            or (>= 0xBA and <= 0xC0)             // OEM ; = , - . / `
            or (>= 0xDB and <= 0xDF)             // OEM [ \ ] '
            or VkBack or VkDelete or VkLeft or VkRight or VkHome or VkEnd
            or VkShift or VkLShift or VkRShift
            or VkEnter or VkEscape or VkSpace;

    private const int VkBack = 0x08;
    private const int VkEnter = 0x0D;
    private const int VkShift = 0x10;
    private const int VkEscape = 0x1B;
    private const int VkSpace = 0x20;
    private const int VkEnd = 0x23;
    private const int VkHome = 0x24;
    private const int VkLeft = 0x25;
    private const int VkRight = 0x27;
    private const int VkDelete = 0x2E;
    private const int VkLShift = 0xA0;
    private const int VkRShift = 0xA1;
}
