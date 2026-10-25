using Pudding.Contracts.Desktop;
using PuddingBrowser.Automation;

namespace Pudding.DesktopService;

/// <summary>
/// 「AgentAuthorized 上下文里的页面默认被授权」的**去重判据**（用户裁定 2026-10-25）。
///
/// <para>
/// 抽成纯函数的原因是一次可复现的假故障：去重用的是 <see cref="BrowserCallerIdentity.IsSameTaskScope"/>
/// （只看 CallerId），消费端 <c>BrowserAutomationAuthority.Admit</c> 用的是
/// <see cref="BrowserCallerIdentity.IsSameActor"/>（Kind + CallerId + TaskId）。
/// 而主 Agent 的 <c>TaskId</c> 就是本次运行的 runId（见 <c>BrowserCallerIdentityProjection</c>），
/// **每轮运行都会变**。于是上一轮签发的 5 分钟授权会同时被两处推出相反结论：
/// </para>
/// <list type="number">
///   <item>去重认为「已有覆盖该页的有效授权」⇒ 跳过签发；</item>
///   <item>裁决认为「这张授权发给了别的行动者」⇒ <c>CallerMismatch</c> 拒绝。</item>
/// </list>
/// <para>
/// 结果是写操作在授权到期前**稳定被拒**，而读操作因为走只读基线不查授权，照常可用 ——
/// 表现为「能看不能动」，极难从现象倒推原因。
/// </para>
/// <para>
/// 因此本判据必须与消费端**逐字一致**：能证明「消费端会接受」的，才允许跳过签发。
/// </para>
/// </summary>
internal static class BrowserDefaultPageGrantPolicy
{
    /// <summary>
    /// 是否还需要签发一张默认页级授权。
    /// <para>
    /// 返回 <c>false</c> 仅当存在一张**同一行动者**（Kind/CallerId/TaskId 全等）且未过期的同页授权 ——
    /// 也就是消费端 <c>Admit</c> 必定会接受的那一张。其余一律返回 <c>true</c>（去签发）。
    /// </para>
    /// </summary>
    public static bool ShouldIssue(
        IEnumerable<BrowserPageGrant>? grants,
        DesktopPageTarget page,
        BrowserCallerIdentity caller,
        DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(page);
        ArgumentNullException.ThrowIfNull(caller);

        if (grants is null)
        {
            return true;
        }

        foreach (var grant in grants)
        {
            if (grant.Target != page)
            {
                continue;
            }

            // 与 Admit 同一条判据：不同 Kind / 不同 runId 的授权，消费端不会接受，不能拿来去重。
            if (!grant.Grantee.IsSameActor(caller))
            {
                continue;
            }

            if (grant.ExpiresAtUtc is { } expiresAtUtc && expiresAtUtc <= nowUtc)
            {
                continue;
            }

            return false;
        }

        return true;
    }
}
