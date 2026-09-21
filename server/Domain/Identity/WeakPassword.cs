using System.Collections.Frozen;
using System.Globalization;
using System.Text;

namespace EndpointPlatform.Domain.Identity;

/// <summary>
/// Screens a password that already clears the length floor for the patterns
/// people actually choose when told to pick twelve characters.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this is not a "top 10,000 passwords" list.</b> The usual advice is to
/// check the password against a breach corpus. Against a 12-character floor that
/// check is nearly worthless: <c>password</c>, <c>qwerty</c> and <c>123456</c>
/// are already refused for length, and essentially the whole head of every
/// published list is under twelve characters. What survives a length rule is a
/// different population - a common word with a numeric suffix, a keyboard row, a
/// short unit repeated to fill the quota, or the person's own name. Those are
/// patterns, not entries, so this screens for patterns and keeps the word list
/// small and defensible rather than large and imported.
/// </para>
/// <para>
/// <b>Everything here is a pure function.</b> No I/O, no network, no embedded
/// corpus file. The server makes no outbound HTTP calls anywhere, and password
/// validation is the wrong place to introduce the first one: an on-premises
/// platform should not consult a third party, or fail open when it cannot, at the
/// moment somebody sets a credential.
/// </para>
/// <para>
/// <b>False positives are the cost being accepted.</b> A refusal here tells
/// somebody to choose differently, which is a small annoyance. The alternative -
/// admitting <c>Techsara@2026</c> on an internet-facing console - is not. Where a
/// rule had to err, it errs toward refusing.
/// </para>
/// </remarks>
public static class WeakPassword
{
    /// <summary>
    /// A stem must be at least this long before it is worth matching on.
    /// </summary>
    /// <remarks>
    /// Shorter fragments appear inside ordinary words by coincidence: "ate" is in
    /// "moderate", "ram" is in "diagram". Matching those would refuse good
    /// passphrases for no gain.
    /// </remarks>
    private const int MinimumStemLength = 4;

    /// <summary>
    /// How many times a unit must repeat before the repetition is the password.
    /// </summary>
    /// <remarks>
    /// Two repetitions is a real choice - "horsehorse" is a weak password but
    /// "boubou" is a word. Three or more is padding to reach a length rule.
    /// </remarks>
    private const int RepetitionThreshold = 3;

    /// <summary>
    /// Runs of this many adjacent keyboard or alphabet characters are a walk.
    /// </summary>
    /// <remarks>
    /// Six is long enough that it cannot happen by accident and short enough to
    /// catch <c>qwerty</c> or <c>abcdef</c> embedded in a longer string.
    /// </remarks>
    private const int WalkLength = 6;

