using Pudding.Contracts;
using Pudding.Contracts.Desktop;

namespace PuddingBrowser.Authorization;

/// <summary>
/// Agent 浏览器控制的**唯一权威（授权决策点）**。
///
/// 它回答的问题只有一个：**「用户此刻是否把这一页交给了 Agent，且桥仍是当初那一代？」**
/// 它不回答「调用者是不是 Agent」——身份只用于审计（见方案 §9.2：把身份当门槛既挡不住提示注入，
/// 又会在环境上下文丢失时把本地浏览器整体关死）。
///
/// 状态机（**目标作废**时推进 <see cref="ControlEpoch"/>，使在途票据立刻失效；
/// 接管/交还**只翻转** <c>_isUserControlling</c>，因为接管是运行控制、目标并未作废）：
/// <code>
///                    OnBridgeReady(gen)
///   [无桥] ─────────────────────────────► [桥已就绪]
///      ▲                                     │  SelectTarget(page) ──► [有目标]
///      │ OnBridgeLost / Close                │                            │
///      └─────────────────────────────────────┴── Revoke / 目标切换 / 用户接管 ──► 目标作废
/// </code>
///
/// 线程安全：全部状态在 <see cref="Lock"/> 下读写。
/// </summary>
public sealed class AgentTargetSessionAuthority
{
    private readonly Lock _sync = new();
    private readonly TimeProvider _timeProvider;

    private ConnectionGeneration _generation = ConnectionGeneration.None;
    private BrowserControlEpoch _controlEpoch = BrowserControlEpoch.Initial;
    private AgentTargetSession? _session;
    private AgentTargetSessionId? _lastRevokedSessionId;
    private AgentTargetPermit? _permitInFlight;
    private string? _reason;
    private bool _isUserControlling;
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

