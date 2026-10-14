using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using EndpointAgent.Core.CompanyPortal;
using EndpointAgent.Windows.SessionNotice;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace EndpointAgent.Windows.CompanyPortal;

/// <summary>
/// Hosts the Company Portal pipe inside the LocalSystem service: takes one
/// request per connection from a signed-in user's portal, has it answered, writes
/// the answer and hangs up.
/// </summary>
/// <remarks>
/// <para>
/// <b>Access.</b> SYSTEM has full control. Interactive users may read and write
/// -- enough to send a line and read one back -- and nothing else: no new
/// instance of the pipe, no change of permissions, no change of owner. Every
/// other principal has no entry and so no access.
/// </para>
/// <para>
/// <b>Every byte from a client is hostile</b> until the protocol has accepted it.
/// A connection gets one line, read into a buffer one byte longer than the longest
/// request the protocol allows and for no longer than <see cref="ReadTimeout"/>;
/// whatever does not fit, does not arrive or does not decode is answered
/// <see cref="PortalError.BadRequest"/> and the connection closed. Nothing a client
/// says about itself is believed: its session comes from the pipe driver, the
/// user's name from Windows for that session, and the handler counts its limits
/// against the session.
/// </para>
/// <para>
/// <b>One request per connection.</b> The answer is written and the instance
/// closed; a portal that wants more connects again. That leaves no state per
/// connection to get wrong, and bounds what a stuck or hostile client can hold:
/// one of <see cref="MaxInstances"/> instances, for at most the read and write
/// timeouts. Connections are served concurrently so one slow server call does
/// not queue every session behind it.
/// </para>
/// <para>
/// <b>The name is claimed, not shared.</b> As with the session-notice pipe,
/// whenever the service holds no instance it creates the next with
/// <see cref="PipeOptions.FirstPipeInstance"/>, which fails if anyone else has the
/// name. A squatter is logged and the claim retried; a portal would refuse a
/// squatter anyway, for the reasons <see cref="SessionNoticePipe"/> gives.
/// </para>
/// <para>
/// <b>Stopping.</b> The listening instance is closed and the service goes on.
/// A connection being served is cut off by the stop and nothing waits on a
/// portal: the portal sees the pipe close, shows the service as unavailable and
/// tries again later.
/// </para>
/// </remarks>
public sealed class CompanyPortalPipeServer : BackgroundService
{
    /// <summary>Connections served at once. A portal makes one request at a time, and a few sessions is the most a workstation has.</summary>
    internal const int MaxInstances = 8;

    /// <summary>How long a connected client has to send its one line. A portal sends it at once.</summary>
    internal static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(5);

    /// <summary>How long a client has to take its answer. A catalog is at most half a megabyte.</summary>
    internal static readonly TimeSpan WriteTimeout = TimeSpan.FromSeconds(5);

    internal static readonly TimeSpan DefaultSquatRetry = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How long a stop waits for connections already cut off by it to finish
    /// their clean-up. Not a wait on a client: a handler that ignores the
    /// cancellation is left behind.
    /// </summary>
    internal static readonly TimeSpan StopDrain = TimeSpan.FromSeconds(1);

    private readonly string _pipeName;
    private readonly IPortalRequestHandler _handler;
    private readonly SecurityIdentifier _serviceIdentity;
    private readonly TimeSpan _squatRetry;
    private readonly TimeProvider _time;
    private readonly ILogger<CompanyPortalPipeServer> _logger;
    private readonly Lock _gate = new();
    private readonly HashSet<NamedPipeServerStream> _instances = [];
    private readonly List<Task> _serving = [];
    private readonly SemaphoreSlim _slots = new(MaxInstances, MaxInstances);
    private int _inFlight;

    public CompanyPortalPipeServer(
        IPortalRequestHandler handler, ILogger<CompanyPortalPipeServer> logger, TimeProvider? timeProvider = null)
        : this(CompanyPortalPipe.Name, handler, logger, timeProvider: timeProvider)
    {
    }

