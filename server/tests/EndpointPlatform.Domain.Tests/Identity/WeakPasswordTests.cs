using EndpointPlatform.Domain.Identity;

namespace EndpointPlatform.Domain.Tests.Identity;

/// <summary>
/// What the platform refuses as guessable, once a password already clears the
/// twelve-character floor.
/// </summary>
/// <remarks>
/// <para>
/// These rules exist because a length floor changes which passwords people
/// choose rather than making them choose well. The classic breach-corpus entries
/// - <c>password</c>, <c>qwerty</c>, <c>123456</c> - are all refused for length
/// long before any list is consulted, so screening against such a list buys
/// almost nothing here. What survives twelve characters is a stem with a numeric
/// suffix, a keyboard row, a short unit repeated, or the account's own name, and
/// those are the four things pinned below.
/// </para>
/// <para>
/// The accept cases matter as much as the refuse cases: a screening rule that
/// refuses ordinary passphrases pushes people back toward a sticky note, which
/// is the outcome the length-over-composition policy was chosen to avoid.
/// </para>
/// </remarks>
public sealed class WeakPasswordTests
{
    // ------------------------------------------------------------- the account

    /// <summary>
    /// A password built from the account's own identity is refused.
    /// </summary>
    /// <remarks>
    /// The highest-value rule in the set. On an internet-facing console the
    /// e-mail address is the username, so an attacker who can reach the form
    /// already knows it; a password derived from it has close to no strength
    /// against the only attacker who matters.
    /// </remarks>
    [Theory]
    [InlineData("SamRivera2026x")]          // the family name
    [InlineData("rivera-2026-quarry")]      // the family name, buried in a phrase
    [InlineData("Northwind-2026-ok")]       // the domain
    [InlineData("xxriveraxxquarryq")]       // buried, not at the start
    [InlineData("S4mR1v3r4-2026x")]         // leet-spelled
    public void A_password_containing_the_account_identity_is_refused(string password)
    {
        var context = PasswordContext.For("sam.rivera@northwind.test", "Sam Rivera");

        WeakPassword.Inspect(password, context).ShouldNotBeNull();
    }

    /// <summary>A deployment's own terms are refused without shipping them to everybody.</summary>
    [Fact]
    public void A_password_containing_a_deployment_forbidden_term_is_refused()
    {
        var context = PasswordContext.For("someone@nowhere.test", "Some One", "Northwind", "EndpointPlatform");

        WeakPassword.Inspect("Northwind@2026!", context).ShouldNotBeNull();
        WeakPassword.Inspect("endpointplatform99", context).ShouldNotBeNull();
    }

    /// <summary>
    /// Without account context the identity rules cannot fire, and that is a real cost.
    /// </summary>
    /// <remarks>
    /// Pinned so the gap is visible: any caller that sets a password and does not
    /// pass <see cref="PasswordContext"/> silently loses the most valuable rule.
    /// It is the reason the two-argument overload exists and the reason callers
    /// should prefer it.
    /// </remarks>
    [Fact]
    public void Without_context_the_identity_rules_do_not_fire()
    {
        WeakPassword.Inspect("SamRivera2026x", PasswordContext.None).ShouldBeNull();

        var context = PasswordContext.For("sam.rivera@northwind.test", "Sam Rivera");
        WeakPassword.Inspect("SamRivera2026x", context).ShouldNotBeNull();
    }

    /// <summary>A short fragment is not matched, or ordinary words would be refused.</summary>
    /// <remarks>
    /// "ali" inside "normalisation" is a coincidence, not a weakness. The floor
    /// is four characters.
    /// </remarks>
    [Fact]
    public void A_fragment_below_the_stem_floor_is_not_matched()
    {
        var context = PasswordContext.For("ali@nowhere.test", "Ali Bo");

        WeakPassword.Inspect("normalisation drift", context).ShouldBeNull();
    }

    // ------------------------------------------------------------- structural

    [Theory]
    [InlineData("abcabcabcabc")]
    [InlineData("xyxyxyxyxyxy")]
    [InlineData("123412341234")]
    [InlineData("nononononono")]
    public void A_short_unit_repeated_to_reach_the_floor_is_refused(string password)
    {
        WeakPassword.Inspect(password, PasswordContext.None).ShouldNotBeNull();
    }

