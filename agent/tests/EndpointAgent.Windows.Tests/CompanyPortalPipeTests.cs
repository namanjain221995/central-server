using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using EndpointAgent.Core.CompanyPortal;
using EndpointAgent.Windows.CompanyPortal;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace EndpointAgent.Windows.Tests;

/// <summary>
/// The portal's side of the service-to-session boundary, exercised on real
/// Windows pipes with a fake handler behind them.
/// </summary>
/// <remarks>
/// <para>
/// The client in every test is this test process -- an interactive user, which is
/// exactly what a portal is -- so what the tests prove is what a portal can do:
/// connect, send one line, read one answer, and nothing more. The session and
/// user the server reports to the handler are read from the real pipe driver and
/// the real session, not supplied by the client.
/// </para>
/// <para>
/// Every pipe name here is unique to the test, so nothing collides with an agent
/// installed on the machine running the suite. Nothing here reaches a server.
/// </para>
/// </remarks>
public sealed class CompanyPortalPipeTests
{
    private static string UniquePipe() => $"EndpointPlatformAgent.CompanyPortal.Test.{Guid.NewGuid():N}";

    private static readonly PortalApp App = new(
        Guid.Parse("7d2f9c1e-4b6a-4c3d-9e8f-0a1b2c3d4e5f"), "Contoso App", "Does things", "Tools", true, "2.1.0", "Contoso",
        new DateTimeOffset(2026, 10, 1, 12, 30, 0, TimeSpan.Zero), "Available");

    private static readonly byte[] ListRequest = CompanyPortalProtocol.EncodeRequest(PortalRequest.ListApps);

    /// <summary>
    /// A server on a private name. By default this test user is named as the
    /// service identity, because serving connections concurrently needs more than
    /// one instance and only the full-control principal may create those; the one
    /// test that proves what an interactive user may do keeps the real default.
    /// </summary>
    private static CompanyPortalPipeServer Server(
        string name, IPortalRequestHandler handler, bool asServiceIdentity = true,
        ILogger<CompanyPortalPipeServer>? logger = null, TimeSpan? squatRetry = null) =>
        new(name, handler, logger ?? NullLogger<CompanyPortalPipeServer>.Instance,
            serviceIdentity: asServiceIdentity ? WindowsIdentity.GetCurrent().User : null, squatRetry: squatRetry);

    /// <summary>Connects as this test's (interactive) user, sends the bytes as given, and reads one answer line, or null when the server hung up first.</summary>
    private static async Task<byte[]?> ExchangeAsync(string name, byte[] request)
    {
        await using var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
        await client.ConnectAsync(5000);
        await TryWriteAsync(client, request);
        return await ReadLineAsync(client);
    }

    /// <summary>A server that refuses what it was sent may already have hung up by the time the write lands.</summary>
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
        var buffer = new byte[CompanyPortalProtocol.MaxResponseBytes + 1];
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

    private static PortalResponse Decode(byte[]? line)
    {
        line.ShouldNotBeNull("the server must answer");
        CompanyPortalProtocol.TryDecodeResponse(line, out var response).ShouldBeTrue("the answer must be one the protocol accepts");
        return response!;
    }

    private static async Task<bool> EventuallyAsync(Func<bool> condition, int milliseconds = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(milliseconds);
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(25);
        }

