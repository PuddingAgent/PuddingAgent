using Pudding.Contracts.Desktop;

namespace Pudding.DesktopService;

/// <summary>
/// 「Agent 默认持有管理面授权」（新建标签 / 建关 context）的**去重判据**（用户裁定 2026-10-25）。
///
/// <para>
/// 背景：策略表把 <c>TabNew</c> / <c>ContextCreate</c> / <c>ContextClose</c> 归入
/// <see cref="BrowserGrantKind.Management"/>，而生产代码从未签发过这类授权
/// （<c>IssueManagementGrant</c> 只在测试里被调用）。结果是一道**永远打不开的死门**：
/// Agent 看不到任何页面时（<c>pageCount=0</c>）既不能靠页级授权驱动，也不能新建标签，
/// 表现为「工具能加载、一调用就报 no task-level management grant」。
/// </para>
///
/// <para>
/// 关键差别（与 <see cref="BrowserDefaultPageGrantPolicy"/> 相反，务必不要统一）：
/// </para>
/// <list type="bullet">
///   <item>
///     页级授权按 <see cref="BrowserCallerIdentity.IsSameActor"/>（Kind + CallerId + TaskId）比较，
///     因为主 Agent 的 TaskId 是本次 runId —— 它是「某一次运行的页面授权」。
///   </item>
///   <item>
///     管理面授权按 <see cref="BrowserCallerIdentity.IsSameTaskScope"/>（CallerId）比较，
///     因为消费端 <c>BrowserAutomationAuthority.HasManagementGrant</c> 就是这么判的：
///     管理能力属于**任务**而不是某一次运行（否则每轮都要重新签发，永远命中不到）。
///   </item>
/// </list>
/// <para>
/// 因此这里的去重判据必须与消费端**逐字一致**：只有能证明「消费端会接受」的才允许跳过签发。
/// 判错方向的代价是把今天的假故障换个形状再复现一次。
/// </para>
/// </summary>
internal static class BrowserDefaultManagementGrantPolicy
{
    /// <summary>
    /// 是否还需要签发一张默认管理面授权。
    /// <para>
    /// 返回 <c>false</c> 仅当存在一张**同一任务作用域**且未过期的管理面授权 ——
    /// 也就是消费端 <c>HasManagementGrant</c> 必定会接受的那一张。其余一律返回 <c>true</c>。
    /// </para>
    /// </summary>
    public static bool ShouldIssue(
        IEnumerable<BrowserManagementGrant>? grants,
        BrowserCallerIdentity caller,
        DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(caller);

        if (grants is null)
        {
            return true;
        }

        foreach (var grant in grants)
        {
            // 与 HasManagementGrant 同一条判据（不含 runId）。
            if (!grant.Grantee.IsSameTaskScope(caller))
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
