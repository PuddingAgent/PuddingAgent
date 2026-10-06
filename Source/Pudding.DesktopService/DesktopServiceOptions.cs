using Pudding.Contracts;
using Pudding.Contracts.Desktop;

namespace Pudding.DesktopService;

/// <summary>DesktopService 的准入与超时策略。</summary>
public sealed record DesktopServiceOptions
{
    /// <summary>本窗口实际启用的能力上限（≤ 与 Core 协商成功的集合）；未列出的能力一律拒绝。</summary>
    public required DesktopCapability AllowedCapabilities { get; init; }

    /// <summary>
    /// 无页面目标的 Shell 能力所采用的可信级别。
    /// 默认 <see cref="DesktopContextTrust.Untrusted"/>：在 Core 侧授权链路接线之前，
    /// 对话框/Picker/剪贴板一律不开放（计划 §8 切片 E：逐能力声明支持与授权）。
    /// </summary>
    public DesktopContextTrust ShellCallerTrust { get; init; } = DesktopContextTrust.Untrusted;

    /// <summary>
    /// **浏览器上下文作用域**能力（<c>browser.contexts</c> / <c>browser.context.create</c> /
    /// <c>browser.context.close</c>：都是无页面目标的能力）采用的可信级别。
    /// <para>
    /// 默认 <see cref="DesktopContextTrust.AgentAuthorized"/>，与 <see cref="ShellCallerTrust"/> **分开**，因为：
    /// ① 这些能力只可能来自 Core 的 Agent 浏览器工具层（窄端口），不是 Shell 自己的 UI 动作；
    /// ② 它们没有页面目标 ⇒ 拿不到 <c>pageState.Trust</c>；若沿用 Shell 信任（默认 Untrusted），
    ///    启用能力通道后 <c>browser_context</c> 的 list/create/close 会直接 Unauthorized
    ///    —— 这正是外部审查报告（2026-10-06）的 P1-1：通道握手成功却什么也做不了。
    /// </para>
    /// <para>
    /// 对话框 / Picker / 剪贴板**不受此影响**，仍走 <see cref="ShellCallerTrust"/>（fail closed，逐能力开放）。
    /// </para>
    /// </summary>
    public DesktopContextTrust BrowserContextCallerTrust { get; init; } = DesktopContextTrust.AgentAuthorized;

    internal void Validate()
    {
        if (AllowedCapabilities == DesktopCapability.None)
        {
            throw new ArgumentException("At least one capability must be enabled.", nameof(AllowedCapabilities));
        }
    }
}