    /// <summary>
    /// Common stems, lower-case and already stripped of any suffix.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Deliberately short. Each entry earns its place by being a word people
    /// demonstrably build twelve-character passwords out of, and the suffix and
    /// leet normalisation below multiplies each one across the variants it
    /// actually appears in - so <c>password</c> also covers <c>Password123</c>,
    /// <c>p@ssw0rd!</c> and <c>PASSWORD2026</c>. Adding entries is cheap; the
    /// point is that a curated few hundred with normalisation beats an imported
    /// ten thousand without it.
    /// </para>
    /// <para>
    /// Organisation-specific terms - a company, product or site name - do not
    /// belong here. They are supplied per call through
    /// <see cref="PasswordContext.ForbiddenTerms"/>, because they differ by
    /// deployment and a term that matters to one tenant is noise to another.
    /// </para>
    /// </remarks>
    private static readonly FrozenSet<string> Stems = new[]
    {
        // The credential words themselves.
        "password", "passwd", "passcode", "pass", "secret", "login", "logon",
        "signin", "credential", "changeme", "letmein", "access", "opensesame",
        "temporary", "temppass", "newpass", "mypass", "defaultpass", "notsecure",

        // Roles and accounts.
        "admin", "administrator", "sysadmin", "netadmin", "root", "superuser",
        "operator", "manager", "support", "helpdesk", "service", "guest", "user",
        "owner", "master", "control", "console", "domain", "workstation",

        // Keyboard and sequence words that survive as words.
        "qwerty", "qwertyuiop", "azerty", "asdfgh", "asdfghjkl", "zxcvbn",
        "zxcvbnm", "qazwsx", "qazwsxedc", "poiuyt", "lkjhgf", "mnbvcxz",
        "abcdef", "abcdefg", "abcabc", "aaabbb", "123abc", "abc123",

        // Deployment and test words, which are the ones that survive longest.
        "test", "testing", "tester", "demo", "sample", "example", "default",
        "template", "staging", "production", "development", "sandbox", "backup",
        "install", "setup", "config", "system", "server", "network", "computer",
        "internet", "database", "localhost",

        // Affection and encouragement, the largest real category.
        "iloveyou", "ilove", "loveyou", "lovely", "forever", "sunshine",
        "princess", "sweetheart", "darling", "beautiful", "gorgeous", "angel",
        "heaven", "blessed", "grateful", "happy", "smile", "welcome", "hello",
        "freedom", "trustno", "whatever", "believe", "dream", "hope", "peace",

        // Names that top every corpus.
        "michael", "jennifer", "jessica", "michelle", "matthew", "joshua",
        "daniel", "charlie", "andrew", "robert", "thomas", "william", "george",
        "nicole", "ashley", "amanda", "hannah", "samantha", "elizabeth",
        "anthony", "richard", "patrick", "steven", "justin", "brandon",

        // Culture, sport and animals.
        "superman", "batman", "spiderman", "ironman", "starwars", "startrek",
        "pokemon", "minecraft", "fortnite", "football", "baseball", "basketball",
        "cricket", "soccer", "hockey", "liverpool", "arsenal", "chelsea",
        "barcelona", "madrid", "united", "dragon", "monkey", "tigger", "shadow",
        "phoenix", "falcon", "eagle", "dolphin", "butterfly", "flower",
        "chocolate", "cookie", "pepper", "ginger", "whisky", "guinness",

        // Colours, seasons, elements.
        "purple", "orange", "yellow", "silver", "golden", "diamond", "crystal",
        "summer", "winter", "spring", "autumn", "thunder", "lightning",
        "midnight", "sunset", "sunrise", "rainbow", "mountain", "ocean", "river",

        // Vehicles and brands people reuse.
        "harley", "ferrari", "porsche", "mustang", "corvette", "maverick",
        "samsung", "google", "facebook", "twitter", "youtube", "amazon",
        "microsoft", "windows", "android", "iphone", "yahoo", "hotmail",

        // Faith and place, heavily represented in South Asian corpora.
        "jesus", "christ", "allah", "krishna", "ganesh", "shiva", "buddha",
        "india", "bharat", "mumbai", "delhi", "bangalore", "chennai", "kolkata",
        "hyderabad", "punjab", "london", "newyork", "canada", "australia",

        // Famous examples, which become common precisely by being examples.
        "correcthorsebatterystaple", "troubador", "hunter", "monkeybusiness",
        "thisismypassword", "mypasswordis", "nopassword", "justapassword",
    }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>Keyboard rows, used to detect walks in either direction.</summary>
    /// <remarks>
    /// QWERTY only. A deployment on another layout would need its rows added;
    /// detecting walks on a layout the chooser is not using would be noise.
    /// </remarks>
    private static readonly string[] KeyboardRows =
    [
        "1234567890", "qwertyuiop", "asdfghjkl", "zxcvbnm",
        "abcdefghijklmnopqrstuvwxyz",
    ];

