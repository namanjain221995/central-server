using System.Text;
using System.Text.Json;

namespace EndpointAgent.Core.WorkTime;

/// <summary>
/// The wire format of the activity pipe: the in-session sampler sends one line,
/// <c>{v, idleSeconds}</c>, and the service answers a one-line ack. Presence only —
/// how long since the last input, never what it was.
/// </summary>
/// <remarks>
/// The service reads the request from an ordinary user's process and treats every byte
/// as hostile: a line is refused if it is too long, is not exactly <c>v</c> and
/// <c>idleSeconds</c>, names the wrong version, or carries an out-of-range number.
/// </remarks>
public static class ActivityPipeProtocol
{
    public const int Version = 1;

    /// <summary>A request is a version, a timestamp and one bounded integer; this is generous.</summary>
    public const int MaxRequestBytes = 160;

    public const int MaxResponseBytes = 64;

    /// <summary>A week of idleness is already nonsense; anything beyond is refused.</summary>
    public const int MaxIdleSeconds = 7 * 24 * 60 * 60;

    /// <summary>
    /// The sample carries its own minute (<paramref name="atEpochSeconds"/>, UTC) so a
    /// sample buffered while the service was down keeps its real time when it is finally sent.
    /// </summary>
    public static byte[] EncodeRequest(long atEpochSeconds, int idleSeconds) =>
        Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { v = Version, at = atEpochSeconds, idleSeconds }) + "\n");

    public static bool TryDecodeRequest(ReadOnlySpan<byte> line, out long atEpochSeconds, out int idleSeconds)
    {
        atEpochSeconds = 0;
        idleSeconds = 0;

        if (line.IsEmpty || line.Length > MaxRequestBytes)
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(line.ToArray(), new JsonDocumentOptions { MaxDepth = 2 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            var names = root.EnumerateObject().Select(p => p.Name).ToList();
            if (names.Count != 3 || !names.Contains("v") || !names.Contains("at") || !names.Contains("idleSeconds"))
            {
                return false;
            }

            if (root.GetProperty("v").ValueKind != JsonValueKind.Number
                || !root.GetProperty("v").TryGetInt32(out var version)
                || version != Version)
            {
                return false;
            }

            var at = root.GetProperty("at");
            if (at.ValueKind != JsonValueKind.Number || !at.TryGetInt64(out var epoch) || epoch <= 0)
            {
                return false;
            }

            var idle = root.GetProperty("idleSeconds");
            if (idle.ValueKind != JsonValueKind.Number
                || !idle.TryGetInt32(out var seconds)
                || seconds < 0 || seconds > MaxIdleSeconds)
            {
                return false;
            }

            atEpochSeconds = epoch;
            idleSeconds = seconds;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public static byte[] EncodeAck() =>
        Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { v = Version, ok = true }) + "\n");

    public static bool TryDecodeAck(ReadOnlySpan<byte> line)
    {
        if (line.IsEmpty || line.Length > MaxResponseBytes)
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(line.ToArray(), new JsonDocumentOptions { MaxDepth = 2 });
            var root = document.RootElement;
            return root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("v", out var v) && v.TryGetInt32(out var version) && version == Version
                && root.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
