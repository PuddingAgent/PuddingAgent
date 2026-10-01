using System.Collections.Concurrent;
using System.Threading.Channels;
using Google.Protobuf;
using Pudding.Contracts;
using Pudding.Contracts.Audit;
using Pudding.Contracts.Desktop;
using Pudding.DesktopConnection;
using Proto = Pudding.Rpc.Protocol.V1;

namespace DesktopConnectionTests;

internal static class TestWait
{
    public static readonly TimeSpan Default = TimeSpan.FromSeconds(10);

    public static async Task UntilAsync(Func<bool> condition, string because, TimeSpan? timeout = null)
    {
        var limit = DateTime.UtcNow + (timeout ?? Default);
        while (!condition())
        {
            if (DateTime.UtcNow > limit)
            {
                throw new TimeoutException($"Condition was not met within the test budget: {because}");
            }

            await Task.Delay(10);
        }
    }
}

/// <summary>
/// 测试时钟：<see cref="GetUtcNow"/> = 真实时间 + 手动偏移（所以存活看门狗等真实定时器语义仍然有效，
/// 需要构造「已过期」或「TTL 已过」时用 <see cref="Advance"/> 平移）。
/// </summary>
internal sealed class TestTimeProvider : TimeProvider
{
    private long _offsetTicks;

    public override DateTimeOffset GetUtcNow() =>
        new(DateTimeOffset.UtcNow.UtcTicks + Interlocked.Read(ref _offsetTicks), TimeSpan.Zero);

    public void Advance(TimeSpan delta) => Interlocked.Add(ref _offsetTicks, delta.Ticks);

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
        new SystemTimer(callback, state, dueTime, period);

    private sealed class SystemTimer : ITimer
    {
        private readonly Timer _timer;

        public SystemTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
            _timer = new Timer(callback, state, dueTime, period);

        public bool Change(TimeSpan dueTime, TimeSpan period) => _timer.Change(dueTime, period);

        public void Dispose() => _timer.Dispose();

        public ValueTask DisposeAsync()
        {
            _timer.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}

/// <summary>
/// 假服务端的一条双向流：测试扮演 Core，向 Desktop 推送帧并观察 Desktop 写出的帧。
/// 同时断言「每条方向只有一个 writer」。
/// </summary>
internal sealed class FakeCoreStream : DesktopChannelStream
{
    private readonly Channel<Proto.CoreFrame> _toDesktop = Channel.CreateUnbounded<Proto.CoreFrame>();
    private readonly Channel<Proto.DesktopFrame> _written = Channel.CreateUnbounded<Proto.DesktopFrame>();
    private readonly List<Proto.DesktopFrame> _writtenSnapshot = [];
    private readonly List<Proto.DesktopFrame> _otherFrames = [];
    private readonly object _sync = new();
    private TaskCompletionSource? _writeGate;
    private int _activeWriters;

    public ConcurrentQueue<Exception> ObservedReadFaults { get; } = new();

    public bool RequestStreamCompleted { get; private set; }

    public int MaxConcurrentWriters { get; private set; }

    public IReadOnlyList<Proto.DesktopFrame> Written
    {
        get
        {
            lock (_sync)
            {
                return _writtenSnapshot.ToArray();
            }
        }
    }

    public IReadOnlyList<Proto.DesktopFrame> OtherFrames
    {
        get
        {
            lock (_sync)
            {
                return _otherFrames.ToArray();
            }
        }
    }

    public void Send(Proto.CoreFrame frame) => _toDesktop.Writer.TryWrite(frame);

    public void SendRaw(Proto.CoreFrame frame) => _toDesktop.Writer.TryWrite(frame);

    /// <summary>服务端正常结束（half-close）。</summary>
    public void CloseFromServer() => _toDesktop.Writer.TryComplete();

    /// <summary>服务端以异常结束读循环（模拟 RpcException）。</summary>
    public void FailFromServer(Exception exception) => _toDesktop.Writer.TryComplete(exception);

