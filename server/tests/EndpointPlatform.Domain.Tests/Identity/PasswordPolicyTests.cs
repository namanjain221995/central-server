using EndpointPlatform.Domain.Identity;

namespace EndpointPlatform.Domain.Tests.Identity;

/// <summary>
/// What the platform accepts as an administrator password.
/// </summary>
/// <remarks>
/// The rule weights length over composition: a 12-character floor, and no
/// requirement for a digit, symbol or mixed case. Composition rules reliably
/// produce "Password1!" and a sticky note, while length is the property that
/// actually costs an attacker something. These tests pin that choice so it is not
/// quietly reverted to complexity rules by someone assuming they are stricter.
/// </remarks>
public sealed class PasswordPolicyTests
{
    [Theory]
    [InlineData("rivet manifold cobalt drizzle")]   // a passphrase nobody has published
    [InlineData("aaaaaaaaaaab")]                    // exactly 12, not all identical
    [InlineData("Kv7ndlp&3xqm")]
    [InlineData("            x")]                   // whitespace is a character like any other
    [InlineData("これは長いパスワードです")]
    public void An_acceptable_password_is_accepted(string password)
    {
        PasswordPolicy.Validate(password).ShouldBeNull();
    }

    /// <summary>
    /// The famous example passwords are refused, precisely because they are famous.
    /// </summary>
    /// <remarks>
    /// <para>
    /// These two were the fixtures this file originally used to mean "obviously
    /// fine". They are not, and the reason is worth stating: an example password
    /// printed in a comic read by millions of engineers stops being an
    /// illustration of a strong password and becomes a common password. Both
    /// appear in published guessing lists for exactly that reason.
    /// </para>
    /// <para>
    /// Pinned as a pair so that nobody restores them as "acceptable" fixtures
    /// later without reading this.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData("correct horse battery staple")]
    [InlineData("Tr0ub4dor&3xyz")]
    public void A_famous_example_password_is_refused(string password)
    {
        password.Length.ShouldBeGreaterThanOrEqualTo(PasswordPolicy.MinimumLength);

        PasswordPolicy.Validate(password).ShouldNotBeNull();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\n")]
    public void An_absent_password_is_refused(string? password)
    {
        PasswordPolicy.Validate(password).ShouldNotBeNull();
    }

    [Theory]
    [InlineData("short")]
    [InlineData("elevenchars")]                     // 11
    [InlineData("Passw0rd!")]                       // complex but short: still refused
    public void A_password_below_the_floor_is_refused(string password)
    {
        var reason = PasswordPolicy.Validate(password);

        reason.ShouldNotBeNull();
        reason!.ShouldContain($"{PasswordPolicy.MinimumLength}");
    }

    /// <summary>
    /// Length is measured in characters, not bytes.
    /// </summary>
    /// <remarks>
    /// A byte-based floor would let a passphrase in a multi-byte script pass on
    /// far fewer actual characters, which is the opposite of what the rule is for.
    /// </remarks>
    [Fact]
    public void Length_is_counted_in_characters_not_bytes()
    {
        // 11 characters, but well over 12 bytes in UTF-8.
        //
        // Deliberately varied rather than one character repeated: accents are
        // folded away before the guessability rules run (WeakPassword.Normalise),
        // so a run of the same letter under different accents reads as a single
        // repeated character and is refused on that ground instead. That refusal
        // is correct, but it would test something other than the length rule.
        var elevenMultiByte = "éàüñörçkèdv";
        elevenMultiByte.Length.ShouldBe(11);

        PasswordPolicy.Validate(elevenMultiByte).ShouldNotBeNull();
        PasswordPolicy.Validate(elevenMultiByte + 'î').ShouldBeNull();
    }

    /// <summary>
    /// A single repeated character clears the length floor while carrying almost
    /// no entropy, and is the obvious way to satisfy a length rule without
    /// choosing a password at all.
    /// </summary>
    [Theory]
    [InlineData("aaaaaaaaaaaa")]
    [InlineData("000000000000")]
    [InlineData("....................")]
    public void A_single_repeated_character_is_refused(string password)
    {
        PasswordPolicy.Validate(password).ShouldNotBeNull();
    }

    /// <summary>
    /// The ceiling exists to bound hasher work, not to discourage passphrases.
    /// </summary>
    /// <remarks>
    /// Deliberately expensive hashing turns unbounded input into an availability
    /// problem, so the limit is far above any real passphrase rather than close
    /// to one.
    /// </remarks>
    [Fact]
    public void An_absurdly_long_password_is_refused_to_bound_hasher_work()
    {
        // Exactly at the ceiling, with two distinct characters so the
        // repeated-character rule is not what is being measured.
        var atCeiling = new string('a', PasswordPolicy.MaximumLength - 1) + "b";
        atCeiling.Length.ShouldBe(PasswordPolicy.MaximumLength);
        PasswordPolicy.Validate(atCeiling).ShouldBeNull();

        var overCeiling = new string('a', PasswordPolicy.MaximumLength) + "b";
        overCeiling.Length.ShouldBe(PasswordPolicy.MaximumLength + 1);
        PasswordPolicy.Validate(overCeiling).ShouldNotBeNull();
    }

    /// <summary>
    /// The floor matches the one the bootstrap tool enforces.
    /// </summary>
    /// <remarks>
    /// If these drifted apart, a password acceptable at bootstrap could be
    /// rejected when changed, or the reverse -- and the weaker of the two would
    /// silently become the real policy.
    /// </remarks>
    [Fact]
    public void The_minimum_matches_the_bootstrap_requirement()
    {
        PasswordPolicy.MinimumLength.ShouldBe(12);
    }

    /// <summary>One reason at a time, because the caller is retyping a password.</summary>
    [Fact]
    public void Only_the_first_failure_is_reported()
    {
        var reason = PasswordPolicy.Validate("aa");

        reason.ShouldNotBeNull();
        reason!.ShouldNotContain(";");
    }
}
