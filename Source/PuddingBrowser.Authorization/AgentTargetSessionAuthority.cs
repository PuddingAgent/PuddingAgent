using Pudding.Contracts;
using Pudding.Contracts.Desktop;

namespace PuddingBrowser.Authorization;

/// <summary>
/// Agent 浏览器控制的**唯一权威（授权决策点）**。
///
/// 它回答两个问题：**「用户此刻是否把这一页交给了这个 Core 会话，且桥仍是当初那一代？」**
/// 以及**「这次操作在入队后是否仍然被允许」**。它不回答「调用者是不是 Agent」——
/// 所有者由宿主作为可信控制句柄注入（模型不能自报），见 ADR-097 D3。
///
/// 状态机（**目标作废**时推进 <see cref="ControlEpoch"/> 与 <see cref="WriteEpoch"/>，
/// 使在途票据立刻失效；**暂停/接管**是独立的运行控制轴，只推进写世代并取消在途写，
/// 因为接管不撤销阅读、目标并未作废；退出暂停/接管**不**复活旧写票据）：
/// <code>
///                    OnBridgeReady(gen)
///   [无桥] ─────────────────────────────► [桥已就绪]
///      ▲                                     │  SelectTarget(target, handle) ──► [有目标]
///      │ OnBridgeLost / Close / Revoke       │                                   │
///      └─────────────────────────────────────┴── 换目标 / 关页 / 所有者失效 ──► 目标作废
/// </code>
///
/// 执行不变量（D4）：每张写票据有唯一 <see cref="AgentTargetPermitId"/>；复检核对
/// 会话、目标、连接世代、控制世代、写世代、票据仍在途与取消状态；释放后的票据不可再用；
/// A 的迟到/重复完成不得释放 B 的占用。
///
/// 线程安全：全部状态在 <see cref="Lock"/> 下读写；取消令牌在锁外触发。
/// </summary>
public sealed class AgentTargetSessionAuthority
{
    private readonly Lock _sync = new();
    private readonly TimeProvider _timeProvider;

    private ConnectionGeneration _generation = ConnectionGeneration.None;
    private BrowserControlEpoch _controlEpoch = BrowserControlEpoch.Initial;
    private AgentTargetWriteEpoch _writeEpoch = AgentTargetWriteEpoch.Initial;
    private AgentTargetSession? _session;
    private AgentTargetSessionId? _lastRevokedSessionId;
    private AgentTargetPermit? _permitInFlight;
    private CancellationTokenSource? _permitCancellation;
    private AgentTargetControlState _controlState;
    private string? _reason;
    private bool _isClosed;

