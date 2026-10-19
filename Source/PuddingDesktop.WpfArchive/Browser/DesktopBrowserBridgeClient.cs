using System.Net.WebSockets;
using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using Pudding.Contracts.Desktop;
using PuddingBrowser.Protocol;

namespace PuddingDesktop.Browser;

/// <summary>
/// Per-connection session state. Each connection attempt creates a new instance.
/// Old generation sessions cannot affect new generation state.
/// </summary>
internal sealed class DesktopBrowserClientConnection : IAsyncDisposable
{
    public long Generation { get; }
    public IDesktopBrowserWebSocket Socket { get; }
    public CancellationTokenSource Lifetime { get; }
    public Channel<BrowserBridgeEnvelope> Outbound { get; }
    public TaskCompletionSource<BrowserBridgeHelloAck> HelloAck { get; }
    public TaskCompletionSource ReceiveStarted { get; }
    public DateTimeOffset LastReceivedAt { get; set; }
    public Task SendTask { get; set; } = Task.CompletedTask;
    public Task ReceiveTask { get; set; } = Task.CompletedTask;
    public Task HeartbeatTask { get; set; } = Task.CompletedTask;
    public Task WatchdogTask { get; set; } = Task.CompletedTask;
    public Task CommandTask { get; set; } = Task.CompletedTask;
    public Channel<PendingBrowserCommand> Commands { get; } = Channel.CreateBounded<PendingBrowserCommand>(
        new BoundedChannelOptions(128) { SingleReader = true, SingleWriter = true });
    public ConcurrentDictionary<Guid, PendingBrowserCommand> PendingCommands { get; } = new();
    public string? CompletionReason { get; private set; }

    private int _completed;
    private int _disposed;

    public DesktopBrowserClientConnection(
        long generation,
        IDesktopBrowserWebSocket socket,
        CancellationTokenSource lifetime,
        IBrowserBridgeClock clock)
    {
        Generation = generation;
        Socket = socket;
        Lifetime = lifetime;
        LastReceivedAt = clock.UtcNow;
        HelloAck = new TaskCompletionSource<BrowserBridgeHelloAck>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        ReceiveStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        Outbound = Channel.CreateBounded<BrowserBridgeEnvelope>(new BoundedChannelOptions(128)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false
        });
    }

    /// <summary>
    /// Atomically completes this session. Only the first call triggers teardown.
    /// </summary>
    public bool TryComplete(string? reason = null)
    {
        if (Interlocked.Exchange(ref _completed, 1) != 0) return false;
        CompletionReason = reason;
        Outbound.Writer.TryComplete();
        Commands.Writer.TryComplete();
        Lifetime.Cancel();
        return true;
    }

    public bool IsCompleted => Volatile.Read(ref _completed) != 0;

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        TryComplete();
        await Socket.DisposeAsync();
        Lifetime.Dispose();
    }
}

internal sealed record PendingBrowserCommand(
    BrowserBridgeEnvelope Envelope, BrowserBridgeCommand Command, CancellationTokenSource Cancellation);

/// <summary>
/// WebSocket client that connects to Core's /desktop/browser-bridge endpoint.
/// 
/// Fixed architecture (Phase 2A-1 final acceptance):
/// - Receive Loop starts BEFORE Hello is sent (fixes HelloAck deadlock)
/// - Independent Watchdog cancels blocked ReceiveAsync on timeout
/// - Per-connection session objects prevent generation cross-contamination
/// - IDesktopBrowserWebSocket interface enables deterministic testing
/// - Single reconnect task (no recursive loops)
/// </summary>
public sealed class DesktopBrowserBridgeClient : IDesktopBrowserBridgeClient
{
    private readonly BrowserBridgeCommandDispatcher _dispatcher;
    private readonly IDesktopBrowserWebSocketFactory _wsFactory;
    private readonly IBrowserBridgeClock _clock;
    private readonly DesktopBrowserBridgeClientOptions _options;
    private readonly string _desktopInstanceId = Guid.NewGuid().ToString("N");

    private readonly object _stateLock = new();
    private volatile BrowserBridgeConnectionState _state = BrowserBridgeConnectionState.Disconnected;

    private Uri? _coreBaseAddress;
    private string? _controlToken;
    private CancellationTokenSource? _lifetimeCts;
    private bool _desiredConnected;
    private bool _disposed;

