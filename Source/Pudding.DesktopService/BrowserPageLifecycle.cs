using Pudding.Contracts;
using Pudding.Contracts.Desktop;
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

    // ── 控制面门面 ────────────────────────────────────────────────────────
    // Shell 侧（Bridge 闸门、窗口控制器）只需要"读两个轴 + 写两个轴"。
    // 直接暴露 Authority 会迫使每个消费方都引用 Automation 组件；而 Shell 本来就不该关心
    // 授权/租约/世代这些内部模型 —— 于是这里给出**同一事实源**的最小门面。

    /// <summary>是否被暂停（读权威快照）。</summary>
    public bool IsPaused => _authority.Capture().IsPaused;

    /// <summary>是否被用户接管（读权威快照）。</summary>
    public bool IsUserTakeover => _authority.Capture().IsUserTakeover;

    /// <summary>暂停/恢复；写的是同一个权威实例。</summary>
    public void SetPaused(bool paused, string? reason = null) => _authority.SetPaused(paused, reason);

    /// <summary>用户接管开关；写的是同一个权威实例（接管**不**撤销阅读授权，§3.1）。</summary>
    public void SetUserTakeover(bool takenOver, string? reason = null) =>
        _authority.SetUserTakeover(takenOver, reason);

    /// <summary>关闭一个页面：两个注册表 + 该页全部授权 + 该页写租约。</summary>
    public BrowserLifecycleOutcome ClosePage(DesktopPageTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);

        lock (_sync)
        {
            var wasRegistered = _pageTargets.Resolve(target) is not null;

            _pageTargets.ClosePage(target);
            _browserTargets.UnregisterPage(target.ContextId, target.PageId);

            var (grantsRevoked, leaseReleased) = RevokePageAuthorization(target);

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
