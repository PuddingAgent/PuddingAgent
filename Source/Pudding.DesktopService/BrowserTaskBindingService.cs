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
    BrowserGrantIssuer issuer)
{
    private readonly BrowserPageLifecycle _lifecycle =
        lifecycle ?? throw new ArgumentNullException(nameof(lifecycle));

    private readonly BrowserGrantIssuer _issuer = issuer ?? throw new ArgumentNullException(nameof(issuer));

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

        // ② 签发（有效期上限/已登记/世代/子集都在签发器里判）。
        if (!_issuer.TryIssue(
                new BrowserGrantIssuanceRequest(
                    binding.Task, binding.Target, binding.Scope, binding.Validity, binding.Source),
                out var grant,
                out var epoch,
                out var reason))
        {
            return new BrowserBindingAcceptance(false, reason, default);
        }

        // ③ 同一绑定重复受理：先撤旧授权，避免同一 (任务,目标) 上留下两份并存授权。
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
    public int RevokeAllForCaller(string callerId, string? taskId = null)
    {
        if (string.IsNullOrWhiteSpace(callerId))
        {
            return 0;
        }

        var doomed = _active
            .Where(pair => string.Equals(pair.Value.Task.CallerId, callerId, StringComparison.Ordinal)
                && (taskId is null || string.Equals(pair.Value.Task.TaskId, taskId, StringComparison.Ordinal)))
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
