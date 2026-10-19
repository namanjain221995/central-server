using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using EndpointAgent.Core.ScreenLock;
using EndpointAgent.Windows.ScreenLock;
using Microsoft.Extensions.Logging.Abstractions;

namespace EndpointAgent.Windows.Tests;

/// <summary>
/// The screen-lock pipe's side of the service-to-session boundary, on real Windows
/// pipes with a fake handler behind them. The client in every test is this test
/// process -- an interactive user, which is exactly what the lock overlay is -- so
/// what the tests prove is what the overlay can do: ask what to show, submit a
/// code, and nothing more. The verifier is never on the wire; the handler stands
/// in for the SYSTEM-side verification.
/// </summary>
public sealed class ScreenLockPipeTests
{
    private static string UniquePipe() => $"EndpointPlatformAgent.ScreenLock.Test.{Guid.NewGuid():N}";

    private static readonly byte[] ShowRequest = ScreenLockPipeProtocol.EncodeRequest(LockPipeRequest.Show);

    private static byte[] Submit(string code) => ScreenLockPipeProtocol.EncodeRequest(LockPipeRequest.SubmitCode(code));

    private static ScreenLockPipeServer Server(
        string name, IScreenLockPipeHandler handler, bool asServiceIdentity = true) =>
        new(name, handler, NullLogger<ScreenLockPipeServer>.Instance,
            serviceIdentity: asServiceIdentity ? WindowsIdentity.GetCurrent().User : null);

    private static async Task<byte[]?> ExchangeAsync(string name, byte[] request)
    {
        await using var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
        await client.ConnectAsync(5000);
        await TryWriteAsync(client, request);
        return await ReadLineAsync(client);
    }

    private static async Task TryWriteAsync(Stream stream, byte[] bytes)
    {
        try
        {
            await stream.WriteAsync(bytes);
            await stream.FlushAsync();
        }
        catch (IOException)
        {
        }
    }

    private static async Task<byte[]?> ReadLineAsync(Stream stream)
    {
        var buffer = new byte[ScreenLockPipeProtocol.MaxResponseBytes + 1];
        var filled = 0;
        while (filled < buffer.Length)
        {
            int read;
            try
            {
                read = await stream.ReadAsync(buffer.AsMemory(filled)).AsTask().WaitAsync(TimeSpan.FromSeconds(10));
            }
            catch (IOException)
            {
                return null;
            }

            if (read == 0)
            {
                return null;
            }

            var newline = Array.IndexOf(buffer, (byte)'\n', filled, read);
            filled += read;
            if (newline >= 0)
            {
                return buffer[..newline];
            }
        }

        return null;
    }

    private static LockPipeResponse Decode(byte[]? line)
    {
        line.ShouldNotBeNull("the server must answer");
        ScreenLockPipeProtocol.TryDecodeResponse(line, out var response).ShouldBeTrue("the answer must be one the protocol accepts");
        return response!;
    }

    // --------------------------------------------------------------- access

