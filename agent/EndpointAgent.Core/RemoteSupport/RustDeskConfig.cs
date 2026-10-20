using System.Text.RegularExpressions;

namespace EndpointAgent.Core.RemoteSupport;

/// <summary>
/// Pure, platform-neutral parsing of RustDesk's configuration file.
/// </summary>
/// <remarks>
/// The one fiddly part of reading the RustDesk ID -- pulling it out of a line of
/// <c>RustDesk2.toml</c> -- lives here rather than in the Windows reader, so it can
/// be unit-tested without a Windows file system or a RustDesk install. The Windows
/// reader locates the file and reads its bytes; this turns that text into an ID.
/// </remarks>
public static partial class RustDeskConfig
{
    /// <summary>
    /// The device ID from the text of a <c>RustDesk2.toml</c>, or null when there is
    /// no usable ID line (absent, empty, or not yet assigned).
    /// </summary>
    /// <remarks>
    /// Matches a top-level <c>id = 'NNN'</c>, <c>id = "NNN"</c> or bare
    /// <c>id = NNN</c>, trims one layer of surrounding quotes, and deliberately never
    /// matches <c>enc_id</c> or any other key (the <c>id</c> key is anchored to the
    /// start of a line). An empty value reads as null, which is the RustDesk ID "not
    /// yet registered" state.
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

        var raw = match.Groups["value"].Value.Trim();

        // Trim exactly one layer of matching quotes, leaving a bare value untouched.
        if (raw.Length >= 2
            && ((raw[0] == '\'' && raw[^1] == '\'') || (raw[0] == '"' && raw[^1] == '"')))
        {
            raw = raw[1..^1];
        }

        raw = raw.Trim();
        return raw.Length == 0 ? null : raw;
    }

    // Start of a line (Multiline), optional indent, the key `id` exactly -- so
    // `enc_id`, `id_server` and the like never match -- `=`, then the value up to an
    // optional trailing comment. Quotes are trimmed by the caller.
    [GeneratedRegex(@"^[ \t]*id[ \t]*=[ \t]*(?<value>.+?)[ \t]*(?:#.*)?$", RegexOptions.Multiline)]
    private static partial Regex IdLine();
}
