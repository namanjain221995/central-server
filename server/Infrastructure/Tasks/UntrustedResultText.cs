using System.Text.RegularExpressions;

namespace EndpointPlatform.Infrastructure.Tasks;

/// <summary>
/// Makes agent-supplied task-result text storable: PostgreSQL cannot hold a NUL
/// character, in a text column (SQLSTATE 22021) or in jsonb, which refuses the
/// <c>\u0000</c> escape outright (22P05).
/// </summary>
/// <remarks>
/// <para>
/// A result an agent posts is untrusted, and a NUL in it -- a driver title read from
/// a badly built INF, a stray terminator from a native string -- used to fail the
/// save of the whole completion. The agent's result spool then re-posted the same
/// result on every heartbeat until it gave up, and the outcome (an install that
/// actually finished) was lost for good. Dropping the NUL loses nothing anyone can
/// read; refusing the result loses everything.
/// </para>
/// <para>
/// Strictly more permissive: text with no NUL in it comes back unchanged, the same
/// instance, so nothing that was accepted before changes.
/// </para>
/// </remarks>
internal static partial class UntrustedResultText
{
    /// <summary>The text with every NUL character removed; null stays null.</summary>
    public static string? WithoutNul(string? value) =>
        value is not null && value.Contains('\0') ? value.Replace("\0", string.Empty) : value;

    /// <summary>
    /// A JSON document with every NUL removed: raw NUL characters (never valid JSON
    /// anyway) and every <c>\u0000</c> escape inside a string. Null stays null.
    /// </summary>
    /// <remarks>
    /// An escape is only a NUL when the backslash run in front of <c>u0000</c> is odd:
    /// <c>\\u0000</c> is an escaped backslash followed by the literal text "u0000" and
    /// is left alone. The pattern keeps the escaped-backslash pairs and drops only the
    /// final <c>\u0000</c>.
    /// </remarks>
    public static string? JsonWithoutNul(string? json)
    {
        json = WithoutNul(json);
        return json is not null && json.Contains(@"\u0000", StringComparison.Ordinal)
            ? NulEscape().Replace(json, "$1")
            : json;
    }

    [GeneratedRegex(@"(?<!\\)((?:\\\\)*)\\u0000", RegexOptions.CultureInvariant)]
    private static partial Regex NulEscape();
}
