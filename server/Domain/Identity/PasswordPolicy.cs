namespace EndpointPlatform.Domain.Identity;

/// <summary>
/// What the platform will accept as an administrator password.
/// </summary>
/// <remarks>
/// <para>
/// A pure rule, deliberately separate from hashing and from the change-password
/// flow, so that "what counts as acceptable" can be asserted without a database,
/// an HTTP request, or a hasher.
/// </para>
/// <para>
/// <b>Length is weighted over composition.</b> The minimum is 12 characters, and
/// there is no requirement for a digit, a symbol, or mixed case. Composition
/// rules reliably produce <c>Password1!</c> and a sticky note; length is the
/// property that actually costs an attacker something. This follows current NIST
/// 800-63B guidance rather than the older complexity-rule tradition, and it
/// matches the 12-character floor the bootstrapper already enforces.
/// </para>
/// <para>
/// The upper bound exists to stop a password long enough to be a denial of
/// service against the hasher, not because long passphrases are undesirable.
/// </para>
/// </remarks>
public static class PasswordPolicy
{
    /// <summary>The same floor the bootstrap tool enforces, so the two cannot disagree.</summary>
    public const int MinimumLength = 12;

    /// <summary>
    /// Bounded so a caller cannot make the hasher do unbounded work.
    /// </summary>
    /// <remarks>
    /// Argon2/PBKDF2-class hashing is intentionally expensive; feeding it a
    /// megabyte of input turns that cost into an availability problem. 256 is far
    /// above any real passphrase.
    /// </remarks>
    public const int MaximumLength = 256;

    /// <summary>
    /// Returns a refusal reason, or null when the password is acceptable.
    /// </summary>
    /// <remarks>
    /// Applies the universal rules only. Prefer
    /// <see cref="Validate(string?, PasswordContext)"/> wherever the account is
    /// known: without it, the rule that refuses a password built from the
    /// account's own name or e-mail cannot fire, and that is the most valuable
    /// rule on an internet-facing console.
    /// </remarks>
    public static string? Validate(string? password) => Validate(password, PasswordContext.None);

    /// <summary>
    /// Returns a refusal reason, or null when the password is acceptable.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Returns the first failure rather than a list. The caller shows this to a
    /// person who is retyping a password, and a wall of simultaneous complaints is
    /// harder to act on than one clear instruction.
    /// </para>
    /// <para>
    /// The length rules run before <see cref="WeakPassword"/>, so a short
    /// password is told it is short rather than told it is guessable. Both are
    /// true; the length message is the one that can be acted on.
    /// </para>
    /// </remarks>
    public static string? Validate(string? password, PasswordContext context)
    {
        if (string.IsNullOrWhiteSpace(password))
        {
            return "A password is required.";
        }

        // Length in characters, not bytes. A passphrase in a non-Latin script
        // would otherwise clear a byte-based floor on far fewer characters.
        if (password.Length < MinimumLength)
        {
            return $"The password must be at least {MinimumLength} characters.";
        }

        if (password.Length > MaximumLength)
        {
            return $"The password must be at most {MaximumLength} characters.";
        }

        // Whitespace-only was caught above; a password that is merely one
        // character repeated clears the length floor while carrying almost no
        // entropy, and is a common way to satisfy a length rule without meaning
        // to choose a password at all.
        if (password.Distinct().Count() == 1)
        {
            return "The password must not be a single repeated character.";
        }

        // Everything above is a property of the string. Everything in
        // WeakPassword is a judgement about how guessable it is, which is why it
        // lives in its own type and runs last.
        return WeakPassword.Inspect(password, context);
    }
}
