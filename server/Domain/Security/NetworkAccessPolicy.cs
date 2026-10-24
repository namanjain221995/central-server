using System.Net;
using System.Net.Sockets;
using EndpointPlatform.Domain.Common;

namespace EndpointPlatform.Domain.Security;

/// <summary>
/// The platform-wide network access policy for the ADMIN console: the set of
/// networks a request may come from before it is allowed to reach anything,
/// including the login form.
/// </summary>
/// <remarks>
/// <para>
/// <b>A singleton, not a per-organization setting, and that is structural.</b> The
/// check runs before authentication - the point is that a blocked network cannot
/// even attempt to sign in - so there is no identity yet and therefore no
/// organization to scope by. Exactly one row exists, with <see cref="SingletonId"/>
/// as its key.
/// </para>
/// <para>
/// Entries are stored as normalized CIDR strings: a bare address is widened to a
/// single-address network (<c>/32</c> for IPv4, <c>/128</c> for IPv6) so that
/// "allow my office IP" needs no CIDR knowledge, and every stored entry parses
/// back with <see cref="IPNetwork.TryParse(string, out IPNetwork)"/>.
/// </para>
/// <para>
/// The policy never applies to loopback (the operator on the box is the documented
/// break-glass) and never to the Agent API - devices call in from arbitrary home
/// networks. Both rules are enforced where the policy is checked, not here; this
/// type only holds and validates the configuration.
/// </para>
/// </remarks>
public sealed class NetworkAccessPolicy : Entity
{
    /// <summary>The one row's fixed key. There is no second policy to address.</summary>
    public static readonly Guid SingletonId = Guid.Parse("b5b1f1aa-8c6a-41a9-9f70-3e26b2d9c001");

    /// <summary>
    /// The most entries one policy may hold. Enough for every office, VPN pool and
    /// operator address an estate realistically has; low enough that the compiled
    /// list is checked per request without a thought.
    /// </summary>
    public const int MaxEntries = 64;

    /// <summary>Sanity ceiling per entry before parsing; the longest legal CIDR is far shorter.</summary>
    public const int MaxEntryLength = 128;

    private NetworkAccessPolicy()
        : base(SingletonId)
    {
        AllowedCidrs = [];
    }

    /// <summary>Whether the restriction is in force. Off means every network may reach the console.</summary>
    public bool Enabled { get; private set; }

    /// <summary>Normalized CIDR strings, deduplicated, at most <see cref="MaxEntries"/>.</summary>
    public IReadOnlyList<string> AllowedCidrs { get; private set; }

    public DateTimeOffset UpdatedAtUtc { get; private set; }

    public Guid? UpdatedByUserId { get; private set; }

    public string? UpdatedByEmail { get; private set; }

    /// <summary>Creates the singleton row. Validation is <see cref="Update"/>'s, exactly.</summary>
    public static NetworkAccessPolicy Create(
        bool enabled,
        IReadOnlyList<string> allowedCidrs,
        DateTimeOffset now,
        Guid? updatedByUserId,
        string? updatedByEmail)
    {
        var policy = new NetworkAccessPolicy();
        policy.Update(enabled, allowedCidrs, now, updatedByUserId, updatedByEmail);
        return policy;
    }

    /// <summary>
    /// Replaces the whole policy. Entries are normalized (bare IP to <c>/32</c> or
    /// <c>/128</c>), deduplicated case-insensitively and capped at
    /// <see cref="MaxEntries"/>; enabling with an empty list is refused because it
    /// would block every network including the caller's own.
    /// </summary>
    /// <exception cref="ArgumentException">An entry does not validate.</exception>
    public void Update(
        bool enabled,
        IReadOnlyList<string> allowedCidrs,
        DateTimeOffset now,
        Guid? updatedByUserId,
        string? updatedByEmail)
    {
        ArgumentNullException.ThrowIfNull(allowedCidrs);

        if (ValidateAndNormalize(enabled, allowedCidrs, out var normalized) is { } problem)
        {
            throw new ArgumentException(problem, nameof(allowedCidrs));
        }

        Enabled = enabled;
        AllowedCidrs = normalized;
        UpdatedAtUtc = now;
        UpdatedByUserId = updatedByUserId;
        UpdatedByEmail = Guard.OptionalMaxLength(updatedByEmail, 320);
    }