    /// <summary>
    /// For tests: a private pipe name, so a test never collides with an installed
    /// agent, and optionally the identity that owns the pipe.
    /// </summary>
    /// <param name="serviceIdentity">
    /// The account the service runs as, which gets full control -- including the
    /// right to create the further instances that let connections be served
    /// concurrently. LocalSystem in production, matching the installer. A test
    /// cannot run as SYSTEM, so a test that needs more than one instance passes its
    /// own identity; one that checks what an interactive user may do keeps the
    /// default, so the test user stays an ordinary interactive caller.
    /// </param>
    /// <param name="squatRetry">How long to wait before claiming the name again after finding it taken.</param>
    internal CompanyPortalPipeServer(
        string pipeName, IPortalRequestHandler handler, ILogger<CompanyPortalPipeServer> logger,
        SecurityIdentifier? serviceIdentity = null, TimeProvider? timeProvider = null, TimeSpan? squatRetry = null)
    {
        ArgumentNullException.ThrowIfNull(handler);

        _pipeName = pipeName;
        _handler = handler;
        _logger = logger;
        _serviceIdentity = serviceIdentity ?? new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        _time = timeProvider ?? TimeProvider.System;
        _squatRetry = squatRetry ?? DefaultSquatRetry;
    }

    /// <summary>Instances this server holds right now, listening or serving. For tests and diagnostics.</summary>
    internal int InstanceCount
    {
        get
        {
            lock (_gate)
            {
                return _instances.Count;
            }
        }
    }

