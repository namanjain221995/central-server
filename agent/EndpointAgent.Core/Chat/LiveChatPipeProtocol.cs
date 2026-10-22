using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using EndpointPlatform.Contracts.Agent;

namespace EndpointAgent.Core.Chat;

/// <summary>What the chat window asks the service over the live-chat pipe.</summary>
public enum ChatPipeOp
{
    /// <summary>Read-only: the messages after a cursor, whether the chat is still open, and the admin's name. No side effect.</summary>
    Poll,

    /// <summary>The one write op: a line the person at the device typed. The service relays it to the server.</summary>
    Send,

    /// <summary>The window is closing (the person closed it). No payload; the service ends the chat on this device.</summary>
    Closed,
}

/// <summary>A decoded, validated request from the chat window.</summary>
/// <param name="Op">What is asked.</param>
/// <param name="AfterSeq">For <see cref="ChatPipeOp.Poll"/>, the cursor: return only messages with a higher seq. Zero for the others.</param>
/// <param name="Body">The typed message, for <see cref="ChatPipeOp.Send"/>; null for the others.</param>
public sealed record ChatPipeRequest(ChatPipeOp Op, int AfterSeq, string? Body)
{
    public static ChatPipeRequest Poll(int afterSeq) => new(ChatPipeOp.Poll, afterSeq < 0 ? 0 : afterSeq, null);

    public static ChatPipeRequest Send(string body) => new(ChatPipeOp.Send, 0, body);

    public static readonly ChatPipeRequest Closed = new(ChatPipeOp.Closed, 0, null);
}

/// <summary>Why a request got no useful answer. One word each; the window has fixed text for every one.</summary>
public enum ChatPipeError
{
    /// <summary>The request was not one the service understands.</summary>
    BadRequest,

    /// <summary>Too many requests in the last minute; slow down.</summary>
    TooManyRequests,
}

/// <summary>
/// What the service answers. For <see cref="ChatPipeOp.Poll"/>, <see cref="Active"/>
/// is set and the admin name and messages follow; for <see cref="ChatPipeOp.Send"/>,
/// <see cref="Accepted"/> is set; for <see cref="ChatPipeOp.Closed"/>,
/// <see cref="Acknowledged"/>. <see cref="Error"/> is set instead when the request is refused.
/// </summary>
public sealed record ChatPipeResponse(
    bool? Active = null,
    string? AdminDisplay = null,
    IReadOnlyList<ChatMessageDto>? Messages = null,
    bool? Accepted = null,
    bool? Acknowledged = null,
    ChatPipeError? Error = null)
{
    public static ChatPipeResponse Polled(bool active, string? adminDisplay, IReadOnlyList<ChatMessageDto> messages) =>
        new(Active: active, AdminDisplay: active ? adminDisplay : null, Messages: messages);

    public static ChatPipeResponse Sent(bool accepted) => new(Accepted: accepted);

    public static ChatPipeResponse Acked() => new(Acknowledged: true);

    public static ChatPipeResponse Failed(ChatPipeError error) => new(Error: error);
}

/// <summary>
/// The wire format of the live-chat pipe: one line of JSON per request and per
/// response, parsed strictly on both sides.
/// </summary>
/// <remarks>
/// <para>
/// Strict in the direction that matters most: the service reads requests from an
/// ordinary user's process and treats every byte as hostile. A request line is
/// refused if it is too long, is not exactly the properties its operation expects,
/// names an unknown version or operation, or carries a body that is not plain text
/// within bounds. The window is strict too, for the reason the overlay is -- the
/// server it connected to is trusted by session, not identity.
/// </para>
/// <para>
/// There is no path, package, command or id anywhere in this protocol. The only
/// thing a window sends the service is the plain text the person typed, which the
/// service relays to the server over the device-credentialled chat endpoint and
/// never acts on itself; and the only thing the service sends back is the plain
/// text the server already recorded. The admin's words are drawn as data, never
/// parsed.
/// </para>
/// </remarks>
public static class LiveChatPipeProtocol
{
    public const int Version = 1;

    /// <summary>A request is an op, a small cursor and at most one message body. Generous for a 2000-char UTF-8 line.</summary>
    public const int MaxRequestBytes = 12 * 1024;

    /// <summary>Room for a page of messages, each up to the body bound, as raw UTF-8.</summary>
    public const int MaxResponseBytes = 256 * 1024;

    /// <summary>The longest message the window may send, matching the server's own bound.</summary>
    public const int MaxBodyChars = 2000;

    /// <summary>The most messages one poll answer carries; the window advances its cursor and asks again for the rest.</summary>
    public const int MaxMessagesPerPoll = 50;

    private const string PollOp = "poll";
    private const string SendOp = "send";
    private const string ClosedOp = "closed";

    /// <summary>The three sender values the server uses, matched exactly. A message with any other sender is refused.</summary>
    private static readonly HashSet<string> Senders = new(StringComparer.Ordinal) { "Admin", "Employee", "System" };

    private static readonly Dictionary<string, ChatPipeError> ErrorWords =
        Enum.GetValues<ChatPipeError>().ToDictionary(e => e.ToString(), e => e, StringComparer.Ordinal);

    // Raw UTF-8 for non-ASCII, like the screen-lock protocol, so plain text of any
    // script fits the byte bound where \uXXXX escaping would not.
    private static readonly JsonSerializerOptions TextOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private static readonly JsonSerializerOptions StrictJson = new(TextOptions)
    {
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 6,
    };