    // Per-connection state (guarded by generation)
    private long _generation;
    private DesktopBrowserClientConnection? _connection;
    private Task? _reconnectTask;

    public BrowserBridgeConnectionState State => _state;

    /// <summary>
    /// 最近一次成功 HelloAck 中 Core 的构建身份（D9 诊断用；null = 尚未握手或旧 Core 未携带）。
    /// 与发布清单核对由部署/诊断层完成，不参与任何授权判断。
    /// </summary>
    public BrowserBridgeBuildIdentity? LastDesktopBuildIdentity { get; private set; }

    public event EventHandler<BrowserBridgeStateChangedEventArgs>? StateChanged;

    public DesktopBrowserBridgeClient(
        BrowserBridgeCommandDispatcher dispatcher,
        IDesktopBrowserWebSocketFactory? wsFactory = null,
        IBrowserBridgeClock? clock = null,
        DesktopBrowserBridgeClientOptions? options = null)
    {
        _dispatcher = dispatcher;
        _wsFactory = wsFactory ?? new DefaultDesktopBrowserWebSocketFactory();
        _clock = clock ?? new SystemBrowserBridgeClock();
        _options = options ?? new DesktopBrowserBridgeClientOptions();
        if (_options.HelloTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(options), "Hello timeout must be positive.");
        if (_options.WatchdogInterval <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(options), "Watchdog interval must be positive.");
        if (_options.ReconnectDelays.Count == 0 || _options.ReconnectDelays.Any(delay => delay < TimeSpan.Zero))
            throw new ArgumentOutOfRangeException(nameof(options), "Reconnect delays must be non-empty and non-negative.");
    }

    public async Task ConnectAsync(Uri coreBaseAddress, string controlToken, CancellationToken cancellationToken)
    {
        DesktopBrowserClientConnection? existing;
        lock (_stateLock)
        {
            if (_desiredConnected && _coreBaseAddress == coreBaseAddress && _controlToken == controlToken
                && _state is BrowserBridgeConnectionState.Connected or BrowserBridgeConnectionState.Connecting)
            {
                return;
            }

            _coreBaseAddress = coreBaseAddress;
            _controlToken = controlToken;
            _desiredConnected = true;
            if (_lifetimeCts is null || _lifetimeCts.IsCancellationRequested)
            {
                _lifetimeCts?.Dispose();
                _lifetimeCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            }
            existing = _connection;
        }

        if (existing is not null)
            await CloseSessionAsync(existing, cancellationToken, closeSocket: true);

        await ConnectInternalAsync(_lifetimeCts!.Token);
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken)
    {
        CancellationTokenSource? lifetime;
        DesktopBrowserClientConnection? conn;
        Task? reconnect;
        lock (_stateLock)
        {
            _desiredConnected = false;
            lifetime = _lifetimeCts;
            _lifetimeCts = null;
            conn = _connection;
            reconnect = _reconnectTask;
            _reconnectTask = null;
        }

        lifetime?.Cancel();
        if (conn is not null)
            await CloseSessionAsync(conn, cancellationToken, closeSocket: true);
        if (reconnect is not null)
            await reconnect.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        lifetime?.Dispose();
            TransitionTo(BrowserBridgeConnectionState.Disconnected, "Intentional disconnect");
            _dispatcher.Lifecycle.NotifyBridgeDisconnected();
    }

