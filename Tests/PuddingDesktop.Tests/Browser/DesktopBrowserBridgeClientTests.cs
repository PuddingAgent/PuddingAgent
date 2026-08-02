using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text.Json;
using System.Threading.Channels;
using PuddingBrowser.Protocol;
using PuddingDesktop.Browser;

namespace PuddingDesktop.Tests.Browser;

public sealed class DesktopBrowserBridgeClientTests
{
    private static readonly DesktopBrowserBridgeClientOptions TestOptions = new()
    {
        HelloTimeout = TimeSpan.FromMilliseconds(250),
        WatchdogInterval = TimeSpan.FromSeconds(2),
        ReconnectDelays = [TimeSpan.FromMinutes(10)]
    };

    [Fact]
    public async Task Connect_StartsReceiveBeforeHello_AndConnectsOnlyAfterAcceptedAck()
    {
        var socket = new FakeDesktopBrowserWebSocket(HelloAckMode.Accept);
        var client = CreateClient(new QueueWebSocketFactory(socket));
        var states = new List<BrowserBridgeConnectionState>();
        var transitions = new List<string>();
        client.StateChanged += (_, args) =>
        {
            states.Add(args.NewState);
            transitions.Add($"{args.OldState}->{args.NewState}:{args.Reason}");
        };

        await client.ConnectAsync(new Uri("http://127.0.0.1:12345"), "secret-token", CancellationToken.None);

        Assert.True(
            client.State == BrowserBridgeConnectionState.Connected,
            string.Join(" | ", transitions) + " send=" + socket.LastSendException);
        Assert.True(socket.ReceiveCallCount > 0);
        Assert.True(socket.ReceiveWasRunningWhenHelloSent);
        Assert.Equal(1, socket.Sent.Count(envelope => envelope.Kind == BrowserBridgeMessageKind.Hello));
        Assert.Contains(BrowserBridgeConnectionState.Connecting, states);
        Assert.Equal(BrowserBridgeConnectionState.Connected, states.Last());

        await client.DisconnectAsync(CancellationToken.None);
        await client.DisposeAsync();
    }

    [Fact]
    public async Task Connect_RejectedAck_NeverTransitionsToConnected()
    {
        var socket = new FakeDesktopBrowserWebSocket(HelloAckMode.Reject);
        var client = CreateClient(new QueueWebSocketFactory(socket));
        var states = new List<BrowserBridgeConnectionState>();
        client.StateChanged += (_, args) => states.Add(args.NewState);

        await client.ConnectAsync(new Uri("http://127.0.0.1:12345"), "secret-token", CancellationToken.None);

        Assert.NotEqual(BrowserBridgeConnectionState.Connected, client.State);
        Assert.DoesNotContain(BrowserBridgeConnectionState.Connected, states);
        Assert.True(socket.ReceiveCallCount >= 1);

        await client.DisconnectAsync(CancellationToken.None);
        await client.DisposeAsync();
    }

    [Fact]
    public async Task Command_IsDispatched_AndResultPreservesEnvelopeCorrelation()
    {
        var socket = new FakeDesktopBrowserWebSocket(HelloAckMode.Accept);
        var dispatcher = new BrowserBridgeCommandDispatcher();
        dispatcher.SetHandler(new SuccessfulCommandHandler());
        var client = CreateClient(new QueueWebSocketFactory(socket), dispatcher);
        await client.ConnectAsync(new Uri("http://127.0.0.1:12345"), "secret-token", CancellationToken.None);

        var commandEnvelopeId = Guid.NewGuid();
        var operationId = Guid.NewGuid();
        socket.QueueEnvelope(new BrowserBridgeEnvelope
        {
            MessageId = commandEnvelopeId,
            Kind = BrowserBridgeMessageKind.Command,
            CreatedAt = DateTimeOffset.UtcNow,
            Payload = JsonSerializer.SerializeToElement(new BrowserBridgeCommand
            {
                OperationId = operationId,
                Name = BrowserBridgeCommandNames.ContextCreate,
                DeadlineUtc = DateTimeOffset.UtcNow.AddMinutes(1),
                Arguments = JsonSerializer.SerializeToElement(new { })
            }, BrowserBridgeSerializerOptionsForTests.Default)
        });

        await WaitUntilAsync(() => socket.Sent.Any(envelope =>
            envelope.Kind == BrowserBridgeMessageKind.CommandResult
            && envelope.CorrelationId == commandEnvelopeId));

        var resultEnvelope = socket.Sent.Single(envelope =>
            envelope.Kind == BrowserBridgeMessageKind.CommandResult);
        var result = BrowserBridgeSerializer.DeserializePayload<BrowserBridgeCommandResult>(resultEnvelope);
        Assert.Equal(operationId, result.OperationId);
        Assert.True(result.Success);

        await client.DisconnectAsync(CancellationToken.None);
        await client.DisposeAsync();
    }

