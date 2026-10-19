using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace EndpointAgent.Core.ScreenLock;

/// <summary>What the overlay asks the service over the passphrase pipe.</summary>
public enum LockPipeOp
{
    /// <summary>Read-only: what to show, and whether the device is still locked. No payload.</summary>
    Show,

    /// <summary>The one privileged op: submit a candidate recovery code. The service verifies and releases on a match.</summary>
    Submit,
}

/// <summary>A decoded, validated request from the overlay.</summary>
/// <param name="Op">What is asked.</param>
/// <param name="Code">The candidate recovery code, for <see cref="LockPipeOp.Submit"/>; null for Show.</param>
public sealed record LockPipeRequest(LockPipeOp Op, string? Code)
{
    public static readonly LockPipeRequest Show = new(LockPipeOp.Show, null);

    public static LockPipeRequest SubmitCode(string code) => new(LockPipeOp.Submit, code);
}

/// <summary>Why a request got no useful answer. One word each; the overlay has fixed text for every one.</summary>
public enum LockPipeError
{
    /// <summary>The request was not one the service understands.</summary>
    BadRequest,

    /// <summary>Too many codes submitted in the last minute; wait before trying again.</summary>
    TooManyRequests,
}

/// <summary>
/// What the service answers. For <see cref="LockPipeOp.Show"/>, <see cref="Locked"/>
/// is set and the words follow when locked; for <see cref="LockPipeOp.Submit"/>,
/// <see cref="Unlocked"/> is set. <see cref="Error"/> is set instead when the
/// request is refused.
/// </summary>
public sealed record LockPipeResponse(
    bool? Locked = null,
    string? Message = null,
    string? Contact = null,
    bool? Unlocked = null,
    LockPipeError? Error = null)
{
    public static LockPipeResponse Showing(bool locked, string? message, string? contact) =>
        new(Locked: locked, Message: locked ? message : null, Contact: locked ? contact : null);

    public static LockPipeResponse Verified(bool unlocked) => new(Unlocked: unlocked);

    public static LockPipeResponse Failed(LockPipeError error) => new(Error: error);
}

/// <summary>
/// The wire format of the screen-lock pipe: one line of JSON per request and per
/// response, parsed strictly on both sides.
/// </summary>
/// <remarks>
/// <para>
/// Strict in the direction that matters most: the service reads requests from an
/// ordinary user's process and treats every byte as hostile. A request line is
/// refused if it is too long, is not exactly the expected properties for its
/// operation, names an unknown version or operation, or carries a code that is
/// not plain text within bounds. The overlay is strict too, for the reason the
/// notifier is: the server it connected to is trusted by session, not identity.
/// </para>
/// <para>
/// There is no path, package, command or id anywhere in this protocol. A Show
/// answer carries only the administrator's plain-text words; a Submit answer is a
/// single bool. The verifier is never on the wire.
/// </para>
/// </remarks>
public static class ScreenLockPipeProtocol
{
    public const int Version = 1;

    /// <summary>A request is an operation and at most one code of up to 256 characters; this is generous.</summary>
    public const int MaxRequestBytes = 1024;

    /// <summary>A response is a bool or the bounded message and contact line as raw UTF-8.</summary>
    public const int MaxResponseBytes = 4096;

    /// <summary>The longest recovery code the overlay may submit.</summary>
    public const int MaxCodeChars = 256;

    private const string ShowOp = "show";
    private const string SubmitOp = "submit";

    private static readonly Dictionary<string, LockPipeError> ErrorWords =
        Enum.GetValues<LockPipeError>().ToDictionary(e => e.ToString(), e => e, StringComparer.Ordinal);

    // Raw UTF-8 for non-ASCII, like the session-notice protocol, so 500 + 200
    // characters of any plain text fit the byte bound where \uXXXX escaping would not.
    private static readonly JsonSerializerOptions TextOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    public static byte[] EncodeRequest(LockPipeRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var json = request.Op == LockPipeOp.Show
            ? JsonSerializer.Serialize(new { v = Version, op = ShowOp })
            : JsonSerializer.Serialize(new { v = Version, op = SubmitOp, code = request.Code }, TextOptions);

        return Encoding.UTF8.GetBytes(json + "\n");
    }

    public static bool TryDecodeRequest(ReadOnlySpan<byte> line, out LockPipeRequest? request)
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
                case ShowOp:
                    // Exactly v and op; a show that carries anything else is not a show.
                    request = names.Count == 2 ? LockPipeRequest.Show : null;
                    return request is not null;

                case SubmitOp:
                    if (names.Count != 3 || !names.Contains("code"))
                    {
                        return false;
                    }

                    var codeElement = root.GetProperty("code");
                    if (codeElement.ValueKind != JsonValueKind.String)
                    {
                        return false;
                    }

                    var code = codeElement.GetString();
                    if (!IsValidCode(code))
                    {
                        return false;
                    }

                    request = LockPipeRequest.SubmitCode(code!);
                    return true;

                default:
                    return false;
            }
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public static byte[] EncodeResponse(LockPipeResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);

        var json = JsonSerializer.Serialize(
            new WireResponse(Version, response.Locked, response.Message, response.Contact, response.Unlocked, response.Error?.ToString()),
            TextOptions);

        return Encoding.UTF8.GetBytes(json + "\n");
    }

    public static bool TryDecodeResponse(ReadOnlySpan<byte> line, out LockPipeResponse? response)
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
            if (!ErrorWords.TryGetValue(wire.Error, out var error) || wire.Locked is not null
                || wire.Unlocked is not null || wire.Message is not null || wire.Contact is not null)
            {
                return false;
            }

            response = LockPipeResponse.Failed(error);
            return true;
        }

        if (wire.Unlocked is { } unlocked)
        {
            // A verification answer is exactly a bool.
            if (wire.Locked is not null || wire.Message is not null || wire.Contact is not null)
            {
                return false;
            }

            response = LockPipeResponse.Verified(unlocked);
            return true;
        }

        if (wire.Locked is { } locked)
        {
            if (!locked)
            {
                // Not locked: no words.
                if (wire.Message is not null || wire.Contact is not null)
                {
                    return false;
                }

                response = LockPipeResponse.Showing(locked: false, null, null);
                return true;
            }

            if (!ScreenLockText.IsValidMessage(wire.Message) || !ScreenLockText.IsValidContact(wire.Contact))
            {
                return false;
            }

            response = LockPipeResponse.Showing(locked: true, wire.Message, wire.Contact);
            return true;
        }

        return false;
    }

    /// <summary>A recovery code is plain text of 1..256 characters with no control characters.</summary>
    public static bool IsValidCode(string? code) =>
        !string.IsNullOrEmpty(code)
        && code.Length <= MaxCodeChars
        && !code.Any(static c => char.IsControl(c));

    // The reading side of TextOptions: the same camelCase names, and any property
    // the reader has no member for refuses the line.
    private static readonly JsonSerializerOptions StrictJson = new(TextOptions)
    {
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 3,
    };

    private sealed record WireResponse(int V, bool? Locked, string? Message, string? Contact, bool? Unlocked, string? Error);
}
