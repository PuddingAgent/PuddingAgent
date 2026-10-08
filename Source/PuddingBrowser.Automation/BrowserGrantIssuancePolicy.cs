using Pudding.Contracts.Desktop;

namespace PuddingBrowser.Automation;

/// <summary>
/// 授权签发策略（设计方案 §3.2）：<b>子代理只能取得父任务授权的子集</b>。
///
/// 为什么在签发处强制而不是在准入处：准入处无法区分「子代理借用了父任务的授权」与
/// 「Runtime 真的给子代理签了同等范围」。把判据放在签发点，父任务的授权就是子代理范围的
/// <b>上界</b>，扩大授权会在这里直接失败，而不是等到一次越权调用才被发现。
/// </summary>
public static class BrowserGrantIssuancePolicy
{
    /// <summary>
    /// 校验子代理授权不超过父任务在同一页面/同一 frame 上的范围；返回 <c>null</c> 表示通过。
    /// 非子代理（Agent/User）不做上界检查。
    /// </summary>
    public static BrowserAuthorizationDenial? CheckSubAgentScope(
        BrowserPageGrant grant,
        IEnumerable<BrowserPageGrant> existingGrants)
    {
        ArgumentNullException.ThrowIfNull(grant);
        ArgumentNullException.ThrowIfNull(existingGrants);

        if (!grant.Grantee.IsSubAgent)
        {
            return null;
        }

        BrowserPageGrant? parent = null;
        foreach (var candidate in existingGrants)
        {
            // 父任务的授权＝发给同一任务身份（TaskId == 子代理的 ParentTaskId）的那一条。
            if (!string.Equals(candidate.Grantee.TaskId, grant.Grantee.ParentTaskId, StringComparison.Ordinal)
                || !candidate.TargetsSamePage(grant.Target)
                || !string.Equals(candidate.FrameId, grant.FrameId, StringComparison.Ordinal))
            {
                continue;
            }

            parent = candidate;
            break;
        }

        // 父任务在该页面上没有任何授权 ⇒ 子代理不得凭空获得授权。
        if (parent is null)
        {
            return BrowserAuthorizationDenial.NoGrant;
        }

        if ((grant.Scope & ~parent.Scope) != 0)
        {
            return BrowserAuthorizationDenial.ScopeInsufficient;
        }

        return null;
    }

    /// <summary>失败时抛 <see cref="ArgumentException"/>（签发路径 fail closed，不静默降级）。</summary>
    public static void EnsureSubAgentScopeIsSubset(
        BrowserPageGrant grant,
        IEnumerable<BrowserPageGrant> existingGrants)
    {
        var denial = CheckSubAgentScope(grant, existingGrants);
        if (denial is null)
        {
            return;
        }

        throw new ArgumentException(
            $"sub-agent grant '{grant.GrantId.Value}' is not a subset of its parent task's grant on"
            + $" '{grant.Target.Key}' ({BrowserAuthorizationDenialWire.NameOf(denial.Value)}).",
            nameof(grant));
    }
}
