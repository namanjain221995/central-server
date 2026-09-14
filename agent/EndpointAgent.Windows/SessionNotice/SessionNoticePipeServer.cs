using System.Diagnostics;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using EndpointAgent.Core.Abstractions;
using EndpointAgent.Core.SessionNotice;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace EndpointAgent.Windows.SessionNotice;

/// <summary>
/// Hosts the session-notice pipe inside the LocalSystem service, tells every
/// connected session notifier about a restart Windows has accepted, and sees to
/// it that every signed-in session has a notifier to tell.
/// </summary>
/// <remarks>
/// <para>
/// <b>Access.</b> SYSTEM has full control. Interactive users may read and nothing
/// else -- no write, no change of permissions -- so a connected notifier can
/// receive a notice and has no way to send one. Every other principal has no
/// entry and so no access. The server writes; it never reads.
/// </para>
/// <para>
/// <b>The name is claimed, not shared.</b> Whenever the service holds no instance
/// of the pipe it creates the next with <see cref="PipeOptions.FirstPipeInstance"/>,
/// which fails if anyone else already has the name. If something is squatting on
/// it the service logs that, sends no notices, and tries again; notifiers would
/// refuse a squatter anyway (see <see cref="SessionNoticePipe"/>), and Windows'
/// own shutdown warning is unaffected either way.
/// </para>
/// <para>
/// <b>Who starts the notifier.</b> Windows does, at sign-in, from the machine Run
/// key. This service does too, through <see cref="SessionNoticeLauncher"/>: once
/// when it starts, for users already signed in when the agent was installed,
/// updated or restarted; and again for each restart it announces, for any session
/// still without one. The launcher starts one fixed program with no arguments; the
/// pipe stays one-way and carries no request in either direction.
/// </para>
/// <para>
/// <b>Late sign-in.</b> The latest notice is kept and replayed to a notifier that
/// connects while its countdown still means something, so a user who signs in
/// five minutes into a ten-minute restart still sees the five minutes -- and so
/// does a notifier this service has just started.
/// </para>
/// <para>
/// <b>Stopping.</b> A notifier ends itself when the service it was reading from
/// goes away, and on stop this service waits briefly for connected notifiers to do
/// so. That is what lets an upgrade replace the runtime files a running notifier
/// shares with the service: the installer stops the service before it touches a
/// file, and by the time it does, nothing of the agent's is running.
/// </para>
/// </remarks>
public sealed class SessionNoticePipeServer : BackgroundService, IRestartNotifier
{
    /// <summary>Enough for every session a workstation or small terminal server has.</summary>
    internal const int MaxInstances = 32;

    /// <summary>How long a stopping service waits for its notifiers to exit before going on without them.</summary>
    internal static readonly TimeSpan DefaultClientExitWait = TimeSpan.FromSeconds(2);

    private static readonly TimeSpan WriteTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan SquatRetry = TimeSpan.FromSeconds(30);

    private readonly string _pipeName;
    private readonly SecurityIdentifier _serviceIdentity;
    private readonly SessionNoticeLauncher? _launcher;
    private readonly TimeSpan _clientExitWait;
    private readonly TimeProvider _time;
    private readonly ILogger<SessionNoticePipeServer> _logger;
    private readonly Lock _gate = new();
    private readonly List<Client> _clients = [];
    private RestartNotice? _current;
    private bool _startedNotifiers;

    public SessionNoticePipeServer(
        ILogger<SessionNoticePipeServer> logger, SessionNoticeLauncher launcher, TimeProvider? timeProvider = null)
        : this(SessionNoticePipe.Name, logger, timeProvider, launcher: launcher)
    {
        ArgumentNullException.ThrowIfNull(launcher);
    }

