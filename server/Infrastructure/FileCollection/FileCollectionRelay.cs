using System.Text.Json;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace EndpointPlatform.Infrastructure.FileCollection;

/// <summary>
/// The server side of a file-collection transfer: a bounded, ciphertext-only relay
/// between the device (which uploads through the agent API) and the administrator's
/// browser (which downloads through the admin API).
/// </summary>
/// <remarks>
/// <para>
/// The two APIs are separate processes that share only Postgres and Redis, and the
/// device is behind NAT, so the encrypted archive has to pass through the server. It
/// is never stored: this relay holds, at most, a small window of ciphertext frames in
/// Redis at once (<see cref="WindowFrames"/> × <c>MaxFrameBytes</c>), the agent blocks
/// when that window is full, and each frame is deleted the moment the browser drains
/// it. The server cannot read any of it — the archive is encrypted to a key only the
/// browser holds.
/// </para>
/// <para>
/// Coordination is deliberately simple: integer <c>produced</c>/<c>consumed</c>
/// counters give backpressure, and both sides poll on a short interval rather than
/// using pub/sub (which the platform does not use anywhere). Keys carry a TTL so an
/// abandoned transfer evaporates on its own.
/// </para>
/// </remarks>
public sealed class FileCollectionRelay(
    IConnectionMultiplexer redis,
    ILogger<FileCollectionRelay> logger)
{
    private const string KeyPrefix = "endpointplatform:filecoll:";

    /// <summary>How many frames the agent may run ahead of the browser before it must wait.</summary>
    public const int WindowFrames = 4;

    /// <summary>How long a transfer's control state lives if nothing cleans it up first.</summary>
    public static readonly TimeSpan ControlTtl = TimeSpan.FromHours(4);

    /// <summary>A single buffered frame's TTL — short, because it should be drained within moments.</summary>
    private static readonly TimeSpan FrameTtl = TimeSpan.FromMinutes(10);

    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(75);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IConnectionMultiplexer _redis = redis ?? throw new ArgumentNullException(nameof(redis));
    private readonly ILogger<FileCollectionRelay> _logger = logger;

    private IDatabase Db => _redis.GetDatabase();

    private static string DevKey(string id) => $"{KeyPrefix}{id}:dev";
    private static string StateKey(string id) => $"{KeyPrefix}{id}:state";
    private static string HeaderKey(string id) => $"{KeyPrefix}{id}:hdr";
    private static string ResultKey(string id) => $"{KeyPrefix}{id}:result";
    private static string ProducedKey(string id) => $"{KeyPrefix}{id}:produced";
    private static string ConsumedKey(string id) => $"{KeyPrefix}{id}:consumed";
    private static string FrameKey(string id, long seq) => $"{KeyPrefix}{id}:f:{seq}";

    // ------------------------------------------------------------- admin: start

    /// <summary>
    /// Registers a transfer as pending and binds it to the one device that may feed
    /// it. Called from the admin API when the operator starts a collection, before
    /// the task is queued.
    /// </summary>
    public async Task CreateAsync(string transferId, Guid organizationId, Guid deviceId)
    {
        var db = Db;
        await db.StringSetAsync(DevKey(transferId), $"{organizationId:N}:{deviceId:N}", ControlTtl);
        await db.StringSetAsync(StateKey(transferId), StateValues.Pending, ControlTtl);
        await db.StringSetAsync(ProducedKey(transferId), "-1", ControlTtl);
        await db.StringSetAsync(ConsumedKey(transferId), "-1", ControlTtl);
    }

    /// <summary>Removes a transfer's control state (e.g. when the task could not be queued).</summary>
    public async Task AbandonAsync(string transferId)
    {
        var db = Db;
        await db.KeyDeleteAsync([
            DevKey(transferId), StateKey(transferId), HeaderKey(transferId),
            ResultKey(transferId), ProducedKey(transferId), ConsumedKey(transferId),
        ]);
    }

    /// <summary>
    /// The organization and device a transfer is bound to, or null if it is unknown
    /// or expired. Both the agent endpoints and the browser stream use this to refuse
    /// a transfer that is not theirs.
    /// </summary>
    public async Task<(Guid OrganizationId, Guid DeviceId)?> GetBindingAsync(string transferId)
    {
        var value = await Db.StringGetAsync(DevKey(transferId));
        if (value.IsNullOrEmpty)
        {
            return null;
        }

        var parts = ((string)value!).Split(':', 2);
        if (parts.Length != 2 || !Guid.TryParse(parts[0], out var org) || !Guid.TryParse(parts[1], out var device))
        {
            return null;
        }

        return (org, device);
    }

    // ------------------------------------------------------------- agent: write

    /// <summary>Publishes the agent's key material and marks the transfer active.</summary>
    public async Task SetHeaderAsync(string transferId, string headerJson)
    {
        var db = Db;
        await db.StringSetAsync(HeaderKey(transferId), headerJson, ControlTtl);
        await db.StringSetAsync(StateKey(transferId), StateValues.Active, ControlTtl);
    }

    /// <summary>
    /// Writes one ciphertext frame, waiting for room in the window first.
    /// <see cref="FrameWrite.Backpressure"/> means the window stayed full past
    /// <paramref name="timeout"/> (the agent retries the same frame);
    /// <see cref="FrameWrite.Aborted"/> means the browser went away or the transfer
    /// expired (the agent stops).
    /// </summary>
    public async Task<FrameWrite> TryWriteFrameAsync(
        string transferId, long seq, byte[] frame, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var db = Db;
        var deadline = DateTimeOffset.UtcNow + timeout;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var state = (string?)await db.StringGetAsync(StateKey(transferId));
            if (state is null or StateValues.Aborted or StateValues.Done or StateValues.Failed)
            {
                return FrameWrite.Aborted;
            }

            var consumed = (long)await db.StringGetAsync(ConsumedKey(transferId));
            if (seq - consumed <= WindowFrames)
            {
                await db.StringSetAsync(FrameKey(transferId, seq), frame, FrameTtl);
                // Publish availability only after the bytes are in place, so a reader
                // that sees produced>=seq can always find the frame.
                await db.StringSetAsync(ProducedKey(transferId), seq.ToString(), ControlTtl);
                return FrameWrite.Written;
            }

            if (DateTimeOffset.UtcNow >= deadline)
            {
                _logger.LogDebug(
                    "File-collection {TransferId} backpressure: frame {Seq} waited past the window timeout.",
                    transferId, seq);
                return FrameWrite.Backpressure;
            }

            await Task.Delay(PollInterval, cancellationToken);
        }
    }

    /// <summary>Records the agent's final result and the terminal state.</summary>
    public async Task CompleteAsync(string transferId, string resultJson, bool succeeded)
    {
        var db = Db;
        await db.StringSetAsync(ResultKey(transferId), resultJson, ControlTtl);
        await db.StringSetAsync(
            StateKey(transferId), succeeded ? StateValues.Done : StateValues.Failed, ControlTtl);
    }

    // ----------------------------------------------------------- browser: read

    /// <summary>
    /// Waits for the agent's key material (posted once, before any frame). Returns
    /// null if it does not arrive before <paramref name="timeout"/> or the transfer
    /// ends first.
    /// </summary>
    public async Task<string?> WaitForHeaderAsync(
        string transferId, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var db = Db;
        var deadline = DateTimeOffset.UtcNow + timeout;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var header = (string?)await db.StringGetAsync(HeaderKey(transferId));
            if (header is not null)
            {
                return header;
            }

            var state = (string?)await db.StringGetAsync(StateKey(transferId));
            if (state is null or StateValues.Failed or StateValues.Aborted)
            {
                return null;
            }

            if (DateTimeOffset.UtcNow >= deadline)
            {
                return null;
            }

            await Task.Delay(PollInterval, cancellationToken);
        }
    }

    /// <summary>
    /// The next frame for the browser: <see cref="FrameRead.Data"/> with the bytes,
    /// <see cref="FrameRead.EndOfStream"/> when every produced frame has been drained
    /// and the transfer finished, or <see cref="FrameRead.Failed"/> when it ended
    /// badly or timed out. The caller acknowledges a data frame with
    /// <see cref="AckFrameAsync"/> once it has been written to the response.
    /// </summary>
    public async Task<FrameRead> ReadFrameAsync(
        string transferId, long seq, TimeSpan idleTimeout, CancellationToken cancellationToken)
    {
        var db = Db;
        var deadline = DateTimeOffset.UtcNow + idleTimeout;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var frame = await db.StringGetAsync(FrameKey(transferId, seq));
            if (!frame.IsNullOrEmpty)
            {
                return FrameRead.Data((byte[])frame!);
            }

            var state = (string?)await db.StringGetAsync(StateKey(transferId));
            var produced = (long)await db.StringGetAsync(ProducedKey(transferId));

            if (state is null or StateValues.Aborted)
            {
                return FrameRead.Failed;
            }

            // Terminal and nothing more to hand out: a clean end.
            if (state is StateValues.Done or StateValues.Failed && seq > produced)
            {
                return state == StateValues.Done ? FrameRead.EndOfStream : FrameRead.Failed;
            }

            if (DateTimeOffset.UtcNow >= deadline)
            {
                return FrameRead.Failed;
            }

            await Task.Delay(PollInterval, cancellationToken);
        }
    }

    /// <summary>Marks a frame consumed (freeing a window slot) and deletes its bytes.</summary>
    public async Task AckFrameAsync(string transferId, long seq)
    {
        var db = Db;
        await db.StringSetAsync(ConsumedKey(transferId), seq.ToString(), ControlTtl);
        await db.KeyDeleteAsync(FrameKey(transferId, seq));
    }

    /// <summary>The agent's final result, or null if none was recorded.</summary>
    public async Task<string?> GetResultAsync(string transferId) =>
        (string?)await Db.StringGetAsync(ResultKey(transferId));

    /// <summary>Signals that the browser has gone away, so the agent stops uploading.</summary>
    public async Task AbortAsync(string transferId) =>
        await Db.StringSetAsync(StateKey(transferId), StateValues.Aborted, ControlTtl);

    private static class StateValues
    {
        public const string Pending = "pending";
        public const string Active = "active";
        public const string Done = "done";
        public const string Failed = "failed";
        public const string Aborted = "aborted";
    }
}

/// <summary>The outcome of an agent trying to write one frame into the relay.</summary>
public enum FrameWrite
{
    /// <summary>The frame was accepted into the window.</summary>
    Written,

    /// <summary>The window stayed full; the agent should retry the same frame shortly.</summary>
    Backpressure,

    /// <summary>The transfer ended (browser gone or expired); the agent should stop.</summary>
    Aborted,
}

/// <summary>The outcome of asking the relay for the next browser-bound frame.</summary>
public readonly struct FrameRead
{
    public enum ReadKind
    {
        Data,
        EndOfStream,
        Failed,
    }

    private FrameRead(ReadKind kind, byte[]? bytes)
    {
        Kind = kind;
        Bytes = bytes;
    }

    public ReadKind Kind { get; }

    public byte[]? Bytes { get; }

    public static FrameRead Data(byte[] bytes) => new(ReadKind.Data, bytes);

    public static readonly FrameRead EndOfStream = new(ReadKind.EndOfStream, null);

    public static readonly FrameRead Failed = new(ReadKind.Failed, null);
}