    /// <summary>
    /// Returns a refusal reason, or null when nothing weak was recognised.
    /// </summary>
    /// <remarks>
    /// Returns the first failure, matching
    /// <see cref="PasswordPolicy.Validate(string?)"/>: somebody retyping a
    /// password acts on one clear instruction better than on a list.
    /// </remarks>
    public static string? Inspect(string password, PasswordContext context)
    {
        ArgumentNullException.ThrowIfNull(password);

        // TWO normal forms, and the distinction is load-bearing.
        //
        //   plain  - case, accents and punctuation removed, DIGITS KEPT AS DIGITS.
        //   folded - the same, plus leet substitution, so 0 becomes o and @ becomes a.
        //
        // Folding is what lets a few hundred stems match "P@ssw0rd", but it must
        // not be used for the digit-sensitive rules: folded, "password1234" reads
        // as "passwordi2ea", whose trailing digits can no longer be stripped, and
        // the keyboard row "1234567890" could never match itself. So walks,
        // repetition and padding-stripping all run on `plain`, and only stem
        // matching considers both.
        var plain = Normalise(password, applyLeet: false);
        var folded = Normalise(password, applyLeet: true);

        // Order affects only which message is shown first. Contextual failures
        // lead because they are the most actionable - "it contains your name"
        // tells somebody exactly what to change.
        if (ContainsOwnIdentity(plain, folded, context) is { } identity)
        {
            return identity;
        }

        if (IsRepeatedUnit(plain))
        {
            return "The password is a short sequence repeated. Choose something with more variety.";
        }

        if (ContainsWalk(plain))
        {
            return "The password contains a run of adjacent keys or letters. Choose something less predictable.";
        }

        if (MatchesStem(plain) || MatchesStem(folded))
        {
            return "The password is based on a very common word. Choose something less guessable.";
        }

        return null;
    }

    // ------------------------------------------------------------- contextual

