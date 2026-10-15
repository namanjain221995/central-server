using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using EndpointAgent.Core.SessionNotice;
using EndpointAgent.Windows.SessionNotice;
using Microsoft.Extensions.Logging.Abstractions;

namespace EndpointAgent.Windows.Tests;

/// <summary>
/// The service-to-session boundary, exercised on real Windows pipes.
/// </summary>
/// <remarks>
/// <para>
/// These use the operating system, not stand-ins. The forgery test creates a real
/// pipe server in the test's own interactive session and proves the reader
/// refuses it; the trust test reads the session of a real Windows service's pipe
/// and proves it is accepted -- which is the only way to show the check works
/// from an unprivileged process, since the test cannot run a server in session 0
/// itself.
/// </para>
/// <para>
/// Every pipe name here is unique to the test, so nothing collides with an agent
/// installed on the machine running the suite. Nothing here restarts anything.
/// </para>
/// </remarks>
public sealed class SessionNoticePipeTests
{
    private static string UniquePipe() => $"EndpointPlatformAgent.SessionNotice.Test.{Guid.NewGuid():N}";

    private static readonly RestartNotice Notice = new(DateTimeOffset.UtcNow.AddMinutes(10), 600);

    /// <summary>
    /// An untrusted server tries to deliver a valid notice. A reader that refuses
    /// it hangs up the moment it has judged the server, so the write commonly finds
    /// the pipe already broken -- which is the refusal working, not the test failing.
    /// </summary>
    private static async Task OfferAsync(NamedPipeServerStream untrusted)
    {
        try
        {
            await untrusted.WriteAsync(SessionNoticeProtocol.Encode(Notice));
            await untrusted.FlushAsync();
        }
        catch (IOException)
        {
            // Pipe is broken: the reader already disconnected without reading.
        }
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

    // ---------------------------------------------------------------- trust

    [Theory]
    [InlineData(0u, SessionNoticePipe.Trust.Trusted)]
    [InlineData(1u, SessionNoticePipe.Trust.InteractiveSession)]
    [InlineData(2u, SessionNoticePipe.Trust.InteractiveSession)]
    [InlineData(uint.MaxValue, SessionNoticePipe.Trust.InteractiveSession)]
    public void Only_a_server_in_session_0_is_trusted(uint session, SessionNoticePipe.Trust expected) =>
        SessionNoticePipe.Evaluate(session).ShouldBe(expected);

    [Fact]
    public void A_server_whose_session_cannot_be_read_is_not_trusted() =>
        SessionNoticePipe.Evaluate(null).ShouldBe(SessionNoticePipe.Trust.SessionUnknown);

    /// <summary>
    /// The forgery. An ordinary user creates a pipe with the notifier's name and
    /// sends a perfectly valid notice. The reader must refuse the server and
    /// record nothing -- the notice is never shown.
    /// </summary>
    [Fact]
    public async Task A_notice_from_a_pipe_created_in_a_user_session_is_refused_and_never_recorded()
    {
        var name = UniquePipe();
        await using var forged = new NamedPipeServerStream(name, PipeDirection.Out, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

        // The REAL trust decision, reading the REAL server session from the pipe driver.
        var reader = new SessionNoticeReader(name, SessionNoticePipe.EvaluateServer, TimeProvider.System);
        using var stop = new CancellationTokenSource();
        var running = reader.RunAsync(stop.Token);

        await forged.WaitForConnectionAsync().WaitAsync(TimeSpan.FromSeconds(10));
        await OfferAsync(forged);

        (await EventuallyAsync(() => reader.LastTrust is not null)).ShouldBeTrue("the reader must have connected and judged the server");
        reader.LastTrust.ShouldBe(SessionNoticePipe.Trust.InteractiveSession,
            $"this test runs in session {System.Diagnostics.Process.GetCurrentProcess().SessionId}, not session 0");

        await Task.Delay(300);
        reader.Latest.ShouldBeNull("a notice from an untrusted server must never be recorded, let alone shown");

        stop.Cancel();
        await running;
    }

    /// <summary>
    /// The positive half of the check, on a real Windows service. The test process
    /// cannot host a server in session 0, so it connects to one that exists on
    /// every Windows machine and confirms the pipe driver reports session 0 to an
    /// unprivileged caller -- the property the agent's notifier depends on.
    /// </summary>
    [Fact]
    public void A_real_windows_service_pipe_reports_session_0_and_is_trusted()
    {
        var elevated = new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);

        // lsass's pipe exists on every Windows installation and accepts connections
        // from authenticated users.
        using var client = new NamedPipeClientStream(".", "lsass", PipeDirection.InOut);
        client.Connect(3000);

        SessionNoticePipe.ServerSessionId(client.SafePipeHandle).ShouldBe(0u,
            $"the pipe driver must report a service's session to a caller that cannot open the service (elevated: {elevated})");
        SessionNoticePipe.EvaluateServer(client).ShouldBe(SessionNoticePipe.Trust.Trusted);
    }

    // --------------------------------------------------------------- access

    [Fact]
    public void The_pipe_grants_SYSTEM_full_control_and_interactive_users_read_and_nothing_else()
    {
        var rules = SessionNoticePipeServer.BuildSecurity()
            .GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier))
            .Cast<PipeAccessRule>()
            .ToList();