    /// <summary>Connections accepted and not yet finished with, clean-up included. For tests and diagnostics.</summary>
    internal int InFlightCount => Volatile.Read(ref _inFlight);

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);

        Task[] serving;
        lock (_gate)
        {
            serving = [.. _serving];
            _serving.Clear();
        }

        // Already cut off by the stop; a moment to run their clean-up, so a
        // dispose that follows finds nothing still holding a slot. Bounded.
        try
        {
            await Task.WhenAll(serving).WaitAsync(StopDrain, cancellationToken);
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
        {
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            // A free slot first: MaxInstances is the pipe's own ceiling, and asking
            // Windows for one more would fail exactly as if the name were taken.
            try
            {
                await _slots.WaitAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            NamedPipeServerStream listener;
            try
            {
                listener = CreateInstance();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // FirstPipeInstance refused: the name is already in use by someone
                // else. Do not share it; wait and try to claim it again.
                _slots.Release();
                _logger.LogError(ex,
                    "The Company Portal pipe name is already in use by another process, or the pipe could not be " +
                    "created; the portal is unavailable until it is released. Retrying.");
                await DelayAsync(_squatRetry, stoppingToken);
                continue;
            }

            try
            {
                await listener.WaitForConnectionAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                Release(listener);
                break;
            }
            catch (IOException)
            {
                // The client went away between connecting and being accepted.
                Release(listener);
                continue;
            }

            // Served on its own; the loop goes straight back to listening so a
            // second session is not kept waiting on the first's server call.
            Interlocked.Increment(ref _inFlight);
            var serving = ServeAsync(listener, stoppingToken);
            lock (_gate)
            {
                _serving.RemoveAll(t => t.IsCompleted);
                _serving.Add(serving);
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
            claimName = _instances.Count == 0;
        }

        var options = PipeOptions.Asynchronous | (claimName ? PipeOptions.FirstPipeInstance : PipeOptions.None);

        var instance = NamedPipeServerStreamAcl.Create(
            _pipeName,
            PipeDirection.InOut,
            MaxInstances,
            PipeTransmissionMode.Byte,
            options,
            inBufferSize: CompanyPortalProtocol.MaxRequestBytes * 4,
            outBufferSize: CompanyPortalProtocol.MaxResponseBytes,
            BuildSecurity(_serviceIdentity));

        lock (_gate)
        {
            _instances.Add(instance);
        }

        return instance;
    }

    private void Release(NamedPipeServerStream instance)
    {
        bool held;
        lock (_gate)
        {
            held = _instances.Remove(instance);
        }

        instance.Dispose();

        if (held)
        {
            try
            {
                _slots.Release();
            }
            catch (ObjectDisposedException)
            {
                // The server was disposed while this connection was still being
                // served. Its slot is not needed again.
            }
        }
    }

    /// <summary>The service account: full control. Interactive users: read and write only. Nobody else: nothing.</summary>
    internal static PipeSecurity BuildSecurity(SecurityIdentifier? serviceIdentity = null)
    {
        var security = new PipeSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);

        security.AddAccessRule(new PipeAccessRule(
            serviceIdentity ?? new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            PipeAccessRights.FullControl,
            AccessControlType.Allow));

        // ReadWrite is the two generic halves a duplex client asks for, and no more:
        // it carries neither CreateNewInstance, which would let a user add an
        // instance of the service's own pipe and answer other sessions, nor a
        // change of permissions or owner.
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.InteractiveSid, null),
            PipeAccessRights.ReadWrite | PipeAccessRights.Synchronize,
            AccessControlType.Allow));

        return security;
    }

    private async Task ServeAsync(NamedPipeServerStream pipe, CancellationToken stoppingToken)
    {
        var sessionId = SessionNoticePipe.ClientSessionId(pipe.SafePipeHandle);
        var processId = SessionNoticePipe.ClientProcessId(pipe.SafePipeHandle);
        var callerKey = CompanyPortalPipe.CallerKeyFor(sessionId);

        try
        {
            PortalResponse response;
            var line = await ReadRequestLineAsync(pipe, stoppingToken);
            if (line is null)
            {
                _logger.LogWarning(
                    "Company Portal request from {Caller} (process {Pid}) refused: no complete request within the limits.",
                    callerKey, processId);
                response = PortalResponse.Failed(PortalError.BadRequest);
            }
            else if (!CompanyPortalProtocol.TryDecodeRequest(line.Value.Span, out var request) || request is null)
            {
                _logger.LogWarning(
                    "Company Portal request from {Caller} (process {Pid}) refused: not a request this service understands.",
                    callerKey, processId);
                response = PortalResponse.Failed(PortalError.BadRequest);
            }
            else
            {
                response = await AnswerAsync(request, callerKey, sessionId, processId, stoppingToken);
            }

            await WriteResponseAsync(pipe, response, callerKey, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Stopping. The portal sees the pipe close and tries again later.
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
        {
            _logger.LogDebug(ex,
                "Company Portal connection from {Caller} (process {Pid}) ended before it was answered.",
                callerKey, processId);
        }
        catch (Exception ex)
        {
            // Nothing above should let this through; if something does, it is
            // logged here rather than left as an unobserved fault at shutdown.
            _logger.LogError(ex,
                "Company Portal connection from {Caller} (process {Pid}) failed unexpectedly.",
                callerKey, processId);
        }
        finally
        {
            Release(pipe);
            Interlocked.Decrement(ref _inFlight);
        }
    }

    /// <summary>
    /// One newline-terminated line of at most <see cref="CompanyPortalProtocol.MaxRequestBytes"/>,
    /// or null when the client sent more than that without a newline, hung up
    /// first, or took longer than <see cref="ReadTimeout"/>.
    /// </summary>
    private async Task<ReadOnlyMemory<byte>?> ReadRequestLineAsync(Stream stream, CancellationToken stoppingToken)
    {
        using var timeout = new CancellationTokenSource(ReadTimeout, _time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, timeout.Token);

        var buffer = new byte[CompanyPortalProtocol.MaxRequestBytes + 1];
        var filled = 0;
        try
        {
            while (filled < buffer.Length)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(filled), linked.Token);
                if (read == 0)
                {
                    return null;
                }

                var newline = Array.IndexOf(buffer, (byte)'\n', filled, read);
                filled += read;
                if (newline >= 0)
                {
                    return buffer.AsMemory(0, newline);
                }
            }
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !stoppingToken.IsCancellationRequested)
        {
            // Took too long; a portal sends its whole line at once.
        }

        return null;
    }

    private async Task<PortalResponse> AnswerAsync(
        PortalRequest request, string callerKey, uint? sessionId, int? processId, CancellationToken stoppingToken)
    {
        var requestedBy = sessionId is { } session ? SessionUser.NameOf(session) : null;

        PortalResponse response;
        try
        {
            response = await _handler.HandleAsync(request, callerKey, requestedBy, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Whatever went wrong on the way to the server, the portal gets one
            // of its fixed words and this service keeps serving.
            _logger.LogWarning(ex,
                "Company Portal {Operation} request from {Caller} (process {Pid}) failed.",
                request.Operation, callerKey, processId);
            return PortalResponse.Failed(PortalError.ServerUnavailable);
        }

        switch (response.Error)
        {
            case null:
                _logger.LogDebug(
                    "Company Portal {Operation} request from {Caller} (process {Pid}) answered.",
                    request.Operation, callerKey, processId);
                break;

            case PortalError.TooManyRequests:
            case PortalError.BadRequest:
                _logger.LogWarning(
                    "Company Portal {Operation} request from {Caller} (process {Pid}) refused: {Error}.",
                    request.Operation, callerKey, processId, response.Error);
                break;

            default:
                _logger.LogInformation(
                    "Company Portal {Operation} request from {Caller} (process {Pid}) not answered: {Error}.",
                    request.Operation, callerKey, processId, response.Error);
                break;
        }

        return response;
    }

    private async Task WriteResponseAsync(
        NamedPipeServerStream pipe, PortalResponse response, string callerKey, CancellationToken stoppingToken)
    {
        var line = EncodeWithinBound(response, out var dropped);
        if (dropped > 0)
        {
            _logger.LogWarning(
                "Company Portal catalog for {Caller} is larger than the pipe carries: {Dropped} of {Total} apps dropped from the end of the list.",
                callerKey, dropped, response.Apps!.Count);
        }

        using var timeout = new CancellationTokenSource(WriteTimeout, _time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, timeout.Token);

        await pipe.WriteAsync(line, linked.Token);
        await pipe.FlushAsync(linked.Token);
    }

    /// <summary>
    /// The response as one line within the bound the portal reads. A catalog
    /// that does not fit is cut from the end -- the server sends it sorted by
    /// name -- until it does: the portal refuses a longer line outright, and a
    /// device with that many apps would otherwise never see any of them.
    /// </summary>
    /// <param name="dropped">How many apps were left out; zero for every other response.</param>
    internal static byte[] EncodeWithinBound(PortalResponse response, out int dropped)
    {
        dropped = 0;
        var line = CompanyPortalProtocol.EncodeResponse(response);
        if (line.Length <= CompanyPortalProtocol.MaxResponseBytes || response.Apps is not { Count: > 0 } apps)
        {
            return line;
        }

        // Every app adds bytes, so the longest prefix that fits is found by halving.
        var keep = 0;
        var fit = CompanyPortalProtocol.EncodeResponse(new PortalResponse(Apps: []));
        var low = 1;
        var high = apps.Count - 1;
        while (low <= high)
        {
            var middle = low + (high - low) / 2;
            var candidate = CompanyPortalProtocol.EncodeResponse(new PortalResponse(Apps: apps.Take(middle).ToList()));
            if (candidate.Length <= CompanyPortalProtocol.MaxResponseBytes)
            {
                keep = middle;
                fit = candidate;
                low = middle + 1;
            }
            else
            {
                high = middle - 1;
            }
        }

        dropped = apps.Count - keep;
        return fit;
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

    public override void Dispose()
    {
        _slots.Dispose();
        base.Dispose();
    }
}
