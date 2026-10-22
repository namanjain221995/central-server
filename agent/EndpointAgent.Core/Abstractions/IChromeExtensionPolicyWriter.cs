namespace EndpointAgent.Core.Abstractions;

/// <summary>
/// Carries the Chrome <c>ExtensionSettings</c> policy document onto this machine,
/// or removes it.
/// </summary>
/// <remarks>
/// <para>
/// The document is whole state: <see cref="Apply"/> replaces whatever managed
/// extension policy was in force before, and <see cref="Clear"/> removes exactly
/// the one value the platform owns -- never the policy key around it, where an
/// administrator's other Chrome policies may live. Chrome does the enforcing; the
/// agent only writes the data Chrome reads, so there is no mutating counterpart
/// per extension and no way for this interface to grow into running anything
/// (ADR-0005).
/// </para>
/// <para>
/// Both calls are idempotent and return false instead of throwing for an expected
/// Windows failure (access denied, a registry error), so the executor can fail
/// the task honestly: a policy that could not be written is reported as not
/// written, never as applied.
/// </para>
/// </remarks>
public interface IChromeExtensionPolicyWriter
{
    /// <summary>
    /// Writes the <c>ExtensionSettings</c> document, replacing any previous one.
    /// </summary>
    /// <param name="settingsJson">The document <c>ChromeExtensionPolicyDoc.TryBuild</c> produced.</param>
    /// <returns>True when the policy is in place; false when it could not be written.</returns>
    bool Apply(string settingsJson);

    /// <summary>
    /// Removes the managed extension policy, leaving every other Chrome policy
    /// untouched. Clearing a machine that has none is success.
    /// </summary>
    /// <returns>True when no managed policy remains; false when it could not be removed.</returns>
    bool Clear();
}