        rules.Count.ShouldBe(2);
        rules.ShouldAllBe(r => r.AccessControlType == AccessControlType.Allow);

        var system = rules.Single(r => r.IdentityReference.Value == new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null).Value);
        system.PipeAccessRights.ShouldBe(PipeAccessRights.FullControl);

        var interactive = rules.Single(r => r.IdentityReference.Value == new SecurityIdentifier(WellKnownSidType.InteractiveSid, null).Value);
        (interactive.PipeAccessRights & PipeAccessRights.WriteData).ShouldBe((PipeAccessRights)0, "users must not be able to write");
        (interactive.PipeAccessRights & PipeAccessRights.ChangePermissions).ShouldBe((PipeAccessRights)0);
        (interactive.PipeAccessRights & PipeAccessRights.TakeOwnership).ShouldBe((PipeAccessRights)0);
        (interactive.PipeAccessRights & PipeAccessRights.Read).ShouldBe(PipeAccessRights.Read,
            "a reader asks for GENERIC_READ; granting less refuses the notifier itself");
        (interactive.PipeAccessRights & PipeAccessRights.Delete).ShouldBe((PipeAccessRights)0);
        (interactive.PipeAccessRights & PipeAccessRights.WriteAttributes).ShouldBe((PipeAccessRights)0);
        (interactive.PipeAccessRights & PipeAccessRights.CreateNewInstance).ShouldBe((PipeAccessRights)0,
            "an interactive user must not be able to add an instance of the service's own pipe");

        SessionNoticePipeServer.BuildSecurity().AreAccessRulesProtected.ShouldBeTrue("no inherited entries may widen access");
    }

    /// <summary>
    /// Enforced, not only declared: the test runs as an interactive user, and
    /// opening the real pipe to write is refused by Windows -- while opening it to
    /// read, which is all a notifier does, is allowed. The read succeeding is what
    /// proves the write refusal is the rule working, not everything being denied.
    /// </summary>
    [Fact]
    public async Task An_interactive_user_may_read_the_pipe_but_cannot_open_it_to_write()
    {
        var name = UniquePipe();
        var server = new SessionNoticePipeServer(name, NullLogger<SessionNoticePipeServer>.Instance);
        using var stop = new CancellationTokenSource();
        await server.StartAsync(stop.Token);

        try
        {
            using (var reader = new NamedPipeClientStream(".", name, PipeDirection.In))
            {
                Should.NotThrow(() => reader.Connect(3000), "an interactive user must be able to read, or no notifier could work");
            }

            using var writer = new NamedPipeClientStream(".", name, PipeDirection.Out);
            Should.Throw<UnauthorizedAccessException>(() => writer.Connect(3000));

            using var readWrite = new NamedPipeClientStream(".", name, PipeDirection.InOut);
            Should.Throw<UnauthorizedAccessException>(() => readWrite.Connect(3000));
        }
        finally
        {
            await server.StopAsync(CancellationToken.None);
        }
    }

    /// <summary>
    /// A user who creates the pipe first cannot share the name with the service:
    /// the service claims it with FirstPipeInstance and refuses to host alongside
    /// anyone. And a reader that finds the squatter refuses it.
    /// </summary>
    [Fact]
    public async Task A_squatter_on_the_name_is_neither_joined_by_the_service_nor_believed_by_the_reader()
    {
        var name = UniquePipe();
        await using var squatter = new NamedPipeServerStream(name, PipeDirection.Out, 4, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

        var server = new SessionNoticePipeServer(name, NullLogger<SessionNoticePipeServer>.Instance);
        using var stop = new CancellationTokenSource();
        await server.StartAsync(stop.Token);

        var reader = new SessionNoticeReader(name, SessionNoticePipe.EvaluateServer, TimeProvider.System);
        var reading = reader.RunAsync(stop.Token);

        await squatter.WaitForConnectionAsync().WaitAsync(TimeSpan.FromSeconds(10));
        await OfferAsync(squatter);

        server.RestartScheduled(Notice);

        (await EventuallyAsync(() => reader.LastTrust is not null)).ShouldBeTrue();
        reader.LastTrust.ShouldBe(SessionNoticePipe.Trust.InteractiveSession);
        server.ConnectedCount.ShouldBe(0, "the service must not have created an instance beside the squatter");
        reader.Latest.ShouldBeNull();

        stop.Cancel();
        await server.StopAsync(CancellationToken.None);
        await reading;
    }

    // ------------------------------------------------------------- delivery

    /// <summary>
    /// The plumbing, end to end on a real pipe. The test's server necessarily
    /// runs in the test's session, so the reader is told to accept it -- the trust
    /// check itself is proven by the two tests above.
    /// </summary>
    [Fact]
    public async Task A_scheduled_restart_reaches_a_connected_reader_exactly()
    {
        var name = UniquePipe();
        var server = new SessionNoticePipeServer(name, NullLogger<SessionNoticePipeServer>.Instance);
        using var stop = new CancellationTokenSource();
        await server.StartAsync(stop.Token);

        var reader = new SessionNoticeReader(name, _ => SessionNoticePipe.Trust.Trusted, TimeProvider.System);
        var reading = reader.RunAsync(stop.Token);

        (await EventuallyAsync(() => server.ConnectedCount == 1)).ShouldBeTrue("the reader must connect");

        server.RestartScheduled(Notice);

        (await EventuallyAsync(() => reader.Latest is not null)).ShouldBeTrue();
        reader.Latest.ShouldBe(Notice);

        stop.Cancel();
        await server.StopAsync(CancellationToken.None);
        await reading;
    }

    /// <summary>A user who signs in part-way through a countdown still sees it.</summary>
    [Fact]
    public async Task A_reader_that_connects_after_the_notice_still_receives_it()
    {
        var name = UniquePipe();
        var server = new SessionNoticePipeServer(name, NullLogger<SessionNoticePipeServer>.Instance);
        using var stop = new CancellationTokenSource();
        await server.StartAsync(stop.Token);

        server.RestartScheduled(Notice);

        var reader = new SessionNoticeReader(name, _ => SessionNoticePipe.Trust.Trusted, TimeProvider.System);
        var reading = reader.RunAsync(stop.Token);

        (await EventuallyAsync(() => reader.Latest is not null)).ShouldBeTrue();
        reader.Latest.ShouldBe(Notice);

        stop.Cancel();
        await server.StopAsync(CancellationToken.None);
        await reading;
    }

    /// <summary>
    /// A cancellation replaces the countdown: the reader's latest notice becomes
    /// the cancellation, stamped by the server's clock, and a session that
    /// connects afterwards is replayed the cancellation rather than the restart.
    /// </summary>
    [Fact]
    public async Task A_cancellation_replaces_the_countdown_for_connected_and_later_readers()
    {
        var name = UniquePipe();
        var at = new DateTimeOffset(2026, 10, 7, 10, 0, 0, TimeSpan.Zero);
        // Two readers at once need a second pipe instance, which only the service
        // identity may create; as in the multi-session test, that is this user here.
        var server = new SessionNoticePipeServer(
            name, NullLogger<SessionNoticePipeServer>.Instance, new FixedClock(at),
            serviceIdentity: WindowsIdentity.GetCurrent().User);
        using var stop = new CancellationTokenSource();
        await server.StartAsync(stop.Token);

        var reader = new SessionNoticeReader(name, _ => SessionNoticePipe.Trust.Trusted, new FixedClock(at));
        var reading = reader.RunAsync(stop.Token);
        (await EventuallyAsync(() => server.ConnectedCount == 1)).ShouldBeTrue("the reader must connect");

        server.RestartScheduled(new RestartNotice(at.AddMinutes(5), 300));
        (await EventuallyAsync(() => reader.Latest is { Cancelled: false })).ShouldBeTrue();

        server.RestartCancelled();

        (await EventuallyAsync(() => reader.Latest is { Cancelled: true })).ShouldBeTrue();
        reader.Latest.ShouldBe(RestartNotice.CancelledAt(at));

        var late = new SessionNoticeReader(name, _ => SessionNoticePipe.Trust.Trusted, new FixedClock(at.AddSeconds(30)));
        var lateReading = late.RunAsync(stop.Token);
        (await EventuallyAsync(() => late.Latest is not null)).ShouldBeTrue("the replay must carry the cancellation");
        late.Latest!.Cancelled.ShouldBeTrue();

        stop.Cancel();
        await server.StopAsync(CancellationToken.None);
        await reading;
        await lateReading;
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    // -------------------------------------------------------- announcements

    [Fact]
    public async Task An_announcement_reaches_a_connected_reader_exactly()
    {
        var name = UniquePipe();
        var server = new SessionNoticePipeServer(name, NullLogger<SessionNoticePipeServer>.Instance);
        using var stop = new CancellationTokenSource();
        await server.StartAsync(stop.Token);

        var reader = new SessionNoticeReader(name, _ => SessionNoticePipe.Trust.Trusted, TimeProvider.System);
        var reading = reader.RunAsync(stop.Token);

        (await EventuallyAsync(() => server.ConnectedCount == 1)).ShouldBeTrue("the reader must connect");

        // The count the executor reports comes from the connections themselves:
        // one connected notifier is one session, whether or not the pipe driver
        // names its session id.
        server.ConnectedSessionCount.ShouldBe(1);

        var announcement = new AnnouncementNotice(
            Guid.NewGuid(), "Maintenance", "The office network is down tonight.", DateTimeOffset.UtcNow);
        server.AnnouncementReceived(announcement);

        (await EventuallyAsync(() => reader.LatestAnnouncement is not null)).ShouldBeTrue();
        reader.LatestAnnouncement.ShouldBe(announcement);

        stop.Cancel();
        await server.StopAsync(CancellationToken.None);
        await reading;
    }

    /// <summary>
    /// The two kinds live side by side: a message never displaces a countdown,
    /// and a countdown never eats a message.
    /// </summary>
    [Fact]
    public async Task An_announcement_and_a_restart_are_each_kept_without_displacing_the_other()
    {
        var name = UniquePipe();
        var server = new SessionNoticePipeServer(name, NullLogger<SessionNoticePipeServer>.Instance);
        using var stop = new CancellationTokenSource();
        await server.StartAsync(stop.Token);

        var reader = new SessionNoticeReader(name, _ => SessionNoticePipe.Trust.Trusted, TimeProvider.System);
        var reading = reader.RunAsync(stop.Token);
        (await EventuallyAsync(() => server.ConnectedCount == 1)).ShouldBeTrue();

        var announcement = new AnnouncementNotice(
            Guid.NewGuid(), "Heads up", "Printers move to the second floor on Friday.", DateTimeOffset.UtcNow);
        server.RestartScheduled(Notice);
        server.AnnouncementReceived(announcement);

        (await EventuallyAsync(() => reader.Latest is not null && reader.LatestAnnouncement is not null)).ShouldBeTrue();
        reader.Latest.ShouldBe(Notice);
        reader.LatestAnnouncement.ShouldBe(announcement);

        stop.Cancel();
        await server.StopAsync(CancellationToken.None);
        await reading;
    }

    /// <summary>
    /// A session that connects while both notices still matter is replayed both,
    /// and the restart comes first: a reboot countdown must never arrive behind a
    /// message. Read with a raw pipe client, because the order on the wire is the
    /// thing under test.
    /// </summary>
    [Fact]
    public async Task A_late_connection_is_replayed_the_restart_before_the_announcement()
    {
        var name = UniquePipe();
        var at = new DateTimeOffset(2026, 10, 16, 10, 0, 0, TimeSpan.Zero);
        var server = new SessionNoticePipeServer(name, NullLogger<SessionNoticePipeServer>.Instance, new FixedClock(at));
        using var stop = new CancellationTokenSource();
        await server.StartAsync(stop.Token);

        var restart = new RestartNotice(at.AddMinutes(5), 300);
        var announcement = new AnnouncementNotice(Guid.NewGuid(), "Maintenance", "Back at noon.", at);
        server.RestartScheduled(restart);
        server.AnnouncementReceived(announcement);

        await using var raw = new NamedPipeClientStream(".", name, PipeDirection.In, PipeOptions.Asynchronous);
        await raw.ConnectAsync(TimeSpan.FromSeconds(5), CancellationToken.None);

        var lines = await ReadLinesAsync(raw, 2);

        lines.Count.ShouldBe(2, "the replay must carry both notices");
        SessionNoticeProtocol.TryDecode(lines[0], out var firstRestart, out var firstAnnouncement).ShouldBeTrue();
        firstRestart.ShouldBe(restart, "the restart must come first");
        firstAnnouncement.ShouldBeNull();
        SessionNoticeProtocol.TryDecode(lines[1], out var secondRestart, out var secondAnnouncement).ShouldBeTrue();
        secondRestart.ShouldBeNull();
        secondAnnouncement.ShouldBe(announcement);

        stop.Cancel();
        await server.StopAsync(CancellationToken.None);
    }

    /// <summary>An announcement past its linger is not replayed; the still-live restart is.</summary>
    [Fact]
    public async Task A_lingered_out_announcement_is_not_replayed_to_a_late_connection()
    {
        var name = UniquePipe();
        var sent = new DateTimeOffset(2026, 10, 16, 10, 0, 0, TimeSpan.Zero);
        var later = sent + AnnouncementView.Linger + TimeSpan.FromMinutes(1);
        var clock = new MutableClock(sent);
        var server = new SessionNoticePipeServer(name, NullLogger<SessionNoticePipeServer>.Instance, clock);
        using var stop = new CancellationTokenSource();
        await server.StartAsync(stop.Token);

        var restart = new RestartNotice(later.AddMinutes(5), 300);
        server.RestartScheduled(restart);
        server.AnnouncementReceived(new AnnouncementNotice(Guid.NewGuid(), "Old news", "This was over an hour ago.", sent));

        clock.Now = later;
        await using var raw = new NamedPipeClientStream(".", name, PipeDirection.In, PipeOptions.Asynchronous);
        await raw.ConnectAsync(TimeSpan.FromSeconds(5), CancellationToken.None);

        // Ask for two lines and give a wrongly-replayed announcement time to
        // arrive; only the restart may.
        var lines = await ReadLinesAsync(raw, 2, TimeSpan.FromSeconds(2));

        lines.Count.ShouldBe(1);
        SessionNoticeProtocol.TryDecode(lines[0], out var replayed, out var announcement).ShouldBeTrue();
        replayed.ShouldBe(restart);
        announcement.ShouldBeNull("an announcement past its linger is old news, not a replay");

        stop.Cancel();
        await server.StopAsync(CancellationToken.None);
    }

    private sealed class MutableClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    /// <summary>Reads whole newline-terminated lines from a pipe, without their terminators.</summary>
    private static async Task<List<byte[]>> ReadLinesAsync(Stream stream, int count, TimeSpan? wait = null)
    {
        var buffer = new byte[SessionNoticeProtocol.MaxLineBytes * 4];
        var filled = 0;
        var lines = new List<byte[]>();
        using var timeout = new CancellationTokenSource(wait ?? TimeSpan.FromSeconds(10));

        while (lines.Count < count)
        {
            int read;
            try
            {
                read = await stream.ReadAsync(buffer.AsMemory(filled), timeout.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            if (read == 0)
            {
                break;
            }

            filled += read;

            int newline;
            while ((newline = Array.IndexOf(buffer, (byte)'\n', 0, filled)) >= 0)
            {
                lines.Add(buffer.AsSpan(0, newline).ToArray());
                var rest = filled - newline - 1;
                Array.Copy(buffer, newline + 1, buffer, 0, rest);
                filled = rest;
            }
        }

        return lines;
    }

    /// <summary>
    /// Every connected session is told.
    /// </summary>
    /// <remarks>
    /// The second and later sessions connect to extra instances of the pipe, and
    /// only the pipe's full-control principal may create those. In production that
    /// is SYSTEM. This test cannot run as SYSTEM, so it names its own identity as
    /// the service account; with the default, the test user is an ordinary
    /// interactive reader and correctly cannot create an instance.
    /// </remarks>
    [Fact]
    public async Task Several_sessions_each_receive_the_notice()
    {
        var name = UniquePipe();
        var server = new SessionNoticePipeServer(
            name, NullLogger<SessionNoticePipeServer>.Instance, serviceIdentity: WindowsIdentity.GetCurrent().User);
        using var stop = new CancellationTokenSource();
        await server.StartAsync(stop.Token);

        var readers = Enumerable.Range(0, 3)
            .Select(_ => new SessionNoticeReader(name, _ => SessionNoticePipe.Trust.Trusted, TimeProvider.System))
            .ToList();
        var reading = readers.Select(r => r.RunAsync(stop.Token)).ToList();

        (await EventuallyAsync(() => server.ConnectedCount == 3)).ShouldBeTrue();
        server.RestartScheduled(Notice);

        (await EventuallyAsync(() => readers.All(r => r.Latest is not null))).ShouldBeTrue();
        readers.ShouldAllBe(r => r.Latest == Notice);

        stop.Cancel();
        await server.StopAsync(CancellationToken.None);
        await Task.WhenAll(reading);
    }

    // ------------------------------------------------------- reader lifetime

    /// <summary>
    /// The service stops -- an update, a restart -- and the notifier that was
    /// reading from it is finished: it exits, freeing the files it shares with the
    /// service, and the service starts a fresh one when it is back.
    /// </summary>
    [Fact]
    public async Task The_reader_ends_when_the_service_it_was_reading_from_goes_away()
    {
        var name = UniquePipe();
        var server = new SessionNoticePipeServer(name, NullLogger<SessionNoticePipeServer>.Instance, clientExitWait: TimeSpan.FromMilliseconds(50));
        await server.StartAsync(CancellationToken.None);

        using var stop = new CancellationTokenSource();
        var reader = new SessionNoticeReader(name, _ => SessionNoticePipe.Trust.Trusted, TimeProvider.System);
        var reading = reader.RunAsync(stop.Token);

        (await EventuallyAsync(() => server.ConnectedCount == 1)).ShouldBeTrue();
        reader.ServiceLost.ShouldBeFalse();

        await server.StopAsync(CancellationToken.None);

        await reading.WaitAsync(TimeSpan.FromSeconds(5));
        reader.ServiceLost.ShouldBeTrue("the reader must report the service gone, not silently keep polling");
        stop.IsCancellationRequested.ShouldBeFalse("it ended on its own, not because it was cancelled");
    }

    /// <summary>
    /// A squatter it refused hanging up is not the service going away: the notifier
    /// stays, still waiting for the real service. Otherwise any user could end
    /// every notifier in their session by creating and closing a pipe -- which
    /// they can do to their own process anyway, but it should not be this easy.
    /// </summary>
    [Fact]
    public async Task The_reader_keeps_waiting_when_a_refused_server_hangs_up()
    {
        var name = UniquePipe();
        var forged = new NamedPipeServerStream(name, PipeDirection.Out, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

        var reader = new SessionNoticeReader(name, SessionNoticePipe.EvaluateServer, TimeProvider.System);
        using var stop = new CancellationTokenSource();
        var reading = reader.RunAsync(stop.Token);

        await forged.WaitForConnectionAsync().WaitAsync(TimeSpan.FromSeconds(10));
        (await EventuallyAsync(() => reader.LastTrust == SessionNoticePipe.Trust.InteractiveSession)).ShouldBeTrue();

        await forged.DisposeAsync();
        await Task.Delay(500);

        reading.IsCompleted.ShouldBeFalse("a refused server disappearing must not end the notifier");
        reader.ServiceLost.ShouldBeFalse();

        stop.Cancel();
        await reading;
    }

    /// <summary>Signed in before the service was up: the notifier keeps trying until it is.</summary>
    [Fact]
    public async Task The_reader_keeps_waiting_while_there_is_no_service_yet()
    {
        var reader = new SessionNoticeReader(UniquePipe(), _ => SessionNoticePipe.Trust.Trusted, TimeProvider.System);
        using var stop = new CancellationTokenSource();
        var reading = reader.RunAsync(stop.Token);

        await Task.Delay(500);

        reading.IsCompleted.ShouldBeFalse();
        reader.ServiceLost.ShouldBeFalse();

        stop.Cancel();
        await reading;
    }

    /// <summary>
    /// The service knows which sessions have a notifier connected -- from the pipe
    /// driver, not from anything the notifier says -- so it can start one only
    /// where none is.
    /// </summary>
    [Fact]
    public async Task The_service_knows_which_sessions_have_a_notifier_connected()
    {
        var name = UniquePipe();
        var server = new SessionNoticePipeServer(name, NullLogger<SessionNoticePipeServer>.Instance, clientExitWait: TimeSpan.FromMilliseconds(50));
        await server.StartAsync(CancellationToken.None);

        using var stop = new CancellationTokenSource();
        var reader = new SessionNoticeReader(name, _ => SessionNoticePipe.Trust.Trusted, TimeProvider.System);
        var reading = reader.RunAsync(stop.Token);

        var thisSession = (uint)System.Diagnostics.Process.GetCurrentProcess().SessionId;
        (await EventuallyAsync(() => server.ConnectedSessions.Contains(thisSession))).ShouldBeTrue();
        server.ConnectedSessions.Count.ShouldBe(1);

        stop.Cancel();
        await server.StopAsync(CancellationToken.None);
        await reading;

        server.ConnectedSessions.ShouldBeEmpty();
    }

    /// <summary>
    /// Stopping waits for connected notifiers to exit, but never on its own
    /// process and never longer than the bound: the installer that stopped the
    /// service must not be kept waiting by a notifier that will not go.
    /// </summary>
    [Fact]
    public async Task Stopping_waits_for_notifiers_within_a_bound_and_never_on_itself()
    {
        var name = UniquePipe();
        var server = new SessionNoticePipeServer(name, NullLogger<SessionNoticePipeServer>.Instance, clientExitWait: TimeSpan.FromSeconds(10));
        await server.StartAsync(CancellationToken.None);

        using var stop = new CancellationTokenSource();
        var reader = new SessionNoticeReader(name, _ => SessionNoticePipe.Trust.Trusted, TimeProvider.System);
        var reading = reader.RunAsync(stop.Token);
        (await EventuallyAsync(() => server.ConnectedCount == 1)).ShouldBeTrue();

        // The only connected client is this very process, which cannot exit to
        // satisfy the wait; stopping must recognise that and not sit out the bound.
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        await server.StopAsync(CancellationToken.None);
        stopwatch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(5));

        await reading.WaitAsync(TimeSpan.FromSeconds(5));
    }

    // --------------------------------------------------------- reader hardening

    [Fact]
    public async Task The_reader_ignores_malformed_lines_and_keeps_reading()
    {
        var reader = new SessionNoticeReader(UniquePipe(), _ => SessionNoticePipe.Trust.Trusted, TimeProvider.System);
        var bytes = Encoding.UTF8.GetBytes("garbage\n{\"v\":9}\n").Concat(SessionNoticeProtocol.Encode(Notice)).ToArray();

        await reader.ReadAsync(new MemoryStream(bytes), CancellationToken.None);

        reader.Latest.ShouldBe(Notice);
    }

    [Fact]
    public async Task An_overlong_line_ends_the_connection_and_nothing_after_it_is_read()
    {
        var reader = new SessionNoticeReader(UniquePipe(), _ => SessionNoticePipe.Trust.Trusted, TimeProvider.System);
        var flood = new byte[SessionNoticeProtocol.MaxLineBytes * 4];
        Array.Fill(flood, (byte)'x');
        var bytes = flood.Concat(SessionNoticeProtocol.Encode(Notice)).ToArray();

        await reader.ReadAsync(new MemoryStream(bytes), CancellationToken.None);

        reader.Latest.ShouldBeNull("a sender that floods past the line limit is not listened to further");
    }

    /// <summary>
    /// Even from a trusted server, a notice must describe a restart that could
    /// really be pending: not days away, and not long past.
    /// </summary>
    [Theory]
    [InlineData(24 * 3600)]
    [InlineData(3600 + 120)]
    [InlineData(-600)]
    public void An_implausible_notice_is_not_accepted(int secondsFromNow)
    {
        var now = DateTimeOffset.UtcNow;

        SessionNoticeReader.IsPlausible(new RestartNotice(now.AddSeconds(secondsFromNow), 60), now).ShouldBeFalse();
    }

    [Theory]
    [InlineData(30)]
    [InlineData(3600)]
    [InlineData(-30)]
    public void A_plausible_notice_is_accepted(int secondsFromNow)
    {
        var now = DateTimeOffset.UtcNow;

        SessionNoticeReader.IsPlausible(new RestartNotice(now.AddSeconds(secondsFromNow), 60), now).ShouldBeTrue();
    }

    /// <summary>
    /// Even from a trusted server, an announcement is only worth keeping while it
    /// would still be shown: not past its linger, not stamped in the future
    /// beyond clock skew.
    /// </summary>
    [Theory]
    [InlineData(-31, false)]
    [InlineData(-29, true)]
    [InlineData(0, true)]
    [InlineData(2, false)]
    public void An_announcement_is_plausible_only_while_it_would_still_be_shown(int minutesFromNow, bool plausible)
    {
        var now = DateTimeOffset.UtcNow;

        SessionNoticeReader
            .IsPlausibleAnnouncement(new AnnouncementNotice(Guid.NewGuid(), "T", "M", now.AddMinutes(minutesFromNow)), now)
            .ShouldBe(plausible);
    }

    [Fact]
    public async Task A_stale_or_future_stamped_announcement_is_not_recorded()
    {
        var now = new DateTimeOffset(2026, 10, 16, 10, 0, 0, TimeSpan.Zero);
        var reader = new SessionNoticeReader(UniquePipe(), _ => SessionNoticePipe.Trust.Trusted, new FixedClock(now));
        var stale = SessionNoticeProtocol.Encode(
            new AnnouncementNotice(Guid.NewGuid(), "Old", "Too old.", now - AnnouncementView.Linger - TimeSpan.FromMinutes(1)));
        var future = SessionNoticeProtocol.Encode(new AnnouncementNotice(Guid.NewGuid(), "Soon", "Too soon.", now.AddMinutes(5)));
        var fresh = SessionNoticeProtocol.Encode(new AnnouncementNotice(Guid.NewGuid(), "Now", "Just right.", now.AddMinutes(-1)));

        await reader.ReadAsync(new MemoryStream([.. stale, .. future]), CancellationToken.None);
        reader.LatestAnnouncement.ShouldBeNull("a stale or future-stamped announcement must not be recorded");

        await reader.ReadAsync(new MemoryStream(fresh), CancellationToken.None);
        reader.LatestAnnouncement.ShouldNotBeNull();
        reader.LatestAnnouncement!.Title.ShouldBe("Now");
    }
}