    public static byte[] EncodeRequest(ChatPipeRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var json = request.Op switch
        {
            ChatPipeOp.Poll => JsonSerializer.Serialize(new { v = Version, op = PollOp, after = request.AfterSeq }),
            ChatPipeOp.Send => JsonSerializer.Serialize(new { v = Version, op = SendOp, body = request.Body }, TextOptions),
            _ => JsonSerializer.Serialize(new { v = Version, op = ClosedOp }),
        };

        return Encoding.UTF8.GetBytes(json + "\n");
    }

    public static bool TryDecodeRequest(ReadOnlySpan<byte> line, out ChatPipeRequest? request)
    {
        request = null;

        if (line.IsEmpty || line.Length > MaxRequestBytes)
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

            var names = root.EnumerateObject().Select(p => p.Name).ToList();
            if (!names.Contains("v") || !names.Contains("op"))
            {
                return false;
            }

            if (root.GetProperty("v").ValueKind != JsonValueKind.Number
                || !root.GetProperty("v").TryGetInt32(out var version)
                || version != Version)
            {
                return false;
            }

            var op = root.GetProperty("op");
            if (op.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            switch (op.GetString())
            {
                case PollOp:
                    if (names.Count != 3 || !names.Contains("after"))
                    {
                        return false;
                    }

                    var after = root.GetProperty("after");
                    if (after.ValueKind != JsonValueKind.Number || !after.TryGetInt32(out var afterSeq) || afterSeq < 0)
                    {
                        return false;
                    }

                    request = ChatPipeRequest.Poll(afterSeq);
                    return true;

                case SendOp:
                    if (names.Count != 3 || !names.Contains("body"))
                    {
                        return false;
                    }

                    var bodyElement = root.GetProperty("body");
                    if (bodyElement.ValueKind != JsonValueKind.String)
                    {
                        return false;
                    }

                    var body = bodyElement.GetString();
                    if (!ChatText.IsValidBody(body))
                    {
                        return false;
                    }

                    request = ChatPipeRequest.Send(body!);
                    return true;

                case ClosedOp:
                    // Exactly v and op; a closed that carries anything else is not a closed.
                    request = names.Count == 2 ? ChatPipeRequest.Closed : null;
                    return request is not null;

                default:
                    return false;
            }
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public static byte[] EncodeResponse(ChatPipeResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);

        var json = JsonSerializer.Serialize(
            new WireResponse(
                Version,
                response.Active,
                response.AdminDisplay,
                response.Messages?.Select(m => new WireMessage(
                    m.Seq, m.Sender, m.SenderDisplay, m.Body, m.SentAt.ToUniversalTime().ToString("O"))).ToList(),
                response.Accepted,
                response.Acknowledged,
                response.Error?.ToString()),
            TextOptions);

        return Encoding.UTF8.GetBytes(json + "\n");
    }

    public static bool TryDecodeResponse(ReadOnlySpan<byte> line, out ChatPipeResponse? response)
    {
        response = null;

        if (line.IsEmpty || line.Length > MaxResponseBytes)
        {
            return false;
        }

        WireResponse? wire;
        try
        {
            wire = JsonSerializer.Deserialize<WireResponse>(line, StrictJson);
        }
        catch (JsonException)
        {
            return false;
        }

        if (wire is null || wire.V != Version)
        {
            return false;
        }

        if (wire.Error is not null)
        {
            if (!ErrorWords.TryGetValue(wire.Error, out var error)
                || wire.Active is not null || wire.AdminDisplay is not null || wire.Messages is not null
                || wire.Accepted is not null || wire.Acknowledged is not null)
            {
                return false;
            }

            response = ChatPipeResponse.Failed(error);
            return true;
        }

        if (wire.Acknowledged is { } acknowledged)
        {
            if (!acknowledged || wire.Active is not null || wire.AdminDisplay is not null
                || wire.Messages is not null || wire.Accepted is not null)
            {
                return false;
            }

            response = ChatPipeResponse.Acked();
            return true;
        }

        if (wire.Accepted is { } accepted)
        {
            if (wire.Active is not null || wire.AdminDisplay is not null || wire.Messages is not null)
            {
                return false;
            }

            response = ChatPipeResponse.Sent(accepted);
            return true;
        }

        if (wire.Active is { } active)
        {
            if (wire.Messages is null || wire.Messages.Count > MaxMessagesPerPoll)
            {
                return false;
            }

            if (!active && (wire.AdminDisplay is not null || wire.Messages.Count != 0))
            {
                // A closed chat carries no name and no messages.
                return false;
            }

            if (active && !ChatText.IsValidAdminDisplay(wire.AdminDisplay))
            {
                return false;
            }

            var messages = new List<ChatMessageDto>(wire.Messages.Count);
            foreach (var m in wire.Messages)
            {
                if (m.Seq <= 0
                    || !Senders.Contains(m.Sender)
                    || !ChatText.IsValidSenderDisplay(m.SenderDisplay)
                    || !ChatText.IsValidBody(m.Body)
                    || !DateTimeOffset.TryParse(
                        m.SentAt, System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.RoundtripKind, out var sentAt))
                {
                    return false;
                }

                messages.Add(new ChatMessageDto(m.Seq, m.Sender, m.SenderDisplay, m.Body, sentAt.ToUniversalTime()));
            }

            response = ChatPipeResponse.Polled(active, wire.AdminDisplay, messages);
            return true;
        }

        return false;
    }

    private sealed record WireResponse(
        int V, bool? Active, string? AdminDisplay, IReadOnlyList<WireMessage>? Messages,
        bool? Accepted, bool? Acknowledged, string? Error);

    private sealed record WireMessage(int Seq, string Sender, string SenderDisplay, string Body, string SentAt);
}