        lock (_sync)
        {
            var changed = _generation.Value != generation.Value;
            _generation = generation;

            if (changed)
            {
                // 换代 ⇒ 旧目标一律作废：它绑定的世代已经不能被校验。
                InvalidateTargetUnsafe(reason ?? "bridge generation changed");
            }

            _reason = reason;
            return Snapshot();
        }
    }

    /// <summary>桥断开：世代归 <see cref="ConnectionGeneration.None"/>，目标作废，在途票据失效。</summary>
    public AgentTargetSessionSnapshot OnBridgeLost(string? reason = null)
    {
        lock (_sync)
        {
            _generation = ConnectionGeneration.None;
            InvalidateTargetUnsafe(reason ?? "bridge disconnected");
            return Snapshot();
        }
    }

    // ── 用户的授权动作（**唯一**的授权来源）──

    /// <summary>
    /// 用户把某一页设为 Agent 目标。
    ///
    /// · 与当前目标相同 ⇒ 幂等（保留同一会话）；与当前目标不同 ⇒ **原子替换**并作废旧会话；
    /// · 桥未就绪时**不产生目标**（不静默降级），原因由快照反映。
    /// </summary>
    public AgentTargetSessionSnapshot SelectTarget(DesktopPageTarget target, string? reason = null)
    {
        ArgumentNullException.ThrowIfNull(target);

        lock (_sync)
        {
            if (_isClosed)
            {
                _reason = "browser automation is closed";
                return Snapshot();
            }

            if (!_generation.IsLive)
            {
                _reason = AgentTargetErrorCodes.TransportUnavailable;
                return Snapshot();
            }

            if (_session is not null
                && string.Equals(_session.Target.Key, target.Key, StringComparison.Ordinal))
            {
                _reason = "target selection is idempotent for the current page";
                return Snapshot();
            }

            if (_session is not null)
            {
                InvalidateTargetUnsafe(reason ?? "target switched");
            }

            _session = new AgentTargetSession(
                AgentTargetSessionId.New(),
                target,
                _generation,
                _timeProvider.GetUtcNow());
            // 保留 _lastRevokedSessionId：它是审计事实（“此会话已被替换”），不因新选择而抹除。
            _reason = reason;
            return Snapshot();
        }
    }

    /// <summary>用户撤销目标（或不接管而直接收回）。</summary>
    public AgentTargetSessionSnapshot Revoke(string? reason = null)
    {
        lock (_sync)
        {
            InvalidateTargetUnsafe(reason ?? "revoked by user");
            return Snapshot();
        }
    }

    // ── 运行控制（**不是权限审批**：接管不撤销阅读，只阻断副作用）──

    /// <summary>
    /// 用户接管/暂停：阻断所有**副作用**操作并让在途票据失效；**保留目标**，
    /// 因此只读观察仍被允许（依据 69 规格 §1.1「接管是运行控制，不是权限审批」）。
    /// </summary>
    public AgentTargetSessionSnapshot BeginUserControl(string? reason = null)
    {
        lock (_sync)
        {
            _isUserControlling = true;
            // 注意：**不推进控制世代**。接管是运行控制，目标未作废（69 规格 §1.1）；
            // 在途副作用由 Revalidate 以 Busy 拦下，因此不会“跨接管复活”。
            _reason = reason ?? "user took control";
            return Snapshot();
        }
    }

    /// <summary>用户交还控制权。目标（若有）继续有效。</summary>
    public AgentTargetSessionSnapshot EndUserControl(string? reason = null)
    {
        lock (_sync)
        {
            _isUserControlling = false;
            _reason = reason;
            return Snapshot();
        }
    }

    /// <summary>关闭浏览器自动化（不可逆）。</summary>
    public AgentTargetSessionSnapshot Close(string? reason = null)
    {
        lock (_sync)
        {
            _isClosed = true;
            InvalidateTargetUnsafe(reason ?? "browser automation closed");
            return Snapshot();
        }
    }

    // ── 两段式判定：入队前 Admit，触碰页面前 Revalidate ────────────────

    /// <summary>第一段：入队前判定。</summary>
    public AgentTargetDecision Authorize(AgentTargetRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        lock (_sync)
        {
            var gate = GateUnsafe(request.Target);
            if (gate is not null)
            {
                return gate;
            }

            // 接管/暂停是**运行控制**：只阻断副作用，阅读继续（69 规格 §1.1）。
            if (_isUserControlling && request.Operation.IsSideEffect())
            {
                return AgentTargetDecision.Deny(
                    AgentTargetDenial.Busy,
                    "the user is currently controlling this desktop browser");
            }

            // 副作用操作串行化：同一时刻最多一张票据在途。
            if (request.Operation.IsSideEffect())
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

            var permit = new AgentTargetPermit(
                _session!.SessionId,
                _generation,
                _controlEpoch,
                request.Operation.IsSideEffect());
            if (request.Operation.IsSideEffect())
            {
                _permitInFlight = permit;
            }

            return AgentTargetDecision.Allow(permit);
        }
    }

    /// <summary>第二段：真正触碰页面前的复检（消除「判定通过 → 期间被接管/换代」的竞态）。</summary>
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

            if (_isUserControlling && permit.RequiresExclusiveOwnership)
            {
                return AgentTargetDecision.Deny(
                    AgentTargetDenial.Busy,
                    "the user took control while this operation was queued");
            }

            return AgentTargetDecision.Allow(permit);
        }
    }

    /// <summary>操作结束（无论成败）后清空在途票据。幂等；不匹配的票据不会误清新票据。</summary>
    public AgentTargetSessionSnapshot CompleteOperation(AgentTargetPermit permit)
    {
        ArgumentNullException.ThrowIfNull(permit);

        lock (_sync)
        {
            if (_permitInFlight is { } inFlight
                && inFlight.SessionId.Value == permit.SessionId.Value
                && inFlight.ControlEpoch.Value == permit.ControlEpoch.Value)
            {
                _permitInFlight = null;
            }

            return Snapshot();
        }
    }

    // ── 内部 ──────────────────────────────────────────────────────────────

    private AgentTargetDecision? GateUnsafe(DesktopPageTarget target)
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

        // 页级隔离：只认用户选中的那一页，不因为"同一个 context 的其他页"而放行。
        if (!string.Equals(_session.Target.Key, target.Key, StringComparison.Ordinal))
        {
            return AgentTargetDecision.Deny(
                AgentTargetDenial.TargetRequired,
                "the requested page is not the page the user selected as the agent target");
        }

        return null;
    }

    private void InvalidateTargetUnsafe(string reason)
    {
        if (_session is not null)
        {
            _lastRevokedSessionId = _session.SessionId;
            _session = null;
        }

        AdvanceControlEpochUnsafe();
        _reason = reason;
    }

    private void AdvanceControlEpochUnsafe()
    {
        _controlEpoch = _controlEpoch.Next();
        // 控制世代一变，任何在途票据立即失效（含接管打断正在排队的写）。
        _permitInFlight = null;
    }

    private AgentTargetSessionSnapshot Snapshot() => new(
        _generation.IsLive,
        _generation,
        _controlEpoch,
        _session,
        _isUserControlling,
        _isClosed,
        _lastRevokedSessionId,
        _reason);
}
