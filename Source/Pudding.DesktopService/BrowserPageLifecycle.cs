using Pudding.Contracts;
using Pudding.Contracts.Desktop;
using PuddingBrowser.Authorization;
using PuddingBrowser.Automation;

namespace Pudding.DesktopService;

/// <summary>
/// 一次关闭操作的结果。设计 §3.3 要求"关闭后同时清掉 UI、两个注册表、grant、ref/cursor 和租约"，
/// 而"是否真的清干净"必须**可判定**（否则只能靠人记得）——因此逐项如实报告。
/// </summary>
public sealed record BrowserLifecycleOutcome(
    string ContextId,
    DesktopPageTarget? Target,
    bool WasRegistered,
    int PagesCleared,
    int GrantsRevoked,
    bool LeaseReleased,
    bool ExecutionScopeRevoked,
    BrowserPageGrantEpoch GrantEpoch)
{
    /// <summary>本次操作是否真的改变过任何事实（用于幂等断言与诊断，不作为"成功"判据）。</summary>
    public bool ChangedAnything =>
        PagesCleared > 0 || GrantsRevoked > 0 || LeaseReleased || ExecutionScopeRevoked;
}

/// <summary>
/// 页面/上下文生命周期的**唯一清理点**（设计方案 §3.3）。
///
/// 为什么必须收成一个端口：关闭一处资源却漏掉另一处，是本项目已经踩过的坑
/// （审计 2026-10-08 结论 #1/#2 都是"两处状态不一致"）。关闭一个页面要同时做四件事：
/// ① 从页面注册表移除（准入与"引用随版本失效"的依据）；② 从浏览器目标注册表移除（可信级别/目标标记）；
/// ③ **撤销该页的全部授权**（页面没了 ⇒ 连只读授权也不该留）；④ **释放该页的写租约**。
///
/// 与"切换 Agent 目标"的区别（§3.2）：切换用 `RevokeExecutionScope`（**保留**只读授权，用户还能看），
/// 关闭必须连只读一起撤 —— 页面都不存在了，"还能读"没有任何意义。
///
/// 失败安全：即使注册表里已经没有这个页面（重复关闭/顺序错乱），**仍然**撤销授权并释放租约 ——
/// 宁可多撤销一次，也不能让一个已关闭页面的授权继续活着。
/// </summary>
public sealed class BrowserPageLifecycle
{
    private readonly BrowserTargetRegistry _browserTargets;
    private readonly DesktopTargetRegistry _pageTargets;
    private readonly BrowserAutomationAuthority _authority;
    private readonly Lock _sync = new();

    /// <summary>
    /// 浏览器会话授权（ADR-097 D3）：页面可驱动性的**唯一裁决者**（用户交页 + 可信会话所有者 +
    /// 写世代/唯一票据）。只在本组件内部使用；Shell/桥侧只能经本文件的标量门面访问。
    /// </summary>
    private readonly AgentTargetSessionAuthority _sessions = new();

    public BrowserPageLifecycle(
        BrowserTargetRegistry browserTargets,
        DesktopTargetRegistry pageTargets,
        BrowserAutomationAuthority authority)
    {
        _browserTargets = browserTargets ?? throw new ArgumentNullException(nameof(browserTargets));
        _pageTargets = pageTargets ?? throw new ArgumentNullException(nameof(pageTargets));
        _authority = authority ?? throw new ArgumentNullException(nameof(authority));
    }

    /// <summary>
    /// 为一个 Desktop 进程构造**唯一**生命周期端口（含它自己的权威实例与两个注册表）。
    /// 调用方（Shell 的组合根）因此不需要引用 Automation 组件，也不可能把"另一份注册表"配错进来。
    /// </summary>
    public static BrowserPageLifecycle Create(
        DesktopInstanceId desktopId,
        DesktopProcessInstanceId processInstanceId,
        TimeProvider? clock = null) =>
        new(
            new BrowserTargetRegistry(),
            new DesktopTargetRegistry(),
            new BrowserAutomationAuthority(desktopId, processInstanceId, clock));

