using System.Security.Cryptography;
using EndpointPlatform.Domain.Identity;

namespace EndpointPlatform.Infrastructure.Security;

/// <summary>
/// A strong initial password that a person can actually retype.
/// </summary>
/// <remarks>
/// <para>
/// Used when an administrator account is created, or when its password is reset by
/// another administrator. The value is displayed exactly once and must then be
/// replaced by its owner, so it has to survive being read off one screen and typed
/// into another — twice, because the change-password form asks for the current
/// password as well.
/// </para>
/// <para>
/// <see cref="SecretGenerator.GenerateSecret"/> is deliberately NOT reused here. It
/// produces 64 hexadecimal characters, which is right for a machine-to-machine
/// secret and wrong for this: five mistypes trip the sign-in lockout, and there is
/// no self-service unlock. This generator trades a little entropy for a value a
/// human can transcribe without error, and still leaves far more than a password
/// anybody would choose.
/// </para>
/// <para>
/// Ambiguous glyphs are excluded on purpose: <c>0</c>/<c>O</c> and
/// <c>1</c>/<c>l</c>/<c>I</c> are the pairs people actually get wrong when copying
/// from a screen, and a wrong guess here costs a lockout rather than a retry.
/// </para>
/// </remarks>
public static class GeneratedPassword
{
    /// <summary>
    /// Unambiguous characters only. Both cases are present, so the result also
    /// satisfies any future policy that asks for mixed case.
    /// </summary>
    private const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789";

    /// <summary>Characters per hyphen-separated group.</summary>
    private const int GroupSize = 5;

    /// <summary>Number of groups. Four groups of five is 20 characters of alphabet.</summary>
    private const int Groups = 4;

    /// <summary>
    /// Creates a password of the form <c>Ab3De-Fg7Hj-Km9Np-Qr2St</c>.
    /// </summary>
    /// <remarks>
    /// The hyphens are part of the password. They cost nothing to type, make the
    /// value far easier to read aloud or copy by hand, and are already permitted —
    /// <see cref="PasswordPolicy"/> constrains length and rejects a single repeated
    /// character, nothing more.
    /// </remarks>
    public static string Create()
    {
        // 20 alphabet characters over a 56-character alphabet is about 116 bits.
        var characters = new char[(GroupSize * Groups) + (Groups - 1)];
        var index = 0;

        for (var group = 0; group < Groups; group++)
        {
            if (group > 0)
            {
                characters[index++] = '-';
            }

            for (var position = 0; position < GroupSize; position++)
            {
                // GetItems draws from the cryptographic RNG; it is not Random.
                characters[index++] = RandomNumberGenerator.GetItems<char>(Alphabet, 1)[0];
            }
        }

        var password = new string(characters);

        // The policy is the authority on what is acceptable, so it is asserted here
        // rather than assumed. A generator that silently drifted below the floor
        // would surface as an unexplained refusal when the account is created.
        if (PasswordPolicy.Validate(password) is { } failure)
        {
            throw new InvalidOperationException(
                $"The generated password did not satisfy the password policy: {failure}");
        }

        return password;
    }

    /// <summary>
    /// Characters for a recovery code: upper case and digits, still unambiguous.
    /// </summary>
    /// <remarks>
    /// Case-insensitive on purpose, unlike <see cref="Alphabet"/>. A recovery code
    /// is written down and retyped months later, often from a printed card, and
    /// mixed case in that setting produces support calls rather than security.
    /// The lost entropy is bought back with length.
    /// </remarks>
    private const string RecoveryAlphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";

    /// <summary>
    /// Creates a single-use recovery code of the form <c>K7QM-2XRB-9FTD</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Twelve characters over a 32-character alphabet is 60 bits - far beyond
    /// guessing, and deliberately not reusing <see cref="Create"/>. A recovery
    /// code bypasses the second factor entirely, so it must not look like a
    /// password or invite being stored as one; the distinct shape is a cue that
    /// this is a different kind of secret.
    /// </para>
    /// <para>
    /// The hyphens are presentation only. The code is normalised - letters and
    /// digits, upper-cased - before it is hashed or compared, so whatever
    /// separators somebody types are irrelevant.
    /// </para>
    /// </remarks>
    public static string CreateRecoveryCode()
    {
        const int groupSize = 4;
        const int groups = 3;

        var characters = new char[(groupSize * groups) + (groups - 1)];
        var index = 0;

        for (var group = 0; group < groups; group++)
        {
            if (group > 0)
            {
                characters[index++] = '-';
            }

            for (var position = 0; position < groupSize; position++)
            {
                characters[index++] = RandomNumberGenerator.GetItems<char>(RecoveryAlphabet, 1)[0];
            }
        }

        return new string(characters);
    }
}
