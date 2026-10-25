using Pudding.Contracts.Desktop;

namespace Pudding.DesktopService;

/// <summary>一次绑定的受理结果（拒绝必须给具体原因；不回退成"先签了再说"）。</summary>
public sealed record BrowserBindingAcceptance(
    bool Accepted,
    string? Reason,
    BrowserPageGrantEpoch Epoch,
    BrowserPageGrantId? GrantId = null);

/// <summary>
/// **任务绑定的受理与撤销**（方案 §4.1）：Desktop 收到 Core 的 <see cref="BrowserTaskBinding"/> 后，
/// 只有在"该目标**当前**正是用户选定的 Agent 目标"时才签发；切目标/任务结束调用 <see cref="Revoke"/>。
///
/// 关键不变量：**绑定不能替代用户选择**。用户没有把这一页选定给 Agent，就不存在可签发的授权 ——
/// 这是"一条普通调用不能自动把自己授权为目标页 owner"在 Desktop 侧的落点。
///
/// 有效期上限、目标已登记、连接世代存活、子代理子集等策略**不在本类重写**，由 <see cref="BrowserGrantIssuer"/> 与唯一 authority 判。
/// </summary>
public sealed class BrowserTaskBindingService(
    BrowserPageLifecycle lifecycle,
    BrowserGrantIssuer issuer,
    // B6（§5）：子授权期限必须夹到父授权**剩余期限**内 ⇒ 需要读当前时间。
    TimeProvider? clock = null)
{
    private readonly BrowserPageLifecycle _lifecycle =
        lifecycle ?? throw new ArgumentNullException(nameof(lifecycle));

    private readonly BrowserGrantIssuer _issuer = issuer ?? throw new ArgumentNullException(nameof(issuer));

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    private sealed record ActiveBinding(DesktopPageTarget Target, BrowserCallerIdentity Task, BrowserPageGrantId GrantId);

    private readonly Dictionary<string, ActiveBinding> _active = new(StringComparer.Ordinal);

    // 用户裁定 2026-10-25：AgentAuthorized 上下文里的页面默认就是 Agent 目标，
    // 开关（以及它依赖的「只默认一次」标记）已移除，因此不再需要历史账。

    /// <summary>当前已受理的绑定数（用于撤销与测试断言）。</summary>
    public int ActiveCount => _active.Count;

    public BrowserBindingAcceptance Accept(BrowserTaskBinding binding)
    {
        ArgumentNullException.ThrowIfNull(binding);

        // ① 目标判据。三条分支，只有前两条允许签发：
        //
        //    (a) 已显式选定 —— 目标必须**当下**就是用户选定给 Agent 的那一页（原判据，未放宽）；
        //    (b) 从未选定，且该页所在上下文是 AgentAuthorized —— 把它**默认**当作 Agent 目标。
        //        依据：Desktop 的工具面板浏览器本来就是给 Agent 用的（用户裁定 2026-10-25），
        //        每页都要手点一次「设为 Agent 目标」是纯摩擦，不是安全增益。
        //        这条同时修掉一个可复现的假故障：面板里有页面，工具却报
        //        「no page grant covers ...」，因为没人点过那个按钮。
        //    (c) 其余一律拒绝 —— 尤其**非 AgentAuthorized 上下文**（工作台、第三方页面）永远走拒绝，
        //        默认化不得外溢到用户自己的浏览区。
        //
        //    默认化只生效一次（_autoDefaulted）：显式撤销之后不再自动恢复。
        if (_lifecycle.PageTargets.AgentTarget is { } selected)
        {
            if (selected != binding.Target)
            {
                return new BrowserBindingAcceptance(
                    false,
                    "the binding target is not the page the user currently selected for the agent",
                    default);
            }
        }
        else if (_lifecycle.BrowserTargets.TrustFor(binding.Target.ContextId)
                 == DesktopContextTrust.AgentAuthorized)
        {
            // 两个注册表都对齐，避免出现「准入说这是目标页、UI 说没有」的漂移。
            //
            // 这里曾经有「只默认一次」的标记（`_autoDefaulted`）：默认化过一次后不再自动恢复，
            // 以免「设为 Agent 目标」开关变成骗人的控件。用户裁定移除该开关后，这个标记就失去意义，
            // 反而制造了新的假故障：短期授权到期后无法再次申请，页面在使用中突然不可驱动。
            // 故去掉标记 —— 默认化按需重入，判据（上下文可信级）仍是唯一闸门。
            _lifecycle.SetAgentTarget(binding.Target.ContextId, binding.Target.PageId);
        }
        else
        {
            return new BrowserBindingAcceptance(
                false,
                "the binding target is not the page the user currently selected for the agent",
                default);
        }

        // ② 选中页**可以**绑定，但**不能匿名共享**（方案 §5.1：许可由控制面签发，普通调用只消费）。
        //    同一选中页若已被**另一个任务**绑定，则无关任务必须拿到自己的显式选择许可 —— 不得因为
        //    "目标正好是用户当前选中的那一页"就自动获得读写授权（外部复核 P0：选页后无关任务也能获批）。
        var rival = _active.Values.FirstOrDefault(active =>
            active.Target == binding.Target && !active.Task.IsSameTaskScope(binding.Task));

        if (rival is not null)
        {
            return new BrowserBindingAcceptance(
                false,
                "the selected page is already bound to another task; an unrelated task needs its own explicit user selection",
                default);
        }

        // ③ B6（§5）：子代理只能取得**父授权**的子集，且期限不得超过父授权的**剩余期限**。
        //    父授权必须真实存在；找不到就明确拒绝（绝不静默放宽成"无父也放行"）。
        BrowserPageGrantScope? parentScope = null;
        var validity = binding.Validity;

        if (binding.Task.Kind == BrowserCallerKind.SubAgent)
        {
            if (binding.Task.ParentTaskId is not { Length: > 0 } parentTaskId)
            {
                return new BrowserBindingAcceptance(false, "a sub-agent binding must carry its parent task id", default);
            }

            var parent = _lifecycle.Authority.ActiveGrants.FirstOrDefault(grant =>
                grant.Target == binding.Target
                && string.Equals(grant.Grantee.TaskId, parentTaskId, StringComparison.Ordinal));

            if (parent is null)
            {
                return new BrowserBindingAcceptance(
                    false, "no active parent grant for this sub-agent's parent task on the requested page", default);
            }

            parentScope = parent.Scope;

            if (parent.ExpiresAtUtc is { } parentExpiry)
            {
                var remaining = parentExpiry - _clock.GetUtcNow();
                // 留 1 秒安全边界：签发方会在稍后取一次 now，正好等于 remaining 时子到期会晚于父若干微秒（实测踩到）。
                var clamped = remaining - TimeSpan.FromSeconds(1);
                if (clamped <= TimeSpan.Zero)
                {
                    return new BrowserBindingAcceptance(false, "the parent grant is about to expire", default);
                }

                if (validity > clamped)
                {
                    validity = clamped; // 夹取而非拒绝：§5 要求"期限不超过父授权"，夹取后仍是子集。
                }
            }
        }

        // ④ 签发（有效期上限/已登记/世代/子集都在签发器里判）。
        if (!_issuer.TryIssue(
                new BrowserGrantIssuanceRequest(
                    binding.Task, binding.Target, binding.Scope, validity, binding.Source, parentScope),
                out var grant,
                out var epoch,
                out var reason))
        {
            return new BrowserBindingAcceptance(false, reason, default);
        }

        // ⑤ 同一绑定重复受理：先撤旧授权，避免同一 (任务,目标) 上留下两份并存授权。
        if (_active.TryGetValue(Key(binding), out var previous))
        {
            _lifecycle.Authority.RevokeGrant(previous.GrantId);
        }

        _active[Key(binding)] = new ActiveBinding(binding.Target, binding.Task, grant!.GrantId);
        return new BrowserBindingAcceptance(true, null, epoch, grant!.GrantId);
    }

    /// <summary>
    /// 受理**任务级管理授权**（新建/关闭标签、建关 context）。
    ///
    /// <para>
    /// 与 <see cref="Accept"/> 的区别：管理面操作发生时**还没有页面可以授权**
    /// （新建标签更是要创建页面），所以没有 <see cref="DesktopPageTarget"/> 可绑定，
    /// 也不进 <c>_active</c>（那张表以「页 + 任务」为键）。
    /// </para>
    ///
    /// <para>
    /// 子代理判据不在本类重写：签发器要求子代理的父任务**已经**持有管理面授权，
    /// 这里只负责把那个事实查出来，不替它放宽。
    /// </para>
    /// </summary>
    public BrowserBindingAcceptance AcceptManagement(
        BrowserCallerIdentity task,
        TimeSpan validity)
    {
        ArgumentNullException.ThrowIfNull(task);

        var parentHoldsManagement = false;

        if (task.Kind == BrowserCallerKind.SubAgent)
        {
            if (task.ParentTaskId is not { Length: > 0 } parentTaskId)
            {
                return new BrowserBindingAcceptance(
                    false, "a sub-agent management request must carry its parent task id", default);
            }

            parentHoldsManagement = _lifecycle.Authority.ActiveManagementGrants.Any(grant =>
                string.Equals(grant.Grantee.TaskId, parentTaskId, StringComparison.Ordinal));
        }

        if (!_issuer.TryIssueManagement(
                task,
                validity,
                parentHoldsManagement,
                out var grant,
                out var epoch,
                out var reason))
        {
            return new BrowserBindingAcceptance(false, reason, default);
        }

        // 不回带 grant id：BrowserBindingAcceptance.GrantId 是**页级**授权 ID 的类型
        // （撤销时按它精确撤），管理面目前没有撤销路径，硬塞一个不相干的 ID 只会误导调用方。
        return new BrowserBindingAcceptance(true, null, epoch);
    }

    /// <summary>撤销该绑定对应的授权（切目标/任务结束/父撤销时调用）；不存在则返回 <c>false</c>。</summary>
    public bool Revoke(BrowserTaskBinding binding)
    {
        ArgumentNullException.ThrowIfNull(binding);
        if (!_active.Remove(Key(binding), out var active))
        {
            return false;
        }

        _lifecycle.Authority.RevokeGrant(active.GrantId);
        return true;
    }

    /// <summary>
    /// **切目标时的确定性撤销**（方案 §4.1）：该页不再被操作 ⇒ 撤掉它上面所有适用绑定。
    /// 返回被撤销的绑定数（0 表示本来就没有，不是错误）。
    /// </summary>
    public int RevokeForTarget(DesktopPageTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        var doomed = _active.Where(pair => pair.Value.Target == target).Select(pair => pair.Key).ToArray();
        foreach (var key in doomed)
        {
            _lifecycle.Authority.RevokeGrant(_active[key].GrantId);
            _active.Remove(key);
        }

        return doomed.Length;
    }

    /// <summary>**任务结束/父撤销时的确定性撤销**：按行动者撤销其全部绑定。</summary>
    public int RevokeAllForCaller(BrowserCallerIdentity task)
    {
        ArgumentNullException.ThrowIfNull(task);

        var doomed = _active
            // 任务级管理撤销：判据走身份辅助（IsSameTaskScope），不手写 CallerId 字符串比较
            .Where(pair => pair.Value.Task.IsSameTaskScope(task))
            .Select(pair => pair.Key)
            .ToArray();

        foreach (var key in doomed)
        {
            _lifecycle.Authority.RevokeGrant(_active[key].GrantId);
            _active.Remove(key);
        }

        return doomed.Length;
    }

    public bool Revoke(DesktopPageTarget target, BrowserCallerIdentity task)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(task);
        if (!_active.Remove(Key(target, task), out var revoked))
        {
            return false;
        }
        _lifecycle.Authority.RevokeGrant(revoked.GrantId);
        return true;
    }
    private static string Key(BrowserTaskBinding binding) => Key(binding.Target, binding.Task);
    private static string Key(DesktopPageTarget target, BrowserCallerIdentity task) =>
        $"{target.ContextId}/{target.PageId}|{task.CallerId}|{task.TaskId}";
}