    /// <summary>
    /// 本端口持有的**唯一权威实例**（设计方案 §3.1）。
    /// 一个进程只应构造一个本类实例：它同时是控制权事实源、两个注册表与关闭清理点的所有者，
    /// 因此"注册表更新了但授权没撤"这类分叉在类型层面就不成立。
    /// </summary>
    public BrowserAutomationAuthority Authority => _authority;

    /// <summary>浏览器目标注册表（可信级别 / Agent 目标 / 活动页）。</summary>
    public BrowserTargetRegistry BrowserTargets => _browserTargets;

    /// <summary>页面目标注册表（准入读的那一份：版本 / 就绪度 / page 级目标标记）。</summary>
    public DesktopTargetRegistry PageTargets => _pageTargets;

    /// <summary>
    /// Bridge 准入判据（方案 §5.2/§5.3）：该调用者是否在目标页上持有满足**最小范围**的有效页面授权。
    ///
    /// ⚠ 为什么要有这个方法而不是让调用方直接读 <see cref="Authority"/>：本类所在源文件会被
    /// **链接编译进 WinUI Shell**，而 Shell 的引用白名单**不含** `PuddingBrowser.Automation`
    /// （2026-10-12 实测：Bridge 分发器直接读 `Authority.ActiveGrants` 会让 Shell 构建
    /// `CS0012 类型 BrowserAutomationAuthority 在未引用的程序集中定义`）。
    /// ⇒ 判据必须**下沉到这里**，且签名**只出现 Contracts 类型**。
    /// </summary>
    public bool HasSufficientPageGrant(
        BrowserCallerIdentity caller,
        DesktopPageTarget target,
        BrowserPageGrantScope required,
        DateTimeOffset nowUtc)
    {
        // ① 用户显式选中的 Agent 目标页（唯一的显式授权动作；切换目标后旧页立即不再命中）。
        if (_browserTargets.IsAgentTarget(target.ContextId, target.PageId))
        {
            return true;
        }

        // ② **刻意不再**提供「桥在线 ⇒ 任意页面可驱动」的 V1 过渡放行。
        //    连接状态只证明传输活性，不证明目标归属（ADR-097 D4 / 审阅报告 F3）；
        //    默认 Bridge 的真实授权来源是 ①（用户在 Desktop 上选页）与 ③（能力通道签发的页级 grant）。
        //    若将来要放宽，必须是页级/会话级的显式判据，而不是传输在线这一全局事实。

        // ③ 既有页级授权（能力通道路径签发的 grant）继续兼容。
        return _authority.ActiveGrants.Any(grant =>
            grant.Target == target
            && grant.Grantee.IsSameTaskScope(caller)
            && (grant.Scope & required) == required
            && (grant.ExpiresAtUtc is null || grant.ExpiresAtUtc > nowUtc));
    }

    /// <summary>
    /// 读取基线存储（设计方案 §145）：由本端口**持有并随生命周期清除** ——
    /// "页面没了、内容还留着"与"授权/租约没撤"是同一类错误，因此交给同一个所有者。
    /// </summary>
    public BrowserReadingBaselineStore Readings { get; } = new();

    // ── 控制面门面 ────────────────────────────────────────────────────────
    // Shell 侧（Bridge 闸门、窗口控制器）只需要"读两个轴 + 写两个轴"。
    // 直接暴露 Authority 会迫使每个消费方都引用 Automation 组件；而 Shell 本来就不该关心
    // 授权/租约/世代这些内部模型 —— 于是这里给出**同一事实源**的最小门面。

    /// <summary>
    /// 桥完成握手并确认世代：把「连接已建立」翻译成「权威已活」。
    ///
    /// **这是历史上缺失的那一环**：`NotifyConnectionGeneration` 在生产代码曾**零调用者**，
    /// 于是「Core 已连接、桌面权威恒不活」是代码的必然结果（实测证据见方案 §10.4）。
    /// 世代用 Desktop 侧自增计数器（每次连接 +1），因此重连必然换代、旧记录不会被误判为重复。
    /// 参数取 <c>long</c> 而非常量：Shell 侧（链接编译本文件）不得出现 Automation 类型。
    /// </summary>
    public void NotifyBridgeConnected(long generation)
    {
        var live = ConnectionGeneration.Require(generation);
        _authority.NotifyConnectionGeneration(live, "bridge connected");
        _sessions.OnBridgeReady(live, "bridge connected");
    }

