namespace EndpointAgent.Core.Inventory.Chrome;

/// <summary>
/// One profile as Chrome's <c>Local State</c> file lists it.
/// </summary>
/// <remarks>
/// Deliberately a plain record with no Windows types: it is what the parser
/// produces from a stream and what the tests construct, so every rule about
/// profiles can be exercised with fixtures instead of a real machine.
/// </remarks>
/// <param name="ProfileKey">The profile directory name under <c>User Data</c> ("Default", "Profile 3").</param>
/// <param name="Name">The display name Chrome recorded, or null.</param>
/// <param name="IsManaged">Chrome's enterprise-managed flag, or null when unrecorded.</param>
/// <param name="LastActiveAt">When the profile was last used, or null when unrecorded.</param>
/// <param name="AccountEmail">The Google account signed in to the profile, as Chrome records it; null when none is.</param>
public sealed record ChromeProfileInfo(
    string ProfileKey,
    string? Name,
    bool? IsManaged,
    DateTimeOffset? LastActiveAt,
    string? AccountEmail = null);

/// <summary>What <c>Local State</c> says about the profiles of one Chrome installation.</summary>
/// <param name="Profiles">Every profile in the info cache, in file order.</param>
/// <param name="LastUsed">The profile Chrome last opened, when recorded.</param>
public sealed record ChromeLocalStateInfo(
    IReadOnlyList<ChromeProfileInfo> Profiles,
    string? LastUsed);

/// <summary>
/// One extension as a profile's <c>Preferences</c> or <c>Secure Preferences</c>
/// file records it, before normalization.
/// </summary>
/// <remarks>
/// <paramref name="Location"/> is Chrome's raw number, carried untouched so the
/// mapping to a wire name happens in exactly one place (the normalizer) and is
/// pinned by one set of tests.
/// </remarks>
/// <param name="ExtensionId">The 32-character id, already checked to be shaped like one.</param>
/// <param name="Location">Chrome's <c>ManifestLocation</c> value, or null when absent or not a number.</param>
/// <param name="Enabled">
/// True when the record shows no disable reason; false when it shows one; null
/// when the record says nothing either way.
/// </param>
public sealed record ChromeExtensionEntry(
    string ExtensionId,
    string? Name,
    string? Version,
    int? ManifestVersion,
    bool? Enabled,
    int? Location,
    bool? FromWebStore,
    string? UpdateUrl,
    DateTimeOffset? InstalledAt,
    DateTimeOffset? UpdatedAt);

/// <summary>
/// One profile directory the Windows collector found and read, with the
/// extension records from both preference files.
/// </summary>
/// <remarks>
/// Both files are carried separately rather than merged here because which one
/// wins for an extension recorded in both is a normalization rule, and those
/// live in one testable place. <paramref name="SecurePreferences"/> is the file
/// Chrome protects against tampering and is the one it trusts for install state,
/// so it takes precedence there.
/// </remarks>
/// <param name="UserSid">The Windows account whose profile directory this is.</param>
/// <param name="UserAccount">That account's resolved name, or the SID.</param>
/// <param name="Info">What <c>Local State</c> recorded about the profile.</param>
/// <param name="ProfilePath">The profile directory, absolute and local.</param>
/// <param name="SecurePreferences">Extension records from <c>Secure Preferences</c>; empty when unreadable.</param>
/// <param name="Preferences">Extension records from <c>Preferences</c>; empty when unreadable.</param>
public sealed record DiscoveredChromeProfile(
    string UserSid,
    string? UserAccount,
    ChromeProfileInfo Info,
    string ProfilePath,
    IReadOnlyList<ChromeExtensionEntry> SecurePreferences,
    IReadOnlyList<ChromeExtensionEntry> Preferences);

/// <summary>
/// The installed browser as the Windows collector found it, before clamping.
/// </summary>
/// <param name="Scope"><c>Machine</c> or <c>User</c>; the normalizer clamps and validates it.</param>
public sealed record DiscoveredChromeInstallation(
    string? Version,
    string? ExecutablePath,
    string? Architecture,
    string? Channel,
    string Scope,
    string? InstalledForUser,
    string? UpdaterVersion,
    DateTimeOffset? LastUpdateCheck);

/// <summary>
/// Chrome's two clocks, converted to <see cref="DateTimeOffset"/>.
/// </summary>
/// <remarks>
/// Chrome stores extension install times as a decimal string of microseconds
/// since 1601-01-01 (the Windows epoch), and profile activity as floating-point
/// seconds since 1970-01-01. Out-of-range or malformed values become null rather
/// than a default date, because a wrong date is worse than no date.
/// </remarks>
public static class ChromeTime
{
    private static readonly DateTimeOffset WindowsEpoch = new(1601, 1, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>Plausibility bounds: nothing before Chrome existed, nothing far in the future.</summary>
    private static readonly DateTimeOffset Earliest = new(2008, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Latest = new(2100, 1, 1, 0, 0, 0, TimeSpan.Zero);

    // The upper bound again in each clock's own unit, so an implausible count is
    // refused before it reaches the date arithmetic. That order matters: a
    // DateTimeOffset stops at the year 9999 and AddTicks/FromUnixTimeSeconds
    // throw past it rather than saturating, and a microsecond count above about
    // 9.2e17 wraps negative when scaled to ticks. Both lie far beyond Latest, so
    // a raw count over it is nonsense that must become null on the inventory
    // path, never an exception out of it.
    private static readonly long LatestWindowsMicroseconds = (Latest - WindowsEpoch).Ticks / 10;
    private static readonly long LatestUnixSeconds = Latest.ToUnixTimeSeconds();

    /// <summary>Microseconds since the Windows epoch, as Chrome writes install and update times.</summary>
    public static DateTimeOffset? FromWindowsMicroseconds(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || !long.TryParse(value.Trim(), out var microseconds) || microseconds <= 0)
        {
            return null;
        }

        if (microseconds > LatestWindowsMicroseconds)
        {
            return null;
        }

        // Ticks are 100 ns; one microsecond is ten of them. The bound above keeps
        // the product inside a long, and the plausibility check still decides the
        // exact edges.
        return Plausible(WindowsEpoch.AddTicks(microseconds * 10));
    }

    /// <summary>Seconds since the Unix epoch, as Chrome writes profile activity.</summary>
    public static DateTimeOffset? FromUnixSeconds(double? value)
    {
        if (value is null || double.IsNaN(value.Value) || double.IsInfinity(value.Value) || value.Value <= 0)
        {
            return null;
        }

        // Whole seconds are enough; sub-second activity times carry no meaning here.
        var seconds = Math.Floor(value.Value);
        if (seconds > LatestUnixSeconds)
        {
            return null;
        }

        return Plausible(DateTimeOffset.FromUnixTimeSeconds((long)seconds));
    }

    private static DateTimeOffset? Plausible(DateTimeOffset value) =>
        value >= Earliest && value < Latest ? value : null;
}