    [Fact]
    public async Task Heartbeat_IsAcknowledged_WithOriginalMessageCorrelation()
    {
        var socket = new FakeDesktopBrowserWebSocket(HelloAckMode.Accept);
        var client = CreateClient(new QueueWebSocketFactory(socket));
        await client.ConnectAsync(new Uri("http://127.0.0.1:12345"), "secret-token", CancellationToken.None);

        var heartbeatId = Guid.NewGuid();
        socket.QueueEnvelope(new BrowserBridgeEnvelope
        {
            MessageId = heartbeatId,
            Kind = BrowserBridgeMessageKind.Heartbeat,
            CreatedAt = DateTimeOffset.UtcNow,
            Payload = JsonSerializer.SerializeToElement(new { })
        });

        await WaitUntilAsync(() => socket.Sent.Any(envelope =>
            envelope.Kind == BrowserBridgeMessageKind.HeartbeatAck
            && envelope.CorrelationId == heartbeatId));

        await client.DisconnectAsync(CancellationToken.None);
        await client.DisposeAsync();
    }

    [Fact]
    public async Task Watchdog_AdvancingFakeClock_CancelsBlockedReceive()
    {
        var clock = new FakeBrowserBridgeClock();
        var socket = new FakeDesktopBrowserWebSocket(HelloAckMode.Accept);
        var client = CreateClient(new QueueWebSocketFactory(socket), clock: clock);
        await client.ConnectAsync(new Uri("http://127.0.0.1:12345"), "secret-token", CancellationToken.None);

        clock.Advance(TimeSpan.FromSeconds(46));

        await WaitUntilAsync(() => socket.ReceiveCancellationCount > 0);
        await WaitUntilAsync(() => client.State is BrowserBridgeConnectionState.Disconnected
            or BrowserBridgeConnectionState.Reconnecting);

        await client.DisconnectAsync(CancellationToken.None);
        await client.DisposeAsync();
    }

    [Fact]
    public async Task Disconnect_CancelsReconnect_AndNextConnectUsesFreshSession()
    {
        var clock = new FakeBrowserBridgeClock();
        var first = new FakeDesktopBrowserWebSocket(HelloAckMode.Accept);
        var second = new FakeDesktopBrowserWebSocket(HelloAckMode.Accept);
        var factory = new QueueWebSocketFactory(first, second);
        var client = CreateClient(factory, clock: clock);

        await client.ConnectAsync(new Uri("http://127.0.0.1:12345"), "secret-token", CancellationToken.None);
        first.QueueClose();
        await WaitUntilAsync(() => client.State == BrowserBridgeConnectionState.Reconnecting);

        await client.DisconnectAsync(CancellationToken.None);
        clock.Advance(TimeSpan.FromHours(1));
        await Task.Yield();
        Assert.Equal(1, factory.CreateCount);

        await client.ConnectAsync(new Uri("http://127.0.0.1:12345"), "secret-token", CancellationToken.None);
        Assert.Equal(BrowserBridgeConnectionState.Connected, client.State);
        Assert.Equal(2, factory.CreateCount);

        await client.DisconnectAsync(CancellationToken.None);
        await client.DisposeAsync();
    }

    [Fact]
    public async Task OldGenerationLateClose_DoesNotChangeNewConnectedState()
    {
        var clock = new FakeBrowserBridgeClock();
        var first = new FakeDesktopBrowserWebSocket(HelloAckMode.Accept);
        var second = new FakeDesktopBrowserWebSocket(HelloAckMode.Accept);
        var factory = new QueueWebSocketFactory(first, second);
        var options = TestOptions with { ReconnectDelays = [TimeSpan.FromSeconds(1)] };
        var client = CreateClient(factory, clock: clock, options: options);

        await client.ConnectAsync(new Uri("http://127.0.0.1:12345"), "secret-token", CancellationToken.None);
        first.QueueClose();
        await WaitUntilAsync(() => client.State == BrowserBridgeConnectionState.Reconnecting);
        clock.Advance(TimeSpan.FromSeconds(1));
        await WaitUntilAsync(() => client.State == BrowserBridgeConnectionState.Connected && factory.CreateCount == 2);

        first.QueueClose();
        await Task.Delay(20);
        Assert.Equal(BrowserBridgeConnectionState.Connected, client.State);

        await client.DisconnectAsync(CancellationToken.None);
        await client.DisposeAsync();
    }