    public AgentTargetSessionAuthority(TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    // ── 读取面 ────────────────────────────────────────────────────────────

    public BrowserControlEpoch ControlEpoch
    {
        get
        {
            lock (_sync)
            {
                return _controlEpoch;
            }
        }
    }

    public AgentTargetWriteEpoch WriteEpoch
    {
        get
        {
            lock (_sync)
            {
                return _writeEpoch;
            }
        }
    }

    public AgentTargetControlState ControlState
    {
        get
        {
            lock (_sync)
            {
                return _controlState;
            }
        }
    }

    public bool IsBridgeReady
    {
        get
        {
            lock (_sync)
            {
                return _generation.IsLive;
            }
        }
    }

    public AgentTargetSessionSnapshot Capture()
    {
        lock (_sync)
        {
            return Snapshot();
        }
    }

    // ── 传输生命周期（**这是历史上缺失的那一环**：无人把「桥已握手」翻译成「权威已活」）──

    /// <summary>
    /// 桥完成握手并确认世代。**同一世代重复调用是幂等的**；世代变化会作废现有目标。
    /// </summary>
    public AgentTargetSessionSnapshot OnBridgeReady(ConnectionGeneration generation, string? reason = null)
    {
        if (!generation.IsLive)
        {
            throw new ArgumentException(
                "OnBridgeReady requires a live generation (use OnBridgeLost for teardown).",
                nameof(generation));
        }

        CancellationTokenSource? toCancel = null;
        AgentTargetSessionSnapshot snapshot;
        lock (_sync)
        {
            var changed = _generation.Value != generation.Value;
            _generation = generation;

            if (changed)
            {
                // 换代 ⇒ 旧目标一律作废：它绑定的世代已经不能被校验。
                toCancel = InvalidateTargetUnsafe(reason ?? "bridge generation changed");
            }

            _reason = reason;
            snapshot = Snapshot();
        }

        toCancel?.Cancel();
        return snapshot;
    }

    /// <summary>桥断开：世代归 <see cref="ConnectionGeneration.None"/>，目标作废，在途票据失效。</summary>
    public AgentTargetSessionSnapshot OnBridgeLost(string? reason = null)
    {
        CancellationTokenSource? toCancel;
        AgentTargetSessionSnapshot snapshot;
        lock (_sync)
        {
            _generation = ConnectionGeneration.None;
            toCancel = InvalidateTargetUnsafe(reason ?? "bridge disconnected");
            snapshot = Snapshot();
        }

        toCancel?.Cancel();
        return snapshot;
    }

    // ── 用户的授权动作（**唯一**的授权来源）──

    /// <summary>
    /// 用户把某一页设为 **<paramref name="controlHandle"/> 所属 Core 会话**的 Agent 目标。
    ///
    /// · 同一所有者 + 同一目标 ⇒ 幂等（保留同一会话）；
    /// · 换目标或换所有者 ⇒ **原子替换**并作废旧会话（旧执行票据随世代失效，不被新所有者继承）；
    /// · 缺少可信控制句柄或桥未就绪 ⇒ **不产生目标**（fail-closed，不静默降级）。
    /// </summary>
    public AgentTargetSessionSnapshot SelectTarget(
        DesktopPageTarget target,
        AgentTargetControlHandle? controlHandle,
        string? reason = null)
    {
        ArgumentNullException.ThrowIfNull(target);

        CancellationTokenSource? toCancel = null;
        AgentTargetSessionSnapshot snapshot;
        lock (_sync)
        {
            if (_isClosed)
            {
                _reason = AgentTargetErrorCodes.Closed;
                return Snapshot();
            }

            if (controlHandle is null)
            {
                // 没有可信所有者就不能开启会话：宁可保持非目标态，也不产生无主授权。
                _reason = AgentTargetErrorCodes.ControlHandleRequired;
                return Snapshot();
            }

            if (!_generation.IsLive)
            {
                _reason = AgentTargetErrorCodes.TransportUnavailable;
                return Snapshot();
            }

            if (_session is not null
                && string.Equals(_session.Target.Key, target.Key, StringComparison.Ordinal)
                && _session.Owner.Equals(controlHandle.Owner))
            {
                _reason = "target selection is idempotent for the current page";
                return Snapshot();
            }

            if (_session is not null)
            {
                toCancel = InvalidateTargetUnsafe(reason ?? "target switched");
            }

            _session = new AgentTargetSession(
                AgentTargetSessionId.New(),
                target,
                _generation,
                controlHandle,
                _timeProvider.GetUtcNow());
            // 保留 _lastRevokedSessionId：它是审计事实（“此会话已被替换”），不因新选择而抹除。
            _reason = reason;
            snapshot = Snapshot();
        }

        toCancel?.Cancel();
        return snapshot;
    }

    /// <summary>用户撤销目标（或不接管而直接收回）。</summary>
    public AgentTargetSessionSnapshot Revoke(string? reason = null)
    {
        CancellationTokenSource? toCancel;
        AgentTargetSessionSnapshot snapshot;
        lock (_sync)
        {
            toCancel = InvalidateTargetUnsafe(reason ?? "revoked by user");
            snapshot = Snapshot();
        }

        toCancel?.Cancel();
        return snapshot;
    }

    /// <summary>目标页被关闭：只在该页正是当前目标时作废会话（关页 = 原子失效旧控制权）。</summary>
    public AgentTargetSessionSnapshot NotifyPageClosed(DesktopPageTarget target, string? reason = null)
    {
        ArgumentNullException.ThrowIfNull(target);

        CancellationTokenSource? toCancel = null;
        AgentTargetSessionSnapshot snapshot;
        lock (_sync)
        {
            if (_session is not null && string.Equals(_session.Target.Key, target.Key, StringComparison.Ordinal))
            {
                toCancel = InvalidateTargetUnsafe(reason ?? "the agent target page was closed");
            }

            snapshot = Snapshot();
        }

        toCancel?.Cancel();
        return snapshot;
    }

    /// <summary>
    /// 所有者失效（Core 会话结束/被替换）：若当前目标属于该所有者则收回控制权。
    /// 恢复聊天**不**自动继承旧控制权或旧执行票据。
    /// </summary>
    public AgentTargetSessionSnapshot NotifyOwnerUnavailable(AgentTargetOwnerId owner, string? reason = null)
    {
        CancellationTokenSource? toCancel = null;
        AgentTargetSessionSnapshot snapshot;
        lock (_sync)
        {
            if (_session is not null && _session.Owner.Equals(owner))
            {
                toCancel = InvalidateTargetUnsafe(reason ?? "the owning core session is no longer available");
            }

            snapshot = Snapshot();
        }

        toCancel?.Cancel();
        return snapshot;
    }

    // ── 运行控制（**不是权限审批**：暂停/接管不撤销阅读，只阻断副作用）──

    /// <summary>
    /// 用户暂停：阻断所有**副作用**操作并取消在途写；**保留目标**，只读观察仍被允许。
    /// 幂等：已暂停时重复调用不再推进写世代。
    /// </summary>
    public AgentTargetSessionSnapshot BeginPause(string? reason = null)
        => EnterBlocking(AgentTargetControlState.Paused, reason ?? "browser automation is paused by the user");

    /// <summary>用户取消暂停。只清除暂停轴，不影响接管轴。</summary>
    public AgentTargetSessionSnapshot EndPause(string? reason = null)
        => ExitBlocking(AgentTargetControlState.Paused, reason);

    /// <summary>
    /// 用户接管：阻断所有**副作用**操作并取消在途写；**保留目标**，只读观察仍被允许。
    /// 幂等：已接管时重复调用不再推进写世代。接管与暂停是独立两轴，互不覆盖。
    /// </summary>
    public AgentTargetSessionSnapshot BeginUserControl(string? reason = null)
        => EnterBlocking(AgentTargetControlState.UserTakeover, reason ?? "user took control");

    /// <summary>用户交还控制权。目标（若有）继续有效；接管前入队的旧写票据不会复活。</summary>
    public AgentTargetSessionSnapshot EndUserControl(string? reason = null)
        => ExitBlocking(AgentTargetControlState.UserTakeover, reason);

    /// <summary>关闭浏览器自动化（不可逆）。</summary>
    public AgentTargetSessionSnapshot Close(string? reason = null)
    {
        CancellationTokenSource? toCancel;
        AgentTargetSessionSnapshot snapshot;
        lock (_sync)
        {
            _isClosed = true;
            toCancel = InvalidateTargetUnsafe(reason ?? "browser automation closed");
            snapshot = Snapshot();
        }

        toCancel?.Cancel();
        return snapshot;
    }

    // ── 两段式判定：入队前 Admit，触碰页面前 Revalidate ────────────────

    /// <summary>第一段：入队前判定。副作用操作签发唯一写票据并占用唯一写槽。</summary>
    public AgentTargetDecision Authorize(AgentTargetRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        lock (_sync)
        {
            var gate = GateUnsafe(request.Target, request.ControlHandle);
            if (gate is not null)
            {
                return gate;
            }

            var isSideEffect = request.Operation.IsSideEffect();

            // 暂停/接管是**运行控制**：只阻断副作用，阅读继续（69 规格 §1.1）。
            // 两轴合成一个判据：任一阻断状态都拒绝写。
            if (isSideEffect && _controlState != AgentTargetControlState.None)
            {
                return AgentTargetDecision.Deny(
                    AgentTargetDenial.Busy,
                    "the user paused or took control of this desktop browser");
            }

            // 副作用操作串行化：同一时刻最多一张写票据在途。
            if (isSideEffect)
            {
                if (_permitInFlight is not null)
                {
                    return AgentTargetDecision.Deny(
                        AgentTargetDenial.Busy,
                        "another mutating browser operation is already in flight on this desktop");
                }

                if (request.Operation.RequiresUserConfirmation() && !request.HasUserConfirmation)
                {
                    return AgentTargetDecision.Deny(
                        AgentTargetDenial.UserConfirmationRequired,
                        "this operation is classified as high-risk and requires one-time user confirmation");
                }
            }

            var cancellation = isSideEffect
                ? new CancellationTokenSource()
                : null;

            var permit = new AgentTargetPermit(
                AgentTargetPermitId.New(),
                _session!.SessionId,
                _generation,
                _controlEpoch,
                _writeEpoch,
                isSideEffect,
                cancellation?.Token ?? CancellationToken.None);

            if (isSideEffect)
            {
                _permitInFlight = permit;
                _permitCancellation = cancellation;
            }

            return AgentTargetDecision.Allow(permit);
        }
    }

    /// <summary>
    /// 第二段：真正触碰页面前的复检（消除「判定通过 → 期间被接管/暂停/换代/释放」的竞态）。
    ///
    /// 写票据额外核对：当前无阻断状态、取消信号未触发、写世代未变、票据仍在途。
    /// 任何一项不满足 ⇒ 拒绝；已释放的票据永远不能再通过复检。
    /// </summary>
    public AgentTargetDecision Revalidate(AgentTargetPermit permit)
    {
        ArgumentNullException.ThrowIfNull(permit);

        lock (_sync)
        {
            if (_isClosed)
            {
                return AgentTargetDecision.Deny(
                    AgentTargetDenial.Closed, "browser automation is closed");
            }

            if (!_generation.IsLive || _generation.Value != permit.BridgeGeneration.Value)
            {
                return AgentTargetDecision.Deny(
                    AgentTargetDenial.TransportUnavailable,
                    "the bridge generation changed after this operation was admitted");
            }

            if (_controlEpoch.Value != permit.ControlEpoch.Value
                || _session is null
                || _session.SessionId.Value != permit.SessionId.Value)
            {
                return AgentTargetDecision.Deny(
                    AgentTargetDenial.TargetRevoked,
                    "the target was revoked or switched after this operation was admitted");
            }

            if (!permit.RequiresExclusiveOwnership)
            {
                // 只读票据不受暂停/接管/写世代影响：接管不撤销阅读。
                return AgentTargetDecision.Allow(permit);
            }

            if (_controlState != AgentTargetControlState.None)
            {
                return AgentTargetDecision.Deny(
                    AgentTargetDenial.Busy,
                    "the user paused or took control while this operation was queued");
            }

            if (permit.Cancellation.IsCancellationRequested)
            {
                return AgentTargetDecision.Deny(
                    AgentTargetDenial.PermitInvalid,
                    "this operation was cancelled by a control-state change");
            }

            if (permit.WriteEpoch.Value != _writeEpoch.Value)
            {
                return AgentTargetDecision.Deny(
                    AgentTargetDenial.PermitInvalid,
                    "the write generation changed after this operation was admitted; a paused/taken-over operation never revives");
            }

            if (_permitInFlight is not { } inFlight
                || !inFlight.PermitId.Equals(permit.PermitId))
            {
                return AgentTargetDecision.Deny(
                    AgentTargetDenial.PermitInvalid,
                    "this operation permit was already released or cancelled");
            }

            return AgentTargetDecision.Allow(permit);
        }
    }

    /// <summary>
    /// 操作结束（无论成败）后精确释放**这一张**票据并触发其取消信号。
    /// 幂等；票据不匹配（迟到/重复完成）时不会误清后来票据的占用。
    /// </summary>
    public AgentTargetSessionSnapshot CompleteOperation(AgentTargetPermit permit)
    {
        ArgumentNullException.ThrowIfNull(permit);

        CancellationTokenSource? toCancel = null;
        AgentTargetSessionSnapshot snapshot;
        lock (_sync)
        {
            if (_permitInFlight is { } inFlight && inFlight.PermitId.Equals(permit.PermitId))
            {
                toCancel = _permitCancellation;
                _permitInFlight = null;
                _permitCancellation = null;
            }

            snapshot = Snapshot();
        }

        toCancel?.Cancel();
        return snapshot;
    }

    /// <summary>
    /// 取消在途操作（任务取消/超时/调用方放弃）：释放票据并触发取消信号。
    /// 与 <see cref="CompleteOperation"/> 的区别只在语义（“取消”而非“完成”），
    /// 两者都保证释放后的票据不可再用。
    /// </summary>
    public AgentTargetSessionSnapshot CancelOperation(AgentTargetPermit permit, string? reason = null)
    {
        ArgumentNullException.ThrowIfNull(permit);

        CancellationTokenSource? toCancel = null;
        AgentTargetSessionSnapshot snapshot;
        lock (_sync)
        {
            if (_permitInFlight is { } inFlight && inFlight.PermitId.Equals(permit.PermitId))
            {
                toCancel = _permitCancellation;
                _permitInFlight = null;
                _permitCancellation = null;
                _reason = reason ?? "the browser operation was cancelled";
            }

            snapshot = Snapshot();
        }

        toCancel?.Cancel();
        return snapshot;
    }

    // ── 内部 ──────────────────────────────────────────────────────────────

    private AgentTargetDecision? GateUnsafe(DesktopPageTarget target, AgentTargetControlHandle? controlHandle)
    {
        if (_isClosed)
        {
            return AgentTargetDecision.Deny(
                AgentTargetDenial.Closed, "browser automation is closed");
        }

        if (!_generation.IsLive)
        {
            return AgentTargetDecision.Deny(
                AgentTargetDenial.TransportUnavailable,
                "the desktop bridge is not connected; no target session can be honoured");
        }

        if (_session is null)
        {
            return _lastRevokedSessionId is not null
                ? AgentTargetDecision.Deny(
                    AgentTargetDenial.TargetRevoked,
                    "the agent target for this desktop was revoked; select a page again")
                : AgentTargetDecision.Deny(
                    AgentTargetDenial.TargetRequired,
                    "no page is selected as the agent target; the user must select one in the desktop UI");
        }

        // 可信控制句柄：宿主注入。缺失即拒绝（模型不能自报所有者）。
        if (controlHandle is null)
        {
            return AgentTargetDecision.Deny(
                AgentTargetDenial.ControlHandleRequired,
                "this tool call has no trusted control handle; the caller must be bound to a core session");
        }

        // 会话归属：无关 Core 会话不能复用用户交出的页面。
        if (!_session.Owner.Equals(controlHandle.Owner))
        {
            return AgentTargetDecision.Deny(
                AgentTargetDenial.OwnerMismatch,
                "the target belongs to a different core session; the user must hand the page over to this session");
        }

        // 页级隔离：只认用户选中的那一页，不因为"同一个 context 的其他页"而放行。
        if (!string.Equals(_session.Target.Key, target.Key, StringComparison.Ordinal))
        {
            return AgentTargetDecision.Deny(
                AgentTargetDenial.TargetRequired,
                "the requested page is not the page the user selected as the agent target");
        }

        return null;
    }

    private AgentTargetSessionSnapshot EnterBlocking(AgentTargetControlState state, string reason)
    {
        CancellationTokenSource? toCancel = null;
        AgentTargetSessionSnapshot snapshot;
        lock (_sync)
        {
            var entered = !_controlState.HasFlag(state);
            if (entered)
            {
                _controlState |= state;
                // 进入阻断状态 ⇒ 立即推进写世代并取消在途写；退出时不会复活它。
                toCancel = AdvanceWriteEpochUnsafe();
            }

            _reason = reason;
            snapshot = Snapshot();
        }

        toCancel?.Cancel();
        return snapshot;
    }

    private AgentTargetSessionSnapshot ExitBlocking(AgentTargetControlState state, string? reason)
    {
        lock (_sync)
        {
            _controlState &= ~state;
            _reason = reason;
            return Snapshot();
        }
    }

    /// <summary>作废目标：推进控制世代 + 写世代，清空运行控制状态，返回待取消的令牌源。</summary>
    private CancellationTokenSource? InvalidateTargetUnsafe(string reason)
    {
        if (_session is not null)
        {
            _lastRevokedSessionId = _session.SessionId;
            _session = null;
        }

        _controlEpoch = _controlEpoch.Next();
        _controlState = AgentTargetControlState.None;
        var toCancel = AdvanceWriteEpochUnsafe();
        _reason = reason;
        return toCancel;
    }

    /// <summary>推进写世代并清空在途写票据；返回需要取消的令牌源（在锁外取消）。</summary>
    private CancellationTokenSource? AdvanceWriteEpochUnsafe()
    {
        _writeEpoch = _writeEpoch.Next();
        var toCancel = _permitCancellation;
        _permitInFlight = null;
        _permitCancellation = null;
        return toCancel;
    }

    private AgentTargetSessionSnapshot Snapshot() => new(
        _generation.IsLive,
        _generation,
        _controlEpoch,
        _writeEpoch,
        _session,
        _controlState,
        _isClosed,
        _lastRevokedSessionId,
        _reason);
}
