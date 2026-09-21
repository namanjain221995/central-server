namespace EndpointPlatform.Domain.Identity;

/// <summary>
/// What the platform knows about the account a password is being set for.
/// </summary>
/// <remarks>
/// <para>
/// Exists so <see cref="WeakPassword"/> can refuse a password built out of the
/// account's own identity. That is the single most valuable screening rule on an
/// internet-facing console: the e-mail address IS the username, so an attacker
/// who can reach the login form already knows it, and a password derived from it
/// has almost no strength against the only attacker who matters.
/// </para>
/// <para>
/// A struct with no required members, so <see cref="None"/> is a legitimate
/// value. Some callers genuinely have no account context - the generated-password
/// self-check has no user at all - and forcing them to invent one would be worse
/// than letting the contextual rules simply not fire.
/// </para>
/// </remarks>
public readonly record struct PasswordContext
{
    /// <summary>The account's e-mail address, if one is known.</summary>
    public string? Email { get; init; }

    /// <summary>The account's display name, if one is known.</summary>
    public string? DisplayName { get; init; }

    /// <summary>
    /// Extra terms this deployment will not accept inside a password.
    /// </summary>
    /// <remarks>
    /// The organisation or product name belongs here rather than in
    /// <see cref="WeakPassword"/>'s built-in stem list, because it differs by
    /// deployment: a term that matters to one tenant is noise to another. This is
    /// how <c>Techsara@2026</c> gets refused without shipping "techsara" to
    /// everybody.
    /// </remarks>
    public IReadOnlyList<string>? ForbiddenTerms { get; init; }

    /// <summary>No account context; only the universal rules will apply.</summary>
    public static PasswordContext None => default;

    /// <summary>Builds the context for a specific account.</summary>
    public static PasswordContext For(string? email, string? displayName, params string[] forbiddenTerms) =>
        new()
        {
            Email = email,
            DisplayName = displayName,
            ForbiddenTerms = forbiddenTerms.Length == 0 ? null : forbiddenTerms,
        };

    /// <summary>Every term the screening rules should consider, skipping blanks.</summary>
    internal IEnumerable<string> EnumerateTerms()
    {
        if (!string.IsNullOrWhiteSpace(Email))
        {
            yield return Email;
        }

        if (!string.IsNullOrWhiteSpace(DisplayName))
        {
            yield return DisplayName;
        }

        if (ForbiddenTerms is null)
        {
            yield break;
        }

        foreach (var term in ForbiddenTerms)
        {
            if (!string.IsNullOrWhiteSpace(term))
            {
                yield return term;
            }
        }
    }
}