    [Fact]
    public void The_pipe_grants_SYSTEM_full_control_and_interactive_users_read_and_write_and_nothing_else()
    {
        var security = ScreenLockPipeServer.BuildSecurity();
        var rules = security
            .GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier))
            .Cast<PipeAccessRule>()
            .ToList();

        rules.Count.ShouldBe(2);
        rules.ShouldAllBe(r => r.AccessControlType == AccessControlType.Allow);

        var system = rules.Single(r => r.IdentityReference.Value == new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null).Value);
        system.PipeAccessRights.ShouldBe(PipeAccessRights.FullControl);

        var interactive = rules.Single(r => r.IdentityReference.Value == new SecurityIdentifier(WellKnownSidType.InteractiveSid, null).Value);
        interactive.PipeAccessRights.ShouldBe(PipeAccessRights.ReadWrite | PipeAccessRights.Synchronize);
        (interactive.PipeAccessRights & PipeAccessRights.CreateNewInstance).ShouldBe((PipeAccessRights)0,
            "an interactive user must not be able to add an instance of the service's own pipe");
        (interactive.PipeAccessRights & PipeAccessRights.ChangePermissions).ShouldBe((PipeAccessRights)0);
        (interactive.PipeAccessRights & PipeAccessRights.TakeOwnership).ShouldBe((PipeAccessRights)0);

        security.AreAccessRulesProtected.ShouldBeTrue("no inherited entries may widen access");
    }

    // -------------------------------------------------------------- the ops

    [Fact]
    public async Task A_show_request_returns_what_to_display_and_that_the_device_is_locked()
    {
        var name = UniquePipe();
        var handler = new FakeHandler { Locked = true, Content = new LockScreenContent("Locked by IT.", "ext. 4000") };
        var server = Server(name, handler);
        await server.StartAsync(CancellationToken.None);

        try
        {
            var response = Decode(await ExchangeAsync(name, ShowRequest));

            response.Locked.ShouldBe(true);
            response.Message.ShouldBe("Locked by IT.");
            response.Contact.ShouldBe("ext. 4000");
            handler.VerifyCalls.ShouldBe(0, "a show never touches the verifier");
        }
        finally
        {
            await server.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task A_correct_code_is_answered_unlocked()
    {
        var name = UniquePipe();
        var handler = new FakeHandler { Unlocked = true };
        var server = Server(name, handler);
        await server.StartAsync(CancellationToken.None);

        try
        {
            Decode(await ExchangeAsync(name, Submit("the-code"))).Unlocked.ShouldBe(true);
            handler.LastCode.ShouldBe("the-code", "the service verifies the code the overlay sent");
        }
        finally
        {
            await server.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task A_wrong_code_is_answered_not_unlocked()
    {
        var name = UniquePipe();
        var handler = new FakeHandler { Unlocked = false };
        var server = Server(name, handler);
        await server.StartAsync(CancellationToken.None);

        try
        {
            Decode(await ExchangeAsync(name, Submit("nope"))).Unlocked.ShouldBe(false);
        }
        finally
        {
            await server.StopAsync(CancellationToken.None);
        }
    }

    // ------------------------------------------------------------ hardening

    [Fact]
    public async Task An_overlong_request_is_answered_bad_request_without_reaching_the_handler()
    {
        var name = UniquePipe();
        var handler = new FakeHandler();
        var server = Server(name, handler);
        await server.StartAsync(CancellationToken.None);

        try
        {
            var flood = new byte[ScreenLockPipeProtocol.MaxRequestBytes * 2];
            Array.Fill(flood, (byte)'x');
            flood[^1] = (byte)'\n';

            Decode(await ExchangeAsync(name, flood)).Error.ShouldBe(LockPipeError.BadRequest);
            handler.VerifyCalls.ShouldBe(0);
        }
        finally
        {
            await server.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task A_line_that_is_not_a_request_is_answered_bad_request()
    {
        var name = UniquePipe();
        var server = Server(name, new FakeHandler());
        await server.StartAsync(CancellationToken.None);

        try
        {
            Decode(await ExchangeAsync(name, Encoding.UTF8.GetBytes("{\"v\":1,\"op\":\"run\"}\n"))).Error.ShouldBe(LockPipeError.BadRequest);
        }
        finally
        {
            await server.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task A_second_request_on_the_same_connection_is_not_served()
    {
        var name = UniquePipe();
        var handler = new FakeHandler { Locked = true, Content = new LockScreenContent("Locked.", null) };
        var server = Server(name, handler);
        await server.StartAsync(CancellationToken.None);

        try
        {
            await using var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
            await client.ConnectAsync(5000);
            await TryWriteAsync(client, ShowRequest);
            Decode(await ReadLineAsync(client)).Locked.ShouldBe(true);

            await TryWriteAsync(client, ShowRequest);
            (await ReadLineAsync(client)).ShouldBeNull("the connection is over after one request");
        }
        finally
        {
            await server.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Wrong_codes_are_rate_limited_before_the_verifier_is_touched()
    {
        var name = UniquePipe();
        var handler = new FakeHandler { Unlocked = false };
        var server = Server(name, handler);
        await server.StartAsync(CancellationToken.None);

        try
        {
            // The allowance is spent on wrong codes...
            for (var i = 0; i < ScreenLockPipeServer.MaxSubmitsPerMinute; i++)
            {
                Decode(await ExchangeAsync(name, Submit($"guess-{i}"))).Unlocked.ShouldBe(false);
            }

            // ...and the next is refused without the verifier being asked.
            Decode(await ExchangeAsync(name, Submit("one-too-many"))).Error.ShouldBe(LockPipeError.TooManyRequests);
            handler.VerifyCalls.ShouldBe(ScreenLockPipeServer.MaxSubmitsPerMinute,
                "a flood of codes cannot brute-force the lock at machine speed");
        }
        finally
        {
            await server.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Stopping_closes_the_listening_instance_so_nothing_answers_on_the_name()
    {
        var name = UniquePipe();
        var server = Server(name, new FakeHandler());
        await server.StartAsync(CancellationToken.None);

        await server.StopAsync(CancellationToken.None);

        server.InstanceCount.ShouldBe(0);
        using var client = new NamedPipeClientStream(".", name, PipeDirection.InOut);
        Should.Throw<TimeoutException>(() => client.Connect(300), "the name must be gone with the server");
    }

    // ---------------------------------------------------------------- fake

    private sealed class FakeHandler : IScreenLockPipeHandler
    {
        private readonly Lock _gate = new();
        private int _verifyCalls;

        public bool Locked { get; set; }
        public LockScreenContent? Content { get; set; }
        public bool Unlocked { get; set; }
        public string? LastCode { get; private set; }

        public int VerifyCalls
        {
            get
            {
                lock (_gate)
                {
                    return _verifyCalls;
                }
            }
        }

        public bool CurrentlyLocked => Locked;

        public LockScreenContent? CurrentContent => Content;

        public Task<bool> VerifyAndReleaseAsync(string code, CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                _verifyCalls++;
                LastCode = code;
            }

            return Task.FromResult(Unlocked);
        }
    }
}
