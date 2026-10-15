using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace EndpointAgent.Core.SessionNotice;

/// <summary>
/// A restart Windows has accepted: when it will happen and the grace period it
/// was scheduled with - or, with <see cref="Cancelled"/> set, the fact that the
/// restart the user was told about has been called off.
/// </summary>
/// <remarks>
/// <para>
/// Two numbers and a flag, nothing else. There is deliberately no message, title
/// or administrator text here: the words the user sees are fixed in
/// <see cref="RestartNoticeView"/>, so nothing that reaches the endpoint -- not a
/// task payload, not a compromised server, not another process on the machine --
/// can put its own words in a notice that claims to come from IT. Administrator
/// text travels only as an <see cref="AnnouncementNotice"/>, which is the
/// deliberate, bounded exception recorded in ADR-0005.
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
/// JSON per notice, parsed strictly, one <c>kind</c> per line.
/// </summary>
/// <remarks>
/// <para>
/// Strict because the reader runs in an ordinary user's session and must treat
/// every byte as hostile until the pipe's server has been verified -- and even
/// then. A line is refused if it is too long, is not exactly the expected
/// properties for its kind, names an unknown version or kind, or carries a value
/// outside what a real notice can have. Refusing costs one notice; accepting
/// something unexpected could cost a user's trust in a window that claims to
/// speak for IT.
/// </para>
/// <para>
/// Version 3 added <c>kind</c> and the <c>announcement</c> line. A restart line
/// is still numbers and a flag; a cancellation stays kind <c>restart</c> with
/// <c>cancelled</c> set, so the shape count stays at two. An announcement is the
/// one line that carries text -- an administrator's title and message, bounded
/// here and validated again before anything is shown (ADR-0005, announcements
/// amendment). The service and the notifier ship in the same installer, so there
/// is no mixed-version case to tolerate: a version 1 or 2 line is refused like
/// any other unexpected one.
/// </para>
/// <para>
/// Text is encoded without the serializer's non-ASCII escaping, so the character
/// bounds and the byte bound can hold together: 80 + 500 characters of any
/// plain text fit a <see cref="MaxLineBytes"/> line as raw UTF-8, where the
/// default <c>\uXXXX</c> escaping of every non-ASCII character would not.
/// Control characters other than newline and tab are refused instead of
/// escaped -- they have no place in text meant to be read.
/// </para>
/// </remarks>
public static class SessionNoticeProtocol
{
    public const int Version = 3;

    /// <summary>
    /// Sized for the largest announcement: 80 + 500 characters at up to three
    /// UTF-8 bytes each, plus the JSON around them, stays under this with room.
    /// </summary>
    public const int MaxLineBytes = 2048;

    public const int MaxGraceSeconds = 3600;

    public const int MaxTitleChars = 80;

    public const int MaxMessageChars = 500;

    private const string RestartKind = "restart";
    private const string AnnouncementKind = "announcement";

    /// <summary>
    /// Raw UTF-8 for non-ASCII; see the type remarks. Quotes, backslashes and the
    /// permitted control characters still escape, to two bytes each.
    /// </summary>
    private static readonly JsonSerializerOptions TextOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>
    /// Whether a string may travel as an announcement title. One shared rule for
    /// the executor that accepts the payload and the reader that decodes the
    /// line, so nothing the service agrees to send can be refused on arrival.
    /// </summary>
    public static bool IsValidTitle(string? title) => IsValidText(title, MaxTitleChars);

    /// <summary>Whether a string may travel as an announcement message. See <see cref="IsValidTitle"/>.</summary>
    public static bool IsValidMessage(string? message) => IsValidText(message, MaxMessageChars);

    private static bool IsValidText(string? text, int maxChars) =>
        !string.IsNullOrEmpty(text)
        && text.Length <= maxChars
        && !text.Any(static c => char.IsControl(c) && c is not ('\r' or '\n' or '\t'));

    public static byte[] Encode(RestartNotice notice)
    {
        ArgumentNullException.ThrowIfNull(notice);
        var json = JsonSerializer.Serialize(new
        {
            v = Version,
            kind = RestartKind,
            restartAt = notice.RestartAt.ToUniversalTime().ToString("O"),
            graceSeconds = notice.GraceSeconds,
            cancelled = notice.Cancelled,
        });

        return Encoding.UTF8.GetBytes(json + "\n");
    }

