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
        // 文档预览：会把工作区文件内容呈现给用户，普通网页不得触发。
        // 两个可信来源都是**可信上下文**：工作台页面（用户自己点开）与获授权的 Agent
        // （preview_document 工具）。它不是 Shell 自己的 UI 动作，所以不走 ShellCallerTrust。
        [DesktopCapability.ShellDocumentPreview] =
            [DesktopContextTrust.AgentAuthorized, DesktopContextTrust.Workbench],
        // 面板渲染与文档预览同级：会把内容直接呈现给用户，普通网页不得触发。
        [DesktopCapability.ShellPanelRender] =
            [DesktopContextTrust.AgentAuthorized, DesktopContextTrust.Workbench],
        // 快照读取会遍历 DOM（同样是脚本注入）：只对获授权的 Agent 浏览器开放，工作台与普通网页都不允许。
        [DesktopCapability.BrowserSnapshot] = [DesktopContextTrust.AgentAuthorized],
        // §8.1 截图：只读能力，与 snapshot 同级信任。
        [DesktopCapability.BrowserScreenshot] = [DesktopContextTrust.AgentAuthorized],
        [DesktopCapability.BrowserLocate] = [DesktopContextTrust.AgentAuthorized],
        // 交互是变更类：只对获授权的 Agent 浏览器开放（工作台不可注入脚本，普通网页更不允许）。
        [DesktopCapability.BrowserInteract] = [DesktopContextTrust.AgentAuthorized],
        // 等待是只读轮询（同样读取 DOM）：只对获授权的 Agent 浏览器开放。
        [DesktopCapability.BrowserWaitFor] = [DesktopContextTrust.AgentAuthorized],
        // 清单会暴露页面标题与地址：只对获授权的 Agent 浏览器开放。
        [DesktopCapability.BrowserContexts] = [DesktopContextTrust.AgentAuthorized],
        // 标签页切换/关闭是变更类：只对获授权的 Agent 浏览器开放。
        [DesktopCapability.BrowserTabs] = [DesktopContextTrust.AgentAuthorized],
        // 上下文创建/关闭是变更类（缺口 #1）：同样只对获授权的 Agent 浏览器开放。
        [DesktopCapability.BrowserContextCreate] = [DesktopContextTrust.AgentAuthorized],
        [DesktopCapability.BrowserContextClose] = [DesktopContextTrust.AgentAuthorized],
        [DesktopCapability.BrowserBindingApply] = [DesktopContextTrust.AgentAuthorized],
        [DesktopCapability.BrowserBindingRevoke] = [DesktopContextTrust.AgentAuthorized],
    };

    /// <summary>
    /// 该能力是否属于**浏览器上下文作用域**（无页面目标、由 Agent 浏览器工具层发起）。
    /// <para>
    /// 存在的意义：准入需要知道「无页面目标时该用哪一份调用方信任」—— 这个集合必须是**单一定义**，
    /// 否则策略表、准入与测试三处会各写一份并逐渐漂移。
    /// </para>
    /// </summary>
    public static bool IsBrowserContextScoped(DesktopCapability capability) => capability
        is DesktopCapability.BrowserContexts
        or DesktopCapability.BrowserContextCreate
        or DesktopCapability.BrowserContextClose
        or DesktopCapability.BrowserBindingApply
        or DesktopCapability.BrowserBindingRevoke;

    /// <summary>
    /// 该能力是否属于**展示类**（告诉 Shell 把现有内容呈现给用户：文档预览 / 面板渲染）。
    ///
    /// <para>
    /// 为什么要与 Shell 自身 UI 动作分开：这两类能力只可能来自 Core 的 Agent 工具层
    /// （<c>preview_document</c> / <c>render_panel</c>），它们不是用户在本机点按钮引发的动作，
    /// 也没有页面目标（拿不到 <c>pageState.Trust</c>）。若沿用 <c>ShellCallerTrust</c>
    /// （默认 Untrusted），启用能力通道后这两条链路会**握手成功但一律 Unauthorized**
    /// —— 与外部审查 P1-1 是同一类缺陷。
    /// </para>
    /// <para>
    /// 对话框 / Picker / 剪贴板**不在**此列，仍走 <c>ShellCallerTrust</c>（fail closed，逐能力开放）。
    /// </para>
    /// </summary>
    public static bool IsPresentationScoped(DesktopCapability capability) => capability
        is DesktopCapability.ShellDocumentPreview
        or DesktopCapability.ShellPanelRender;

    public static bool IsAllowedForTrust(DesktopCapability capability, DesktopContextTrust trust) =>
        AllowedTrust.TryGetValue(capability, out var allowed) && Array.IndexOf(allowed, trust) >= 0;

    /// <summary>该能力是否要求显式页面目标（Shell 能力不要求）。</summary>
    public static bool RequiresPageTarget(DesktopCapability capability) =>
        DesktopCapabilities.TryGet(capability, out var descriptor)
        && descriptor.Traits.HasFlag(DesktopCapabilityTraits.RequiresPageTarget);

    /// <summary>
    /// 该能力是否要求页面是**用户选定的 Agent 目标页**（page 级闸门）。
    ///
    /// 为什么必须单独有这一条（审计 2026-10-08 结论 #2 / 设计 §3.2）：可信级别是<b>context 级</b>，
    /// 于是「在 AgentAuthorized 上下文里」被当成了「这个页面已获授权」——
    /// 同一上下文里的**非目标页**同样能通过准入，UI 上的「Agent 目标」因此只是暗示。
    ///
    /// 判据直接从信任表推导，避免出现第二份真源：
    /// 要求页面目标，且**只对 AgentAuthorized 开放**（对 Untrusted / Workbench 都不开放）。
    /// 因此 `webview.navigate` 与 `webview.page_state` 不在其列 —— 它们对普通网页与工作台也开放，
    /// 属通用浏览能力（把导航也收紧到「目标页」属 grant 模型切片，见设计 §3.2 的写权限行）。
    /// </summary>
    public static bool RequiresAgentTarget(DesktopCapability capability) =>
        RequiresPageTarget(capability)
        && IsAllowedForTrust(capability, DesktopContextTrust.AgentAuthorized)
        && !IsAllowedForTrust(capability, DesktopContextTrust.Untrusted)
        && !IsAllowedForTrust(capability, DesktopContextTrust.Workbench);

    /// <summary>工作台页面绝不允许脚本注入（策略表之外的硬不变式）。</summary>
    public static bool DeniesWorkbenchScriptInjection(DesktopCapability capability, DesktopContextTrust trust) =>
        capability == DesktopCapability.WebViewExecuteJavascript && trust == DesktopContextTrust.Workbench;

    /// <summary>供测试/审计读取的完整策略快照。</summary>
    public static IReadOnlyDictionary<DesktopCapability, DesktopContextTrust[]> Snapshot =>
        AllowedTrust.ToDictionary(pair => pair.Key, pair => pair.Value.ToArray());
}