    public void BlockWrites()
    {
        lock (_sync)
        {
            _writeGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }

    public void ReleaseWrites()
    {
        TaskCompletionSource? gate;
        lock (_sync)
        {
            gate = _writeGate;
            _writeGate = null;
        }

        gate?.TrySetResult();
    }

    public override async ValueTask<Proto.CoreFrame?> ReadAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await _toDesktop.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false)
                ? await _toDesktop.Reader.ReadAsync(cancellationToken).ConfigureAwait(false)
                : null;
        }
        catch (ChannelClosedException)
        {
            return null;
        }
        catch (Exception ex)
        {
            ObservedReadFaults.Enqueue(ex);
            throw;
        }
    }

    public override async ValueTask WriteAsync(Proto.DesktopFrame frame, CancellationToken cancellationToken)
    {
        var concurrent = Interlocked.Increment(ref _activeWriters);
        try
        {
            lock (_sync)
            {
                MaxConcurrentWriters = Math.Max(MaxConcurrentWriters, concurrent);
            }

            TaskCompletionSource? gate;
            lock (_sync)
            {
                gate = _writeGate;
            }

            if (gate is not null)
            {
                await gate.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }

            lock (_sync)
            {
                _writtenSnapshot.Add(frame);
            }

            _written.Writer.TryWrite(frame);
        }
        finally
        {
            Interlocked.Decrement(ref _activeWriters);
        }
    }

    public override ValueTask CompleteRequestStreamAsync()
    {
        RequestStreamCompleted = true;
        return ValueTask.CompletedTask;
    }

    public override ValueTask DisposeAsync()
    {
        _toDesktop.Writer.TryComplete();
        _written.Writer.TryComplete();
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// 等待写出的帧匹配 predicate。基于快照轮询而不是消费队列：并发操作的结果可能乱序到达，
    /// 消费式等待会把「别的操作的结果」吞掉，导致后续等待永远超时。
    /// </summary>
    public async Task<Proto.DesktopFrame> WaitForWriteAsync(
        Func<Proto.DesktopFrame, bool> predicate, string because, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TestWait.Default);
        while (true)
        {
            lock (_sync)
            {
                var match = _writtenSnapshot.FirstOrDefault(predicate);
                if (match is not null)
                {
                    return match;
                }
            }

            if (DateTime.UtcNow >= deadline)
            {
                lock (_sync)
                {
                    var observed = string.Join(
                        ", ",
                        _writtenSnapshot.Select(frame => frame.Result is { } result
                            ? $"{frame.FrameCase}[{result.OperationId}/{result.OutcomeCase}]"
                            : frame.FrameCase.ToString()));
                    throw new TimeoutException(
                        $"Expected frame was not written: {because}. Written so far: [{observed}]");
                }
            }

            await Task.Delay(10).ConfigureAwait(false);
        }
    }

    public async Task<Proto.OperationResult> WaitForResultAsync(string operationId, TimeSpan? timeout = null)
    {
        var frame = await WaitForWriteAsync(
            candidate => candidate.Result is { } result && result.OperationId == operationId,
            $"result for operation {operationId}",
            timeout).ConfigureAwait(false);

        return frame.Result;
    }

    public Task<Proto.DesktopFrame> WaitForErrorAsync(string operationId, string wireCode, TimeSpan? timeout = null) =>
        WaitForWriteAsync(
            frame => frame.Result is { Error: { } error } result
                && result.OperationId == operationId
                && error.Code == wireCode,
            $"error result '{wireCode}' for operation {operationId}",
            timeout);
}

internal sealed class FakeStreamFactory : IDesktopChannelStreamFactory
{
    private readonly Func<int, DesktopChannelStream>? _factory;
    private readonly List<FakeCoreStream> _streams = [];
    private int _openCount;

    public FakeStreamFactory(Func<int, DesktopChannelStream>? factory = null) => _factory = factory;

    public int OpenCount => Volatile.Read(ref _openCount);

    public IReadOnlyList<FakeCoreStream> Streams
    {
        get
        {
            lock (_streams)
            {
                return _streams.ToArray();
            }
        }
    }

    public FakeCoreStream LastStream => Streams[^1];

