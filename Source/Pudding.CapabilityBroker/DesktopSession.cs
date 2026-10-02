using System.Threading.Channels;
using Pudding.Contracts;
using Pudding.Contracts.Audit;
using Pudding.Contracts.Desktop;
using Proto = Pudding.Rpc.Protocol.V1;

namespace Pudding.CapabilityBroker;

/// <summary>
/// Core 侧的一条已完成握手的会话：命令下发、取消、期限、在途与出站队列预算、
/// 以及断连时对 pending 的确定性收尾。
///
/// 与 Desktop 侧对称但方向相反：Core 写命令、读结果/事件；两侧各自持有自己的状态机与待完成表。
/// </summary>
public sealed class DesktopSession : IAsyncDisposable, IDesktopBrowserCapabilitySurface
{
    private readonly ICoreDesktopChannel _channel;
    private readonly DesktopCapabilityPolicy _policy;
    private readonly TimeProvider _clock;
    private readonly IDesktopCapabilityAuthorizer _authorizer;
    private readonly IDesktopCapabilityAuditSink _audit;
    private readonly SemaphoreSlim _inFlight;

    /// <summary>每个页面目标最近一次被观测到的 PageVersion：比它更旧的期望版本说明引用已作废。</summary>
    private readonly Dictionary<string, long> _knownPageVersions = new(StringComparer.Ordinal);
    private readonly Channel<Proto.CoreFrame> _outbound;
    private readonly Dictionary<OperationId, PendingOperation> _pending = new();
    private readonly object _sync = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private DesktopLinkState _state = DesktopLinkState.Handshaking;
    private DesktopCapabilityError? _lastError;
    private int _shutdownStarted;

