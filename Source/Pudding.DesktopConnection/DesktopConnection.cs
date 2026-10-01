using System.Threading.Channels;
using Grpc.Core;
using Pudding.Contracts;
using Pudding.Contracts.Audit;
using Pudding.Contracts.Desktop;
using Proto = Pudding.Rpc.Protocol.V1;

namespace Pudding.DesktopConnection;

/// <summary>
/// 单条能力通道（一次握手生命周期）：Desktop 主动发起双向流，Core 经响应流下发命令。
///
/// 责任边界（计划 §5/§6）：
/// · 每方向一个 reader、一个 writer；其他线程经有界队列汇聚（禁止并发写同一条流）；
/// · 命令关联（operation_id ↔ 待完成表）、幂等复用、取消、世代失效、deadline；
/// · 出站背压（字节预算）：终态结果不可丢，状态事件可丢并计数；
/// · 断连时把 pending 明确结束为 Disconnected/OutcomeUnknown，绝不假装「未执行」。
///
/// 不负责：UI 线程调度与 UI 控件访问（由 <see cref="IDesktopCapabilityExecutor"/> 实现方负责）。
/// 实例单次使用：<see cref="RunAsync"/> 返回即表示连接已结束。
/// </summary>
public sealed class DesktopConnection : IAsyncDisposable
{
    private static readonly TimeSpan WriterDrainTimeout = TimeSpan.FromSeconds(2);