    public ValueTask<DesktopChannelStream> OpenAsync(CancellationToken cancellationToken)
    {
        var index = Interlocked.Increment(ref _openCount);
        DesktopChannelStream stream;
        if (_factory is not null)
        {
            stream = _factory(index);
        }
        else
        {
            stream = new FakeCoreStream();
        }

        if (stream is FakeCoreStream fake)
        {
            lock (_streams)
            {
                _streams.Add(fake);
            }
        }

        return ValueTask.FromResult(stream);
    }
}

internal sealed class ExecutorCall
{
    public required DesktopCapabilityDescriptor Capability { get; init; }

    public required DesktopCapabilityRequest Request { get; init; }

    public required DesktopCallContext Context { get; init; }
}

internal sealed class FakeExecutor : IDesktopCapabilityExecutor
{
    private readonly Func<ExecutorCall, CancellationToken, Task<DesktopCapabilityResponse>> _handler;
    private readonly ConcurrentQueue<ExecutorCall> _calls = new();

    public FakeExecutor(Func<ExecutorCall, CancellationToken, Task<DesktopCapabilityResponse>>? handler = null) =>
        _handler = handler ?? (static (call, _) => Task.FromResult(DefaultResponse(call.Capability)));

    public IReadOnlyList<ExecutorCall> Calls => _calls.ToArray();

    public int CallCount => _calls.Count;

    public Task<DesktopCapabilityResponse> ExecuteAsync(
        DesktopCapabilityDescriptor capability,
        DesktopCapabilityRequest request,
        DesktopCallContext context,
        CancellationToken cancellationToken)
    {
        var call = new ExecutorCall { Capability = capability, Request = request, Context = context };
        _calls.Enqueue(call);
        return _handler(call, cancellationToken);
    }

    public static DesktopCapabilityDescriptor Descriptor(DesktopCapability capability) =>
        DesktopCapabilities.All.Single(descriptor => descriptor.Capability == capability);

    public static DesktopCapabilityResponse DefaultResponse(DesktopCapabilityDescriptor capability) =>
        capability.Capability switch
        {
            DesktopCapability.WebViewNavigate => DesktopCapabilityResponse.FromNavigate(
                new NavigateResult(
                    Pudding.Contracts.Desktop.NavigateDisposition.Completed,
                    new Uri("https://example.com/done"),
                    DesktopPageVersion.Require(3))),

            DesktopCapability.WebViewExecuteJavascript => DesktopCapabilityResponse.FromJavascript(
                new JavascriptResult(Pudding.Contracts.Desktop.JavascriptValueKind.String, "\"ok\"", false)),

            DesktopCapability.ShellNotification => DesktopCapabilityResponse.FromNotification(
                new DesktopNotificationResult(true, "n-1")),

            _ => DesktopCapabilityResponse.Failure(DesktopCapabilityError.UnsupportedCapability(capability.Name)),
        };

    /// <summary>永不完成的执行（用于 deadline / 断连语义）。</summary>
    public static FakeExecutor NeverCompleting() =>
        new((_, _) => new TaskCompletionSource<DesktopCapabilityResponse>().Task);

    /// <summary>阻塞到显式放行（用于串行化/取消/断连语义）。</summary>
    public static FakeExecutor Gated(TaskCompletionSource<DesktopCapabilityResponse> gate) =>
        new((_, _) => gate.Task);
}

internal static class Handlers
{
    /// <summary>永不完成的执行器行为（deadline / 断连语义）。</summary>
    public static Func<ExecutorCall, CancellationToken, Task<DesktopCapabilityResponse>> NeverCompleting =>
        static (_, _) => new TaskCompletionSource<DesktopCapabilityResponse>(
            TaskCreationOptions.RunContinuationsAsynchronously).Task;

    /// <summary>阻塞到显式放行的执行器行为。</summary>
    public static Func<ExecutorCall, CancellationToken, Task<DesktopCapabilityResponse>> Gated(
        TaskCompletionSource<DesktopCapabilityResponse> gate) => (_, _) => gate.Task;
}

internal sealed class RecordingAuditSink : IDesktopCapabilityAuditSink
{
    private readonly ConcurrentQueue<DesktopCapabilityAuditRecord> _records = new();

