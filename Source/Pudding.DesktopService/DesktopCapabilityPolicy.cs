using Pudding.Contracts;
using Pudding.Contracts.Desktop;

namespace Pudding.DesktopService;

/// <summary>
/// 能力 × 目标可信级别的准入策略（表驱动，可审计）。
///
/// 判据来自方案 §4/§7：
/// · 普通网页不得调用 Shell 交互能力；
/// · 任意脚本只限获授权的 Agent 浏览器目标；
/// · 可信工作台可以使用 Shell 交互能力，但**绝不允许**被注入脚本。
///
/// 不变式：<see cref="DesktopCapabilityTraits.RequiresTrustedContext"/> 的能力绝不允许
/// <see cref="DesktopContextTrust.Untrusted"/>（由契约测试与本地测试双向断言）。
/// </summary>
public static class DesktopCapabilityPolicy
{
    private static readonly Dictionary<DesktopCapability, DesktopContextTrust[]> AllowedTrust = new()
    {
        [DesktopCapability.WebViewNavigate] =
        [
            DesktopContextTrust.Untrusted, DesktopContextTrust.AgentAuthorized, DesktopContextTrust.Workbench,
        ],
        // 脚本注入只对「获授权的 Agent 浏览器」开放；工作台永远不在列表内。
        [DesktopCapability.WebViewExecuteJavascript] = [DesktopContextTrust.AgentAuthorized],
        [DesktopCapability.WebViewPageState] =
        [
            DesktopContextTrust.Untrusted, DesktopContextTrust.AgentAuthorized, DesktopContextTrust.Workbench,
        ],
        [DesktopCapability.ShellNotification] =
        [
            DesktopContextTrust.Untrusted, DesktopContextTrust.AgentAuthorized, DesktopContextTrust.Workbench,
        ],
        [DesktopCapability.ShellStatus] =
        [
            DesktopContextTrust.Untrusted, DesktopContextTrust.AgentAuthorized, DesktopContextTrust.Workbench,
        ],
        // 交互类与剪贴板：只在可信工作台上下文开放（当前默认调用方级别为 Untrusted，故默认不可用）。
        [DesktopCapability.ShellDialog] = [DesktopContextTrust.Workbench],
        [DesktopCapability.ShellFilePicker] = [DesktopContextTrust.Workbench],
        [DesktopCapability.ShellClipboard] = [DesktopContextTrust.Workbench],
        // 快照读取会遍历 DOM（同样是脚本注入）：只对获授权的 Agent 浏览器开放，工作台与普通网页都不允许。
        [DesktopCapability.BrowserSnapshot] = [DesktopContextTrust.AgentAuthorized],
        [DesktopCapability.BrowserLocate] = [DesktopContextTrust.AgentAuthorized],
        // 交互是变更类：只对获授权的 Agent 浏览器开放（工作台不可注入脚本，普通网页更不允许）。
        [DesktopCapability.BrowserInteract] = [DesktopContextTrust.AgentAuthorized],
        // 等待是只读轮询（同样读取 DOM）：只对获授权的 Agent 浏览器开放。
        [DesktopCapability.BrowserWaitFor] = [DesktopContextTrust.AgentAuthorized],
        // 清单会暴露页面标题与地址：只对获授权的 Agent 浏览器开放。
        [DesktopCapability.BrowserContexts] = [DesktopContextTrust.AgentAuthorized],
    };

    public static bool IsAllowedForTrust(DesktopCapability capability, DesktopContextTrust trust) =>
        AllowedTrust.TryGetValue(capability, out var allowed) && Array.IndexOf(allowed, trust) >= 0;

    /// <summary>该能力是否要求显式页面目标（Shell 能力不要求）。</summary>
    public static bool RequiresPageTarget(DesktopCapability capability) =>
        DesktopCapabilities.TryGet(capability, out var descriptor)
        && descriptor.Traits.HasFlag(DesktopCapabilityTraits.RequiresPageTarget);

    /// <summary>工作台页面绝不允许脚本注入（策略表之外的硬不变式）。</summary>
    public static bool DeniesWorkbenchScriptInjection(DesktopCapability capability, DesktopContextTrust trust) =>
        capability == DesktopCapability.WebViewExecuteJavascript && trust == DesktopContextTrust.Workbench;

    /// <summary>供测试/审计读取的完整策略快照。</summary>
    public static IReadOnlyDictionary<DesktopCapability, DesktopContextTrust[]> Snapshot =>
        AllowedTrust.ToDictionary(pair => pair.Key, pair => pair.Value.ToArray());
}