    private readonly IDesktopChannelStreamFactory _streamFactory;
    private readonly IDesktopCapabilityExecutor _executor;
    private readonly DesktopConnectionOptions _options;
    private readonly OperationRegistry _registry;
    private readonly TargetSerializationGate _targetGate = new();
    private readonly Channel<Proto.DesktopFrame> _outbound = Channel.CreateUnbounded<Proto.DesktopFrame>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false, AllowSynchronousContinuations = false });
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _inFlight;
    private readonly ByteBudget _byteBudget;
    private readonly object _stateLock = new();

    private DesktopChannelStream? _stream;
    private Task? _writerLoop;
    private ITimer? _watchdog;
    private volatile bool _shuttingDown;
    private int _runStarted;
    private int _shutdownStarted;
    private int _faultRecorded;
    private int _effectiveMaxInFlight;
    private DesktopCapabilityError? _fault;
    private DesktopConnectionState _state = DesktopConnectionState.Idle;
    private ConnectionGeneration _generation = ConnectionGeneration.None;
    private long _lastFrameTicks;
    private long _droppedBestEffortFrames;
    private long _rejectedFrames;

    public DesktopConnection(
        IDesktopChannelStreamFactory streamFactory,
        IDesktopCapabilityExecutor executor,
        DesktopConnectionOptions options)
    {
        _streamFactory = streamFactory ?? throw new ArgumentNullException(nameof(streamFactory));
        _executor = executor ?? throw new ArgumentNullException(nameof(executor));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _options.Validate();

        _registry = new OperationRegistry(options.MaxTerminalResults, options.TerminalResultTtl);
        _inFlight = new SemaphoreSlim(options.MaxInFlightOperations, options.MaxInFlightOperations);
        _byteBudget = new ByteBudget(options.MaxQueuedBytes);
        _effectiveMaxInFlight = options.MaxInFlightOperations;
        _lastFrameTicks = options.TimeProvider.GetUtcNow().UtcTicks;
    }

    public event Action<DesktopConnectionState>? StateChanged;

    public DesktopConnectionState State
    {
        get
        {
            lock (_stateLock)
            {
                return _state;
            }
        }
    }

    public ConnectionGeneration Generation => _generation;

    public string? ConnectionId { get; private set; }

    public int NegotiatedVersion { get; private set; }

    public DesktopCapability NegotiatedCapabilities { get; private set; } = DesktopCapability.None;

    public Proto.ChannelLimits? NegotiatedLimits { get; private set; }

    /// <summary>生效的在途上限（本机上限与 Core 声明上限取更严格者）。</summary>
    public int EffectiveMaxInFlight => Volatile.Read(ref _effectiveMaxInFlight);

    /// <summary>生效的出站排队字节预算。</summary>
    public long EffectiveMaxQueuedBytes => _byteBudget.Capacity;

    public long QueuedBytes => _byteBudget.Used;

    public int PendingOperationCount => _registry.RunningCount;

    public int TerminalOperationCount => _registry.TerminalCount;

    public long DroppedBestEffortFrameCount => Interlocked.Read(ref _droppedBestEffortFrames);

    public long RejectedFrameCount => Interlocked.Read(ref _rejectedFrames);

    /// <summary>运行一条连接直到结束。返回后实例即终结。</summary>
    public async Task<DesktopConnectionOutcome> RunAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.CompareExchange(ref _runStarted, 1, 0) != 0)
        {
            throw new InvalidOperationException("DesktopConnection instances are single-use.");
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        try
        {
            SetState(DesktopConnectionState.Connecting);

            try
            {
                _stream = await _streamFactory.OpenAsync(linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                SetState(DesktopConnectionState.Disconnected);
                return new DesktopConnectionOutcome(DesktopConnectionState.Disconnected, _generation, null);
            }
            catch (Exception ex)
            {
                RecordFault(DesktopCapabilityError.NotConnected($"channel open failed ({ex.GetType().Name})"));
                return new DesktopConnectionOutcome(DesktopConnectionState.Faulted, _generation, _fault);
            }

            SetState(DesktopConnectionState.Handshaking);
            var handshakeError = await HandshakeAsync(_stream, linked.Token).ConfigureAwait(false);

            if (handshakeError is not null)
            {
                RecordFault(handshakeError);
            }
            else
            {
                SetState(DesktopConnectionState.Ready);
                TouchLastFrame();
                StartWatchdog();
                _writerLoop = Task.Run(() => WriterLoopAsync(_stream!, _lifetime.Token));
                await ReaderLoopAsync(_stream, linked.Token).ConfigureAwait(false);
            }
        }
        finally
        {
            await ShutdownAsync().ConfigureAwait(false);
        }

        var finalState = _fault is null ? DesktopConnectionState.Disconnected : DesktopConnectionState.Faulted;
        SetState(finalState);
        return new DesktopConnectionOutcome(finalState, _generation, _fault);
    }

    public async ValueTask DisposeAsync()
    {
        await ShutdownAsync().ConfigureAwait(false);
    }

    // ── 握手 ──────────────────────────────────────────────────────────────

    private async Task<DesktopCapabilityError?> HandshakeAsync(DesktopChannelStream stream, CancellationToken cancellationToken)
    {
        var declarations = DesktopCapabilities.DeclareFor(_options.SupportedCapabilities);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_options.HandshakeTimeout);

        try
        {
            // 握手阶段连接独占流：此刻尚无 writer loop，也没有任何并发写。
            await stream.WriteAsync(DesktopFrameMapping.Hello(_options, declarations), timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return DesktopCapabilityError.NotConnected("handshake was cancelled");
        }
        catch (OperationCanceledException)
        {
            return DesktopCapabilityError.DeadlineExceeded(mayHaveSideEffects: false);
        }
        catch (RpcException ex)
        {
            return MapRpcException(ex, "handshake write");
        }
        catch (Exception ex)
        {
            return DesktopCapabilityError.NotConnected($"handshake write failed ({ex.GetType().Name})");
        }

        Proto.CoreFrame? frame;
        try
        {
            frame = await stream.ReadAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return DesktopCapabilityError.NotConnected("handshake was cancelled");
        }
        catch (OperationCanceledException)
        {
            return DesktopCapabilityError.DeadlineExceeded(mayHaveSideEffects: false);
        }
        catch (RpcException ex)
        {
            return MapRpcException(ex, "handshake read");
        }
        catch (Exception ex)
        {
            return DesktopCapabilityError.NotConnected($"handshake read failed ({ex.GetType().Name})");
        }

        if (frame is null)
        {
            return DesktopCapabilityError.NotConnected("core closed the stream before the handshake ack");
        }

        if (frame.FrameCase != Proto.CoreFrame.FrameOneofCase.HelloAck)
        {
            return DesktopCapabilityError.InvalidRequest(
                $"the first core frame must be hello_ack, received {frame.FrameCase}");
        }

        return ApplyHandshake(frame.HelloAck, declarations);
    }

    private DesktopCapabilityError? ApplyHandshake(
        Proto.CoreHelloAck? ack, IReadOnlyList<DesktopCapabilityDeclaration> declared)
    {
        if (ack is null || string.IsNullOrEmpty(ack.ConnectionId))
        {
            return DesktopCapabilityError.InvalidRequest("hello_ack.connection_id is missing");
        }

        if (ack.Generation == 0 || ack.Generation > long.MaxValue)
        {
            return DesktopCapabilityError.InvalidRequest("hello_ack.generation must be a positive 63-bit value");
        }

        if (ack.NegotiatedVersion is null)
        {
            return DesktopCapabilityError.InvalidRequest("hello_ack.negotiated_version is missing");
        }

        if (ack.NegotiatedVersion.Minimum > int.MaxValue || ack.NegotiatedVersion.Maximum > int.MaxValue)
        {
            return DesktopCapabilityError.InvalidRequest("hello_ack.negotiated_version is out of range");
        }

        if (!DesktopProtocolVersion.TryNegotiate(
                (int)ack.NegotiatedVersion.Minimum, (int)ack.NegotiatedVersion.Maximum, out var negotiatedVersion))
        {
            return DesktopCapabilityError.UnsupportedCapability("protocol version");
        }

        var granted = new List<DesktopCapabilityDeclaration>(ack.Capabilities.Count);
        foreach (var declaration in ack.Capabilities)
        {
            if (declaration.Version > int.MaxValue)
            {
                return DesktopCapabilityError.InvalidRequest("capability version is out of range");
            }

            var capability = DesktopCapabilities.TryGetByName(declaration.Capability, out var known)
                ? known.Capability
                : DesktopCapability.None;
            granted.Add(new DesktopCapabilityDeclaration(capability, declaration.Capability, (int)declaration.Version));
        }

        var negotiationError = DesktopCapabilityNegotiation.Validate(
            declared, granted, out var negotiatedCapabilities, out _);
        if (negotiationError is not null)
        {
            return negotiationError;
        }

        ConnectionId = ack.ConnectionId;
        _generation = ConnectionGeneration.Require((long)ack.Generation);
        NegotiatedVersion = negotiatedVersion;
        NegotiatedCapabilities = negotiatedCapabilities;
        NegotiatedLimits = ack.Limits;
        ApplyLimits(ack.Limits);
        return null;
    }

    private void ApplyLimits(Proto.ChannelLimits? limits)
    {
        if (limits is null)
        {
            return;
        }

        if (limits.MaxInFlightOperations > 0)
        {
            var effective = (int)Math.Min((uint)_options.MaxInFlightOperations, limits.MaxInFlightOperations);
            if (effective < _options.MaxInFlightOperations)
            {
                // 预占差额并永不释放：把本机 SemaphoreSlim 的上限收缩到协商值。
                for (var i = 0; i < _options.MaxInFlightOperations - effective; i++)
                {
                    if (!_inFlight.Wait(0))
                    {
                        break;
                    }
                }
            }

            Volatile.Write(ref _effectiveMaxInFlight, effective);
        }

        if (limits.MaxQueuedBytes > 0)
        {
            var effective = Math.Min((long)_options.MaxQueuedBytes, limits.MaxQueuedBytes);
            _byteBudget.ShrinkTo(effective);
        }
    }

    // ── 读循环 ────────────────────────────────────────────────────────────

    private async Task ReaderLoopAsync(DesktopChannelStream stream, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            Proto.CoreFrame? frame;
            try
            {
                frame = await stream.ReadAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (RpcException ex)
            {
                if (!_shuttingDown)
                {
                    RecordFault(MapRpcException(ex, "channel read"));
                }

                break;
            }
            catch (Exception ex)
            {
                if (!_shuttingDown)
                {
                    RecordFault(DesktopCapabilityError.NotConnected($"channel read failed ({ex.GetType().Name})"));
                }

                break;
            }

            if (frame is null)
            {
                break;
            }

            TouchLastFrame();

            if (_shuttingDown)
            {
                break;
            }

            try
            {
                HandleFrame(frame);
            }
            catch (Exception ex)
            {
                RecordFault(DesktopCapabilityError.Internal($"frame handling failed ({ex.GetType().Name})"));
                break;
            }
        }
    }

    private void HandleFrame(Proto.CoreFrame frame)
    {
        switch (frame.FrameCase)
        {
            case Proto.CoreFrame.FrameOneofCase.Command:
                HandleCommand(frame.Command);
                break;

            case Proto.CoreFrame.FrameOneofCase.Cancel:
                HandleCancel(frame.Cancel);
                break;

            case Proto.CoreFrame.FrameOneofCase.Heartbeat:
                _ = EnqueueBestEffort(DesktopFrameMapping.HeartbeatAck(frame.Heartbeat?.Sequence ?? 0));
                break;

            case Proto.CoreFrame.FrameOneofCase.HelloAck:
                RecordFault(DesktopCapabilityError.InvalidRequest("received a second hello_ack on an established connection"));
                break;

            default:
                RecordFault(DesktopCapabilityError.InvalidRequest("core sent a frame with no payload"));
                break;
        }
    }

    private void HandleCancel(Proto.OperationCancel? cancel)
    {
        if (cancel is null || !OperationId.IsValid(cancel.OperationId))
        {
            Interlocked.Increment(ref _rejectedFrames);
            return;
        }

        if (cancel.Generation != (ulong)_generation.Value)
        {
            // 旧世代的取消忽略：它不属于当前连接的命令。
            return;
        }

        var record = _registry.Find(new OperationId(cancel.OperationId));
        if (record is null || record.Terminal)
        {
            return;
        }

        _registry.TryCancel(record);
    }

    private void HandleCommand(Proto.CapabilityCommand? command)
    {
        var decoded = CoreFrameMapping.Decode(command, _options.DesktopId);

        if (!decoded.IsSuccess)
        {
            var error = decoded.Error;
            if (command is null || !OperationId.IsValid(command.OperationId))
            {
                // 无法关联结果帧：如实上报（不静默丢弃），并计数。
                Interlocked.Increment(ref _rejectedFrames);
                _ = EnqueueBestEffort(DesktopFrameMapping.ChannelStatus("rejected_command", error.Message));
                return;
            }

            var rejected = _registry.TryBegin(
                new OperationId(command.OperationId),
                fingerprint: string.Empty,
                capability: WireText.Truncate(command.Capability, 64),
                generation: _generation,
                now: Now,
                traceId: null,
                correlationId: null,
                out var rejectedRecord);

            if (rejected is DuplicateKind.InProgress or DuplicateKind.CachedTerminal)
            {
                return;
            }

            if (rejected == DuplicateKind.ConflictingPayload)
            {
                // 已有记录属于另一次调用：只回一条瞬时错误，绝不改写原记录的状态与审计。
                _ = SendTransientErrorAsync(
                    new OperationId(command.OperationId),
                    DesktopCapabilityError.InvalidRequest("duplicate operation id with a different payload"));
                return;
            }

            _ = CompleteRejectedAsync(rejectedRecord, error, _lifetime.Token);
            return;
        }

        var decodedCommand = decoded.Value;

        if (decodedCommand.Generation != _generation)
        {
            // 旧世代命令绝不执行；回明确的 not_connected 让 Core 重新发起。
            var stale = _registry.TryBegin(
                decodedCommand.OperationId,
                decodedCommand.Fingerprint,
                decodedCommand.Capability.Name,
                _generation,
                Now,
                decodedCommand.TraceId,
                decodedCommand.Context.CorrelationId,
                out var staleRecord);

            if (stale is DuplicateKind.InProgress or DuplicateKind.CachedTerminal)
            {
                return;
            }

            if (stale == DuplicateKind.ConflictingPayload)
            {
                _ = SendTransientErrorAsync(
                    decodedCommand.OperationId,
                    DesktopCapabilityError.InvalidRequest("duplicate operation id with a different payload"));
                return;
            }

            _ = CompleteRejectedAsync(
                staleRecord,
                DesktopCapabilityError.NotConnected("command belongs to a stale connection generation"),
                _lifetime.Token);
            return;
        }

        var duplicate = _registry.TryBegin(
            decodedCommand.OperationId,
            decodedCommand.Fingerprint,
            decodedCommand.Capability.Name,
            decodedCommand.Generation,
            Now,
            decodedCommand.TraceId,
            decodedCommand.Context.CorrelationId,
            out var record);

        switch (duplicate)
        {
            case DuplicateKind.InProgress:
                // 复用进行中的任务：不重复执行。
                return;

            case DuplicateKind.CachedTerminal:
                _ = ReplayTerminalAsync(record, _lifetime.Token);
                return;

            case DuplicateKind.ExpiredTerminal:
                // 终态缓存已过期：只回瞬时 outcome_unknown，绝不重新执行（副作用安全优先）。
                _ = SendTransientErrorAsync(
                    decodedCommand.OperationId,
                    DesktopCapabilityError.OutcomeUnknown(
                        "operation already reached a terminal state and the cached result is no longer available"));
                return;

            case DuplicateKind.ConflictingPayload:
                _ = SendTransientErrorAsync(
                    decodedCommand.OperationId,
                    DesktopCapabilityError.InvalidRequest("duplicate operation id with a different payload"));
                return;
        }

        if (!NegotiatedCapabilities.HasFlag(decodedCommand.Capability.Capability))
        {
            _ = CompleteRejectedAsync(
                record, DesktopCapabilityError.UnsupportedCapability(decodedCommand.Capability.Name), _lifetime.Token);
            return;
        }

        if (decodedCommand.Context.IsExpiredAt(Now))
        {
            _ = CompleteRejectedAsync(
                record, DesktopCapabilityError.DeadlineExceeded(mayHaveSideEffects: false), _lifetime.Token);
            return;
        }

        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        record.Cancellation = cancellation;
        record.Execution = RunOperationAsync(record, decodedCommand, cancellation);
    }

    // ── 执行 ──────────────────────────────────────────────────────────────

    private async Task RunOperationAsync(
        OperationRecord record, DecodedCommand command, CancellationTokenSource cancellation)
    {
        var token = cancellation.Token;
        var capability = command.Capability;
        SemaphoreSlim? gate = null;
        var slotHeld = false;
        var started = false;
        DesktopCapabilityOutcome outcome;
        DesktopCapabilityResponse response;

        try
        {
            if (!await CompletesBeforeAsync(_inFlight.WaitAsync(token), command.Context.DeadlineUtc, token).ConfigureAwait(false))
            {
                // 队列满且等到 deadline：明确回 ResourceExhausted，不静默丢命令。
                response = DesktopCapabilityResponse.Failure(
                    DesktopCapabilityError.ResourceExhausted("in-flight operation limit was reached before the deadline"));
                outcome = DesktopCapabilityOutcome.Rejected;
            }
            else
            {
                slotHeld = true;

                var targetKey = capability.Traits.HasFlag(DesktopCapabilityTraits.Mutating)
                    ? command.Request.Target?.Key
                    : null;

                var gateAcquired = true;
                if (targetKey is not null)
                {
                    gate = _targetGate.For(targetKey);
                    gateAcquired = await CompletesBeforeAsync(gate.WaitAsync(token), command.Context.DeadlineUtc, token)
                        .ConfigureAwait(false);
                }

                if (!gateAcquired)
                {
                    response = DesktopCapabilityResponse.Failure(DesktopCapabilityError.DeadlineExceeded(mayHaveSideEffects: false));
                    outcome = DesktopCapabilityOutcome.DeadlineExceeded;
                }
                else if (token.IsCancellationRequested)
                {
                    response = DesktopCapabilityResponse.Failure(DesktopCapabilityError.Cancelled(mayHaveSideEffects: false));
                    outcome = DesktopCapabilityOutcome.Cancelled;
                }
                else
                {
                    started = true;
                    record.Started = true;
                    record.StartedAtUtc = Now;
                    (response, outcome) = await ExecuteAsync(command, token).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
            var sideEffects = started && capability.Traits.HasFlag(DesktopCapabilityTraits.HasSideEffects);
            response = DesktopCapabilityResponse.Failure(DesktopCapabilityError.Cancelled(sideEffects));
            outcome = DesktopCapabilityOutcome.Cancelled;
        }
        catch (Exception ex)
        {
            response = DesktopCapabilityResponse.Failure(
                DesktopCapabilityError.Internal($"dispatch failed ({ex.GetType().Name})"));
            outcome = DesktopCapabilityOutcome.Failed;
        }
        finally
        {
            if (slotHeld)
            {
                _inFlight.Release();
            }

            gate?.Release();
        }

        if (_shuttingDown)
        {
            // 断连路径统一由 ShutdownAsync 审计（Disconnected / OutcomeUnknown）。
            return;
        }

        var frame = DesktopFrameMapping.Result(record.Id, _generation, capability, response);
        var delivered = await TryEnqueueTerminalAsync(frame, _lifetime.Token).ConfigureAwait(false);
        _registry.Complete(record, delivered ? frame : null, response.Error, Now);
        AuditCompletion(record, outcome, response.Error, delivered);
    }

    private async Task<(DesktopCapabilityResponse Response, DesktopCapabilityOutcome Outcome)> ExecuteAsync(
        DecodedCommand command, CancellationToken token)
    {
        var capability = command.Capability;
        var remaining = command.Context.DeadlineUtc - Now;
        if (remaining <= TimeSpan.Zero)
        {
            return (DesktopCapabilityResponse.Failure(DesktopCapabilityError.DeadlineExceeded(mayHaveSideEffects: false)),
                DesktopCapabilityOutcome.DeadlineExceeded);
        }

        var executeTask = _executor.ExecuteAsync(capability, command.Request, command.Context, token);
        var deadlineTask = Task.Delay(remaining, _options.TimeProvider, CancellationToken.None);

        // 取消必须及时生效：不能等执行器「自愿」观察 cancellationToken 才回终态。
        var cancellationSignal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = token.Register(
            static state => ((TaskCompletionSource)state!).TrySetResult(), cancellationSignal);

        var finished = await Task.WhenAny(executeTask, deadlineTask, cancellationSignal.Task).ConfigureAwait(false);

        if (finished == cancellationSignal.Task)
        {
            // 执行器仍在跑：结果不再回传，明确标注可能已产生副作用。
            Observe(executeTask);
            var sideEffects = capability.Traits.HasFlag(DesktopCapabilityTraits.HasSideEffects);
            return (DesktopCapabilityResponse.Failure(DesktopCapabilityError.Cancelled(sideEffects)),
                DesktopCapabilityOutcome.Cancelled);
        }

        if (finished == deadlineTask)
        {
            // 执行没有在期限内结束：不等它、也不重放，明确标注可能已产生副作用。
            Observe(executeTask);
            return (DesktopCapabilityResponse.Failure(DesktopCapabilityError.DeadlineExceeded(mayHaveSideEffects: true)),
                DesktopCapabilityOutcome.DeadlineExceeded);
        }

        try
        {
            var response = await executeTask.ConfigureAwait(false);
            return response.IsFailure
                ? (response, MapOutcome(response.Error!))
                : (response, DesktopCapabilityOutcome.Succeeded);
        }
        catch (OperationCanceledException)
        {
            var sideEffects = capability.Traits.HasFlag(DesktopCapabilityTraits.HasSideEffects);
            return token.IsCancellationRequested && Now >= command.Context.DeadlineUtc
                ? (DesktopCapabilityResponse.Failure(DesktopCapabilityError.DeadlineExceeded(sideEffects)),
                    DesktopCapabilityOutcome.DeadlineExceeded)
                : (DesktopCapabilityResponse.Failure(DesktopCapabilityError.Cancelled(sideEffects)),
                    DesktopCapabilityOutcome.Cancelled);
        }
        catch (Exception ex)
        {
            return (DesktopCapabilityResponse.Failure(DesktopCapabilityError.Internal($"executor failed ({ex.GetType().Name})")),
                DesktopCapabilityOutcome.Failed);
        }
    }

    private async Task<bool> CompletesBeforeAsync(Task task, DateTimeOffset deadlineUtc, CancellationToken token)
    {
        var remaining = deadlineUtc - Now;
        if (remaining <= TimeSpan.Zero)
        {
            return false;
        }

        var delay = Task.Delay(remaining, _options.TimeProvider, CancellationToken.None);
        var completed = await Task.WhenAny(task, delay).ConfigureAwait(false);
        if (completed == task)
        {
            await task.ConfigureAwait(false);
            return true;
        }

        Observe(task);
        return false;
    }

    private static DesktopCapabilityOutcome MapOutcome(DesktopCapabilityError error) => error.Code switch
    {
        DesktopCapabilityErrorCode.Cancelled => DesktopCapabilityOutcome.Cancelled,
        DesktopCapabilityErrorCode.DeadlineExceeded => DesktopCapabilityOutcome.DeadlineExceeded,
        _ => DesktopCapabilityOutcome.Failed,
    };

    // ── 出站 ──────────────────────────────────────────────────────────────

    private async Task CompleteRejectedAsync(OperationRecord record, DesktopCapabilityError error, CancellationToken token)
    {
        var frame = DesktopFrameMapping.ErrorResult(record.Id, _generation, error);
        var delivered = await TryEnqueueTerminalAsync(frame, token).ConfigureAwait(false);
        _registry.Complete(record, delivered ? frame : null, error, Now);
        AuditCompletion(record, DesktopCapabilityOutcome.Rejected, error, delivered);
    }

    /// <summary>
    /// 瞬时错误：为「重复 ID 冲突 / 终态缓存过期」回一条错误结果，但<b>不</b>改写已有记录，
    /// 也不产生第二条审计（一个操作只有一条审计记录）。
    /// </summary>
    private async Task SendTransientErrorAsync(OperationId operationId, DesktopCapabilityError error)
    {
        var frame = DesktopFrameMapping.ErrorResult(operationId, _generation, error);
        await TryEnqueueTerminalAsync(frame, _lifetime.Token).ConfigureAwait(false);
    }

    private async Task ReplayTerminalAsync(OperationRecord record, CancellationToken token)
    {
        if (record.TerminalFrame is { } frame)
        {
            await TryEnqueueTerminalAsync(frame, token).ConfigureAwait(false);
        }
    }

    /// <summary>终态结果：等待字节预算，不丢弃（若连接已断则返回 false ⇒ 审计 OutcomeUnknown）。</summary>
    private async Task<bool> TryEnqueueTerminalAsync(Proto.DesktopFrame frame, CancellationToken token)
    {
        var size = frame.CalculateSize();
        var reserved = size <= _byteBudget.Capacity;

        if (reserved && !await _byteBudget.ReserveAsync(size, token).ConfigureAwait(false))
        {
            return false;
        }

        if (_outbound.Writer.TryWrite(frame))
        {
            return true;
        }

        if (reserved)
        {
            _byteBudget.Release(size);
        }

        return false;
    }

    /// <summary>状态事件/心跳应答：预算不足即丢弃并计数（可合并，不承载终态）。</summary>
    private Task EnqueueBestEffort(Proto.DesktopFrame frame)
    {
        var size = frame.CalculateSize();
        if (!_byteBudget.TryReserve(size))
        {
            Interlocked.Increment(ref _droppedBestEffortFrames);
            return Task.CompletedTask;
        }

        if (_outbound.Writer.TryWrite(frame))
        {
            return Task.CompletedTask;
        }

        _byteBudget.Release(size);
        Interlocked.Increment(ref _droppedBestEffortFrames);
        return Task.CompletedTask;
    }

    private async Task WriterLoopAsync(DesktopChannelStream stream, CancellationToken token)
    {
        try
        {
            while (await _outbound.Reader.WaitToReadAsync(token).ConfigureAwait(false))
            {
                while (_outbound.Reader.TryRead(out var frame))
                {
                    try
                    {
                        await stream.WriteAsync(frame, token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }
                    catch (RpcException ex)
                    {
                        if (!_shuttingDown)
                        {
                            RecordFault(MapRpcException(ex, "frame write"));
                        }

                        return;
                    }
                    catch (Exception ex)
                    {
                        if (!_shuttingDown)
                        {
                            RecordFault(DesktopCapabilityError.NotConnected($"frame write failed ({ex.GetType().Name})"));
                        }

                        return;
                    }
                    finally
                    {
                        _byteBudget.Release(frame.CalculateSize());
                    }
                }
            }

            try
            {
                await stream.CompleteRequestStreamAsync().ConfigureAwait(false);
            }
            catch
            {
                // 对端可能已关闭：半关闭失败不影响终态判定。
            }
        }
        catch (OperationCanceledException)
        {
            // 连接结束。
        }
    }

    // ── 结束 ──────────────────────────────────────────────────────────────

    private async Task ShutdownAsync()
    {
        if (Interlocked.Exchange(ref _shutdownStarted, 1) != 0)
        {
            return;
        }

        _shuttingDown = true;
        _watchdog?.Dispose();
        _watchdog = null;
        _outbound.Writer.TryComplete();

        if (_writerLoop is not null)
        {
            try
            {
                await _writerLoop.WaitAsync(WriterDrainTimeout).ConfigureAwait(false);
            }
            catch
            {
                // 排空失败/超时：连接已不可用。
            }
        }

        if (!_lifetime.IsCancellationRequested)
        {
            _lifetime.Cancel();
        }

        // pending 必须确定结束：未开始 = Disconnected（未执行）；已开始 = OutcomeUnknown（可能已生效）。
        foreach (var record in _registry.SnapshotRunning())
        {
            _registry.TryCancel(record);
            var error = record.Started
                ? DesktopCapabilityError.OutcomeUnknown("channel disconnected while the operation was running")
                : DesktopCapabilityError.Disconnected("channel disconnected before the operation started");
            _registry.Complete(record, null, error, Now);
            Audit(record, DesktopCapabilityOutcome.Disconnected, error, Now);
        }

        if (_stream is not null)
        {
            try
            {
                await _stream.DisposeAsync().ConfigureAwait(false);
            }
            catch
            {
                // 释放失败无需上报：连接已结束。
            }

            _stream = null;
        }

        if (_streamFactory is IAsyncDisposable disposableFactory)
        {
            try
            {
                await disposableFactory.DisposeAsync().ConfigureAwait(false);
            }
            catch
            {
                // 同上。
            }
        }
    }

    // ── 支撑 ──────────────────────────────────────────────────────────────

    private DateTimeOffset Now => _options.TimeProvider.GetUtcNow();

    private void TouchLastFrame() => Interlocked.Exchange(ref _lastFrameTicks, Now.UtcTicks);

    private void StartWatchdog()
    {
        if (_options.InactivityTimeout is not { } timeout || timeout <= TimeSpan.Zero)
        {
            return;
        }

        var period = TimeSpan.FromTicks(Math.Max(timeout.Ticks / 4, TimeSpan.FromMilliseconds(20).Ticks));
        _watchdog = _options.TimeProvider.CreateTimer(_ => CheckLiveness(timeout), null, period, period);
    }

    private void CheckLiveness(TimeSpan timeout)
    {
        if (_shuttingDown)
        {
            return;
        }

        var lastFrame = new DateTimeOffset(Interlocked.Read(ref _lastFrameTicks), TimeSpan.Zero);
        if (Now - lastFrame > timeout)
        {
            RecordFault(DesktopCapabilityError.NotConnected($"no frame received from core within {timeout}"));
        }
    }

    private void RecordFault(DesktopCapabilityError error)
    {
        if (Interlocked.CompareExchange(ref _faultRecorded, 1, 0) == 0)
        {
            _fault = error;
        }

        if (!_lifetime.IsCancellationRequested)
        {
            _lifetime.Cancel();
        }

        SetState(DesktopConnectionState.Faulted);
    }

    private void SetState(DesktopConnectionState state)
    {
        lock (_stateLock)
        {
            if (_state == state)
            {
                return;
            }

            _state = state;
        }

        try
        {
            StateChanged?.Invoke(state);
        }
        catch
        {
            // 订阅者异常不得影响连接。
        }
    }

    private void AuditCompletion(
        OperationRecord record, DesktopCapabilityOutcome outcome, DesktopCapabilityError? error, bool delivered)
    {
        if (delivered)
        {
            Audit(record, outcome, error, Now);
            return;
        }

        Audit(
            record,
            DesktopCapabilityOutcome.Disconnected,
            DesktopCapabilityError.OutcomeUnknown("result frame could not be delivered to core"),
            Now);
    }

    private void Audit(
        OperationRecord record,
        DesktopCapabilityOutcome outcome,
        DesktopCapabilityError? error,
        DateTimeOffset recordedAtUtc)
    {
        if (Interlocked.Exchange(ref record.AuditFlag, 1) != 0)
        {
            return;
        }

        var queueDuration = record.Started ? record.StartedAtUtc - record.CreatedAtUtc : recordedAtUtc - record.CreatedAtUtc;
        var executionDuration = record.Started ? recordedAtUtc - record.StartedAtUtc : TimeSpan.Zero;

        try
        {
            _options.AuditSink.Record(new DesktopCapabilityAuditRecord
            {
                OperationId = record.Id,
                Generation = record.Generation,
                Capability = record.Capability,
                Outcome = outcome,
                QueueDuration = queueDuration,
                ExecutionDuration = executionDuration,
                ErrorCode = error?.Code,
                TraceId = record.TraceId,
                CorrelationId = record.CorrelationId,
                RecordedUtc = recordedAtUtc,
            });
        }
        catch
        {
            // 审计失败不得影响调用结果。
        }
    }

    private static void Observe(Task task)
    {
        _ = task.ContinueWith(
            static completed => _ = completed.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private static DesktopCapabilityError MapRpcException(RpcException exception, string context) =>
        exception.StatusCode switch
        {
            StatusCode.Unauthenticated or StatusCode.PermissionDenied =>
                DesktopCapabilityError.Unauthorized($"{context}: {exception.StatusCode}"),
            StatusCode.Unimplemented => DesktopCapabilityError.UnsupportedCapability("desktop_capability"),
            StatusCode.DeadlineExceeded => DesktopCapabilityError.DeadlineExceeded(mayHaveSideEffects: false),
            StatusCode.Cancelled => DesktopCapabilityError.NotConnected($"{context}: cancelled"),
            StatusCode.ResourceExhausted => DesktopCapabilityError.ResourceExhausted($"{context}: {exception.StatusCode}"),
            StatusCode.Unavailable => DesktopCapabilityError.NotConnected($"{context}: {exception.StatusCode}"),
            _ => DesktopCapabilityError.Internal($"{context}: {exception.StatusCode}"),
        };
}