    public IReadOnlyList<DesktopCapabilityAuditRecord> Records => _records.ToArray();

    public void Record(DesktopCapabilityAuditRecord record) => _records.Enqueue(record);

    public IReadOnlyList<DesktopCapabilityAuditRecord> ForOperation(OperationId operationId) =>
        Records.Where(record => record.OperationId == operationId).ToArray();

    public async Task<DesktopCapabilityAuditRecord> WaitForAsync(OperationId operationId, TimeSpan? timeout = null)
    {
        var limit = DateTime.UtcNow + (timeout ?? TestWait.Default);
        while (DateTime.UtcNow < limit)
        {
            var match = ForOperation(operationId);
            if (match.Count > 0)
            {
                return match[0];
            }

            await Task.Delay(10);
        }

        throw new TimeoutException($"No audit record for operation {operationId}.");
    }
}

internal sealed class HarnessOptions
{
    public DesktopCapability Declared { get; set; } =
        DesktopCapability.WebViewNavigate | DesktopCapability.WebViewExecuteJavascript | DesktopCapability.ShellNotification;

    public DesktopCapability? Granted { get; set; }

    public int MaxInFlightOperations { get; set; } = 16;

    public int MaxQueuedBytes { get; set; } = 4 * 1024 * 1024;

    public int MaxTerminalResults { get; set; } = 128;

    public TimeSpan TerminalResultTtl { get; set; } = TimeSpan.FromMinutes(5);

    public TimeSpan HandshakeTimeout { get; set; } = TimeSpan.FromSeconds(10);

    public TimeSpan? InactivityTimeout { get; set; }

    public Proto.ChannelLimits? Limits { get; set; }

    public Proto.ProtocolRange? VersionRange { get; set; }

    public ulong Generation { get; set; } = 1;

    public string ConnectionId { get; set; } = "conn-1";

    public DesktopChannelAuthentication? Authentication { get; set; }

    public Func<ExecutorCall, CancellationToken, Task<DesktopCapabilityResponse>>? ExecutorHandler { get; set; }

    public bool SendHandshakeAck { get; set; } = true;
}

/// <summary>一条已握手的连接 + 假服务端 + 审计出口。</summary>
internal sealed class ConnectionHarness : IAsyncDisposable
{
    private ConnectionHarness(
        HarnessOptions options,
        FakeStreamFactory factory,
        FakeExecutor executor,
        RecordingAuditSink audit,
        TestTimeProvider clock,
        DesktopConnection connection,
        Task<DesktopConnectionOutcome> runTask)
    {
        Options = options;
        Factory = factory;
        Executor = executor;
        Audit = audit;
        Clock = clock;
        Connection = connection;
        RunTask = runTask;
    }

    public HarnessOptions Options { get; }

    public FakeStreamFactory Factory { get; }

    public FakeExecutor Executor { get; }

    public RecordingAuditSink Audit { get; }

    public TestTimeProvider Clock { get; }

    public DesktopConnection Connection { get; }

    public Task<DesktopConnectionOutcome> RunTask { get; }

    public FakeCoreStream Stream => Factory.LastStream;