    /// <summary>
    /// For tests: a private pipe name, so a test never collides with an installed
    /// agent, and optionally the identity that owns the pipe.
    /// </summary>
    /// <param name="serviceIdentity">
    /// The account the service runs as, which gets full control -- including the
    /// right to create the extra instances further sessions connect to. LocalSystem
    /// in production, matching the installer. A test cannot run as SYSTEM, so a test
    /// that needs several instances passes its own identity; one that checks what an
    /// interactive user may do keeps the default, so the test user stays an
    /// ordinary interactive reader.
    /// </param>
    /// <param name="launcher">What starts notifiers, or null for a server that starts none.</param>
    /// <param name="clientExitWait">How long stopping waits for connected notifiers to exit.</param>
    internal SessionNoticePipeServer(
        string pipeName, ILogger<SessionNoticePipeServer> logger, TimeProvider? timeProvider = null,
        SecurityIdentifier? serviceIdentity = null, SessionNoticeLauncher? launcher = null,
        TimeSpan? clientExitWait = null)
    {
        _pipeName = pipeName;
        _serviceIdentity = serviceIdentity ?? new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        _launcher = launcher;
        _clientExitWait = clientExitWait ?? DefaultClientExitWait;
        _logger = logger;
        _time = timeProvider ?? TimeProvider.System;
    }

    /// <summary>Clients currently connected. For tests and diagnostics.</summary>
    internal int ConnectedCount
    {
        get
        {
            lock (_gate)
            {
                return _clients.Count;
            }
        }
    }

    /// <summary>The sessions with a notifier connected right now, as the pipe driver reports them.</summary>
    internal IReadOnlySet<uint> ConnectedSessions
    {
        get
        {
            lock (_gate)
            {
                return _clients.Where(c => c.SessionId is not null).Select(c => c.SessionId!.Value).ToHashSet();
            }
        }
    }

    /// <inheritdoc />
    public void RestartScheduled(RestartNotice notice)
    {
        ArgumentNullException.ThrowIfNull(notice);

        List<Client> clients;
        lock (_gate)
        {
            _current = notice;
            clients = [.. _clients];
        }

        var bytes = RestartNoticeProtocol.Encode(notice);
        foreach (var client in clients)
        {
            // Fire and forget per client: the executor that called this must not
            // wait on a session that has stopped reading.
            _ = SendAsync(client, bytes);
        }

        _logger.LogInformation(
            "Restart notice sent to {Count} session(s): restart at {RestartAt:u}.", clients.Count, notice.RestartAt);

        // Any signed-in session without a notifier gets one now; it receives this
        // notice as the replay the moment it connects.
        StartNotifiersWhereMissing();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            NamedPipeServerStream server;
            try
            {
                server = CreateInstance();
            }
            catch (IOException ex)
            {
                // FirstPipeInstance refused: the name is already in use by
                // someone else. Do not share it; wait and try to claim it again.
                _logger.LogError(ex,
                    "The session-notice pipe name is already in use by another process; restart notices are " +
                    "disabled until it is released. Windows' own shutdown warning is unaffected.");
                await DelayAsync(SquatRetry, stoppingToken);
                continue;
            }
            catch (UnauthorizedAccessException ex)
            {
                _logger.LogError(ex, "The session-notice pipe could not be created; retrying.");
                await DelayAsync(SquatRetry, stoppingToken);
                continue;
            }

            // The pipe is listening: now users already signed in can be given a
            // notifier that will find it. Once per service start.
            if (!_startedNotifiers)
            {
                _startedNotifiers = true;
                StartNotifiersWhereMissing();
            }

            try
            {
                await server.WaitForConnectionAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                await server.DisposeAsync();
                break;
            }
            catch (IOException)
            {
                await server.DisposeAsync();
                continue;
            }

            var client = new Client(
                server,
                SessionNoticePipe.ClientSessionId(server.SafePipeHandle),
                SessionNoticePipe.ClientProcessId(server.SafePipeHandle));

            RestartNotice? replay;
            lock (_gate)
            {
                _clients.Add(client);
                replay = _current;
            }

            // A notice only worth replaying while it still describes something:
            // the view model hides it once the restart is well past.
            if (replay is not null && RestartNoticeView.For(replay, _time.GetUtcNow()).Visible)
            {
                _ = SendAsync(client, RestartNoticeProtocol.Encode(replay));
            }
        }

        List<Client> remaining;
        lock (_gate)
        {
            remaining = [.. _clients];
            _clients.Clear();
        }

        foreach (var client in remaining)
        {
            await client.Stream.DisposeAsync();
        }

