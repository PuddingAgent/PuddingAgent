using System.Net.WebSockets;
using System.Text.Json;
using System.Threading.Channels;
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
    public DateTimeOffset LastReceivedAt { get; set; }

    private int _completed;

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
    public bool TryComplete()
    {
        if (Interlocked.Exchange(ref _completed, 1) != 0) return false;
        Outbound.Writer.TryComplete();
        Lifetime.Cancel();
        return true;
    }

    public bool IsCompleted => Volatile.Read(ref _completed) != 0;

    public async ValueTask DisposeAsync()
    {
        TryComplete();
        await Socket.DisposeAsync();
        Lifetime.Dispose();
    }
}

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
    private static readonly TimeSpan[] ReconnectDelays =
    [
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(10)
    ];

    private readonly BrowserBridgeCommandDispatcher _dispatcher;
    private readonly IDesktopBrowserWebSocketFactory _wsFactory;
    private readonly IBrowserBridgeClock _clock;
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
    private Task? _receiveTask;
    private Task? _sendTask;
    private Task? _heartbeatTask;
    private Task? _watchdogTask;
    private Task? _reconnectTask;

    public BrowserBridgeConnectionState State => _state;
    public event EventHandler<BrowserBridgeStateChangedEventArgs>? StateChanged;

    public DesktopBrowserBridgeClient(
        BrowserBridgeCommandDispatcher dispatcher,
        IDesktopBrowserWebSocketFactory? wsFactory = null,
        IBrowserBridgeClock? clock = null)
    {
        _dispatcher = dispatcher;
        _wsFactory = wsFactory ?? new DefaultDesktopBrowserWebSocketFactory();
        _clock = clock ?? new SystemBrowserBridgeClock();
    }

    public async Task ConnectAsync(Uri coreBaseAddress, string controlToken, CancellationToken cancellationToken)
    {
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
            _lifetimeCts ??= CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        }

        await ConnectInternalAsync(_lifetimeCts!.Token);
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken)
    {
        lock (_stateLock)
        {
            _desiredConnected = false;
        }

        var conn = _connection;
        if (conn is not null)
        {
            conn.TryComplete();

            if (conn.Socket.State == WebSocketState.Open)
            {
                try
                {
                    await conn.Socket.CloseAsync(WebSocketCloseStatus.NormalClosure,
                        "Desktop disconnecting", cancellationToken);
                }
                catch { /* best effort */ }
            }
        }

        await AwaitConnectionTasksAsync();
        CleanupConnection();
        TransitionTo(BrowserBridgeConnectionState.Disconnected, "Intentional disconnect");
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
        _sendTask = Task.Run(() => SendLoopAsync(session, connCt), CancellationToken.None);

        // Step 3: Start Receive Loop BEFORE Hello (fixes HelloAck deadlock)
        _receiveTask = Task.Run(() => ReceiveLoopAsync(session, connCt), CancellationToken.None);

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
                Capabilities = ["context", "page", "navigation"]
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
            helloTimeoutCts.CancelAfter(TimeSpan.FromSeconds(5));
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
        if (ack is not { Accepted: true })
        {
            session.TryComplete();
            await AwaitConnectionTasksAsync();
            await session.DisposeAsync();
            CleanupConnection();
            TransitionTo(BrowserBridgeConnectionState.Failed,
                ack is not null ? $"Hello rejected: {ack.ErrorMessage}" : "Hello timeout");
            ScheduleReconnect(gen, ct);
            return;
        }

        // HelloAck accepted — truly Connected
        TransitionTo(BrowserBridgeConnectionState.Connected, null);

        // Start Heartbeat and Watchdog
        _heartbeatTask = Task.Run(() => HeartbeatLoopAsync(session, connCt), CancellationToken.None);
        _watchdogTask = Task.Run(() => WatchdogLoopAsync(session, connCt), CancellationToken.None);
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
        var messageBuffer = new MemoryStream();

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

                session.LastReceivedAt = _clock.UtcNow;

                var span = new ReadOnlySpan<byte>(messageBuffer.GetBuffer(), 0, (int)messageBuffer.Length);
                BrowserBridgeEnvelope envelope;
                try
                {
                    envelope = BrowserBridgeSerializer.Deserialize(span);
                }
                catch
                {
                    continue; // skip malformed
                }

                await HandleEnvelopeAsync(session, envelope, ct);
            }
        }
        catch (OperationCanceledException) { /* normal shutdown */ }
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
                await _clock.DelayAsync(TimeSpan.FromSeconds(2), ct);

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
        switch (envelope.Kind)
        {
            case BrowserBridgeMessageKind.HelloAck:
                var ack = BrowserBridgeSerializer.DeserializePayload<BrowserBridgeHelloAck>(envelope);
                session.HelloAck.TrySetResult(ack);
                break;

            case BrowserBridgeMessageKind.Command:
                // Only process commands after handshake
                if (!session.HelloAck.Task.IsCompleted) break;
                var command = BrowserBridgeSerializer.DeserializePayload<BrowserBridgeCommand>(envelope);
                var result = await _dispatcher.DispatchAsync(command, ct);
                await EnqueueResultAsync(session, result, envelope.MessageId, ct);
                break;

            case BrowserBridgeMessageKind.Cancel:
                var cancel = BrowserBridgeSerializer.DeserializePayload<BrowserBridgeCancel>(envelope);
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
        if (!session.TryComplete()) return; // already completed

        // Only transition state if this is still the current generation
        if (Interlocked.Read(ref _generation) != session.Generation) return;

        TransitionTo(BrowserBridgeConnectionState.Disconnected, reason);
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
            var delay = ReconnectDelays[Math.Min(attempt, ReconnectDelays.Length - 1)];

            try { await _clock.DelayAsync(delay, ct); }
            catch (OperationCanceledException) { return; }

            attempt++;

            lock (_stateLock)
            {
                if (!_desiredConnected) return;
            }

            try
            {
                await ConnectInternalAsync(ct);
                if (_state == BrowserBridgeConnectionState.Connected)
                    return;
            }
            catch (OperationCanceledException) { return; }
            catch { /* retry */ }
        }
    }

    private async Task AwaitConnectionTasksAsync()
    {
        if (_receiveTask is not null)
            await _receiveTask.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        if (_sendTask is not null)
            await _sendTask.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        if (_heartbeatTask is not null)
            await _heartbeatTask.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        if (_watchdogTask is not null)
            await _watchdogTask.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
    }

    private void CleanupConnection()
    {
        _connection = null;
        _receiveTask = null;
        _sendTask = null;
        _heartbeatTask = null;
        _watchdogTask = null;
    }

    private void TransitionTo(BrowserBridgeConnectionState newState, string? reason)
    {
        var old = _state;
        if (old == newState) return;
        _state = newState;
        StateChanged?.Invoke(this, new BrowserBridgeStateChangedEventArgs(old, newState, reason));
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

        _lifetimeCts?.Cancel();
        _connection?.TryComplete();

        if (_connection?.Socket.State == WebSocketState.Open)
        {
            try
            {
                await _connection.Socket.CloseAsync(WebSocketCloseStatus.NormalClosure,
                    "Disposing", CancellationToken.None);
            }
            catch { }
        }

        await AwaitConnectionTasksAsync();

        if (_connection is not null)
            await _connection.DisposeAsync();

        CleanupConnection();
        _lifetimeCts?.Dispose();
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
