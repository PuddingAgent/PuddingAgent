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

    /// <summary>当前已受理的绑定数（用于撤销与测试断言）。</summary>
    public int ActiveCount => _active.Count;

    public BrowserBindingAcceptance Accept(BrowserTaskBinding binding)
    {
        ArgumentNullException.ThrowIfNull(binding);

        // ① 绑定不能替代用户选择：目标必须**当下**就是用户选定给 Agent 的那一页。
        if (_lifecycle.PageTargets.AgentTarget is not { } selected || selected != binding.Target)
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