        WaitForClientsToExit(remaining);
    }

    private void StartNotifiersWhereMissing()
    {
        if (_launcher is null)
        {
            return;
        }

        try
        {
            _launcher.StartWhereMissing(ConnectedSessions);
        }
        catch (Exception ex)
        {
            // A courtesy that failed. The restart, and Windows' own warning, stand.
            _logger.LogWarning(ex, "Starting session notifiers failed.");
        }
    }

    /// <summary>
    /// Gives connected notifiers -- which end themselves when this pipe closes --
    /// time to actually exit, so that an installer stopping this service finds no
    /// process of the agent's holding its files. Bounded; the service's own process
    /// is never waited on.
    /// </summary>
    private void WaitForClientsToExit(IReadOnlyList<Client> clients)
    {
        var deadline = _time.GetUtcNow() + _clientExitWait;
        var ownProcessId = Environment.ProcessId;

        foreach (var client in clients)
        {
            if (client.ProcessId is not { } processId || processId == ownProcessId)
            {
                continue;
            }

            var remaining = deadline - _time.GetUtcNow();
            if (remaining <= TimeSpan.Zero)
            {
                break;
            }

            try
            {
                using var process = Process.GetProcessById(processId);
                if (!process.WaitForExit((int)remaining.TotalMilliseconds))
                {
                    _logger.LogWarning("Session notifier (process {Pid}) had not exited when the service stopped.", processId);
                }
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                // Already gone, or not ours to wait on. Either way there is nothing to wait for.
            }
        }
    }

    private NamedPipeServerStream CreateInstance()
    {
        bool claimName;
        lock (_gate)
        {
            // Claim the name whenever this service holds no instance of it at all.
            // Once one exists, later instances cannot use FirstPipeInstance -- the
            // service's own first instance would make them fail -- and do not need
            // to, because the name is already ours.
            claimName = _clients.Count == 0;
        }

        var options = PipeOptions.Asynchronous | (claimName ? PipeOptions.FirstPipeInstance : PipeOptions.None);

        return NamedPipeServerStreamAcl.Create(
            _pipeName,
            PipeDirection.Out,
            MaxInstances,
            PipeTransmissionMode.Byte,
            options,
            inBufferSize: 0,
            outBufferSize: RestartNoticeProtocol.MaxLineBytes * 4,
            BuildSecurity(_serviceIdentity));
    }

    /// <summary>The service account: full control. Interactive users: read only. Nobody else: nothing.</summary>
    internal static PipeSecurity BuildSecurity(SecurityIdentifier? serviceIdentity = null)
    {
        var security = new PipeSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);

        security.AddAccessRule(new PipeAccessRule(
            serviceIdentity ?? new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            PipeAccessRights.FullControl,
            AccessControlType.Allow));

        // PipeAccessRights.Read, not a hand-picked subset of it. A client opening the
        // pipe for reading asks for GENERIC_READ, which maps to read data, attributes,
        // extended attributes and permissions together; granting less than all four
        // refuses the notifier outright. None of them is a write, a permission change
        // or a change of owner.
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.InteractiveSid, null),
            PipeAccessRights.Read | PipeAccessRights.Synchronize,
            AccessControlType.Allow));

        return security;
    }

    private async Task SendAsync(Client client, byte[] bytes)
    {
        using var timeout = new CancellationTokenSource(WriteTimeout);
        try
        {
            await client.Stream.WriteAsync(bytes, timeout.Token);
            await client.Stream.FlushAsync(timeout.Token);
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
        {
            // Gone, or not reading. Drop it; the next notice starts a fresh one.
            Drop(client);
        }
    }

    private void Drop(Client client)
    {
        lock (_gate)
        {
            _clients.Remove(client);
        }

        client.Stream.Dispose();
    }

    private static async Task DelayAsync(TimeSpan delay, CancellationToken token)
    {
        try
        {
            await Task.Delay(delay, token);
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>A connected notifier: its pipe instance, and where the pipe driver says it is.</summary>
    private sealed record Client(NamedPipeServerStream Stream, uint? SessionId, int? ProcessId);
}
