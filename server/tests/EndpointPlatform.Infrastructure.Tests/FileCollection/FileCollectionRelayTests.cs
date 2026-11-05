using EndpointPlatform.Infrastructure.FileCollection;
using Microsoft.Extensions.Logging.Abstractions;
using StackExchange.Redis;

namespace EndpointPlatform.Infrastructure.Tests.FileCollection;

/// <summary>
/// The file-collection relay against a real Redis: binding, the header handoff, the
/// bounded frame window with backpressure, clean completion, and abort.
/// </summary>
/// <remarks>
/// Each test uses a fresh random transfer id, so the fixed key prefix never collides
/// between tests or runs.
/// </remarks>
public sealed class FileCollectionRelayTests
{
    private static readonly Lazy<IConnectionMultiplexer> Redis = new(() =>
        ConnectionMultiplexer.Connect(TestRedis.ConnectionString + ",abortConnect=false"));

    private static readonly TimeSpan Short = TimeSpan.FromMilliseconds(400);

    private static FileCollectionRelay NewRelay() =>
        new(Redis.Value, NullLogger<FileCollectionRelay>.Instance);

    private static string NewTransferId() => Guid.NewGuid().ToString("N");

    [Fact]
    public async Task A_created_transfer_is_bound_to_its_device()
    {
        var relay = NewRelay();
        var id = NewTransferId();
        var org = Guid.NewGuid();
        var device = Guid.NewGuid();

        (await relay.GetBindingAsync(id)).ShouldBeNull();

        await relay.CreateAsync(id, org, device);

        var binding = await relay.GetBindingAsync(id);
        binding.ShouldNotBeNull();
        binding!.Value.OrganizationId.ShouldBe(org);
        binding.Value.DeviceId.ShouldBe(device);

        await relay.AbandonAsync(id);
        (await relay.GetBindingAsync(id)).ShouldBeNull();
    }

    [Fact]
    public async Task The_header_is_handed_from_the_agent_to_the_browser()
    {
        var relay = NewRelay();
        var id = NewTransferId();
        await relay.CreateAsync(id, Guid.NewGuid(), Guid.NewGuid());

        await relay.SetHeaderAsync(id, """{"ephemeralPublicKey":"K","salt":"S"}""");

        var header = await relay.WaitForHeaderAsync(id, Short, CancellationToken.None);
        header.ShouldBe("""{"ephemeralPublicKey":"K","salt":"S"}""");
    }

    [Fact]
    public async Task A_frame_written_by_the_agent_is_read_then_completion_ends_the_stream()
    {
        var relay = NewRelay();
        var id = NewTransferId();
        await relay.CreateAsync(id, Guid.NewGuid(), Guid.NewGuid());
        await relay.SetHeaderAsync(id, "{}");

        (await relay.TryWriteFrameAsync(id, 0, [1, 2, 3], Short, CancellationToken.None))
            .ShouldBe(FrameWrite.Written);

        var read = await relay.ReadFrameAsync(id, 0, Short, CancellationToken.None);
        read.Kind.ShouldBe(FrameRead.ReadKind.Data);
        read.Bytes.ShouldBe(new byte[] { 1, 2, 3 });
        await relay.AckFrameAsync(id, 0);

        await relay.CompleteAsync(id, """{"succeeded":true}""", succeeded: true);

        var end = await relay.ReadFrameAsync(id, 1, Short, CancellationToken.None);
        end.Kind.ShouldBe(FrameRead.ReadKind.EndOfStream);
        (await relay.GetResultAsync(id)).ShouldBe("""{"succeeded":true}""");
    }

    [Fact]
    public async Task The_window_bounds_how_far_the_agent_runs_ahead()
    {
        var relay = NewRelay();
        var id = NewTransferId();
        await relay.CreateAsync(id, Guid.NewGuid(), Guid.NewGuid());
        await relay.SetHeaderAsync(id, "{}");

        // consumed starts at -1, so with WindowFrames=4 the agent may place seq 0..3.
        for (long seq = 0; seq <= 3; seq++)
        {
            (await relay.TryWriteFrameAsync(id, seq, [(byte)seq], Short, CancellationToken.None))
                .ShouldBe(FrameWrite.Written);
        }

        // seq 4 is one past the window: it must wait, then report backpressure.
        (await relay.TryWriteFrameAsync(id, 4, [4], Short, CancellationToken.None))
            .ShouldBe(FrameWrite.Backpressure);

        // Draining frame 0 frees a slot, so seq 4 now fits.
        var zero = await relay.ReadFrameAsync(id, 0, Short, CancellationToken.None);
        zero.Kind.ShouldBe(FrameRead.ReadKind.Data);
        await relay.AckFrameAsync(id, 0);

        (await relay.TryWriteFrameAsync(id, 4, [4], Short, CancellationToken.None))
            .ShouldBe(FrameWrite.Written);
    }

    [Fact]
    public async Task Aborting_stops_the_agent_and_fails_the_reader()
    {
        var relay = NewRelay();
        var id = NewTransferId();
        await relay.CreateAsync(id, Guid.NewGuid(), Guid.NewGuid());
        await relay.SetHeaderAsync(id, "{}");

        await relay.AbortAsync(id);

        (await relay.TryWriteFrameAsync(id, 0, [1], Short, CancellationToken.None))
            .ShouldBe(FrameWrite.Aborted);

        var read = await relay.ReadFrameAsync(id, 0, Short, CancellationToken.None);
        read.Kind.ShouldBe(FrameRead.ReadKind.Failed);
    }

    [Fact]
    public async Task A_failed_completion_reads_as_failed()
    {
        var relay = NewRelay();
        var id = NewTransferId();
        await relay.CreateAsync(id, Guid.NewGuid(), Guid.NewGuid());
        await relay.SetHeaderAsync(id, "{}");

        await relay.CompleteAsync(id, """{"succeeded":false}""", succeeded: false);

        var read = await relay.ReadFrameAsync(id, 0, Short, CancellationToken.None);
        read.Kind.ShouldBe(FrameRead.ReadKind.Failed);
    }
}