    /// <summary>桥断开/失败：世代归 None，目标与租约随之作废，在途动作在复检时必然被拦下。</summary>
    public void NotifyBridgeDisconnected()
    {
        _authority.NotifyConnectionGeneration(ConnectionGeneration.None, "bridge disconnected");
        _sessions.OnBridgeLost("bridge disconnected");
    }

    /// <summary>
    /// 账本命名空间（方案 §4.2）：**桌面进程实例 + 连接世代**。供 Shell 侧（Bridge 派发器）构造
    /// 账本键 —— **只用标量/契约类型**，不暴露 Automation 的 authority 类型（否则 Shell 编译面会 CS0012）。
    /// **世代进键** ⇒ 重连后的同 operationId 落在**新的命名空间**，不会被旧世代的记录误判为重复。
    /// </summary>
    public (string ProcessInstance, long Generation) LedgerNamespace =>
        (_authority.ProcessInstanceId.Value, _authority.ConnectionGeneration.Value);
    public bool IsPaused => _authority.Capture().IsPaused;

    /// <summary>是否被用户接管（读权威快照）。</summary>
    public bool IsUserTakeover => _authority.Capture().IsUserTakeover;

    /// <summary>
    /// 暂停/恢复：同时驱动旧 authority（能力通道路径的租约/准入读它）与会话权威
    /// （桥路径的写阻断/写世代由它裁决）。两条路径读同一份运行控制状态，不允许分叉。
    /// </summary>
    public void SetPaused(bool paused, string? reason = null)
    {
        _authority.SetPaused(paused, reason);
        if (paused)
        {
            _sessions.BeginPause(reason ?? "browser automation is paused by the user");
        }
        else
        {
            _sessions.EndPause(reason);
        }
    }

    /// <summary>用户接管开关；写的是同一个会话权威（接管**不**撤销阅读授权，§3.1）。</summary>
    public void SetUserTakeover(bool takenOver, string? reason = null)
    {
        _authority.SetUserTakeover(takenOver, reason);
        if (takenOver)
        {
            _sessions.BeginUserControl(reason ?? "user took control");
        }
        else
        {
            _sessions.EndUserControl(reason);
        }
    }

    // ── 浏览器会话准入门面（ADR-097 D3/D4；标量类型，Shell 不引用 Authorization）────────

    /// <summary>
    /// 用宿主注入的**可信控制 owner** 确认待交页归属（桥命令第一条自带 Origin 时调用）。
    /// 成功 ⇒ 会话建立、UI 才可显示“已交出”；失败 ⇒ 命令不得执行。
    /// </summary>
    public bool TryClaimControlOwner(string ownerId)
    {
        if (string.IsNullOrWhiteSpace(ownerId))
        {
            return false;
        }

        return _sessions.TryClaimOwner(AgentTargetControlHandle.For(ownerId), out _);
    }

    /// <summary>
    /// 页面命令准入（Bridge 路径）：会话/目标/owner 校验 + 只读/写分级 + 写串行化。
    /// 只读观察在暂停/接管期间仍允许；写操作被 `browser_busy` 拒绝（与运行控制语义一致）。
    /// </summary>
    public BrowserSessionDecision AuthorizePageCommand(
        BrowserCallerIdentity caller,
        BrowserSessionControlHandle handle,
        DesktopPageTarget target,
        bool requiresWrite)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(target);

        if (string.IsNullOrWhiteSpace(handle.OwnerId))
        {
            return BrowserSessionDecision.Denied(
                BrowserSessionErrorCodes.ControlHandleRequired,
                "this command carries no trusted control handle; the core session owner is required");
        }