    [Fact]
    public async Task ControlToken_IsOnlyWrittenToHeader_AndNeverAppearsInStateReason()
    {
        const string token = "sensitive-control-token";
        var socket = new FakeDesktopBrowserWebSocket(HelloAckMode.Reject);
        var client = CreateClient(new QueueWebSocketFactory(socket));
        var reasons = new List<string?>();
        client.StateChanged += (_, args) => reasons.Add(args.Reason);

        await client.ConnectAsync(new Uri("http://127.0.0.1:12345"), token, CancellationToken.None);

        Assert.Equal(token, socket.Headers[BrowserBridgeProtocol.ControlTokenHeader]);
        Assert.DoesNotContain(reasons, reason => reason?.Contains(token, StringComparison.Ordinal) == true);

        await client.DisconnectAsync(CancellationToken.None);
        await client.DisposeAsync();
    }

    private static DesktopBrowserBridgeClient CreateClient(
        IDesktopBrowserWebSocketFactory factory,
        BrowserBridgeCommandDispatcher? dispatcher = null,
        IBrowserBridgeClock? clock = null,
        DesktopBrowserBridgeClientOptions? options = null)
        => new(
            dispatcher ?? new BrowserBridgeCommandDispatcher(),
            factory,
            clock ?? new SystemBrowserBridgeClock(),
            options ?? TestOptions);

    private static async Task WaitUntilAsync(Func<bool> predicate, int timeoutMs = 2_000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!predicate())
        {
            if (DateTime.UtcNow >= deadline)
                throw new TimeoutException("Condition was not reached before the test timeout.");
            await Task.Delay(10);
        }
    }
}

internal enum HelloAckMode
{
    None,
    Accept,
    Reject
}

internal sealed class QueueWebSocketFactory : IDesktopBrowserWebSocketFactory
{
    private readonly ConcurrentQueue<IDesktopBrowserWebSocket> _sockets;
    private int _createCount;

    public int CreateCount => Volatile.Read(ref _createCount);

    public QueueWebSocketFactory(params IDesktopBrowserWebSocket[] sockets)
        => _sockets = new ConcurrentQueue<IDesktopBrowserWebSocket>(sockets);

    public IDesktopBrowserWebSocket Create()
    {
        Interlocked.Increment(ref _createCount);
        return _sockets.TryDequeue(out var socket)
            ? socket
            : throw new InvalidOperationException("No scripted WebSocket remains.");
    }
}

internal sealed class FakeDesktopBrowserWebSocket : IDesktopBrowserWebSocket
{
    private readonly Channel<FakeFrame> _incoming = Channel.CreateUnbounded<FakeFrame>();
    private readonly HelloAckMode _helloAckMode;
    private int _receiveCallCount;
    private int _receiveCancellationCount;

    public WebSocketState State { get; private set; } = WebSocketState.None;
    public ConcurrentQueue<BrowserBridgeEnvelope> Sent { get; } = new();
    public ConcurrentDictionary<string, string> Headers { get; } = new();
    public int ReceiveCallCount => Volatile.Read(ref _receiveCallCount);
    public int ReceiveCancellationCount => Volatile.Read(ref _receiveCancellationCount);
    public bool ReceiveWasRunningWhenHelloSent { get; private set; }
    public Exception? LastSendException { get; private set; }

    public FakeDesktopBrowserWebSocket(HelloAckMode helloAckMode)
        => _helloAckMode = helloAckMode;

    public void SetRequestHeader(string name, string value) => Headers[name] = value;

    public Task ConnectAsync(Uri uri, CancellationToken cancellationToken)
    {
        State = WebSocketState.Open;
        return Task.CompletedTask;
    }