    /// <summary>Two repetitions is a word; three is padding.</summary>
    [Fact]
    public void A_unit_repeated_only_twice_is_accepted()
    {
        WeakPassword.Inspect("marmot-marmot", PasswordContext.None).ShouldBeNull();
    }

    [Theory]
    [InlineData("qwertyuiopas")]            // a keyboard row
    [InlineData("xxasdfghjklx")]            // the home row, buried
    [InlineData("1234567890ab")]            // the number row
    [InlineData("0987654321ab")]            // and backwards
    [InlineData("abcdefghijkl")]            // the alphabet
    [InlineData("zyxwvutsrqpo")]            // and backwards
    public void A_run_of_adjacent_keys_or_letters_is_refused(string password)
    {
        WeakPassword.Inspect(password, PasswordContext.None).ShouldNotBeNull();
    }

    /// <summary>A run shorter than the threshold is left alone.</summary>
    /// <remarks>
    /// "abcde" appears inside ordinary text; refusing on five would be noise.
    /// </remarks>
    [Fact]
    public void A_short_run_is_accepted()
    {
        WeakPassword.Inspect("abcde-marmot-riv", PasswordContext.None).ShouldBeNull();
    }

    // ------------------------------------------------------------------ stems

    [Theory]
    [InlineData("password1234")]
    [InlineData("P@ssw0rd!2026")]
    [InlineData("Administrator1")]
    [InlineData("letmein123456")]
    [InlineData("MyPassword2026")]
    [InlineData("iloveyou1234567")]
    [InlineData("SuperSecret99")]
    public void A_common_stem_with_padding_is_refused(string password)
    {
        WeakPassword.Inspect(password, PasswordContext.None).ShouldNotBeNull();
    }

    /// <summary>
    /// Leet substitution is what gives a small stem list its reach.
    /// </summary>
    /// <remarks>
    /// All four spellings reduce to the same stem, so one entry covers the
    /// variants that actually appear in the wild.
    /// </remarks>
    [Theory]
    [InlineData("password2026")]
    [InlineData("P4ssw0rd2026")]
    [InlineData("p@$$w0rd2026")]
    [InlineData("PASSWORD2026")]
    public void Leet_spellings_reduce_to_the_same_stem(string password)
    {
        WeakPassword.Inspect(password, PasswordContext.None).ShouldNotBeNull();
    }

    /// <summary>
    /// A stem mentioned inside a longer passphrase is accepted.
    /// </summary>
    /// <remarks>
    /// The rule is "the stem IS the password", not "the stem appears in it".
    /// Refusing every passphrase that contains a common word would refuse most
    /// good ones.
    /// </remarks>
    [Theory]
    [InlineData("my dragon ate the copper kettle")]
    [InlineData("summer rain over the quarry wall")]
    public void A_stem_inside_a_longer_passphrase_is_accepted(string password)
    {
        WeakPassword.Inspect(password, PasswordContext.None).ShouldBeNull();
    }

    // ----------------------------------------------------------- good enough

    [Theory]
    [InlineData("rivet manifold cobalt drizzle")]
    [InlineData("Kv7ndlp&3xqm")]
    [InlineData("quarry-lantern-38-birch")]
    [InlineData("これは長いパスワードです")]
    [InlineData("éàüñörçkèdvî")]
    public void An_ordinary_good_password_is_accepted(string password)
    {
        WeakPassword.Inspect(password, PasswordContext.None).ShouldBeNull();
    }

    /// <summary>
    /// Screening never throws on the odd inputs the length rules already handle.
    /// </summary>
    /// <remarks>
    /// <see cref="PasswordPolicy"/> runs the length rules first, so these never
    /// reach <see cref="WeakPassword"/> in production - but a direct caller
    /// should not be able to crash it.
    /// </remarks>
    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("!!!!")]
    [InlineData("aaaa")]
    public void Degenerate_input_does_not_throw(string password)
    {
        Should.NotThrow(() => WeakPassword.Inspect(password, PasswordContext.None));
    }
}