        var operation = requiresWrite ? AgentTargetOperation.Interact : AgentTargetOperation.Observe;
        var decision = _sessions.Authorize(new AgentTargetRequest(
            target,
            operation,
            AgentTargetControlHandle.For(handle.OwnerId)));

        if (!decision.IsAllowed)
        {
            return BrowserSessionDecision.Denied(
                decision.ErrorCode ?? BrowserSessionErrorCodes.OwnerMismatch,
                decision.Message ?? "the browser session refused this operation");
        }

        return BrowserSessionDecision.Allowed(new BrowserSessionPermit(decision.Permit!, handle.OwnerId));
    }

    /// <summary>触碰页面前复检同一张票据（期间可能被接管/暂停/切目标/换代/释放）。</summary>
    public BrowserSessionDecision RevalidatePageCommand(BrowserSessionPermit permit)
    {
        ArgumentNullException.ThrowIfNull(permit);

        var decision = _sessions.Revalidate(permit.Permit);
        return decision.IsAllowed
            ? BrowserSessionDecision.Allowed(permit)
            : BrowserSessionDecision.Denied(
                decision.ErrorCode ?? BrowserSessionErrorCodes.PermitInvalid,
                decision.Message ?? "the browser session permit is no longer valid");
    }

    /// <summary>精确释放这张票据（覆盖所有退出路径；释放后的票据不可再用）。</summary>
    public void CompletePageCommand(BrowserSessionPermit permit)
    {
        ArgumentNullException.ThrowIfNull(permit);
        _sessions.CompleteOperation(permit.Permit);
    }

    /// <summary>控制面快照（标量；UI 用来区分“待确认/已交出/无目标”，桥侧用于诊断）。</summary>
    public BrowserSessionStatus CaptureSessionStatus()
    {
        var snapshot = _sessions.Capture();
        return new BrowserSessionStatus(
            snapshot.IsBridgeReady,
            snapshot.HasTarget,
            snapshot.HasPendingHandover,
            snapshot.Session?.Target.Key,
            snapshot.PendingTarget?.Key,
            snapshot.IsPaused,
            snapshot.IsUserControlling,
            snapshot.IsClosed,
            snapshot.Reason);
    }

    /// <summary>已确认归属的当前目标（无 ⇒ <c>null</c>）；只返回 Contracts 类型，Shell/桥侧可用。</summary>
    public DesktopPageTarget? CaptureSessionTarget() => _sessions.Capture().Session?.Target;

    // ── 创建侧（§3.3：创建与关闭必须同一个所有者）────────────────────────
    // 设计要求「人工 UI 和两种传输均通过该端口创建/关闭 context/tab」，
    // 且「登记失败需回收新资源或明确报告不完整，**不返回可操作页面**」。

    /// <summary>
    /// 登记上下文（两个注册表一起，可信级别创建时确定）。同级别重复登记幂等，换级别抛异常。
    /// </summary>
    public void RegisterContext(string contextId, DesktopContextTrust trust)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contextId);

        lock (_sync)
        {
            // 顺序：先"视图侧"（浏览器目标），后"准入侧"（页面注册表）。
            // 准入侧只认已登记的上下文，因此它若失败就必须**回收**视图侧，绝不留下半登记状态。
            _browserTargets.RegisterContext(contextId, trust);
            try
            {
                _pageTargets.RegisterContext(contextId, trust);
            }
            catch
            {
                _browserTargets.UnregisterContext(contextId);
                throw;
            }
        }
    }

    /// <summary>
    /// 登记页面（两个注册表一起）。
    ///
    /// <paramref name="isAgentTarget"/> 为 <c>null</c> 表示"不表态"（保留原值）——
    /// 页面创建事件重复触发时不得清掉用户已选定的目标页。
    ///
    /// <b>原子性</b>（§3.3）：任一侧登记失败都必须回收另一侧并抛出，
    /// 因此调用方**永远拿不到一个"半登记"的页面**（视图侧有、准入侧没有，或反之）。
    /// </summary>
    public void RegisterPage(
        DesktopPageTarget target,
        DesktopPageVersion version = default,
        DesktopPageReadiness readiness = DesktopPageReadiness.Unknown,
        bool? isAgentTarget = null,
        bool isActive = false)
    {
        ArgumentNullException.ThrowIfNull(target);

        lock (_sync)
        {
            _browserTargets.RegisterPage(target.ContextId, target.PageId, isAgentTarget: false, isActive: isActive);
            try
            {
                // 上下文未登记时这里会抛（"先有页面后有上下文"说明写入顺序错了，
                // 静默补登记会让不可信上下文被当成可信）。
                _pageTargets.RegisterPage(target, version, readiness, isAgentTarget);
            }
            catch
            {
                _browserTargets.UnregisterPage(target.ContextId, target.PageId);
                throw;
            }
        }
    }

    /// <summary>
    /// 设置/撤销「用户选定的 Agent 目标页」（两个注册表一起）。
    ///
    /// 这是**用户交页**动作：两个注册表立刻记录目标（UI/发现/准入读它们），但会话权威只把它记为
    /// **待交页**——归属要由可信 Core 会话在第一条命令里确认（<see cref="TryClaimControlOwner"/>），
    /// 确认成功后 UI 才能显示“已交出”（ADR-097 D3）。切换目标会作废旧会话与旧票据。
    /// </summary>
    public BrowserLifecycleOutcome SetAgentTarget(string? contextId, string? pageId)
    {
        lock (_sync)
        {
            var previous = _pageTargets.AgentTarget;

            var marked = _pageTargets.SetAgentTarget(contextId, pageId);
            _browserTargets.SetAgentTarget(contextId, pageId);

            var scopeRevoked = false;
            if (previous is not null
                && (pageId is null || !string.Equals(previous.PageId, pageId, StringComparison.Ordinal)))
            {
                _authority.RevokeExecutionScope(previous);
                scopeRevoked = true;
            }

            if (string.IsNullOrWhiteSpace(contextId) || string.IsNullOrWhiteSpace(pageId))
            {
                _sessions.Revoke("the agent target was cleared by the user");
            }
            else
            {
                _sessions.OfferTarget(
                    new DesktopPageTarget(contextId, pageId),
                    "user handed the page to the current core session");
            }

            return new BrowserLifecycleOutcome(
                contextId ?? string.Empty,
                previous,
                WasRegistered: marked,
                PagesCleared: 0,
                GrantsRevoked: 0,
                LeaseReleased: false,
                ExecutionScopeRevoked: scopeRevoked,
                GrantEpoch: _authority.Capture().GrantEpoch);
        }
    }

    /// <summary>关闭一个页面：两个注册表 + 该页全部授权 + 该页写租约。</summary>
    public BrowserLifecycleOutcome ClosePage(DesktopPageTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);

        lock (_sync)
        {
            var wasRegistered = _pageTargets.Resolve(target) is not null;

            // 页面没了 ⇒ 会话权威的当前目标/待交页一并作废（在途票据随写世代失效）。
            _sessions.NotifyPageClosed(target, "the agent target page was closed");

            _pageTargets.ClosePage(target);
            _browserTargets.UnregisterPage(target.ContextId, target.PageId);

            var (grantsRevoked, leaseReleased) = RevokePageAuthorization(target);

            // 内容基线随页面清除（§145）：它与授权/租约同属"页面没了就不该再存在"的东西。
            Readings.ClearPage(target);

            return new BrowserLifecycleOutcome(
                target.ContextId,
                target,
                wasRegistered,
                PagesCleared: wasRegistered ? 1 : 0,
                GrantsRevoked: grantsRevoked,
                LeaseReleased: leaseReleased,
                ExecutionScopeRevoked: grantsRevoked > 0,
                GrantEpoch: _authority.Capture().GrantEpoch);
        }
    }

    /// <summary>关闭一个上下文：它的全部页面、两个注册表条目、全部授权与租约。</summary>
    public BrowserLifecycleOutcome CloseContext(string contextId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contextId);

        lock (_sync)
        {
            // 先取出页面清单：注册表移除之后就查不到了，而撤销授权需要逐个页面（授予是按页绑定的）。
            var pages = _pageTargets.PagesInContext(contextId);

            // 会话权威：该上下文里的当前目标/待交页一并作废（上下文没了 ⇒ 控制权不能留）。
            var snapshot = _sessions.Capture();
            if (snapshot.Session?.Target.ContextId == contextId
                || snapshot.PendingTarget?.ContextId == contextId)
            {
                _sessions.Revoke("the browser context was closed");
            }

            var grantsRevoked = 0;
            var leaseReleased = false;
            foreach (var page in pages)
            {
                var (revoked, released) = RevokePageAuthorization(page);
                grantsRevoked += revoked;
                leaseReleased |= released;
            }

            // 上下文没了 ⇒ 任何指向它的残留授权/租约都不能留（含只有 context 信息的记录）。
            var (contextGrants, contextLease) = RevokeContextWideAuthorization(contextId);
            grantsRevoked += contextGrants;
            leaseReleased |= contextLease;

            _browserTargets.UnregisterContext(contextId);
            var cleared = _pageTargets.CloseContext(contextId);

            // 内容基线也随上下文清除（§145）：页面没了还留着它能被"读出来"是同一类漏洞。
            Readings.ClearContext(contextId);

            return new BrowserLifecycleOutcome(
                contextId,
                Target: null,
                WasRegistered: pages.Count > 0,
                PagesCleared: cleared,
                GrantsRevoked: grantsRevoked,
                LeaseReleased: leaseReleased,
                ExecutionScopeRevoked: grantsRevoked > 0,
                GrantEpoch: _authority.Capture().GrantEpoch);
        }
    }

    /// <summary>
    /// 撤销该页**全部**授权（含只读）并释放它的写租约。
    /// 顺序刻意是"先租约、后授权"：撤销授权时权威会顺带释放已失效的租约，
    /// 若反过来做，"租约到底是不是这次释放的"就无法如实报告（回执会说没释放）。
    /// </summary>
    private (int GrantsRevoked, bool LeaseReleased) RevokePageAuthorization(DesktopPageTarget target)
    {
        var leaseReleased = ReleaseLeaseForPage(target);

        var revoked = 0;
        foreach (var grant in _authority.ActiveGrants.Where(grant => grant.TargetsSamePage(target)).ToArray())
        {
            _authority.RevokeGrant(grant.GrantId);
            revoked++;
        }

        return (revoked, leaseReleased);
    }

    /// <summary>
    /// 兜底：没有页面清单可依据时（重复关闭、或页面从未登记），仍按上下文撤销残留授权。
    /// 这条不做"查到才撤"，而是**遍历现有授权**逐个比对上下文。
    /// </summary>
    private (int GrantsRevoked, bool LeaseReleased) RevokeContextWideAuthorization(string contextId)
    {
        var lease = _authority.Capture().ActiveLease;
        var released = lease is not null
            && string.Equals(lease.Target.ContextId, contextId, StringComparison.Ordinal)
            && ReleaseLease(lease.LeaseId);

        var revoked = 0;
        foreach (var grant in _authority.ActiveGrants
                     .Where(grant => string.Equals(grant.Target.ContextId, contextId, StringComparison.Ordinal))
                     .ToArray())
        {
            _authority.RevokeGrant(grant.GrantId);
            revoked++;
        }

        return (revoked, released);
    }

    private bool ReleaseLeaseForPage(DesktopPageTarget target)
    {
        var lease = _authority.Capture().ActiveLease;
        return lease is not null && lease.Target == target && ReleaseLease(lease.LeaseId);
    }

    /// <summary>释放租约；租约在期间已被别人释放时不算失败（幂等）。</summary>
    private bool ReleaseLease(BrowserAutomationLeaseId leaseId)
    {
        var before = _authority.Capture().ActiveLease?.LeaseId;
        if (before is null)
        {
            return false;
        }

        _authority.ReleaseLease(leaseId, "page closed");
        return true;
    }
}