    public static async Task<ConnectionHarness> StartAsync(HarnessOptions? options = null)
    {
        options ??= new HarnessOptions();
        var clock = new TestTimeProvider();
        var factory = new FakeStreamFactory();
        var audit = new RecordingAuditSink();
        var executor = new FakeExecutor(options.ExecutorHandler);

        var connectionOptions = new DesktopConnectionOptions
        {
            DesktopId = new DesktopInstanceId("desk-1"),
            ProcessInstanceId = new DesktopProcessInstanceId("proc-1"),
            SupportedCapabilities = options.Declared,
            Authentication = options.Authentication,
            MaxInFlightOperations = options.MaxInFlightOperations,
            MaxQueuedBytes = options.MaxQueuedBytes,
            MaxTerminalResults = options.MaxTerminalResults,
            TerminalResultTtl = options.TerminalResultTtl,
            HandshakeTimeout = options.HandshakeTimeout,
            InactivityTimeout = options.InactivityTimeout,
            AuditSink = audit,
            TimeProvider = clock,
        };

        var connection = new DesktopConnection(factory, executor, connectionOptions);
        var runTask = connection.RunAsync();

        var harness = new ConnectionHarness(options, factory, executor, audit, clock, connection, runTask);

        await TestWait.UntilAsync(() => factory.Streams.Count > 0, "channel stream opened", TimeSpan.FromSeconds(5));

        // 握手问候帧必须由 Desktop 主动写出。
        await factory.LastStream.WaitForWriteAsync(frame => frame.Hello is not null, "desktop hello");

        if (options.SendHandshakeAck)
        {
            factory.LastStream.Send(Frames.HelloAck(options));
            await TestWait.UntilAsync(
                () => connection.State == DesktopConnectionState.Ready,
                "connection reaches Ready",
                TimeSpan.FromSeconds(5));
        }

        return harness;
    }

    public Proto.OperationResult? FindResult(string operationId) =>
        Stream.Written
            .Where(frame => frame.Result is { } result && result.OperationId == operationId)
            .Select(frame => frame.Result)
            .FirstOrDefault();

    /// <summary>失败时的状态快照，用来区分「读循环停了」与「写循环停了」。</summary>
    public string Diagnostics() =>
        $"state={Connection.State} generation={Connection.Generation.Value} pending={Connection.PendingOperationCount} "
        + $"executorCalls={Executor.CallCount} rejectedFrames={Connection.RejectedFrameCount} "
        + $"dropped={Connection.DroppedBestEffortFrameCount} queuedBytes={Connection.QueuedBytes}/{Connection.EffectiveMaxQueuedBytes} "
        + $"inFlightLimit={Connection.EffectiveMaxInFlight} written={Stream.Written.Count} audit={Audit.Records.Count} "
        + $"runTask={RunTask.Status} requestStreamCompleted={Stream.RequestStreamCompleted}";

    public async Task<Proto.OperationResult> WaitForResultAsync(string operationId, TimeSpan? timeout = null)
    {
        try
        {
            return await Stream.WaitForResultAsync(operationId, timeout).ConfigureAwait(false);
        }
        catch (TimeoutException ex)
        {
            throw new TimeoutException($"{ex.Message} | {Diagnostics()}");
        }
    }

    public int ResultCount(string operationId) =>
        Stream.Written.Count(frame => frame.Result is { } result && result.OperationId == operationId);

    public async ValueTask DisposeAsync()
    {
        await Connection.DisposeAsync().ConfigureAwait(false);
        try
        {
            await RunTask.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        catch
        {
            // 测试收尾：忽略。
        }
    }
}

internal static class Frames
{
    public static Proto.CoreFrame HelloAck(HarnessOptions options)
    {
        var ack = new Proto.CoreHelloAck
        {
            ConnectionId = options.ConnectionId,
            Generation = options.Generation,
            NegotiatedVersion = options.VersionRange ?? new Proto.ProtocolRange { Minimum = 1, Maximum = 1 },
        };

        foreach (var declaration in DesktopCapabilities.DeclareFor(options.Granted ?? options.Declared))
        {
            ack.Capabilities.Add(new Proto.CapabilityDeclaration
            {
                Capability = declaration.Name,
                Version = (uint)declaration.Version,
            });
        }

        if (options.Limits is not null)
        {
            ack.Limits = options.Limits;
        }

        return new Proto.CoreFrame { HelloAck = ack };
    }

    public static Proto.CoreFrame HelloAck(
        string connectionId = "conn-1",
        ulong generation = 1,
        DesktopCapability granted = DesktopCapability.WebViewNavigate,
        Proto.ChannelLimits? limits = null,
        uint minimum = 1,
        uint maximum = 1) =>
        HelloAck(new HarnessOptions
        {
            ConnectionId = connectionId,
            Generation = generation,
            Granted = granted,
            Limits = limits,
            VersionRange = new Proto.ProtocolRange { Minimum = minimum, Maximum = maximum },
        });

