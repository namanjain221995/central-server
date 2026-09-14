using System.Text;
using System.Text.Json;

namespace EndpointAgent.Core.SessionNotice;

/// <summary>
/// A restart Windows has accepted: when it will happen and the grace period it
/// was scheduled with. The only thing the service ever tells the signed-in user's
/// session.
/// </summary>
/// <remarks>
/// Two numbers and nothing else. There is deliberately no message, title or
/// administrator text here: the words the user sees are fixed in
/// <see cref="RestartNoticeView"/>, so nothing that reaches the endpoint -- not a
/// task payload, not a compromised server, not another process on the machine --
/// can put its own words in a notice that claims to come from IT.
/// </remarks>
public sealed record RestartNotice(DateTimeOffset RestartAt, int GraceSeconds);

/// <summary>
/// The wire format between the service and the session notifier: one line of
/// JSON per notice, parsed strictly.
/// </summary>
/// <remarks>
/// <para>
/// Strict because the reader runs in an ordinary user's session and must treat
/// every byte as hostile until the pipe's server has been verified -- and even
/// then. A line is refused if it is too long, is not exactly the three expected
/// properties, names an unknown version, or carries a value outside what a real
/// restart can have. Refusing costs one notice; accepting something unexpected
/// is how a parser becomes an attack surface.
/// </para>
/// <para>
/// The format carries data only. There is no command, path or text in it, so
/// there is nothing a reader could be persuaded to execute or display.
/// </para>
/// </remarks>
public static class RestartNoticeProtocol
{
    /// <summary>The only version this reader understands.</summary>
    public const int Version = 1;

    /// <summary>Far above any real notice (about 70 bytes); a longer line is refused unread.</summary>
    public const int MaxLineBytes = 256;

    /// <summary>The longest grace period the agent will ever schedule (its own clamp).</summary>
    public const int MaxGraceSeconds = 3600;

    /// <summary>The line the service writes, newline included.</summary>
    public static byte[] Encode(RestartNotice notice)
    {
        ArgumentNullException.ThrowIfNull(notice);
        var json = JsonSerializer.Serialize(new
        {
            v = Version,
            restartAt = notice.RestartAt.ToUniversalTime().ToString("O"),
            graceSeconds = notice.GraceSeconds,
        });

        return Encoding.UTF8.GetBytes(json + "\n");
    }

    /// <summary>
    /// Parses one line, without its newline. Returns false for anything that is
    /// not exactly a well-formed notice.
    /// </summary>
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

            // Exactly these three. An extra property is not ignored: a sender that
            // adds fields is not the sender this reader was written for.
            var names = root.EnumerateObject().Select(p => p.Name).ToList();
            if (names.Count != 3 || !names.Contains("v") || !names.Contains("restartAt") || !names.Contains("graceSeconds"))
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

            notice = new RestartNotice(restartAt.ToUniversalTime(), grace);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}

/// <summary>What the notice window shows at a given moment.</summary>
/// <param name="Visible">Whether there is anything to show at all.</param>
/// <param name="Countdown">Time remaining as HH:MM:SS, or null once it has run out.</param>
/// <param name="Restarting">True once the scheduled time has passed.</param>
public sealed record RestartNoticeView(bool Visible, string? Countdown, bool Restarting)
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

    /// <summary>
    /// After the scheduled time, how long the notice stays before it assumes the
    /// restart did not happen -- aborted, or the clock was wrong -- and goes away
    /// rather than claiming "restarting now" indefinitely on a machine that is
    /// plainly still running.
    /// </summary>
    public static readonly TimeSpan LingerAfterRestart = TimeSpan.FromMinutes(2);

    private static readonly RestartNoticeView Hidden = new(false, null, false);

    /// <summary>The view for <paramref name="notice"/> at <paramref name="now"/>.</summary>
    public static RestartNoticeView For(RestartNotice? notice, DateTimeOffset now)
    {
        if (notice is null)
        {
            return Hidden;
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

    /// <summary>
    /// HH:MM:SS, rounded up: a notice that has 0.4 seconds left must not already
    /// say 00:00:00 while the machine is still waiting.
    /// </summary>
    public static string Format(TimeSpan remaining)
    {
        var seconds = (long)Math.Ceiling(Math.Max(0, remaining.TotalSeconds));
        return $"{seconds / 3600:D2}:{seconds % 3600 / 60:D2}:{seconds % 60:D2}";
    }
}
