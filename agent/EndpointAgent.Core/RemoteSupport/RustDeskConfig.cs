using System.Text.RegularExpressions;

namespace EndpointAgent.Core.RemoteSupport;

/// <summary>
/// Pure, platform-neutral reading of a RustDesk device ID from its configuration.
/// </summary>
/// <remarks>
/// RustDesk keeps the ID in <c>RustDesk.toml</c>: older versions as a plaintext
/// <c>id = 'NNN'</c>, current versions (1.4.7+) only as an encrypted <c>enc_id</c>
/// (<see cref="RustDeskEncId"/>, which <c>Config.store()</c> writes while blanking the
/// plaintext <c>id</c>). Both are handled here, so the Windows reader has only to
/// locate the file and supply the machine UUID; turning that into an ID -- the one
/// fiddly part -- stays here, unit-testable without a Windows file system or a RustDesk
/// install.
/// </remarks>
public static partial class RustDeskConfig
{
    /// <summary>
    /// The device ID from a config file's text: the plaintext <c>id</c> if present,
    /// otherwise <c>enc_id</c> decrypted with this machine's UUID. Null when neither
    /// yields a usable ID (absent, empty, not yet assigned, or not decryptable here).
    /// </summary>
    /// <param name="machineGuid">
    /// The Windows MachineGuid string, exactly as stored (lowercase, hyphenated, no
    /// braces) -- the key RustDesk sealed <c>enc_id</c> with. When null or empty, only
    /// the plaintext <c>id</c> can be read.
    /// </param>
    public static string? ParseDeviceId(string? tomlText, string? machineGuid)
    {
        var plain = ParseDeviceId(tomlText);
        if (plain is not null)
        {
            return plain;
        }

        if (string.IsNullOrEmpty(tomlText))
        {
            return null;
        }

        var match = EncIdLine().Match(tomlText);
        if (!match.Success)
        {
            return null;
        }

        var enc = TrimOneQuoteLayer(match.Groups["value"].Value.Trim());

        // RustDesk prefixes the base64 with its two-character string-version "00".
        if (!enc.StartsWith("00", StringComparison.Ordinal) || enc.Length <= 2)
        {
            return null;
        }

        return RustDeskEncId.Decrypt(enc[2..], machineGuid);
    }

    /// <summary>
    /// The plaintext device ID from a config file's text, or null when there is no
    /// usable plaintext <c>id</c> line (absent, empty, or encrypted into
    /// <c>enc_id</c>).
    /// </summary>
    /// <remarks>
    /// Matches a top-level <c>id = 'NNN'</c>, <c>id = "NNN"</c> or bare <c>id = NNN</c>,
    /// and deliberately never matches <c>enc_id</c> or any other key (the <c>id</c> key
    /// is anchored to the start of a line). An empty value reads as null, which is the
    /// RustDesk "not yet registered" state.
    /// </remarks>
    public static string? ParseDeviceId(string? tomlText)
    {
        if (string.IsNullOrEmpty(tomlText))
        {
            return null;
        }

        var match = IdLine().Match(tomlText);
        if (!match.Success)
        {
            return null;
        }

        var raw = TrimOneQuoteLayer(match.Groups["value"].Value.Trim());
        return raw.Length == 0 ? null : raw;
    }

    /// <summary>Trims exactly one layer of matching quotes, leaving a bare value untouched.</summary>
    private static string TrimOneQuoteLayer(string raw)
    {
        if (raw.Length >= 2
            && ((raw[0] == '\'' && raw[^1] == '\'') || (raw[0] == '"' && raw[^1] == '"')))
        {
            raw = raw[1..^1];
        }

        return raw.Trim();
    }

    // Start of a line (Multiline), optional indent, the key `id` exactly -- so
    // `enc_id`, `id_server` and the like never match -- `=`, then the value up to an
    // optional trailing comment. Quotes are trimmed by the caller.
    [GeneratedRegex(@"^[ \t]*id[ \t]*=[ \t]*(?<value>.+?)[ \t]*(?:#.*)?$", RegexOptions.Multiline)]
    private static partial Regex IdLine();

    // The encrypted id line, anchored the same way so nothing else matches.
    [GeneratedRegex(@"^[ \t]*enc_id[ \t]*=[ \t]*(?<value>.+?)[ \t]*(?:#.*)?$", RegexOptions.Multiline)]
    private static partial Regex EncIdLine();
}
