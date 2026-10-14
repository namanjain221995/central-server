using System.Text;
using System.Text.Json;

namespace EndpointAgent.Core.CompanyPortal;

/// <summary>
/// What the Company Portal, running in a signed-in user's session, may ask the
/// LocalSystem service: the three operations, and nothing else.
/// </summary>
/// <remarks>
/// This is the one place where a user's process sends anything to the service
/// (ADR-0005, Company Portal amendment). A request is an operation name and, for
/// two of them, a catalog app id. There is no path, no package name, no
/// argument, no text that could be displayed or executed. The service answers
/// by asking the management server, and the server decides.
/// </remarks>
public enum PortalOperation
{
    /// <summary>The published apps this device may offer, each with its state here.</summary>
    List,

    /// <summary>One app's PNG tile.</summary>
    Icon,

    /// <summary>Ask the server to queue one app's install on this device.</summary>
    Install,
}

/// <summary>A decoded, validated request from the portal.</summary>
/// <param name="Operation">What is asked.</param>
/// <param name="AppId">The catalog app, for <see cref="PortalOperation.Icon"/> and <see cref="PortalOperation.Install"/>; null for List.</param>
public sealed record PortalRequest(PortalOperation Operation, Guid? AppId)
{
    public static readonly PortalRequest ListApps = new(PortalOperation.List, null);

    public static PortalRequest IconOf(Guid appId) => new(PortalOperation.Icon, appId);

    public static PortalRequest InstallOf(Guid appId) => new(PortalOperation.Install, appId);
}

/// <summary>One tile, as the portal draws it. Mirrors the server's entry minus nothing it needs.</summary>
public sealed record PortalApp(
    Guid Id,
    string DisplayName,
    string? Description,
    string? Category,
    bool HasIcon,
    string Version,
    string? Publisher,
    DateTimeOffset UpdatedAt,
    string State);

/// <summary>Why a request got no answer. One word each; the portal has fixed text for every one.</summary>
public enum PortalError
{
    /// <summary>The request was not one the service understands.</summary>
    BadRequest,

    /// <summary>The device is not enrolled yet, so there is no catalog to show.</summary>
    NotEnrolled,

    /// <summary>The management server could not be reached, or answered with an error.</summary>
    ServerUnavailable,

    /// <summary>The session asked too often; try again in a minute.</summary>
    TooManyRequests,
}

/// <summary>
/// What the service answers. Exactly one of the payload members is set,
/// matching the request; <see cref="Error"/> is set when none is.
/// </summary>
public sealed record PortalResponse(
    IReadOnlyList<PortalApp>? Apps = null,
    byte[]? Icon = null,
    string? Outcome = null,
    PortalError? Error = null)
{
    public bool Ok => Error is null;

    public static PortalResponse Failed(PortalError error) => new(Error: error);
}

/// <summary>
/// The wire format of the portal pipe: one line of JSON per request and per
/// response, parsed strictly on both sides.
/// </summary>
/// <remarks>
/// <para>
/// Strict in the direction that matters most: the service reads requests from an
/// ordinary user's process and must treat every byte as hostile. A request line
/// is refused if it is too long, is not exactly the expected properties, names
/// an unknown version or operation, or carries an id that is not a GUID. The
/// portal is strict too, for the same reason the restart notifier is -- the
/// server it connected to is verified by session, not by identity.
/// </para>
/// <para>
/// Responses can be larger than requests: a list of apps, or one PNG as base64.
/// Both are bounded, and the bound is what the reader enforces before parsing.
/// </para>
/// </remarks>
public static class CompanyPortalProtocol
{
    public const int Version = 1;

    /// <summary>A request is an operation and at most one GUID; this is generous.</summary>
    public const int MaxRequestBytes = 256;

    /// <summary>
    /// Room for a catalog of a few hundred entries with descriptions, or one
    /// 64 KB icon as base64. Anything longer is not a response this reader was
    /// written for.
    /// </summary>
    public const int MaxResponseBytes = 512 * 1024;

    /// <summary>The server's icon ceiling; a longer icon is refused here as well.</summary>
    public const int MaxIconBytes = 64 * 1024;

    /// <summary>The error words, exactly as written. A lookup by name, not Enum.TryParse, which would also take a number in a string.</summary>
    private static readonly Dictionary<string, PortalError> ErrorWords =
        Enum.GetValues<PortalError>().ToDictionary(e => e.ToString(), e => e, StringComparer.Ordinal);

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>The reading side of <see cref="Json"/>: the same names, and a property the reader has no member for refuses the line.</summary>
    private static readonly JsonSerializerOptions StrictJson = new(Json)
    {
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 4,
    };

    public static byte[] EncodeRequest(PortalRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var json = request.Operation == PortalOperation.List
            ? JsonSerializer.Serialize(new { v = Version, op = OperationName(request.Operation) })
            : JsonSerializer.Serialize(new { v = Version, op = OperationName(request.Operation), id = request.AppId });

        return Encoding.UTF8.GetBytes(json + "\n");
    }

