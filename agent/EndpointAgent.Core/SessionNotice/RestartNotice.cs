using System.Text;
using System.Text.Json;

namespace EndpointAgent.Core.SessionNotice;

/// <summary>
/// A restart Windows has accepted: when it will happen and the grace period it
/// was scheduled with - or, with <see cref="Cancelled"/> set, the fact that the
/// restart the user was told about has been called off. The only things the
/// service ever tells the signed-in user's session.
/// </summary>
/// <remarks>
/// <para>
/// Two numbers and a flag, nothing else. There is deliberately no message, title
/// or administrator text here: the words the user sees are fixed in
/// <see cref="RestartNoticeView"/>, so nothing that reaches the endpoint -- not a
/// task payload, not a compromised server, not another process on the machine --
/// can put its own words in a notice that claims to come from IT.
/// </para>
/// <para>
/// For a cancellation, <see cref="RestartAt"/> is the moment the restart was
/// aborted and <see cref="GraceSeconds"/> is zero. The window shows the
/// cancellation briefly from that moment and then goes away; a notifier that
/// connects long after it would not show it at all.
/// </para>
/// </remarks>
public sealed record RestartNotice(DateTimeOffset RestartAt, int GraceSeconds, bool Cancelled = false)
{
    /// <summary>The notice that says a pending restart was aborted at <paramref name="at"/>.</summary>
    public static RestartNotice CancelledAt(DateTimeOffset at) => new(at, 0, Cancelled: true);
}

/// <summary>
/// The wire format between the service and the session notifier: one line of
/// JSON per notice, parsed strictly.
/// </summary>
/// <remarks>
/// <para>
/// Strict because the reader runs in an ordinary user's session and must treat
/// every byte as hostile until the pipe's server has been verified -- and even
/// then. A line is refused if it is too long, is not exactly the four expected
/// properties, names an unknown version, or carries a value outside what a real
/// restart can have. Refusing costs one notice; accepting something unexpected
/// could cost a user's trust in a window that claims to speak for IT.
/// </para>
/// <para>
/// Version 2 added <c>cancelled</c>. The service and the notifier ship in the
/// same installer, so there is no mixed-version case to tolerate: a version 1
/// line is refused like any other unexpected one.
/// </para>
/// </remarks>
public static class RestartNoticeProtocol
{
    public const int Version = 2;

    public const int MaxLineBytes = 256;

    public const int MaxGraceSeconds = 3600;

    public static byte[] Encode(RestartNotice notice)
    {
        ArgumentNullException.ThrowIfNull(notice);
        var json = JsonSerializer.Serialize(new
        {
            v = Version,
            restartAt = notice.RestartAt.ToUniversalTime().ToString("O"),
            graceSeconds = notice.GraceSeconds,
            cancelled = notice.Cancelled,
        });

        return Encoding.UTF8.GetBytes(json + "\n");
    }

    public static bool TryDecode(ReadOnlySpan<byte> line, out RestartNotice? notice)
    {
        notice = null;

        if (line.IsEmpty || line.Length > MaxLineBytes)
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(line.ToArray(), new JsonDocumentOptions { MaxDepth = 2 });
            var root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            // Exactly these four. An extra property is not ignored: a sender that
            // adds fields is not the sender this reader was written for.
            var names = root.EnumerateObject().Select(p => p.Name).ToList();
            if (names.Count != 4
                || !names.Contains("v") || !names.Contains("restartAt")
                || !names.Contains("graceSeconds") || !names.Contains("cancelled"))
            {
                return false;
            }

            if (root.GetProperty("v").ValueKind != JsonValueKind.Number
                || !root.GetProperty("v").TryGetInt32(out var version)
                || version != Version)
            {
                return false;
            }

            var graceElement = root.GetProperty("graceSeconds");
            if (graceElement.ValueKind != JsonValueKind.Number
                || !graceElement.TryGetInt32(out var grace)
                || grace is < 0 or > MaxGraceSeconds)
            {
                return false;
            }

            var cancelledElement = root.GetProperty("cancelled");
            if (cancelledElement.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            {
                return false;
            }

            var restartElement = root.GetProperty("restartAt");
            if (restartElement.ValueKind != JsonValueKind.String
                || !DateTimeOffset.TryParse(
                    restartElement.GetString(),
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.RoundtripKind,
                    out var restartAt))
            {
                return false;
            }

            notice = new RestartNotice(restartAt.ToUniversalTime(), grace, cancelledElement.GetBoolean());
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}

/// <summary>
/// What the window shows for a notice at a given moment: nothing, a countdown,
/// "restarting now", or "cancelled". Pure, so every state is testable without a
/// window.
/// </summary>
/// <param name="Visible">Whether there is anything to show.</param>
/// <param name="Countdown">Time remaining as HH:MM:SS, or null once it has run out.</param>
/// <param name="Restarting">True once the scheduled time has passed.</param>
/// <param name="Cancelled">True while a cancellation is being shown.</param>
public sealed record RestartNoticeView(bool Visible, string? Countdown, bool Restarting, bool Cancelled = false)
{
    /// <summary>Fixed. Never taken from the wire, the task, or the server.</summary>
    public const string Title = "Restart Scheduled";

    /// <summary>Fixed. Says who scheduled it, as the requirement asks, and nothing else.</summary>
    public const string Headline = "Your IT administrator has scheduled this device to restart.";

    /// <summary>Fixed.</summary>
    public const string CountdownLabel = "Restarting in";

    /// <summary>Fixed.</summary>
    public const string RestartingNow = "Restarting now...";

    /// <summary>Fixed.</summary>
    public const string Footer = "Please save your work.";

    /// <summary>Fixed. The title while a cancellation is shown.</summary>
    public const string CancelledTitle = "Restart Cancelled";

    /// <summary>Fixed. Says who cancelled it, and nothing else.</summary>
    public const string CancelledHeadline = "Your IT administrator has cancelled the scheduled restart.";

    /// <summary>Fixed.</summary>
    public const string CancelledBody = "No restart is pending.";

    /// <summary>Fixed.</summary>
    public const string CancelledFooter = "You can continue working.";

    public static readonly TimeSpan LingerAfterRestart = TimeSpan.FromMinutes(2);

    /// <summary>
    /// How long a cancellation stays on screen. Long enough to be read by someone
    /// who looked up at the countdown a moment ago; short enough not to become a
    /// second interruption.
    /// </summary>
    public static readonly TimeSpan LingerAfterCancel = TimeSpan.FromMinutes(1);

    private static readonly RestartNoticeView Hidden = new(false, null, false);

    public static RestartNoticeView For(RestartNotice? notice, DateTimeOffset now)
    {
        if (notice is null)
        {
            return Hidden;
        }

        if (notice.Cancelled)
        {
            var since = now - notice.RestartAt;
            return since >= TimeSpan.Zero && since < LingerAfterCancel
                ? new RestartNoticeView(true, null, Restarting: false, Cancelled: true)
                : Hidden;
        }

        var remaining = notice.RestartAt - now;

        if (remaining > TimeSpan.Zero)
        {
            return new RestartNoticeView(true, Format(remaining), Restarting: false);
        }

        return -remaining < LingerAfterRestart
            ? new RestartNoticeView(true, null, Restarting: true)
            : Hidden;
    }

    public static string Format(TimeSpan remaining)
    {
        var seconds = (long)Math.Ceiling(Math.Max(0, remaining.TotalSeconds));
        return $"{seconds / 3600:D2}:{seconds % 3600 / 60:D2}:{seconds % 60:D2}";
    }
}