    /// <summary>
    /// Refuses a password built from the account's own e-mail or display name.
    /// </summary>
    /// <remarks>
    /// This is the single highest-value check in the file. An attacker
    /// enumerating an internet-facing console already knows the e-mail address -
    /// it is the username - so a password derived from it has close to no
    /// strength against the one attacker who matters.
    /// </remarks>
    private static string? ContainsOwnIdentity(string plain, string folded, PasswordContext context)
    {
        foreach (var term in context.EnumerateTerms())
        {
            foreach (var fragment in SplitIntoFragments(term))
            {
                if (fragment.Length < MinimumStemLength)
                {
                    continue;
                }

                // Both forms, so "N4man" is caught as readily as "naman".
                if (plain.Contains(fragment, StringComparison.Ordinal)
                    || folded.Contains(fragment, StringComparison.Ordinal))
                {
                    return "The password must not contain your name, e-mail address or the organisation's name.";
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Breaks a term into the word-like pieces worth matching on.
    /// </summary>
    /// <remarks>
    /// "naman.jain@techsarasolutions.com" has to yield "naman", "jain" and
    /// "techsarasolutions" - matching the whole string would never fire, because
    /// nobody puts their entire e-mail address in a password, but plenty of
    /// people use their first name.
    /// </remarks>
    private static IEnumerable<string> SplitIntoFragments(string term)
    {
        // Plain form: a name fragment is compared against BOTH forms of the
        // password by the caller, so folding it here as well would only let
        // "1" in a surname match an "i" in the password.
        var normalised = Normalise(term, applyLeet: false);
        if (normalised.Length == 0)
        {
            yield break;
        }

        yield return normalised;

        // Split on the separators that appear in names and addresses. The TLD is
        // dropped: "com" is below the stem floor anyway, but being explicit here
        // documents that "example.com" should not make "com" a forbidden word.
        var pieces = term.Split(
            ['@', '.', '-', '_', '+', ' ', '\t'],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (var piece in pieces)
        {
            var cleaned = Normalise(piece, applyLeet: false);
            if (cleaned.Length >= MinimumStemLength && !IsPublicSuffix(cleaned))
            {
                yield return cleaned;
            }
        }
    }

    /// <summary>Common suffixes that carry no identity and would only cause noise.</summary>
    private static bool IsPublicSuffix(string piece) =>
        piece is "com" or "net" or "org" or "edu" or "gov" or "info" or "local"
            or "mail" or "email" or "inc" or "ltd" or "llc" or "plc" or "gmbh"
            or "co" or "io" or "ai" or "dev";

    // ------------------------------------------------------------- structural

    /// <summary>
    /// True when the password is one short unit repeated to reach the floor.
    /// </summary>
    /// <remarks>
    /// <c>abcabcabcabc</c> clears twelve characters and a distinct-character
    /// check while carrying the entropy of three characters. Only units up to a
    /// third of the length are considered, so a genuine passphrase that happens
    /// to repeat a word twice is not caught.
    /// </remarks>
    private static bool IsRepeatedUnit(string value)
    {
        if (value.Length < RepetitionThreshold)
        {
            return false;
        }

        for (var unit = 1; unit <= value.Length / RepetitionThreshold; unit++)
        {
            if (value.Length % unit != 0)
            {
                continue;
            }

            var candidate = value.AsSpan(0, unit);
            var repeats = true;

            for (var offset = unit; offset < value.Length; offset += unit)
            {
                if (!value.AsSpan(offset, unit).SequenceEqual(candidate))
                {
                    repeats = false;
                    break;
                }
            }

            if (repeats)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// True when the password contains a long run along a keyboard row or the alphabet.
    /// </summary>
    /// <remarks>
    /// Checked in both directions, because <c>0987654321</c> is exactly as
    /// predictable as <c>1234567890</c>.
    /// </remarks>
    private static bool ContainsWalk(string value)
    {
        foreach (var row in KeyboardRows)
        {
            if (ContainsRunFrom(value, row) || ContainsRunFrom(value, Reverse(row)))
            {
                return true;
            }
        }

        return false;
    }

    private static bool ContainsRunFrom(string value, string row)
    {
        for (var start = 0; start + WalkLength <= row.Length; start++)
        {
            if (value.Contains(row.Substring(start, WalkLength), StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static string Reverse(string value)
    {
        var characters = value.ToCharArray();
        Array.Reverse(characters);
        return new string(characters);
    }

    // ------------------------------------------------------------------ stems

    /// <summary>
    /// True when the password reduces to a known stem once padding is removed.
    /// </summary>
    /// <remarks>
    /// The reduction is what gives the small list its reach. <c>Passw0rd@2026</c>
    /// normalises to <c>password2026</c>, loses its trailing digits, and matches
    /// <c>password</c>. A stem is also matched when it simply appears inside the
    /// password and accounts for most of it, so <c>xpasswordx</c> is caught while
    /// a long passphrase that merely mentions a common word is not.
    /// </remarks>
    private static bool MatchesStem(string normalised)
    {
        if (Stems.Contains(normalised))
        {
            return true;
        }

        var stripped = StripPadding(normalised);
        if (stripped.Length >= MinimumStemLength && Stems.Contains(stripped))
        {
            return true;
        }

        // A stem that dominates the password. Half is the threshold: below it the
        // remaining material is doing real work, above it the stem IS the password.
        foreach (var stem in Stems)
        {
            if (stem.Length >= MinimumStemLength
                && stem.Length * 2 >= stripped.Length
                && stripped.Contains(stem, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Removes the digits and punctuation people append to reach a length rule.</summary>
    private static string StripPadding(string value) =>
        value.AsSpan().TrimStart("0123456789").TrimEnd("0123456789").ToString();

    // ----------------------------------------------------------- normalisation

    /// <summary>
    /// Folds a password to the form the rules above are written against.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Lower-cases, removes diacritics and drops everything that is not a letter
    /// or a digit. With <paramref name="applyLeet"/> it additionally substitutes
    /// the common character swaps, which is what lets a few hundred stems cover
    /// the variants that actually appear: <c>P@$$w0rd!</c> becomes
    /// <c>password</c>.
    /// </para>
    /// <para>
    /// <b>Leet folding is not always wanted.</b> It rewrites digits as letters,
    /// which destroys exactly the information the padding and keyboard-walk rules
    /// depend on - see the note in <see cref="Inspect"/>. Pass false unless the
    /// comparison is against the stem list.
    /// </para>
    /// <para>
    /// Only ever used for COMPARISON. The password itself is never stored,
    /// hashed or transmitted in this form - that would discard the entropy the
    /// substitutions represent.
    /// </para>
    /// </remarks>
    private static string Normalise(string value, bool applyLeet)
    {
        var decomposed = value.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);

        foreach (var character in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            var lowered = char.ToLowerInvariant(character);
            var mapped = applyLeet
                ? lowered switch
                {
                    '0' => 'o',
                    '1' => 'i',
                    '3' => 'e',
                    '4' => 'a',
                    '5' => 's',
                    '7' => 't',
                    '8' => 'b',
                    '9' => 'g',
                    '@' => 'a',
                    '$' => 's',
                    '!' => 'i',
                    '|' => 'i',
                    '+' => 't',
                    _ => lowered,
                }
                : lowered;

            if (char.IsLetterOrDigit(mapped))
            {
                builder.Append(mapped);
            }
        }

        return builder.ToString();
    }
}