    /// <summary>
    /// The validation <see cref="Update"/> enforces, exposed so an endpoint can turn
    /// a bad request into a clean 400 - and get the normalized list the self-lockout
    /// check needs - without driving the entity by exception.
    /// </summary>
    /// <returns>A problem description, or null when every entry is valid.</returns>
    public static string? ValidateAndNormalize(
        bool enabled,
        IReadOnlyList<string?>? entries,
        out IReadOnlyList<string> normalized)
    {
        var result = new List<string>(entries?.Count ?? 0);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        normalized = [];

        foreach (var entry in entries ?? [])
        {
            if (string.IsNullOrWhiteSpace(entry))
            {
                return "Allowed ranges must not contain blank entries.";
            }

            if (!TryNormalizeCidr(entry, out var canonical))
            {
                return $"'{entry.Trim()}' is not a valid IP address or CIDR range. Use a single address "
                       + "(203.0.113.7) or a network in CIDR form whose base address is the start of the "
                       + "network (203.0.113.0/24).";
            }

            if (seen.Add(canonical))
            {
                result.Add(canonical);
            }
        }

        if (result.Count > MaxEntries)
        {
            return $"At most {MaxEntries} allowed ranges may be configured; {result.Count} were supplied.";
        }

        if (enabled && result.Count == 0)
        {
            return "Enabling the network access policy requires at least one allowed range; "
                   + "an empty list would block every network, including yours.";
        }

        normalized = result;
        return null;
    }

    /// <summary>
    /// Normalizes one entry to canonical CIDR text: a bare IPv4 address becomes
    /// <c>/32</c>, a bare IPv6 address <c>/128</c>, an IPv4-mapped IPv6 address is
    /// unmapped first, and an entry already in CIDR form must parse exactly
    /// (host bits set after the prefix are refused, not silently masked - a policy
    /// must mean what the administrator typed).
    /// </summary>
    public static bool TryNormalizeCidr(string? entry, out string normalized)
    {
        normalized = string.Empty;

        if (string.IsNullOrWhiteSpace(entry))
        {
            return false;
        }

        var trimmed = entry.Trim();

        if (trimmed.Length > MaxEntryLength)
        {
            return false;
        }

        var slashIndex = trimmed.IndexOf('/', StringComparison.Ordinal);

        if (slashIndex >= 0)
        {
            if (!IPNetwork.TryParse(trimmed, out var network))
            {
                return false;
            }

            // IPNetwork.TryParse quietly masks host bits set after the prefix
            // (203.0.113.7/24 parses as 203.0.113.0/24). Refused here instead:
            // such an entry is ambiguous - the /32 on that host, or the whole
            // /24? - and a stored policy must mean exactly what was typed.
            if (!IPAddress.TryParse(trimmed.AsSpan(0, slashIndex), out var typedBase)
                || !typedBase.Equals(network.BaseAddress))
            {
                return false;
            }

            normalized = network.ToString();
            return true;
        }

        if (!IPAddress.TryParse(trimmed, out var address))
        {
            return false;
        }

        address = NormalizeAddress(address);
        var prefixLength = address.AddressFamily == AddressFamily.InterNetwork ? 32 : 128;
        normalized = new IPNetwork(address, prefixLength).ToString();
        return true;
    }

    /// <summary>
    /// The one definition of "the address the policy compares": an IPv4-mapped IPv6
    /// address (<c>::ffff:a.b.c.d</c>, which dual-stack sockets report for plain
    /// IPv4 callers) is unmapped to its IPv4 form, because
    /// <see cref="IPNetwork.Contains(IPAddress)"/> is strict about address family
    /// and would otherwise never match the caller against an IPv4 range.
    /// </summary>
    public static IPAddress NormalizeAddress(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        return address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
    }
}