    /// <summary>
    /// Core connection logic. Fixed sequence:
    /// 1. Create session
    /// 2. Start SendLoop
    /// 3. Start ReceiveLoop (BEFORE Hello — fixes deadlock)
    /// 4. Enqueue Hello
    /// 5. Await HelloAck (5s)
    /// 6. If accepted: Connected → start Heartbeat + Watchdog
    /// 7. If rejected/timeout: complete session once
    /// </summary>
    private async Task ConnectInternalAsync(CancellationToken ct)
    {
        var gen = Interlocked.Increment(ref _generation);
        TransitionTo(BrowserBridgeConnectionState.Connecting, null);

        var ws = _wsFactory.Create();
        var wsUri = BuildWebSocketUri(_coreBaseAddress!);
        ws.SetRequestHeader(BrowserBridgeProtocol.ControlTokenHeader, _controlToken!);

        try
        {
            await ws.ConnectAsync(wsUri, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await ws.DisposeAsync();
            TransitionTo(BrowserBridgeConnectionState.Failed, $"Connection failed: {ex.Message}");
            ScheduleReconnect(gen, ct);
            return;
        }
        catch (OperationCanceledException)
        {
            await ws.DisposeAsync();
            return;
        }

        // Create per-connection session
        var connectionCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var session = new DesktopBrowserClientConnection(gen, ws, connectionCts, _clock);

        lock (_stateLock)
        {
            _connection = session;
        }

        var connCt = connectionCts.Token;

        // Step 2: Start Send Loop (only writer to WebSocket)
        session.SendTask = Task.Run(() => SendLoopAsync(session, connCt), CancellationToken.None);

        // Step 3: Start Receive Loop BEFORE Hello (fixes HelloAck deadlock)
        session.ReceiveTask = Task.Run(() => ReceiveLoopAsync(session, connCt), CancellationToken.None);
        session.CommandTask = Task.Run(() => CommandLoopAsync(session, connCt), CancellationToken.None);

        // Do not merely schedule the receive loop: wait until it has actually
        // entered its receive phase before making Hello observable on the wire.
        await session.ReceiveStarted.Task.WaitAsync(connCt);

        // Step 4: Enqueue Hello
        var helloEnvelope = new BrowserBridgeEnvelope
        {
            MessageId = Guid.NewGuid(),
            Kind = BrowserBridgeMessageKind.Hello,
            CreatedAt = _clock.UtcNow,
            Payload = JsonSerializer.SerializeToElement(new BrowserBridgeHello
            {
                ProtocolVersion = BrowserBridgeProtocol.CurrentVersion,
                DesktopInstanceId = _desktopInstanceId,
                Capabilities = ["context", "page", "navigation", "snapshot", "locator", "interact", "wait"],
                // D9：握手携带本进程构建身份，供与发布清单/日志核对（不参与授权）。
                Build = BrowserBridgeBuildIdentity.FromAssembly(
                    System.Reflection.Assembly.GetEntryAssembly()
                        ?? typeof(DesktopBrowserBridgeClient).Assembly),
            }, BrowserBridgeSerializerOptions.Default)
        };

        try
        {
            await session.Outbound.Writer.WriteAsync(helloEnvelope, connCt);
        }
        catch (OperationCanceledException)
        {
            await session.DisposeAsync();
            return;
        }

        // Step 5: Await HelloAck (5s timeout)
        BrowserBridgeHelloAck? ack = null;
        using (var helloTimeoutCts = CancellationTokenSource.CreateLinkedTokenSource(connCt))
        {
            helloTimeoutCts.CancelAfter(_options.HelloTimeout);
            try
            {
                ack = await session.HelloAck.Task.WaitAsync(helloTimeoutCts.Token);
            }
            catch (OperationCanceledException)
            {
                // timeout or connection cancelled
            }
        }

        // Step 6/7: Check result
        if (ack is not { Accepted: true } || session.IsCompleted || connCt.IsCancellationRequested)
        {
            var reason = session.CompletionReason
                ?? (ack is { Accepted: false } ? $"Hello rejected: {ack.ErrorCode}: {ack.ErrorMessage}"
                    : connCt.IsCancellationRequested ? "Hello cancelled" : "Hello timeout");
            session.TryComplete(reason);
            if (Interlocked.Read(ref _generation) == gen)
                TransitionTo(BrowserBridgeConnectionState.Failed, reason);
            await CloseSessionAsync(session, CancellationToken.None, closeSocket: true);
            ScheduleReconnect(gen, ct);
            return;
        }

        // HelloAck accepted — truly Connected
        TransitionTo(BrowserBridgeConnectionState.Connected, null);

        // 把「桥已连接」翻译成「准入权威已活」。此前该通知在生产代码零调用者，
        // 于是所有浏览器命令都停在「the desktop connection is not established」（方案 §10.4）。
        _dispatcher.Lifecycle.NotifyBridgeConnected(gen);

        // Start Heartbeat and Watchdog
        session.HeartbeatTask = Task.Run(() => HeartbeatLoopAsync(session, connCt), CancellationToken.None);
        session.WatchdogTask = Task.Run(() => WatchdogLoopAsync(session, connCt), CancellationToken.None);
    }

    private async Task SendLoopAsync(DesktopBrowserClientConnection session, CancellationToken ct)
    {
        try
        {
            await foreach (var envelope in session.Outbound.Reader.ReadAllAsync(ct))
            {
                if (session.Socket.State != WebSocketState.Open) break;
                var bytes = BrowserBridgeSerializer.Serialize(envelope);
                await session.Socket.SendAsync(bytes, WebSocketMessageType.Text, true, ct);
            }
        }
        catch (OperationCanceledException) { /* normal */ }
        catch (WebSocketException) { CompleteConnectionOnce(session, "Send failed"); }
        catch (Exception) { CompleteConnectionOnce(session, "Send unexpected error"); }
    }

    /// <summary>
    /// Receive Loop: runs from connection start (before Hello).
    /// Handles HelloAck during handshake phase, then Commands/Heartbeats after.
    /// </summary>
    private async Task ReceiveLoopAsync(DesktopBrowserClientConnection session, CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];
        using var messageBuffer = new MemoryStream();
        session.ReceiveStarted.TrySetResult();

        try
        {
            while (session.Socket.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                messageBuffer.SetLength(0);
                ValueWebSocketReceiveResult result;

                do
                {
                    result = await session.Socket.ReceiveAsync(buffer, ct);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        CompleteConnectionOnce(session, "Server closed connection");
                        return;
                    }
                    if (result.MessageType == WebSocketMessageType.Binary)
                    {
                        CompleteConnectionOnce(session, "Binary message (unsupported)");
                        return;
                    }
                    messageBuffer.Write(buffer, 0, result.Count);
                    if (messageBuffer.Length > BrowserBridgeProtocol.MaxMessageBytes)
                    {
                        CompleteConnectionOnce(session, "Message too large");
                        return;
                    }
                } while (!result.EndOfMessage);

                var span = new ReadOnlySpan<byte>(messageBuffer.GetBuffer(), 0, (int)messageBuffer.Length);
                BrowserBridgeEnvelope envelope;
                try
                {
                    envelope = BrowserBridgeSerializer.Deserialize(span);
                }
                catch (BrowserBridgeProtocolException ex)
                {
                    CompleteConnectionOnce(session, $"{ex.ErrorCode}: {ex.Message}");
                    return;
                }
                catch (JsonException)
                {
                    CompleteConnectionOnce(session, "Malformed protocol message");
                    return;
                }

                session.LastReceivedAt = _clock.UtcNow;
                await HandleEnvelopeAsync(session, envelope, ct);
            }
        }
        catch (OperationCanceledException) { /* normal shutdown */ }
        catch (BrowserBridgeProtocolException ex) { CompleteConnectionOnce(session, $"{ex.ErrorCode}: {ex.Message}"); }
        catch (JsonException) { CompleteConnectionOnce(session, "Invalid protocol payload or required fields"); }
        catch (WebSocketException) { CompleteConnectionOnce(session, "WebSocket receive error"); }
        catch (Exception) { CompleteConnectionOnce(session, "Unexpected receive error"); }
    }

    /// <summary>
    /// Independent Watchdog: checks LastReceivedAt every 1-5 seconds.
    /// If exceeded DefaultHeartbeatTimeout, cancels the session CTS,
    /// which unblocks the ReceiveAsync in the Receive Loop.
    /// </summary>
    private async Task WatchdogLoopAsync(DesktopBrowserClientConnection session, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await _clock.DelayAsync(_options.WatchdogInterval, ct);

                var elapsed = _clock.UtcNow - session.LastReceivedAt;
                if (elapsed > BrowserBridgeProtocol.DefaultHeartbeatTimeout)
                {
                    CompleteConnectionOnce(session, "Heartbeat timeout (watchdog)");
                    return;
                }
            }
        }
        catch (OperationCanceledException) { /* normal */ }
    }

    private async Task HeartbeatLoopAsync(DesktopBrowserClientConnection session, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await _clock.DelayAsync(BrowserBridgeProtocol.DefaultHeartbeatInterval, ct);

                var heartbeat = new BrowserBridgeEnvelope
                {
                    MessageId = Guid.NewGuid(),
                    Kind = BrowserBridgeMessageKind.Heartbeat,
                    CreatedAt = _clock.UtcNow,
                    Payload = JsonSerializer.SerializeToElement(new { },
                        BrowserBridgeSerializerOptions.Default)
                };

                session.Outbound.Writer.TryWrite(heartbeat);
            }
        }
        catch (OperationCanceledException) { /* normal */ }
    }

    private async Task HandleEnvelopeAsync(
        DesktopBrowserClientConnection session, BrowserBridgeEnvelope envelope, CancellationToken ct)
    {
        if (envelope.Kind != BrowserBridgeMessageKind.HelloAck
            && (!session.HelloAck.Task.IsCompletedSuccessfully
                || session.HelloAck.Task.Result is not { Accepted: true }))
        {
            CompleteConnectionOnce(session, "Message received before accepted HelloAck");
            return;
        }

        switch (envelope.Kind)
        {
            case BrowserBridgeMessageKind.HelloAck:
                var ack = BrowserBridgeSerializer.DeserializePayload<BrowserBridgeHelloAck>(envelope);
                if (ack.ProtocolVersion != BrowserBridgeProtocol.CurrentVersion)
                {
                    CompleteConnectionOnce(session,
                        $"{BrowserBridgeErrorCodes.BrowserProtocolMismatch}: HelloAck expected={BrowserBridgeProtocol.CurrentVersion} received={ack.ProtocolVersion}");
                    return;
                }
                LastDesktopBuildIdentity = ack.Build;
                if (ack.Build is { } coreBuild)
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"[BrowserBridge] HelloAck from Core {ack.ProtocolVersion}: {coreBuild.Describe()}");
                }

                session.HelloAck.TrySetResult(ack);
                break;

            case BrowserBridgeMessageKind.Command:
                var command = BrowserBridgeSerializer.DeserializePayload<BrowserBridgeCommand>(envelope);
                if (!TryAcceptCaller(command.Caller, out var callerError))
                {
                    await EnqueueResultAsync(
                        session,
                        new BrowserBridgeCommandResult
                        {
                            OperationId = command.OperationId,
                            Success = false,
                            ErrorCode = BrowserBridgeErrorCodes.BrowserUnauthorized,
                            ErrorMessage = callerError,
                        },
                        envelope.MessageId,
                        ct);
                    break;
                }

                // Keep execution ordered, but never await it in the socket receive pump:
                // a screenshot/wait must not prevent heartbeat ACKs or cancellation.
                var pending = new PendingBrowserCommand(envelope, command,
                    CancellationTokenSource.CreateLinkedTokenSource(ct));
                if (!session.PendingCommands.TryAdd(envelope.MessageId, pending))
                {
                    pending.Cancellation.Dispose();
                    CompleteConnectionOnce(session, "Duplicate pending command message id");
                    break;
                }
                if (!session.Commands.Writer.TryWrite(pending))
                {
                    session.PendingCommands.TryRemove(envelope.MessageId, out _);
                    pending.Cancellation.Dispose();
                    await EnqueueResultAsync(session, new BrowserBridgeCommandResult
                    {
                        OperationId = command.OperationId, Success = false,
                        ErrorCode = BrowserBridgeErrorCodes.BrowserNotAvailable,
                        ErrorMessage = "Browser command queue is full; command was not dispatched",
                    }, envelope.MessageId, ct);
                }
                break;

            case BrowserBridgeMessageKind.Cancel:
                var cancel = BrowserBridgeSerializer.DeserializePayload<BrowserBridgeCancel>(envelope);
                foreach (var queued in session.PendingCommands.Values)
                {
                    if (queued.Command.OperationId != cancel.OperationId) continue;
                    try { queued.Cancellation.Cancel(); }
                    catch (ObjectDisposedException) { /* completed concurrently */ }
                }
                _dispatcher.Cancel(cancel.OperationId);
                break;

            case BrowserBridgeMessageKind.Heartbeat:
                await EnqueueHeartbeatAckAsync(session, envelope.MessageId, ct);
                break;

            case BrowserBridgeMessageKind.HeartbeatAck:
                // Core acknowledged our heartbeat — LastReceivedAt already updated
                break;
        }
    }

    private async Task CommandLoopAsync(DesktopBrowserClientConnection session, CancellationToken ct)
    {
        try
        {
            await foreach (var pending in session.Commands.Reader.ReadAllAsync(ct))
            {
                try
                {
                    BrowserBridgeCommandResult result;
                    if (pending.Cancellation.IsCancellationRequested)
                        result = new BrowserBridgeCommandResult
                        {
                            OperationId = pending.Command.OperationId, Success = false,
                            ErrorCode = BrowserBridgeErrorCodes.BrowserCancelled,
                            ErrorMessage = "Command cancelled before dispatch",
                        };
                    else
                        result = await _dispatcher.DispatchAsync(pending.Command, pending.Cancellation.Token);
                    await EnqueueResultAsync(session, result, pending.Envelope.MessageId, ct);
                }
                finally
                {
                    session.PendingCommands.TryRemove(pending.Envelope.MessageId, out _);
                    pending.Cancellation.Dispose();
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception) { CompleteConnectionOnce(session, "Command worker failed"); }
        finally
        {
            while (session.Commands.Reader.TryRead(out var pending))
            {
                session.PendingCommands.TryRemove(pending.Envelope.MessageId, out _);
                pending.Cancellation.Dispose();
            }
        }
    }

    private async Task EnqueueResultAsync(
        DesktopBrowserClientConnection session, BrowserBridgeCommandResult result,
        Guid correlationId, CancellationToken ct)
    {
        var envelope = new BrowserBridgeEnvelope
        {
            MessageId = Guid.NewGuid(),
            CorrelationId = correlationId,
            Kind = BrowserBridgeMessageKind.CommandResult,
            CreatedAt = _clock.UtcNow,
            Payload = JsonSerializer.SerializeToElement(result, BrowserBridgeSerializerOptions.Default)
        };

        try { await session.Outbound.Writer.WriteAsync(envelope, ct); }
        catch (OperationCanceledException) { /* shutting down */ }
    }

    private async Task EnqueueHeartbeatAckAsync(
        DesktopBrowserClientConnection session, Guid correlationId, CancellationToken ct)
    {
        var ack = new BrowserBridgeEnvelope
        {
            MessageId = Guid.NewGuid(),
            CorrelationId = correlationId,
            Kind = BrowserBridgeMessageKind.HeartbeatAck,
            CreatedAt = _clock.UtcNow,
            Payload = JsonSerializer.SerializeToElement(new { }, BrowserBridgeSerializerOptions.Default)
        };

        try { await session.Outbound.Writer.WriteAsync(ack, ct); }
        catch (OperationCanceledException) { /* shutting down */ }
    }

    /// <summary>
    /// Atomic: only the first call for a given session triggers teardown.
    /// Stale generation sessions cannot affect new generation state.
    /// </summary>
    private void CompleteConnectionOnce(DesktopBrowserClientConnection session, string reason)
    {
        if (!session.TryComplete(reason)) return; // already completed

        // Only transition state if this is still the current generation
        if (Interlocked.Read(ref _generation) != session.Generation) return;

        TransitionTo(BrowserBridgeConnectionState.Disconnected, reason);
        _dispatcher.Lifecycle.NotifyBridgeDisconnected();
        _dispatcher.FailAllPending(BrowserBridgeErrorCodes.BrowserBridgeDisconnected, reason);

        ScheduleReconnect(session.Generation, _lifetimeCts?.Token ?? CancellationToken.None);
    }

    /// <summary>
    /// Ensures only one reconnect task exists at any time.
    /// </summary>
    private void ScheduleReconnect(long failedGen, CancellationToken ct)
    {
        lock (_stateLock)
        {
            if (!_desiredConnected) return;
            if (_reconnectTask is { IsCompleted: false }) return;

            _reconnectTask = Task.Run(() => ReconnectLoopAsync(failedGen, ct), CancellationToken.None);
        }
    }

    private async Task ReconnectLoopAsync(long failedGen, CancellationToken ct)
    {
        var attempt = 0;
        while (!ct.IsCancellationRequested)
        {
            lock (_stateLock)
            {
                if (!_desiredConnected) return;
            }

            TransitionTo(BrowserBridgeConnectionState.Reconnecting, $"Attempt {attempt + 1}");
            var delay = _options.ReconnectDelays[Math.Min(attempt, _options.ReconnectDelays.Count - 1)];

            try { await _clock.DelayAsync(delay, ct); }
            catch (OperationCanceledException) { return; }

            attempt++;

            lock (_stateLock)
            {
                if (!_desiredConnected) return;
            }

            try
            {
                var failedSession = GetCurrentConnection(failedGen);
                if (failedSession is not null)
                    await CloseSessionAsync(failedSession, CancellationToken.None, closeSocket: true);

                await ConnectInternalAsync(ct);
                if (_state == BrowserBridgeConnectionState.Connected)
                    return;
            }
            catch (OperationCanceledException) { return; }
            catch { /* retry */ }
        }
    }

    private DesktopBrowserClientConnection? GetCurrentConnection(long generation)
    {
        lock (_stateLock)
            return _connection?.Generation == generation ? _connection : null;
    }

    private static async Task AwaitConnectionTasksAsync(DesktopBrowserClientConnection session)
    {
        await session.ReceiveTask.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        await session.CommandTask.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        await session.SendTask.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        await session.HeartbeatTask.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        await session.WatchdogTask.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
    }

    private async Task CloseSessionAsync(
        DesktopBrowserClientConnection session,
        CancellationToken cancellationToken,
        bool closeSocket)
    {
        session.TryComplete();

        if (closeSocket && session.Socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
        {
            try
            {
                await session.Socket.CloseAsync(
                    WebSocketCloseStatus.NormalClosure,
                    "Desktop connection closing",
                    cancellationToken);
            }
            catch { /* best effort */ }
        }

        await AwaitConnectionTasksAsync(session);
        await session.DisposeAsync();

        lock (_stateLock)
        {
            if (ReferenceEquals(_connection, session))
                _connection = null;
        }
    }

    private void TransitionTo(BrowserBridgeConnectionState newState, string? reason)
    {
        var old = _state;
        if (old == newState) return;
        _state = newState;
        StateChanged?.Invoke(this, new BrowserBridgeStateChangedEventArgs(old, newState, reason));
    }

    /// <summary>
    /// 校验 wire 形态的调用者身份（方案 §3.2）：解析 kind（未知取值拒绝）→ 构造领域身份（空/超长/子代理缺父任务由类型层拒绝）
    /// → 套用**唯一一份**要求规则（缺身份 / 冒充 User / 无运行身份）。
    /// 失败时给出具体原因；**不**返回默认身份。
    /// </summary>
    private static bool TryAcceptCaller(BrowserBridgeCallerIdentity? wire, out string reason)
    {
        if (wire is null)
        {
            reason = "the command carried no trusted browser caller identity";
            return false;
        }

        if (!BrowserCallerKindWire.TryParse(wire.Kind, out var kind))
        {
            reason = $"caller.kind '{wire.Kind}' is not a registered caller kind";
            return false;
        }

        BrowserCallerIdentity identity;
        try
        {
            identity = new BrowserCallerIdentity(
                kind,
                wire.CallerId,
                string.IsNullOrEmpty(wire.TaskId) ? null : wire.TaskId,
                string.IsNullOrEmpty(wire.ParentTaskId) ? null : wire.ParentTaskId);
        }
        catch (ArgumentException ex)
        {
            reason = $"caller identity is invalid: {ex.Message}";
            return false;
        }

        return BrowserCallerRequirement.TryRequireCaller(identity, out _, out reason);
    }
    private static Uri BuildWebSocketUri(Uri coreBaseAddress)
    {
        var scheme = coreBaseAddress.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase) ? "wss" : "ws";
        var builder = new UriBuilder(coreBaseAddress)
        {
            Scheme = scheme,
            Path = BrowserBridgeProtocol.EndpointPath
        };
        return builder.Uri;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        lock (_stateLock)
        {
            _desiredConnected = false;
        }

        var lifetime = _lifetimeCts;
        _lifetimeCts = null;
        lifetime?.Cancel();

        var session = _connection;
        if (session is not null)
            await CloseSessionAsync(session, CancellationToken.None, closeSocket: true);

        var reconnect = _reconnectTask;
        if (reconnect is not null)
            await reconnect.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);

        _reconnectTask = null;
        lifetime?.Dispose();
    }
}

/// <summary>
/// Shared serializer options for Desktop Browser Bridge.
/// </summary>
internal static class BrowserBridgeSerializerOptions
{
    public static readonly JsonSerializerOptions Default = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };
}
