using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using EndpointAgent.Core.Chat;
using EndpointAgent.Windows.SessionNotice;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace EndpointAgent.Windows.LiveChat;

/// <summary>
/// Hosts the live-chat pipe inside the LocalSystem service: takes one request per
/// connection from a session's chat window, answers it, and hangs up. A sent line
/// is relayed to the server here, on the device credential; the service shows and
/// acts on nothing a window gives it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Access.</b> SYSTEM has full control. Interactive users may read and write --
/// enough to send a line and read one back -- and nothing else: no new instance,
/// no change of permissions, no change of owner. Every other principal has no
/// entry and so no access.
/// </para>
/// <para>
/// <b>Every byte from a client is hostile</b> until the protocol has accepted it.
/// A connection gets one line, read into a buffer one byte longer than the longest
/// request the protocol allows and for no longer than <see cref="ReadTimeout"/>;
/// whatever does not fit, does not arrive or does not decode is answered
/// <see cref="ChatPipeError.BadRequest"/> and the connection closed.
/// </para>
/// <para>
/// <b>Rate-limited per session, the Company Portal way.</b> A session may send at
/// most <see cref="MaxSendsPerMinute"/> lines and poll at most
/// <see cref="MaxPollsPerMinute"/> times a minute; past that it is answered
/// <see cref="ChatPipeError.TooManyRequests"/> without the server being touched, so
/// a looping or hostile process in a session cannot turn the service into a way to
/// hammer the chat endpoint.
/// </para>
/// <para>
/// <b>One request per connection, name claimed not shared, concurrent serving,</b>
/// exactly as the Company Portal and screen-lock pipes, for the same reasons.
/// </para>
/// </remarks>
public sealed class LiveChatPipeServer : BackgroundService
{
    internal const int MaxInstances = 8;

    /// <summary>Messages a session may send in a minute. A person types; this is generous headroom.</summary>
    internal const int MaxSendsPerMinute = 60;

    /// <summary>Polls a session may make in a minute. The window polls about once a second; this is headroom.</summary>
    internal const int MaxPollsPerMinute = 180;

    internal static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(5);

    internal static readonly TimeSpan WriteTimeout = TimeSpan.FromSeconds(5);

    internal static readonly TimeSpan DefaultSquatRetry = TimeSpan.FromSeconds(30);

    internal static readonly TimeSpan StopDrain = TimeSpan.FromSeconds(1);

    private readonly string _pipeName;
    private readonly ILiveChatPipeHandler _handler;
    private readonly SecurityIdentifier _serviceIdentity;
    private readonly TimeSpan _squatRetry;
    private readonly TimeProvider _time;
    private readonly ILogger<LiveChatPipeServer> _logger;
    private readonly Lock _gate = new();
    private readonly HashSet<NamedPipeServerStream> _instances = [];
    private readonly List<Task> _serving = [];
    private readonly SemaphoreSlim _slots = new(MaxInstances, MaxInstances);
    private readonly Dictionary<string, List<DateTimeOffset>> _sendWindows = [];
    private readonly Dictionary<string, List<DateTimeOffset>> _pollWindows = [];
    private int _inFlight;

    public LiveChatPipeServer(
        ILiveChatPipeHandler handler, ILogger<LiveChatPipeServer> logger, TimeProvider? timeProvider = null)
        : this(LiveChatPipe.Name, handler, logger, timeProvider: timeProvider)
    {
    }