    public static byte[] Encode(AnnouncementNotice notice)
    {
        ArgumentNullException.ThrowIfNull(notice);

        // The service must never emit a line its own reader would refuse: the
        // executor validates the payload against the same rules, so reaching
        // here with invalid text is a programming error, not a data condition.
        if (!IsValidTitle(notice.Title))
        {
            throw new ArgumentException($"An announcement title must be 1..{MaxTitleChars} printable characters.", nameof(notice));
        }

        if (!IsValidMessage(notice.Message))
        {
            throw new ArgumentException($"An announcement message must be 1..{MaxMessageChars} printable characters.", nameof(notice));
        }

        var json = JsonSerializer.Serialize(new
        {
            v = Version,
            kind = AnnouncementKind,
            // The server's announcement id, so a delivery of the same message --
            // a replay, a re-queued task -- is recognisable as the same message.
            id = notice.Id.ToString("D"),
            at = notice.At.ToUniversalTime().ToString("O"),
            title = notice.Title,
            message = notice.Message,
        }, TextOptions);

        var bytes = Encoding.UTF8.GetBytes(json + "\n");
        if (bytes.Length > MaxLineBytes)
        {
            // Unreachable while the bounds above hold (see MaxLineBytes); kept so
            // a future loosening fails here, loudly, rather than at every reader.
            throw new InvalidOperationException("An encoded announcement exceeded the line limit.");
        }

        return bytes;
    }

    /// <summary>
    /// Decodes one line into exactly one kind of notice, or refuses it. At most
    /// one of <paramref name="restart"/> and <paramref name="announcement"/> is
    /// set, and only when the method returns true.
    /// </summary>
    public static bool TryDecode(ReadOnlySpan<byte> line, out RestartNotice? restart, out AnnouncementNotice? announcement)
    {
        restart = null;
        announcement = null;

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

            if (root.TryGetProperty("v", out var versionElement) is false
                || versionElement.ValueKind != JsonValueKind.Number
                || !versionElement.TryGetInt32(out var version)
                || version != Version)
            {
                return false;
            }

            if (!root.TryGetProperty("kind", out var kindElement) || kindElement.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            return kindElement.GetString() switch
            {
                RestartKind => TryDecodeRestart(root, out restart),
                AnnouncementKind => TryDecodeAnnouncement(root, out announcement),
                _ => false,
            };
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryDecodeRestart(JsonElement root, out RestartNotice? notice)
    {
        notice = null;

        // Exactly these five. An extra property is not ignored: a sender that
        // adds fields is not the sender this reader was written for.
        var names = root.EnumerateObject().Select(p => p.Name).ToList();
        if (names.Count != 5
            || !names.Contains("v") || !names.Contains("kind") || !names.Contains("restartAt")
            || !names.Contains("graceSeconds") || !names.Contains("cancelled"))
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

        if (!TryReadMoment(root, "restartAt", out var restartAt))
        {
            return false;
        }

        notice = new RestartNotice(restartAt, grace, cancelledElement.GetBoolean());
        return true;
    }

    private static bool TryDecodeAnnouncement(JsonElement root, out AnnouncementNotice? notice)
    {
        notice = null;

        var names = root.EnumerateObject().Select(p => p.Name).ToList();
        if (names.Count != 6
            || !names.Contains("v") || !names.Contains("kind") || !names.Contains("id")
            || !names.Contains("at") || !names.Contains("title") || !names.Contains("message"))
        {
            return false;
        }

        // Exactly the form the service writes ("D"); a sender spelling the id
        // another way is not the sender this reader was written for.
        var idElement = root.GetProperty("id");
        if (idElement.ValueKind != JsonValueKind.String
            || !Guid.TryParseExact(idElement.GetString(), "D", out var id))
        {
            return false;
        }

        var titleElement = root.GetProperty("title");
        var messageElement = root.GetProperty("message");
        if (titleElement.ValueKind != JsonValueKind.String || messageElement.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        var title = titleElement.GetString();
        var message = messageElement.GetString();
        if (!IsValidTitle(title) || !IsValidMessage(message))
        {
            return false;
        }

        if (!TryReadMoment(root, "at", out var at))
        {
            return false;
        }

        notice = new AnnouncementNotice(id, title!, message!, at);
        return true;
    }

    private static bool TryReadMoment(JsonElement root, string name, out DateTimeOffset moment)
    {
        moment = default;
        var element = root.GetProperty(name);
        if (element.ValueKind != JsonValueKind.String
            || !DateTimeOffset.TryParse(
                element.GetString(),
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind,
                out var parsed))
        {
            return false;
        }

        moment = parsed.ToUniversalTime();
        return true;
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
