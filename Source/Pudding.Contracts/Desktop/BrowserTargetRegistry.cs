namespace Pudding.Contracts.Desktop;

/// <summary>
/// 浏览器目标注册表端口：由 Desktop 侧在页面创建/授权时登记，「可信级别 / 活动页 / 是否 Agent 目标」
/// <b>只能从这里读</b>，绝不接受调用方（模型或网页）填写（计划 §4/§7）。
///
/// 既有浏览器运行时只描述"有哪些页面"，不描述"这些页面对 Agent 意味着什么"；
/// 把后者放在这里，映射层才能在无 UI 环境测试。
/// </summary>
public interface IDesktopBrowserTargetRegistry
{
    /// <summary>该上下文的可信级别；未登记按 <see cref="DesktopContextTrust.Untrusted"/> 处理（fail closed）。</summary>
    DesktopContextTrust TrustFor(string contextId);

    /// <summary>该页面是否为已授权的 Agent 目标。</summary>
    bool IsAgentTarget(string contextId, string pageId);

    /// <summary>当前活动页面（同一上下文内至多一个）；无则返回 <c>null</c>。</summary>
    (string ContextId, string PageId)? ActivePage { get; }
}