    /// <summary>For tests: a private pipe name, and optionally the identity that owns the pipe (see the portal server).</summary>
    internal LiveChatPipeServer(
        string pipeName, ILiveChatPipeHandler handler, ILogger<LiveChatPipeServer> logger,
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
                _slots.Release();
                _logger.LogError(ex,
                    "The live-chat pipe name is already in use by another process, or the pipe could not be " +
                    "created; the chat window cannot reach the service until it is released. Retrying.");
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
                Release(listener);
                continue;
            }

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
            claimName = _instances.Count == 0;
        }

        var options = PipeOptions.Asynchronous | (claimName ? PipeOptions.FirstPipeInstance : PipeOptions.None);

        var instance = NamedPipeServerStreamAcl.Create(
            _pipeName,
            PipeDirection.InOut,
            MaxInstances,
            PipeTransmissionMode.Byte,
            options,
            inBufferSize: LiveChatPipeProtocol.MaxRequestBytes * 2,
            outBufferSize: LiveChatPipeProtocol.MaxResponseBytes,
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
        // not CreateNewInstance (which would let a user add an instance of the
        // service's own pipe and answer other sessions), nor a change of permissions
        // or owner.
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
        var callerKey = LiveChatPipe.CallerKeyFor(sessionId);

        try
        {
            ChatPipeResponse response;
            var line = await ReadRequestLineAsync(pipe, stoppingToken);
            if (line is null)
            {
                response = ChatPipeResponse.Failed(ChatPipeError.BadRequest);
            }
            else if (!LiveChatPipeProtocol.TryDecodeRequest(line.Value.Span, out var request) || request is null)
            {
                _logger.LogWarning(
                    "Live-chat request from {Caller} (process {Pid}) refused: not a request this service understands.",
                    callerKey, processId);
                response = ChatPipeResponse.Failed(ChatPipeError.BadRequest);
            }
            else
            {
                response = await AnswerAsync(request, callerKey, processId, stoppingToken);
            }

            await WriteResponseAsync(pipe, response, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
        {
            _logger.LogDebug(ex,
                "Live-chat connection from {Caller} (process {Pid}) ended before it was answered.", callerKey, processId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Live-chat connection from {Caller} (process {Pid}) failed unexpectedly.", callerKey, processId);
        }
        finally
        {
            Release(pipe);
            Interlocked.Decrement(ref _inFlight);
        }
    }

    private async Task<ReadOnlyMemory<byte>?> ReadRequestLineAsync(Stream stream, CancellationToken stoppingToken)
    {
        using var timeout = new CancellationTokenSource(ReadTimeout, _time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, timeout.Token);

        var buffer = new byte[LiveChatPipeProtocol.MaxRequestBytes + 1];
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
        }

        return null;
    }

    private async Task<ChatPipeResponse> AnswerAsync(
        ChatPipeRequest request, string callerKey, int? processId, CancellationToken stoppingToken)
    {
        switch (request.Op)
        {
            case ChatPipeOp.Poll:
                if (!Admit(_pollWindows, callerKey, MaxPollsPerMinute))
                {
                    return ChatPipeResponse.Failed(ChatPipeError.TooManyRequests);
                }

                return _handler.Poll(request.AfterSeq);

            case ChatPipeOp.Send:
                // Rate-limit BEFORE the server is touched, so a flood of sends cannot
                // hammer the chat endpoint at machine speed.
                if (!Admit(_sendWindows, callerKey, MaxSendsPerMinute))
                {
                    _logger.LogWarning("Live-chat sends from {Caller} (process {Pid}) refused: too many in the last minute.", callerKey, processId);
                    return ChatPipeResponse.Failed(ChatPipeError.TooManyRequests);
                }

                bool accepted;
                try
                {
                    accepted = await _handler.RelayEmployeeMessageAsync(request.Body!, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Live-chat send from {Caller} (process {Pid}) could not be relayed to the server.", callerKey, processId);
                    return ChatPipeResponse.Sent(accepted: false);
                }

                return ChatPipeResponse.Sent(accepted);

            case ChatPipeOp.Closed:
                _handler.ReportClosed();
                return ChatPipeResponse.Acked();

            default:
                return ChatPipeResponse.Failed(ChatPipeError.BadRequest);
        }
    }

    private async Task WriteResponseAsync(NamedPipeServerStream pipe, ChatPipeResponse response, CancellationToken stoppingToken)
    {
        var line = LiveChatPipeProtocol.EncodeResponse(response);

        using var timeout = new CancellationTokenSource(WriteTimeout, _time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, timeout.Token);

        await pipe.WriteAsync(line, linked.Token);
        await pipe.FlushAsync(linked.Token);
    }

    /// <summary>Sliding one-minute window per caller, for one kind of request.</summary>
    private bool Admit(Dictionary<string, List<DateTimeOffset>> windows, string callerKey, int limit)
    {
        var now = _time.GetUtcNow();
        lock (_gate)
        {
            if (!windows.TryGetValue(callerKey, out var stamps))
            {
                stamps = [];
                windows[callerKey] = stamps;
            }

            stamps.RemoveAll(t => now - t >= TimeSpan.FromMinutes(1));
            if (stamps.Count >= limit)
            {
                return false;
            }

            stamps.Add(now);

            // Sessions come and go; forget the ones that have gone quiet.
            if (windows.Count > 64)
            {
                foreach (var stale in windows.Where(w => w.Value.Count == 0).Select(w => w.Key).ToList())
                {
                    windows.Remove(stale);
                }
            }

            return true;
        }
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