        return condition();
    }

    // --------------------------------------------------------------- access

    [Fact]
    public void The_pipe_grants_SYSTEM_full_control_and_interactive_users_read_and_write_and_nothing_else()
    {
        var security = CompanyPortalPipeServer.BuildSecurity();
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
            "an interactive user must not be able to add an instance of the service's own pipe and answer other sessions");
        (interactive.PipeAccessRights & PipeAccessRights.ChangePermissions).ShouldBe((PipeAccessRights)0);
        (interactive.PipeAccessRights & PipeAccessRights.TakeOwnership).ShouldBe((PipeAccessRights)0);
        (interactive.PipeAccessRights & PipeAccessRights.Delete).ShouldBe((PipeAccessRights)0);

        security.AreAccessRulesProtected.ShouldBeTrue("no inherited entries may widen access");
    }

    /// <summary>
    /// Enforced, not only declared. With the real service identity on the pipe,
    /// this test user is an ordinary interactive caller, and the read-and-write
    /// entry is what lets it connect, send a line and read the answer.
    /// </summary>
    [Fact]
    public async Task An_interactive_user_can_send_a_list_request_and_receive_the_handlers_answer()
    {
        var name = UniquePipe();
        var handler = new FakeHandler();
        var server = Server(name, handler, asServiceIdentity: false);
        await server.StartAsync(CancellationToken.None);

        try
        {
            var response = Decode(await ExchangeAsync(name, ListRequest));

            response.Ok.ShouldBeTrue();
            response.Apps.ShouldNotBeNull().ShouldBe([App]);
            handler.Calls.ShouldHaveSingleItem().Request.ShouldBe(PortalRequest.ListApps);
        }
        finally
        {
            await server.StopAsync(CancellationToken.None);
        }
    }

    // ------------------------------------------------------------- identity

    /// <summary>
    /// What the handler is told about the caller comes from Windows, not from the
    /// line the client sent: the session the pipe driver reports, which here is
    /// this process's own, and the user Windows has signed in to it.
    /// </summary>
    [Fact]
    public async Task The_handler_is_told_the_callers_session_and_the_sessions_user_not_what_the_client_says()
    {
        var name = UniquePipe();
        var handler = new FakeHandler();
        var server = Server(name, handler);
        await server.StartAsync(CancellationToken.None);

        try
        {
            Decode(await ExchangeAsync(name, CompanyPortalProtocol.EncodeRequest(PortalRequest.InstallOf(App.Id)))).Ok.ShouldBeTrue();

            var call = handler.Calls.ShouldHaveSingleItem();
            call.CallerKey.ShouldStartWith("session:");
            call.CallerKey.ShouldBe($"session:{System.Diagnostics.Process.GetCurrentProcess().SessionId}");
            (call.RequestedBy is null || call.RequestedBy.Contains('\\')).ShouldBeTrue(
                $"the user is DOMAIN\\user as Windows reports it, or nothing; got '{call.RequestedBy}'");
        }
        finally
        {
            await server.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public void The_sessions_user_is_this_test_user_in_domain_backslash_user_form()
    {
        var session = (uint)System.Diagnostics.Process.GetCurrentProcess().SessionId;

        var name = SessionUser.NameOf(session);

        name.ShouldNotBeNull("this test runs in a signed-in session");
        var parts = name.Split('\\');
        parts.Length.ShouldBe(2);
        parts[0].ShouldNotBeEmpty();
        parts[1].ShouldBe(Environment.UserName, StringCompareShould.IgnoreCase);
    }

    [Theory]
    [InlineData(0u, "session 0 is services only")]
    [InlineData(0x7FFFFFF0u, "no such session")]
    [InlineData(uint.MaxValue, "WTS_CURRENT_SESSION, which would answer with the caller's own session")]
    [InlineData(uint.MaxValue - 1, "WTS_ANY_SESSION")]
    public void A_session_nobody_is_signed_in_to_has_no_user(uint session, string because) =>
        SessionUser.NameOf(session).ShouldBeNull(because);

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
            var flood = new byte[CompanyPortalProtocol.MaxRequestBytes * 4];
            Array.Fill(flood, (byte)'x');
            flood[^1] = (byte)'\n';

            var response = Decode(await ExchangeAsync(name, flood));

            response.Error.ShouldBe(PortalError.BadRequest);
            handler.Calls.ShouldBeEmpty();
        }
        finally
        {
            await server.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task A_line_that_is_not_a_request_is_answered_bad_request_without_reaching_the_handler()
    {
        var name = UniquePipe();
        var handler = new FakeHandler();
        var server = Server(name, handler);
        await server.StartAsync(CancellationToken.None);

        try
        {
            var response = Decode(await ExchangeAsync(name, Encoding.UTF8.GetBytes("{\"v\":1,\"op\":\"run\",\"path\":\"C:\\\\x.exe\"}\n")));

            response.Error.ShouldBe(PortalError.BadRequest);
            handler.Calls.ShouldBeEmpty();
        }
        finally
        {
            await server.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task A_client_that_hangs_up_without_finishing_its_line_reaches_no_handler_and_frees_the_instance()
    {
        var name = UniquePipe();
        var handler = new FakeHandler();
        var server = Server(name, handler);
        await server.StartAsync(CancellationToken.None);

        try
        {
            await using (var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous))
            {
                await client.ConnectAsync(5000);
                await TryWriteAsync(client, Encoding.UTF8.GetBytes("{\"v\":1,"));
            }

            (await EventuallyAsync(() => server.InstanceCount == 1)).ShouldBeTrue("only the listening instance should remain");
            handler.Calls.ShouldBeEmpty();
        }
        finally
        {
            await server.StopAsync(CancellationToken.None);
        }
    }

    /// <summary>
    /// One request per connection. After the answer the server has hung up, so a
    /// second line on the same connection goes nowhere and the handler never sees it.
    /// </summary>
    [Fact]
    public async Task A_second_request_on_the_same_connection_is_not_served()
    {
        var name = UniquePipe();
        var handler = new FakeHandler();
        var server = Server(name, handler);
        await server.StartAsync(CancellationToken.None);

        try
        {
            await using var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
            await client.ConnectAsync(5000);
            await TryWriteAsync(client, ListRequest);
            Decode(await ReadLineAsync(client)).Ok.ShouldBeTrue();

            (await EventuallyAsync(() => server.InstanceCount == 1)).ShouldBeTrue("the served instance must be closed");

            await TryWriteAsync(client, ListRequest);
            (await ReadLineAsync(client)).ShouldBeNull("the connection is over");

            await Task.Delay(200);
            handler.Calls.Count.ShouldBe(1);
        }
        finally
        {
            await server.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task A_handler_that_throws_is_answered_server_unavailable_and_the_server_keeps_serving()
    {
        var name = UniquePipe();
        var handler = new FakeHandler { Throws = new InvalidOperationException("boom") };
        var server = Server(name, handler);
        await server.StartAsync(CancellationToken.None);

        try
        {
            Decode(await ExchangeAsync(name, ListRequest)).Error.ShouldBe(PortalError.ServerUnavailable);

            handler.Throws = null;
            Decode(await ExchangeAsync(name, ListRequest)).Ok.ShouldBeTrue("the next connection must be served as if nothing happened");
        }
        finally
        {
            await server.StopAsync(CancellationToken.None);
        }
    }

    // ------------------------------------------------------------- delivery

    /// <summary>
    /// The largest answer the protocol allows arrives whole, although the server
    /// closes its end as soon as the write is done: what is written before the
    /// close is the client's to read.
    /// </summary>
    [Fact]
    public async Task A_catalog_near_the_response_bound_arrives_whole()
    {
        var name = UniquePipe();
        var apps = Enumerable.Range(0, 300)
            .Select(i => App with { Id = Guid.NewGuid(), DisplayName = $"App {i}", Description = new string((char)('a' + i % 26), 1000) })
            .ToList();
        CompanyPortalProtocol.EncodeResponse(new PortalResponse(Apps: apps)).Length
            .ShouldBeInRange(CompanyPortalProtocol.MaxResponseBytes / 2, CompanyPortalProtocol.MaxResponseBytes);

        var handler = new FakeHandler { Response = new PortalResponse(Apps: apps) };
        var server = Server(name, handler);
        await server.StartAsync(CancellationToken.None);

        try
        {
            var response = Decode(await ExchangeAsync(name, ListRequest));

            response.Apps.ShouldNotBeNull().Count.ShouldBe(300);
            response.Apps.ShouldBe(apps);
        }
        finally
        {
            await server.StopAsync(CancellationToken.None);
        }
    }

    /// <summary>
    /// A catalog the pipe cannot carry whole is cut from the end rather than sent
    /// as a line the portal would refuse: the user sees the first apps, and the
    /// log says how many are missing.
    /// </summary>
    [Fact]
    public async Task A_catalog_too_large_for_the_pipe_is_cut_from_the_end_and_still_arrives()
    {
        var name = UniquePipe();
        var apps = Enumerable.Range(0, 2000)
            .Select(i => App with { Id = Guid.NewGuid(), DisplayName = $"App {i:D4}", Description = new string((char)('a' + i % 26), 1000) })
            .ToList();
        CompanyPortalProtocol.EncodeResponse(new PortalResponse(Apps: apps)).Length
            .ShouldBeGreaterThan(CompanyPortalProtocol.MaxResponseBytes * 2, "the fixture must be well past the bound");

        var handler = new FakeHandler { Response = new PortalResponse(Apps: apps) };
        var logger = new RecordingLogger();
        var server = Server(name, handler, logger: logger);
        await server.StartAsync(CancellationToken.None);

        try
        {
            var response = Decode(await ExchangeAsync(name, ListRequest));

            var received = response.Apps.ShouldNotBeNull();
            received.Count.ShouldBeInRange(1, apps.Count - 1);
            received.ShouldBe(apps.Take(received.Count), "what arrives is the start of the list, in order");
            logger.Has(LogLevel.Warning, $"{apps.Count - received.Count} of {apps.Count} apps dropped").ShouldBeTrue("the loss must be logged");
        }
        finally
        {
            await server.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public void A_catalog_is_cut_to_the_longest_prefix_that_fits_and_nothing_else_is_ever_cut()
    {
        var apps = Enumerable.Range(0, 700)
            .Select(i => App with { Id = Guid.NewGuid(), Description = new string('d', 1000) })
            .ToList();

        var line = CompanyPortalPipeServer.EncodeWithinBound(new PortalResponse(Apps: apps), out var dropped);

        dropped.ShouldBeInRange(1, apps.Count - 1);
        line.Length.ShouldBeLessThanOrEqualTo(CompanyPortalProtocol.MaxResponseBytes);
        CompanyPortalProtocol.EncodeResponse(new PortalResponse(Apps: apps.Take(apps.Count - dropped + 1).ToList())).Length
            .ShouldBeGreaterThan(CompanyPortalProtocol.MaxResponseBytes, "one more app would not have fitted");

        CompanyPortalPipeServer.EncodeWithinBound(new PortalResponse(Apps: [App]), out dropped).ShouldBe(CompanyPortalProtocol.EncodeResponse(new PortalResponse(Apps: [App])));
        dropped.ShouldBe(0);
        CompanyPortalPipeServer.EncodeWithinBound(new PortalResponse(Icon: new byte[CompanyPortalProtocol.MaxIconBytes]), out dropped).Length
            .ShouldBeLessThanOrEqualTo(CompanyPortalProtocol.MaxResponseBytes);
        dropped.ShouldBe(0);
    }

    [Fact]
    public async Task Connections_are_served_concurrently()
    {
        var name = UniquePipe();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new FakeHandler { Before = () => gate.Task };
        var server = Server(name, handler);
        await server.StartAsync(CancellationToken.None);

        try
        {
            var first = ExchangeAsync(name, ListRequest);
            var second = ExchangeAsync(name, ListRequest);

            (await EventuallyAsync(() => handler.Calls.Count == 2)).ShouldBeTrue("both connections must be in the handler at once");
            gate.SetResult();

            Decode(await first).Ok.ShouldBeTrue();
            Decode(await second).Ok.ShouldBeTrue();
        }
        finally
        {
            gate.TrySetResult();
            await server.StopAsync(CancellationToken.None);
        }
    }

    // ------------------------------------------------------------- the name

    /// <summary>
    /// Another process holds the name. The service does not create an instance
    /// beside it, says so, and keeps trying; once the name is free it is claimed
    /// and portals are served.
    /// </summary>
    [Fact]
    public async Task A_squatter_on_the_name_is_not_joined_and_the_name_is_claimed_once_it_is_released()
    {
        var name = UniquePipe();
        var squatter = new NamedPipeServerStream(name, PipeDirection.InOut, 4, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        var handler = new FakeHandler();
        var logger = new RecordingLogger();
        var server = Server(name, handler, logger: logger, squatRetry: TimeSpan.FromMilliseconds(100));
        await server.StartAsync(CancellationToken.None);

        try
        {
            (await EventuallyAsync(() => logger.Has(LogLevel.Error, "already in use"))).ShouldBeTrue("the service must say the name is taken");
            server.InstanceCount.ShouldBe(0, "the service must not have created an instance beside the squatter");

            // A portal connecting now reaches the squatter, not the service.
            var squatted = squatter.WaitForConnectionAsync();
            await using (var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous))
            {
                await client.ConnectAsync(5000);
                await squatted.WaitAsync(TimeSpan.FromSeconds(5));
            }

            handler.Calls.ShouldBeEmpty();

            await squatter.DisposeAsync();

            var response = Decode(await ExchangeAsync(name, ListRequest));
            response.Ok.ShouldBeTrue("once the squatter is gone the service must claim the name and answer");
        }
        finally
        {
            await server.StopAsync(CancellationToken.None);
            await squatter.DisposeAsync();
        }
    }

    // ------------------------------------------------------------- stopping

    [Fact]
    public async Task Stopping_closes_the_listening_instance_so_nothing_answers_on_the_name()
    {
        var name = UniquePipe();
        var server = Server(name, new FakeHandler());
        await server.StartAsync(CancellationToken.None);
        (await EventuallyAsync(() => server.InstanceCount == 1)).ShouldBeTrue("the server must be listening");

        await server.StopAsync(CancellationToken.None);

        server.InstanceCount.ShouldBe(0);
        using var client = new NamedPipeClientStream(".", name, PipeDirection.InOut);
        Should.Throw<TimeoutException>(() => client.Connect(300), "the name must be gone with the server");
    }

    /// <summary>
    /// A connection in the middle of a server call does not hold the stop: the
    /// installer that stopped the service must not wait on a portal.
    /// </summary>
    [Fact]
    public async Task Stopping_does_not_wait_on_a_connection_being_served()
    {
        var name = UniquePipe();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new FakeHandler { Before = () => gate.Task };
        var server = Server(name, handler);
        await server.StartAsync(CancellationToken.None);

        var exchange = ExchangeAsync(name, ListRequest);
        (await EventuallyAsync(() => handler.Calls.Count == 1)).ShouldBeTrue("the connection must be in the handler");

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        await server.StopAsync(CancellationToken.None);
        stopwatch.Elapsed.ShouldBeLessThan(CompanyPortalPipeServer.StopDrain + TimeSpan.FromSeconds(1),
            "a handler that ignores the cancellation is left behind once the drain bound is up");

        gate.SetResult();
        (await exchange.WaitAsync(TimeSpan.FromSeconds(10))).ShouldBeNull("the connection was cut by the stop, not answered");
    }

    /// <summary>
    /// The host disposes the service after stopping it, while a connection a
    /// stubborn handler is still holding has yet to run its clean-up. That
    /// clean-up must finish quietly -- not fault on the disposed slot counter
    /// and surface as an unobserved exception at shutdown.
    /// </summary>
    [Fact]
    public async Task Disposing_the_server_with_a_connection_still_in_the_handler_lets_it_finish_cleanly()
    {
        var name = UniquePipe();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new FakeHandler { Before = () => gate.Task };
        var server = Server(name, handler);
        await server.StartAsync(CancellationToken.None);

        var exchange = ExchangeAsync(name, ListRequest);
        (await EventuallyAsync(() => handler.Calls.Count == 1)).ShouldBeTrue("the connection must be in the handler");

        await server.StopAsync(CancellationToken.None);
        server.InFlightCount.ShouldBe(1, "the handler is still holding the connection");

        server.Dispose();
        gate.SetResult();

        (await EventuallyAsync(() => server.InFlightCount == 0)).ShouldBeTrue("the connection's clean-up must run to its end");
        (await exchange.WaitAsync(TimeSpan.FromSeconds(10))).ShouldBeNull();
    }

    // ---------------------------------------------------------------- fakes

    private sealed class FakeHandler : IPortalRequestHandler
    {
        private readonly Lock _gate = new();
        private readonly List<Call> _calls = [];

        public PortalResponse Response { get; set; } = new(Apps: [App]);

        /// <summary>Awaited inside the handler, so a test can hold a connection there.</summary>
        public Func<Task>? Before { get; set; }

        public Exception? Throws { get; set; }

        public IReadOnlyList<Call> Calls
        {
            get
            {
                lock (_gate)
                {
                    return [.. _calls];
                }
            }
        }

        public async Task<PortalResponse> HandleAsync(
            PortalRequest request, string callerKey, string? requestedBy, CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                _calls.Add(new Call(request, callerKey, requestedBy));
            }

            if (Before is not null)
            {
                await Before();
            }

            if (Throws is not null)
            {
                throw Throws;
            }

            return Response;
        }

        public sealed record Call(PortalRequest Request, string CallerKey, string? RequestedBy);
    }

    private sealed class RecordingLogger : ILogger<CompanyPortalPipeServer>
    {
        private readonly Lock _gate = new();
        private readonly List<(LogLevel Level, string Message)> _entries = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (_gate)
            {
                _entries.Add((logLevel, formatter(state, exception)));
            }
        }

        public bool Has(LogLevel level, string fragment)
        {
            lock (_gate)
            {
                return _entries.Any(e => e.Level == level && e.Message.Contains(fragment, StringComparison.Ordinal));
            }
        }
    }
}