    public static bool TryDecodeRequest(ReadOnlySpan<byte> line, out PortalRequest? request)
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

            var opElement = root.GetProperty("op");
            if (opElement.ValueKind != JsonValueKind.String || !TryParseOperation(opElement.GetString(), out var operation))
            {
                return false;
            }

            if (operation == PortalOperation.List)
            {
                // Exactly v and op. A list request that carries an id, or
                // anything else, is not a list request.
                request = names.Count == 2 ? PortalRequest.ListApps : null;
                return request is not null;
            }

            if (names.Count != 3 || !names.Contains("id"))
            {
                return false;
            }

            var idElement = root.GetProperty("id");
            if (idElement.ValueKind != JsonValueKind.String || !Guid.TryParseExact(idElement.GetString(), "D", out var id)
                || id == Guid.Empty)
            {
                return false;
            }

            request = new PortalRequest(operation, id);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public static byte[] EncodeResponse(PortalResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);

        var json = JsonSerializer.Serialize(new WireResponse(
            Version,
            response.Ok,
            response.Error?.ToString(),
            response.Apps?.Select(a => new WireApp(
                a.Id, a.DisplayName, a.Description, a.Category, a.HasIcon, a.Version, a.Publisher,
                a.UpdatedAt.ToUniversalTime().ToString("O"), a.State)).ToList(),
            response.Icon is null ? null : Convert.ToBase64String(response.Icon),
            response.Outcome), Json);

        return Encoding.UTF8.GetBytes(json + "\n");
    }

    public static bool TryDecodeResponse(ReadOnlySpan<byte> line, out PortalResponse? response)
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

        if (!wire.Ok)
        {
            if (wire.Error is null || !ErrorWords.TryGetValue(wire.Error, out var error))
            {
                return false;
            }

            response = PortalResponse.Failed(error);
            return true;
        }

        // Exactly one payload for a successful answer.
        var payloads = (wire.Apps is not null ? 1 : 0) + (wire.Icon is not null ? 1 : 0) + (wire.Outcome is not null ? 1 : 0);
        if (payloads != 1 || wire.Error is not null)
        {
            return false;
        }

        if (wire.Apps is not null)
        {
            var apps = new List<PortalApp>(wire.Apps.Count);
            foreach (var a in wire.Apps)
            {
                if (a.Id == Guid.Empty || string.IsNullOrWhiteSpace(a.DisplayName) || a.DisplayName.Length > 128
                    || a.Description is { Length: > 1000 } || a.Category is { Length: > 64 }
                    || string.IsNullOrWhiteSpace(a.Version) || a.Version.Length > 64
                    || a.Publisher is { Length: > 256 }
                    || string.IsNullOrWhiteSpace(a.State) || a.State.Length > 32
                    || !DateTimeOffset.TryParse(a.UpdatedAt, System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.RoundtripKind, out var updatedAt))
                {
                    return false;
                }

                apps.Add(new PortalApp(
                    a.Id, a.DisplayName, a.Description, a.Category, a.HasIcon, a.Version, a.Publisher,
                    updatedAt.ToUniversalTime(), a.State));
            }

            response = new PortalResponse(Apps: apps);
            return true;
        }

        if (wire.Icon is not null)
        {
            byte[] icon;
            try
            {
                icon = Convert.FromBase64String(wire.Icon);
            }
            catch (FormatException)
            {
                return false;
            }

            if (icon.Length == 0 || icon.Length > MaxIconBytes)
            {
                return false;
            }

            response = new PortalResponse(Icon: icon);
            return true;
        }

        if (wire.Outcome!.Length > 32)
        {
            return false;
        }

        response = new PortalResponse(Outcome: wire.Outcome);
        return true;
    }

    private static string OperationName(PortalOperation operation) => operation switch
    {
        PortalOperation.List => "list",
        PortalOperation.Icon => "icon",
        PortalOperation.Install => "install",
        _ => throw new ArgumentOutOfRangeException(nameof(operation)),
    };

    private static bool TryParseOperation(string? name, out PortalOperation operation)
    {
        switch (name)
        {
            case "list":
                operation = PortalOperation.List;
                return true;
            case "icon":
                operation = PortalOperation.Icon;
                return true;
            case "install":
                operation = PortalOperation.Install;
                return true;
            default:
                operation = default;
                return false;
        }
    }

    private sealed record WireResponse(
        int V, bool Ok, string? Error, IReadOnlyList<WireApp>? Apps, string? Icon, string? Outcome);

    private sealed record WireApp(
        Guid Id, string DisplayName, string? Description, string? Category, bool HasIcon,
        string Version, string? Publisher, string UpdatedAt, string State);
}