    internal DesktopSession(
        DesktopInstanceId desktopId,
        string connectionId,
        ConnectionGeneration generation,
        Proto.CoreHelloAck ack,
        DesktopCapability negotiatedCapabilities,
        ICoreDesktopChannel channel,
        DesktopCapabilityPolicy policy,
        TimeProvider clock,
        IDesktopCapabilityAuthorizer authorizer,
        IDesktopCapabilityAuditSink audit)
    {
        DesktopId = desktopId;
        ConnectionId = connectionId;
        Generation = generation;
        Ack = ack;
        NegotiatedCapabilities = negotiatedCapabilities;
        _channel = channel;
        _policy = policy;
        _clock = clock;
        _authorizer = authorizer;
        _audit = audit;

        _inFlight = new SemaphoreSlim(policy.MaxInFlightPerConnection, policy.MaxInFlightPerConnection);
        _outbound = Channel.CreateBounded<Proto.CoreFrame>(new BoundedChannelOptions(policy.MaxQueuedFrames)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
        });
    }

    public DesktopInstanceId DesktopId { get; }

    public string ConnectionId { get; }

    public ConnectionGeneration Generation { get; }

    public Proto.CoreHelloAck Ack { get; }

    public DesktopCapability NegotiatedCapabilities { get; }

    public DesktopLinkState State
    {
        get
        {
            lock (_sync)
            {
                return _state;
            }
        }
    }

    public DesktopCapabilityError? LastError
    {
        get
        {
            lock (_sync)
            {
                return _lastError;
            }
        }
    }

    public int PendingOperationCount
    {
        get
        {
            lock (_sync)
            {
                return _pending.Count;
            }
        }
    }

    public int AvailableInFlightSlots => _inFlight.CurrentCount;

    public int QueuedFrameCount => _outbound.Reader.Count;

    /// <summary>读循环任务：宿主适配器（gRPC 服务方法）应当 await 它以驱动整条流。</summary>
    public Task Completion => _completion.Task;

    public event Action<DesktopLinkState>? StateChanged;

    public event Action<Proto.DesktopEvent>? EventReceived;

    // ── 能力调用 ──────────────────────────────────────────────────────────

    public Task<CapabilityResult<NavigateResult>> NavigateAsync(
        NavigateRequest request, DesktopCallContext call, CancellationToken cancellationToken = default) =>
        InvokeAsync(
            DesktopCapability.WebViewNavigate,
            DesktopCapabilityRequest.ForNavigate(request),
            call,
            static response => response.Navigate is { } value
                ? CapabilityResult<NavigateResult>.Success(value)
                : CapabilityResult<NavigateResult>.Failure(
                    DesktopCapabilityError.Internal("navigate response payload is missing")),
            cancellationToken);

    public Task<CapabilityResult<JavascriptResult>> ExecuteJavascriptAsync(
        JavascriptRequest request, DesktopCallContext call, CancellationToken cancellationToken = default) =>
        InvokeAsync(
            DesktopCapability.WebViewExecuteJavascript,
            DesktopCapabilityRequest.ForJavascript(request),
            call,
            static response => response.Javascript is { } value
                ? CapabilityResult<JavascriptResult>.Success(value)
                : CapabilityResult<JavascriptResult>.Failure(
                    DesktopCapabilityError.Internal("javascript response payload is missing")),
            cancellationToken);

    public Task<CapabilityResult<DesktopPageState>> GetPageStateAsync(
        DesktopPageTarget target, DesktopCallContext call, CancellationToken cancellationToken = default) =>
        InvokeAsync(
            DesktopCapability.WebViewPageState,
            DesktopCapabilityRequest.ForPageState(target),
            call,
            static response => response.PageState is { } value
                ? CapabilityResult<DesktopPageState>.Success(value)
                : CapabilityResult<DesktopPageState>.Failure(
                    DesktopCapabilityError.Internal("page_state response payload is missing")),
            cancellationToken);

    public Task<CapabilityResult<DesktopNotificationResult>> ShowNotificationAsync(
        DesktopNotificationRequest request, DesktopCallContext call, CancellationToken cancellationToken = default) =>
        InvokeAsync(
            DesktopCapability.ShellNotification,
            DesktopCapabilityRequest.ForNotification(request),
            call,
            static response => response.Notification is { } value
                ? CapabilityResult<DesktopNotificationResult>.Success(value)
                : CapabilityResult<DesktopNotificationResult>.Failure(
                    DesktopCapabilityError.Internal("notification response payload is missing")),
            cancellationToken);

    /// <summary>
    /// 元素交互（切片 D 唯一变更类能力）。成功结果携带<b>交互后的页面状态</b>，
    /// 版本随之推进 ⇒ 交互前的 Ref 在本会话内立即作废（复用按目标的版本跟踪）。
    /// </summary>
    /// <summary>条件等待（只读）：超时用 TimedOut 标注而不是失败，并照常回带页面状态。</summary>
    public Task<CapabilityResult<DesktopWaitResult>> WaitForAsync(
        BrowserWaitForRequest request, DesktopCallContext call, CancellationToken cancellationToken = default) =>
        InvokeAsync(
            DesktopCapability.BrowserWaitFor,
            DesktopCapabilityRequest.ForWaitFor(request),
            call,
            static response => response.Wait is { } value
                ? CapabilityResult<DesktopWaitResult>.Success(value)
                : CapabilityResult<DesktopWaitResult>.Failure(
                    DesktopCapabilityError.Internal("wait_for response payload is missing")),
            cancellationToken);

    /// <summary>列出上下文与页面（只读，浏览器作用域）。</summary>
    public Task<CapabilityResult<DesktopContexts>> GetContextsAsync(
        DesktopCallContext call, CancellationToken cancellationToken = default) =>
        InvokeAsync(
            DesktopCapability.BrowserContexts,
            DesktopCapabilityRequest.ForContexts(),
            call,
            static response => response.Contexts is { } value
                ? CapabilityResult<DesktopContexts>.Success(value)
                : CapabilityResult<DesktopContexts>.Failure(
                    DesktopCapabilityError.Internal("contexts response payload is missing")),
            cancellationToken);

    /// <summary>标签页切换/关闭（变更类）：成功结果带回新的活动页状态与剩余清单。</summary>
    public Task<CapabilityResult<DesktopTabsResult>> TabsAsync(
        BrowserTabsRequest request, DesktopCallContext call, CancellationToken cancellationToken = default) =>
        InvokeAsync(
            DesktopCapability.BrowserTabs,
            DesktopCapabilityRequest.ForTabs(request),
            call,
            static response => response.Tabs is { } value
                ? CapabilityResult<DesktopTabsResult>.Success(value)
                : CapabilityResult<DesktopTabsResult>.Failure(
                    DesktopCapabilityError.Internal("tabs response payload is missing")),
            cancellationToken);

    /// <summary>读取剪贴板（只读，v1）：预算由请求给出，越界截断并由服务侧兜底标注。</summary>
    public Task<CapabilityResult<DesktopClipboardContent>> ReadClipboardAsync(
        ClipboardReadRequest request, DesktopCallContext call, CancellationToken cancellationToken = default) =>
        InvokeAsync(
            DesktopCapability.ShellClipboard,
            DesktopCapabilityRequest.ForClipboard(request),
            call,
            static response => response.Clipboard is { } value
                ? CapabilityResult<DesktopClipboardContent>.Success(value)
                : CapabilityResult<DesktopClipboardContent>.Failure(
                    DesktopCapabilityError.Internal("clipboard response payload is missing")),
            cancellationToken);

    /// <summary>显示对话框（交互类）：取消由结果表达（Canceled），不是失败。</summary>
    public Task<CapabilityResult<DesktopDialogResult>> RequestDialogAsync(
        DesktopDialogRequest request, DesktopCallContext call, CancellationToken cancellationToken = default) =>
        InvokeAsync(
            DesktopCapability.ShellDialog,
            DesktopCapabilityRequest.ForDialog(request),
            call,
            static response => response.Dialog is { } value
                ? CapabilityResult<DesktopDialogResult>.Success(value)
                : CapabilityResult<DesktopDialogResult>.Failure(
                    DesktopCapabilityError.Internal("dialog response payload is missing")),
            cancellationToken);

    /// <summary>文件选择器（交互类）：取消由结果表达；返回路径不代表 Core 可读。</summary>
    public Task<CapabilityResult<DesktopFilePickerResult>> RequestFilePickerAsync(
        DesktopFilePickerRequest request, DesktopCallContext call, CancellationToken cancellationToken = default) =>
        InvokeAsync(
            DesktopCapability.ShellFilePicker,
            DesktopCapabilityRequest.ForFilePicker(request),
            call,
            static response => response.FilePicker is { } value
                ? CapabilityResult<DesktopFilePickerResult>.Success(value)
                : CapabilityResult<DesktopFilePickerResult>.Failure(
                    DesktopCapabilityError.Internal("file_picker response payload is missing")),
            cancellationToken);

    public Task<CapabilityResult<DesktopInteractionResult>> InteractAsync(
        BrowserInteractRequest request, DesktopCallContext call, CancellationToken cancellationToken = default) =>
        InvokeAsync(
            DesktopCapability.BrowserInteract,
            DesktopCapabilityRequest.ForInteract(request),
            call,
            static response => response.Interact is { } value
                ? CapabilityResult<DesktopInteractionResult>.Success(value)
                : CapabilityResult<DesktopInteractionResult>.Failure(
                    DesktopCapabilityError.Internal("interaction response payload is missing")),
            cancellationToken);

    /// <summary>元素定位（切片 D）：结果回填请求的描述符；Ref 只在返回的 PageVersion 内有效。</summary>
    public Task<CapabilityResult<DesktopLocateResult>> LocateAsync(
        BrowserLocateRequest request, DesktopCallContext call, CancellationToken cancellationToken = default) =>
        InvokeAsync(
            DesktopCapability.BrowserLocate,
            DesktopCapabilityRequest.ForLocate(request),
            call,
            static response => response.Locate is { } value
                ? CapabilityResult<DesktopLocateResult>.Success(value)
                : CapabilityResult<DesktopLocateResult>.Failure(
                    DesktopCapabilityError.Internal("locate response payload is missing")),
            cancellationToken);

    /// <summary>页面快照（规划 §9 切片 D）：Ref 只在返回的 PageVersion 内有效。</summary>
    public Task<CapabilityResult<DesktopSnapshot>> SnapshotAsync(
        BrowserSnapshotRequest request, DesktopCallContext call, CancellationToken cancellationToken = default) =>
        InvokeAsync(
            DesktopCapability.BrowserSnapshot,
            DesktopCapabilityRequest.ForSnapshot(request),
            call,
            static response => response.Snapshot is { } value
                ? CapabilityResult<DesktopSnapshot>.Success(value)
                : CapabilityResult<DesktopSnapshot>.Failure(
                    DesktopCapabilityError.Internal("snapshot response payload is missing")),
            cancellationToken);

    /// <summary>只读 Shell 状态（窗口形态/托盘/自动化状态/打开页面数）。</summary>
    public Task<CapabilityResult<DesktopShellStatus>> GetShellStatusAsync(
        DesktopCallContext call, CancellationToken cancellationToken = default) =>
        InvokeAsync(
            DesktopCapability.ShellStatus,
            DesktopCapabilityRequest.ForShellStatus(),
            call,
            static response => response.ShellStatus is { } value
                ? CapabilityResult<DesktopShellStatus>.Success(value)
                : CapabilityResult<DesktopShellStatus>.Failure(
                    DesktopCapabilityError.Internal("shell_status response payload is missing")),
            cancellationToken);

    /// <summary>请求取消一个在途操作（尽力而为：不撤销已执行的脚本，结果由 Desktop 报告）。</summary>
    public async Task<bool> CancelAsync(OperationId operationId, CancellationToken cancellationToken = default)
    {
        PendingOperation? pending;
        lock (_sync)
        {
            if (!_pending.TryGetValue(operationId, out pending))
            {
                return false;
            }
        }

        var frame = new Proto.CoreFrame
        {
            Cancel = new Proto.OperationCancel
            {
                OperationId = operationId.Value,
                Generation = (ulong)Generation.Value,
                Reason = "cancelled by caller",
            },
        };

        return await TryEnqueueAsync(frame, Now + TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
    }

    // ── 生命周期 ──────────────────────────────────────────────────────────

    /// <summary>启动读/写循环（宿主适配器 await <see cref="Completion"/>）。</summary>
    internal void Start()
    {
        SetState(DesktopLinkState.Ready);
        _ = Task.Run(() => WriterLoopAsync(_lifetime.Token), CancellationToken.None);
        _ = Task.Run(() => ReaderLoopAsync(_lifetime.Token), CancellationToken.None);
    }

    /// <summary>断连：完成所有 pending（未开始 ⇒ Disconnected；已开始 ⇒ OutcomeUnknown）并结束循环。</summary>
    public Task DisconnectAsync(DesktopCapabilityError? reason = null) => ShutdownAsync(reason, faulted: false);

    public async ValueTask DisposeAsync()
    {
        await ShutdownAsync(DesktopCapabilityError.NotConnected("session disposed"), faulted: false).ConfigureAwait(false);
        _inFlight.Dispose();
    }

    private async Task ShutdownAsync(DesktopCapabilityError? reason, bool faulted)
    {
        if (Interlocked.Exchange(ref _shutdownStarted, 1) != 0)
        {
            await _completion.Task.ConfigureAwait(false);
            return;
        }

        PendingOperation[] outstanding;
        lock (_sync)
        {
            outstanding = _pending.Values.ToArray();
            _pending.Clear();
            _lastError = reason;
            _state = faulted ? DesktopLinkState.Faulted : DesktopLinkState.Disconnected;
        }

        try
        {
            _lifetime.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        _outbound.Writer.TryComplete();

        foreach (var pending in outstanding)
        {
            pending.Dispose();
            var error = pending.Started
                ? DesktopCapabilityError.OutcomeUnknown("channel disconnected while the operation was running")
                : DesktopCapabilityError.Disconnected("channel disconnected before the operation was sent");

            // 先归还在途额度再完成等待者：等待者恢复时 slotHeld 已置 false，不会重复释放。
            _inFlight.Release();
            pending.Completion.TrySetResult(CapabilityResult<DesktopCapabilityResponse>.Failure(error));
            Audit(pending, DesktopCapabilityOutcome.Disconnected, error);
        }

        try
        {
            await _channel.SendAsync(
                new Proto.CoreFrame { Heartbeat = new Proto.Heartbeat { Sequence = -1 } },
                CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // 流可能已经不可用：断连收尾不因此失败。
        }

        var state = State;
        try
        {
            StateChanged?.Invoke(state);
        }
        catch
        {
            // 订阅者异常不影响收尾。
        }

        _completion.TrySetResult();
    }

    // ── 调用实现 ──────────────────────────────────────────────────────────

    private async Task<CapabilityResult<TResult>> InvokeAsync<TResult>(
        DesktopCapability capability,
        DesktopCapabilityRequest request,
        DesktopCallContext call,
        Func<DesktopCapabilityResponse, CapabilityResult<TResult>> project,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(call);
        ArgumentNullException.ThrowIfNull(request);

        if (!DesktopCapabilities.TryGet(capability, out var descriptor))
        {
            return CapabilityResult<TResult>.Failure(DesktopCapabilityError.Internal("unregistered capability"));
        }

        if (!string.Equals(call.DesktopId.Value, DesktopId.Value, StringComparison.Ordinal))
        {
            return CapabilityResult<TResult>.Failure(
                DesktopCapabilityError.InvalidTarget($"call targets '{call.DesktopId.Value}', not this session"));
        }

        if (State != DesktopLinkState.Ready)
        {
            return CapabilityResult<TResult>.Failure(
                DesktopCapabilityError.NotConnected($"desktop session is {State}"));
        }

        if (!NegotiatedCapabilities.HasFlag(capability))
        {
            return CapabilityResult<TResult>.Failure(
                DesktopCapabilityError.UnsupportedCapability(descriptor.Name));
        }

        if (call.IsExpiredAt(Now))
        {
            return CapabilityResult<TResult>.Failure(DesktopCapabilityError.DeadlineExceeded(mayHaveSideEffects: false));
        }

        var fingerprint = CoreCommandEncoder.Fingerprint(descriptor, request);

        // 重复 OperationId：同 payload 复用进行中的任务；不同 payload 拒绝。
        PendingOperation? existing;
        lock (_sync)
        {
            _pending.TryGetValue(call.OperationId, out existing);
        }

        if (existing is not null)
        {
            if (!string.Equals(existing.Fingerprint, fingerprint, StringComparison.Ordinal))
            {
                return CapabilityResult<TResult>.Failure(
                    DesktopCapabilityError.InvalidRequest("duplicate operation id with a different payload"));
            }

            var reused = await existing.Completion.Task.ConfigureAwait(false);
            return reused.IsSuccess ? project(reused.Value) : CapabilityResult<TResult>.Failure(reused.Error);
        }

        var denial = await _authorizer.AuthorizeAsync(
            new DesktopCapabilityAuthorizationContext(DesktopId, Generation, descriptor, request, call),
            cancellationToken).ConfigureAwait(false);

        if (denial is not null)
        {
            AuditTransient(call.OperationId, descriptor.Name, DesktopCapabilityOutcome.Rejected, denial);
            return CapabilityResult<TResult>.Failure(denial);
        }

        var slotHeld = false;
        PendingOperation? pending = null;
        try
        {
            slotHeld = await AcquireSlotAsync(call.DeadlineUtc, cancellationToken).ConfigureAwait(false);
            if (!slotHeld)
            {
                // 断连与「额度耗尽到期限」必须给出不同语义。
                if (State != DesktopLinkState.Ready)
                {
                    var disconnected = DesktopCapabilityError.NotConnected($"desktop session is {State}");
                    AuditTransient(call.OperationId, descriptor.Name, DesktopCapabilityOutcome.Disconnected, disconnected);
                    return CapabilityResult<TResult>.Failure(disconnected);
                }

                var exhausted = DesktopCapabilityError.ResourceExhausted(
                    "in-flight limit reached before the operation deadline");
                AuditTransient(call.OperationId, descriptor.Name, DesktopCapabilityOutcome.Rejected, exhausted);
                return CapabilityResult<TResult>.Failure(exhausted);
            }

            if (IsStalePageVersion(request, out var knownVersion))
            {
                // 「交互提交后旧 Ref 作废」的 Core 侧强制：不发命令，也不把陈旧引用当有效引用用。
                var stale = new DesktopCapabilityError(
                    DesktopCapabilityErrorCode.PageVersionMismatch,
                    $"request pins page version {request.ExpectedPageVersion.Value} but the desktop is already at {knownVersion}",
                    retryable: true,
                    mayHaveSideEffects: false);
                AuditTransient(call.OperationId, descriptor.Name, DesktopCapabilityOutcome.Rejected, stale);
                return CapabilityResult<TResult>.Failure(stale);
            }

            pending = new PendingOperation(call.OperationId, descriptor.Name, fingerprint, call, Now);
            pending.SetRequestTarget(request.Target);
        pending.SetRequestLocator(request.Locate?.Locator);

            // 期限到点：本地给出 deadline_exceeded（携带「可能已产生副作用」），Desktop 的迟到结果被忽略。
            var remaining = call.DeadlineUtc - Now;
            if (remaining > TimeSpan.Zero)
            {
                pending.AttachTimer(_clock.CreateTimer(
                    _ =>
                    {
                        var expired = DesktopCapabilityError.DeadlineExceeded(pending.Started);
                        CompleteLocally(pending, DesktopCapabilityOutcome.DeadlineExceeded, expired);
                    },
                    null,
                    remaining,
                    Timeout.InfiniteTimeSpan));
            }

            lock (_sync)
            {
                _pending[call.OperationId] = pending;
            }

            var command = CoreCommandEncoder.Encode(
                call.OperationId, Generation, descriptor, request, call, traceId: null);

            if (!await TryEnqueueAsync(new Proto.CoreFrame { Command = command }, call.DeadlineUtc, cancellationToken)
                    .ConfigureAwait(false))
            {
                var exhausted = State != DesktopLinkState.Ready
                    ? DesktopCapabilityError.NotConnected($"desktop session is {State}")
                    : DesktopCapabilityError.ResourceExhausted("outbound queue was full until the operation deadline");
                CompleteLocally(
                    pending,
                    State != DesktopLinkState.Ready
                        ? DesktopCapabilityOutcome.Disconnected
                        : DesktopCapabilityOutcome.Rejected,
                    exhausted);
                slotHeld = false;
                return CapabilityResult<TResult>.Failure(exhausted);
            }

            pending.MarkSent(Now);

            // 调用方取消：只取消这一个操作，发取消帧并立即给出终态。
            using var registration = cancellationToken.Register(
                static state =>
                {
                    var (session, operation) = ((DesktopSession, PendingOperation))state!;
                    _ = session.CancelPendingAsync(operation);
                },
                (this, pending),
                useSynchronizationContext: false);

            var response = await pending.Completion.Task.ConfigureAwait(false);
            slotHeld = false;
            return response.IsSuccess ? project(response.Value) : CapabilityResult<TResult>.Failure(response.Error);
        }
        catch (OperationCanceledException)
        {
            // 调用方取消：未发出的操作如实标注「无副作用」。
            return CapabilityResult<TResult>.Failure(DesktopCapabilityError.Cancelled(mayHaveSideEffects: false));
        }
        finally
        {
            if (slotHeld && pending is not null && !CompleteLocally(
                    pending, DesktopCapabilityOutcome.Cancelled, DesktopCapabilityError.Cancelled(mayHaveSideEffects: false)))
            {
                _inFlight.Release();
            }
        }
    }

    /// <summary>取消单个在途操作：发取消帧 + 本地给出 cancelled（可能已产生副作用）。</summary>
    private async Task CancelPendingAsync(PendingOperation pending)
    {
        var cancelled = DesktopCapabilityError.Cancelled(mayHaveSideEffects: true);
        if (!CompleteLocally(pending, DesktopCapabilityOutcome.Cancelled, cancelled))
        {
            return;
        }

        await TryEnqueueAsync(
            new Proto.CoreFrame
            {
                Cancel = new Proto.OperationCancel
                {
                    OperationId = pending.Id.Value,
                    Generation = (ulong)Generation.Value,
                    Reason = "caller cancelled",
                },
            },
            Now + TimeSpan.FromSeconds(5),
            CancellationToken.None).ConfigureAwait(false);
    }

    /// <summary>获取在途额度：期限耗尽返回 false；调用方取消则抛出（由调用处映射为 cancelled）。</summary>
    private async Task<bool> AcquireSlotAsync(DateTimeOffset deadlineUtc, CancellationToken cancellationToken)
    {
        var remaining = deadlineUtc - Now;
        if (remaining <= TimeSpan.Zero)
        {
            return false;
        }

        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        budget.CancelAfter(remaining);

        try
        {
            await _inFlight.WaitAsync(budget.Token).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // 期限到点，或会话断连：两者都返回「拿不到额度」，由调用处区分语义。
            return false;
        }
    }

    private async Task<bool> TryEnqueueAsync(
        Proto.CoreFrame frame, DateTimeOffset deadlineUtc, CancellationToken cancellationToken)
    {
        var remaining = deadlineUtc - Now;
        if (remaining <= TimeSpan.Zero)
        {
            return false;
        }

        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        budget.CancelAfter(remaining);

        try
        {
            await _outbound.Writer.WriteAsync(frame, budget.Token).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // 队列满且等到 deadline（或会话断连）：明确返回「预算耗尽」，不静默丢弃命令。
            return false;
        }
    }

    private bool CompleteLocally(PendingOperation pending, DesktopCapabilityOutcome outcome, DesktopCapabilityError error)
    {
        lock (_sync)
        {
            if (!_pending.Remove(pending.Id))
            {
                return false;
            }
        }

        pending.Dispose();
        _inFlight.Release();
        pending.Completion.TrySetResult(CapabilityResult<DesktopCapabilityResponse>.Failure(error));
        Audit(pending, outcome, error);
        return true;
    }

    /// <summary>
    /// 期望版本是否已过期：只有「本会话已经观测到更新的版本」才算过期。
    /// 未知版本（0）不作判断——那表示调用方不要求版本约束，而不是「旧版本」。
    /// </summary>
    /// <summary>测试可见：本会话对某目标已知的页面版本（0 = 尚未观测到）。</summary>
    internal long KnownPageVersionFor(DesktopPageTarget target)
    {
        lock (_sync)
        {
            return _knownPageVersions.TryGetValue(target.Key, out var known) ? known : 0;
        }
    }

    private bool IsStalePageVersion(DesktopCapabilityRequest request, out long knownVersion)
    {
        knownVersion = 0;
        var target = request.Target;
        var expected = request.ExpectedPageVersion;
        if (target is null || expected.Value <= 0)
        {
            return false;
        }

        lock (_sync)
        {
            return _knownPageVersions.TryGetValue(target.Key, out knownVersion) && knownVersion > expected.Value;
        }
    }

    /// <summary>从成功结果里提取 (目标, 版本)；结果帧不回带目标时用请求目标补齐。</summary>
    private void RecordPageVersion(PendingOperation pending, DesktopCapabilityResponse response)
    {
        // 交互结果同样推进版本——**这是「交互后旧 Ref 作废」的关键一步**：
        // 漏掉任何一类结果都会让旧引用继续被当成有效引用（真实端点探针正是这样发现的）。
        var target = response.PageState?.Target ?? response.Snapshot?.Target ?? response.Locate?.Target
            ?? response.Interact?.Target ?? pending.RequestTarget;
        var version = response.Navigate?.PageVersion
            ?? response.PageState?.Version
            ?? response.Snapshot?.PageVersion
            ?? response.Locate?.PageVersion
            ?? response.Interact?.Page.Version;

        if (target is null || version is not { } observed || observed.Value <= 0)
        {
            return;
        }

        lock (_sync)
        {
            if (!_knownPageVersions.TryGetValue(target.Key, out var current) || observed.Value > current)
            {
                _knownPageVersions[target.Key] = observed.Value;
            }
        }
    }

    private void CompleteFromDesktop(PendingOperation pending, CapabilityResult<DesktopCapabilityResponse> result)
    {
        lock (_sync)
        {
            if (!_pending.Remove(pending.Id))
            {
                return;
            }
        }

        pending.Dispose();
        _inFlight.Release();

        // 成功结果把「该目标当前是哪一版」记下来：旧版本的引用自此作废。
        if (result.IsSuccess)
        {
            RecordPageVersion(pending, result.Value);
        }

        pending.Completion.TrySetResult(result);
        Audit(pending, result.IsSuccess ? DesktopCapabilityOutcome.Succeeded : DesktopCapabilityOutcome.Failed, result.IsSuccess ? null : result.Error);
    }

    // ── 读/写循环 ─────────────────────────────────────────────────────────

    private async Task WriterLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (await _outbound.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                while (_outbound.Reader.TryRead(out var frame))
                {
                    try
                    {
                        await _channel.SendAsync(frame, cancellationToken).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        await FaultAsync(DesktopCapabilityError.NotConnected($"channel send failed ({ex.GetType().Name})"))
                            .ConfigureAwait(false);
                        return;
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 会话结束。
        }
    }

    private async Task ReaderLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            Proto.DesktopFrame? frame;
            try
            {
                frame = await _channel.ReadAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                await FaultAsync(DesktopCapabilityError.NotConnected($"channel read failed ({ex.GetType().Name})"))
                    .ConfigureAwait(false);
                return;
            }

            if (frame is null)
            {
                // 对端正常结束：这是 Disconnected，不是故障。
                await ShutdownAsync(DesktopCapabilityError.Disconnected("desktop closed the stream"), faulted: false)
                    .ConfigureAwait(false);
                return;
            }

            switch (frame.FrameCase)
            {
                case Proto.DesktopFrame.FrameOneofCase.Result when frame.Result is not null:
                    OnResult(frame.Result);
                    break;

                case Proto.DesktopFrame.FrameOneofCase.Event when frame.Event is not null:
                    try
                    {
                        EventReceived?.Invoke(frame.Event);
                    }
                    catch
                    {
                        // 订阅者异常不影响读循环。
                    }

                    break;

                case Proto.DesktopFrame.FrameOneofCase.Hello:
                    await FaultAsync(DesktopCapabilityError.InvalidRequest("desktop sent a second hello"))
                        .ConfigureAwait(false);
                    return;

                default:
                    await FaultAsync(DesktopCapabilityError.InvalidRequest("desktop sent a frame with no payload"))
                        .ConfigureAwait(false);
                    return;
            }
        }
    }

    private void OnResult(Proto.OperationResult result)
    {
        if (!OperationId.IsValid(result.OperationId))
        {
            return;
        }

        var operationId = new OperationId(result.OperationId);
        PendingOperation? pending;
        lock (_sync)
        {
            _pending.TryGetValue(operationId, out pending);
        }

        if (pending is null)
        {
            // 未知操作（例如上一世代的迟到结果）：绝不完成当前世代的其它命令。
            return;
        }

        if (result.Generation != (ulong)Generation.Value)
        {
            return;
        }

        if (!DesktopCapabilities.TryGetByName(pending.Capability, out var descriptor))
        {
            CompleteFromDesktop(
                pending,
                CapabilityResult<DesktopCapabilityResponse>.Failure(
                    DesktopCapabilityError.Internal("result arrived for an unregistered capability")));
            return;
        }

        CompleteFromDesktop(
                pending,
                DesktopResultDecoder.Decode(
                    result, descriptor.Capability, pending.RequestTarget, pending.RequestLocator));
    }

    private async Task FaultAsync(DesktopCapabilityError error)
    {
        lock (_sync)
        {
            _lastError = error;
            _state = DesktopLinkState.Faulted;
        }

        await ShutdownAsync(error, faulted: true).ConfigureAwait(false);
    }

    // ── 支撑 ──────────────────────────────────────────────────────────────

    private DateTimeOffset Now => _clock.GetUtcNow();

    private void SetState(DesktopLinkState state)
    {
        lock (_sync)
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
            // 订阅者异常不影响会话。
        }
    }

    private void AuditTransient(
        OperationId operationId, string capability, DesktopCapabilityOutcome outcome, DesktopCapabilityError error)
    {
        try
        {
            _audit.Record(new DesktopCapabilityAuditRecord
            {
                OperationId = operationId,
                Generation = Generation,
                Capability = capability,
                Outcome = outcome,
                ErrorCode = error.Code,
                RecordedUtc = Now,
            });
        }
        catch
        {
            // 审计失败不得影响调用结果。
        }
    }

    private void Audit(PendingOperation pending, DesktopCapabilityOutcome outcome, DesktopCapabilityError? error)
    {
        if (Interlocked.Exchange(ref pending.AuditFlag, 1) != 0)
        {
            return;
        }

        var recordedAt = Now;
        try
        {
            _audit.Record(new DesktopCapabilityAuditRecord
            {
                OperationId = pending.Id,
                Generation = Generation,
                Capability = pending.Capability,
                Outcome = outcome,
                QueueDuration = pending.SentAtUtc is { } sent ? sent - pending.CreatedAtUtc : recordedAt - pending.CreatedAtUtc,
                ExecutionDuration = pending.SentAtUtc is { } started ? recordedAt - started : TimeSpan.Zero,
                ErrorCode = error?.Code,
                TraceId = pending.TraceId,
                CorrelationId = pending.Call.CorrelationId,
                RecordedUtc = recordedAt,
            });
        }
        catch
        {
            // 同上。
        }
    }

    /// <summary>一个在途操作：终态由 Desktop 结果、期限、取消或断连四者之一决定（先到先得）。</summary>
    private sealed class PendingOperation
    {
        private ITimer? _deadlineTimer;

        public PendingOperation(
            OperationId id,
            string capability,
            string fingerprint,
            DesktopCallContext call,
            DateTimeOffset createdAtUtc)
        {
            Id = id;
            Capability = capability;
            Fingerprint = fingerprint;
            Call = call;
            CreatedAtUtc = createdAtUtc;
        }

        public OperationId Id { get; }

        public string Capability { get; }

        public string Fingerprint { get; }

        public DesktopCallContext Call { get; }

        public DesktopPageTarget? RequestTarget { get; private set; }

    /// <summary>定位请求的描述符：结果必须回填同一个描述符（结果帧不重复携带它）。</summary>
    public DesktopLocator? RequestLocator { get; private set; }

        public DateTimeOffset CreatedAtUtc { get; }

        public DateTimeOffset? SentAtUtc { get; private set; }

        public bool Started => SentAtUtc is not null;

        public string? TraceId { get; init; }

        public int AuditFlag;

        public TaskCompletionSource<CapabilityResult<DesktopCapabilityResponse>> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void SetRequestTarget(DesktopPageTarget? target) => RequestTarget = target;

    public void SetRequestLocator(DesktopLocator? locator) => RequestLocator = locator;

        public void AttachTimer(ITimer timer) => _deadlineTimer = timer;

        /// <summary>入队成功后调用：此后期限/取消都视为「可能已产生副作用」。</summary>
        public void MarkSent(DateTimeOffset now) => SentAtUtc = now;

        public void Dispose() => _deadlineTimer?.Dispose();
    }
}