    public static Proto.CoreFrame Heartbeat(long sequence = 1) =>
        new() { Heartbeat = new Proto.Heartbeat { Sequence = sequence } };

    public static Proto.CoreFrame Empty() => new();

    public static Proto.CoreFrame NavigateCommand(
        string operationId,
        string capability = "webview.navigate",
        ulong generation = 1,
        string contextId = "ctx-1",
        string pageId = "page-1",
        string url = "https://example.com/",
        long expectedPageVersion = 0,
        DateTimeOffset? deadline = null,
        string? traceId = null,
        string? correlationId = null,
        bool includeTarget = true) =>
        new()
        {
            Command = new Proto.CapabilityCommand
            {
                OperationId = operationId,
                Generation = generation,
                Capability = capability,
                TraceId = traceId ?? string.Empty,
                CorrelationId = correlationId ?? string.Empty,
                Deadline = ProtoTimestamp(deadline ?? DateTimeOffset.UtcNow.AddSeconds(30)),
                Navigate = new Proto.NavigateCommand
                {
                    Target = includeTarget
                        ? new Proto.CommandTarget { ContextId = contextId, PageId = pageId }
                        : null,
                    Url = url,
                    ExpectedPageVersion = expectedPageVersion,
                },
            },
        };

    public static Proto.CoreFrame JavascriptCommand(
        string operationId,
        string script = "return 1;",
        ulong generation = 1,
        uint maxResultBytes = 0,
        DateTimeOffset? deadline = null) =>
        new()
        {
            Command = new Proto.CapabilityCommand
            {
                OperationId = operationId,
                Generation = generation,
                Capability = "webview.execute_javascript",
                Deadline = ProtoTimestamp(deadline ?? DateTimeOffset.UtcNow.AddSeconds(30)),
                ExecuteJavascript = new Proto.ExecuteJavascriptCommand
                {
                    Target = new Proto.CommandTarget { ContextId = "ctx-1", PageId = "page-1" },
                    Script = script,
                    MaxResultBytes = maxResultBytes,
                },
            },
        };

    public static Proto.CoreFrame NotificationCommand(
        string operationId,
        string title = "标题",
        string message = "内容",
        Proto.NotificationPriority priority = Proto.NotificationPriority.High,
        ulong generation = 1,
        DateTimeOffset? deadline = null) =>
        new()
        {
            Command = new Proto.CapabilityCommand
            {
                OperationId = operationId,
                Generation = generation,
                Capability = "shell.notification",
                Deadline = ProtoTimestamp(deadline ?? DateTimeOffset.UtcNow.AddSeconds(30)),
                ShowNotification = new Proto.ShowNotificationCommand
                {
                    Title = title,
                    Message = message,
                    Priority = priority,
                },
            },
        };

    public static Proto.CapabilityCommand Command(
        string operationId,
        string capability,
        ulong generation = 1,
        DateTimeOffset? deadline = null,
        Proto.CapabilityCommand.PayloadOneofCase payload = Proto.CapabilityCommand.PayloadOneofCase.None) =>
        new()
        {
            OperationId = operationId,
            Capability = capability,
            Generation = generation,
            Deadline = deadline is null ? null : ProtoTimestamp(deadline.Value),
            Navigate = payload == Proto.CapabilityCommand.PayloadOneofCase.Navigate
                ? new Proto.NavigateCommand
                {
                    Target = new Proto.CommandTarget { ContextId = "ctx-1", PageId = "page-1" },
                    Url = "https://example.com/",
                }
                : null,
        };

    public static Proto.CoreFrame CommandFrame(Proto.CapabilityCommand? command) =>
        new() { Command = command };

    public static Proto.CoreFrame CancelCommand(string operationId, ulong generation = 1) =>
        new() { Cancel = new Proto.OperationCancel { OperationId = operationId, Generation = generation } };

    public static DateTimeOffset Now => DateTimeOffset.UtcNow;

    private static Google.Protobuf.WellKnownTypes.Timestamp ProtoTimestamp(DateTimeOffset value) =>
        Google.Protobuf.WellKnownTypes.Timestamp.FromDateTimeOffset(value.ToUniversalTime());
}
