namespace EndpointPlatform.Domain.Chrome;

/// <summary>
/// What an administrator decided about one extension on one device.
/// </summary>
/// <remarks>
/// <para>
/// The members mirror the <c>installation_mode</c> values of Chrome's own
/// enterprise <c>ExtensionSettings</c> policy -- <c>force_installed</c>,
/// <c>blocked</c>, <c>allowed</c>, <c>removed</c> -- because that policy is the
/// enforcement mechanism: the agent writes the rules to HKLM and Chrome itself
/// carries them out. This platform adds nothing Chrome cannot enforce.
/// </para>
/// <para>
/// A rule travels to the agent as this member's NAME (the task payload carries
/// the string, never the number), and it is stored as text for the same reason
/// as every other enum here: reordering members can never reinterpret history
/// or a queued task. The member names are therefore a wire contract with
/// deployed agents -- renaming one is a protocol change, and a test pins them.
/// </para>
/// </remarks>
public enum ChromeExtensionRuleMode
{
    /// <summary>
    /// Chrome installs the extension and keeps it installed; the user cannot
    /// remove or disable it.
    /// </summary>
    ForceInstalled = 0,

    /// <summary>
    /// Chrome refuses to install the extension and disables it where it is
    /// already installed. The on-disk copy stays; lifting the rule re-enables it.
    /// </summary>
    Blocked = 1,

    /// <summary>
    /// The user may install, keep and use the extension. The explicit form of
    /// Chrome's default -- declared when an administrator wants the decision on
    /// record (or as the exception under a blanket block Chrome-side).
    /// </summary>
    Allowed = 2,

    /// <summary>
    /// Chrome uninstalls the extension and refuses to reinstall it. Stronger
    /// than <see cref="Blocked"/>: the copy is removed from the profile, not
    /// just disabled.
    /// </summary>
    Removed = 3,
}