    public ValueTask SendAsync(
        ReadOnlyMemory<byte> payload,
        WebSocketMessageType messageType,
        bool endOfMessage,
        CancellationToken cancellationToken)
    {
        BrowserBridgeEnvelope envelope;
        try
        {
            envelope = BrowserBridgeSerializer.Deserialize(payload.Span);
            Sent.Enqueue(envelope);
        }
        catch (Exception ex)
        {
            LastSendException = ex;
            throw;
        }

        if (envelope.Kind == BrowserBridgeMessageKind.Hello)
        {
            ReceiveWasRunningWhenHelloSent = ReceiveCallCount > 0;
            if (_helloAckMode != HelloAckMode.None)
            {
                QueueEnvelope(new BrowserBridgeEnvelope
                {
                    MessageId = Guid.NewGuid(),
                    CorrelationId = envelope.MessageId,
                    Kind = BrowserBridgeMessageKind.HelloAck,
                    CreatedAt = DateTimeOffset.UtcNow,
                    Payload = JsonSerializer.SerializeToElement(new BrowserBridgeHelloAck
                    {
                        ProtocolVersion = BrowserBridgeProtocol.CurrentVersion,
                        Accepted = _helloAckMode == HelloAckMode.Accept,
                        ErrorCode = _helloAckMode == HelloAckMode.Reject ? BrowserBridgeErrorCodes.BrowserProtocolMismatch : null,
                        ErrorMessage = _helloAckMode == HelloAckMode.Reject ? "Rejected for test" : null
                    }, BrowserBridgeSerializerOptionsForTests.Default)
                });
            }
        }

        return ValueTask.CompletedTask;
    }

    public async ValueTask<ValueWebSocketReceiveResult> ReceiveAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _receiveCallCount);
        try
        {
            var frame = await _incoming.Reader.ReadAsync(cancellationToken);
            if (frame.IsClose)
            {
                State = WebSocketState.CloseReceived;
                return new ValueWebSocketReceiveResult(0, WebSocketMessageType.Close, true);
            }

            frame.Payload.CopyTo(buffer);
            return new ValueWebSocketReceiveResult(frame.Payload.Length, WebSocketMessageType.Text, true);
        }
        catch (OperationCanceledException)
        {
            Interlocked.Increment(ref _receiveCancellationCount);
            throw;
        }
    }

    public Task CloseAsync(
        WebSocketCloseStatus closeStatus,
        string? statusDescription,
        CancellationToken cancellationToken)
    {
        State = WebSocketState.Closed;
        return Task.CompletedTask;
    }

    public void QueueEnvelope(BrowserBridgeEnvelope envelope)
        => _incoming.Writer.TryWrite(new FakeFrame(BrowserBridgeSerializer.Serialize(envelope).ToArray(), false));

    public void QueueClose() => _incoming.Writer.TryWrite(new FakeFrame([], true));

    public ValueTask DisposeAsync()
    {
        State = WebSocketState.Closed;
        _incoming.Writer.TryComplete();
        return ValueTask.CompletedTask;
    }

    private sealed record FakeFrame(byte[] Payload, bool IsClose);
}

internal sealed class FakeBrowserBridgeClock : IBrowserBridgeClock
{
    private readonly object _gate = new();
    private readonly List<ScheduledDelay> _delays = [];
    private DateTimeOffset _utcNow = DateTimeOffset.UtcNow;

    public DateTimeOffset UtcNow
    {
        get { lock (_gate) return _utcNow; }
    }

    public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var registration = cancellationToken.Register(() => completion.TrySetCanceled(cancellationToken));
            _delays.Add(new ScheduledDelay(_utcNow + delay, completion, registration));
            return completion.Task;
        }
    }

    public void Advance(TimeSpan amount)
    {
        List<ScheduledDelay> ready;
        lock (_gate)
        {
            _utcNow += amount;
            ready = _delays.Where(delay => delay.DueAt <= _utcNow).ToList();
            _delays.RemoveAll(delay => delay.DueAt <= _utcNow);
        }

        foreach (var delay in ready)
        {
            delay.Registration.Dispose();
            delay.Completion.TrySetResult();
        }
    }

    private sealed record ScheduledDelay(
        DateTimeOffset DueAt,
        TaskCompletionSource Completion,
        CancellationTokenRegistration Registration);
}

internal sealed class SuccessfulCommandHandler : IBrowserCommandHandler
{
    public Task<BrowserBridgeCommandResult> ExecuteAsync(BrowserBridgeCommand command, CancellationToken ct)
        => Task.FromResult(new BrowserBridgeCommandResult
        {
            OperationId = command.OperationId,
            Success = true,
            Value = JsonSerializer.SerializeToElement(new { ok = true })
        });
}

internal static class BrowserBridgeSerializerOptionsForTests
{
    public static readonly JsonSerializerOptions Default = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };
}
