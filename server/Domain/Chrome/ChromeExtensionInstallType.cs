namespace EndpointPlatform.Domain.Chrome;

/// <summary>
/// Where Chrome says an extension came from.
/// </summary>
/// <remarks>
/// <para>
/// The names are the wire contract's <c>InstallType</c> strings exactly, so the
/// Agent API maps them with a case-sensitive <c>Enum.TryParse</c> and a value the
/// contract gains but this enum has not learned about lands on
/// <see cref="Unknown"/> rather than on a wrong member. A test pins the two sets
/// against each other so they cannot drift.
/// </para>
/// <para>
/// The numbers are this platform's, not Chrome's. Chrome's own numbering is an
/// implementation detail it has renumbered before; the agent reports the name and
/// the server never sees the number. Stored as text anyway, so even these numbers
/// carry no meaning on disk.
/// </para>
/// </remarks>
public enum ChromeExtensionInstallType
{
    /// <summary>The agent reported a value this platform does not model.</summary>
    Unknown = 0,

    /// <summary>User-installed, typically from the Web Store.</summary>
    Internal = 1,

    ExternalPref = 2,

    ExternalRegistry = 3,

    /// <summary>Loaded from a directory in developer mode.</summary>
    Unpacked = 4,

    /// <summary>Part of Chrome itself. Not something anyone installed.</summary>
    Component = 5,

    ExternalPrefDownload = 6,

    /// <summary>Installed by enterprise policy.</summary>
    ExternalPolicyDownload = 7,

    CommandLine = 8,

    /// <summary>Installed by enterprise policy.</summary>
    ExternalPolicy = 9,

    /// <summary>A component Chrome fetched rather than shipped. Still Chrome's own.</summary>
    ExternalComponent = 10,
}
