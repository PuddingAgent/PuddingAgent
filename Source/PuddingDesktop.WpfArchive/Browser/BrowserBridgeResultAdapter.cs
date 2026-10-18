using Pudding.Contracts.Desktop;
using PuddingBrowser.Protocol;

namespace PuddingDesktop.Browser;

/// <summary>
/// I2-c 接线所需的**结果反投影**（Contracts → Bridge 线缆描述符）的第一片：
/// `page.getInfo`、`context.list`/`context.getInfo`、`page.list` 三类发现读命令。
///
/// 已知字段缺口（如实记录，不编造）：
/// · `DesktopContextInfo` 没有 UserDataDirectory ⇒ 线缆值用空串（旧 controller 有该字段，接入执行器后
///   工具端上下文发现不会再拿到真实目录；需要时由生命周期侧补来源）；
/// · 页面状态里的 IsActive/IsAgentTarget/CanGoBack/CanGoForward/IsLoading 只有 contexts 页清单才提供，
///   单页 `page.getInfo` 路径如实填 false/默认；
/// · title/url 缺失时填空串（线缆描述符字段为 required）。
/// </summary>
public static class BrowserBridgeResultAdapter
{
    public static BrowserPageDescriptor FromPageState(DesktopPageState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return new BrowserPageDescriptor
        {
            ContextId = state.Target.ContextId,
            PageId = state.Target.PageId,
            Title = state.Title ?? string.Empty,
            Url = state.Url?.AbsoluteUri ?? string.Empty,
            PageVersion = state.Version.Value,
        };
    }

    public static BrowserPageDescriptor FromPageInfo(DesktopPageInfo page)
    {
        ArgumentNullException.ThrowIfNull(page);
        return new BrowserPageDescriptor
        {
            ContextId = page.Target.ContextId,
            PageId = page.Target.PageId,
            Title = page.Title ?? string.Empty,
            Url = page.Url?.AbsoluteUri ?? string.Empty,
            PageVersion = page.Version.Value,
            IsActive = page.IsActive,
            IsAgentTarget = page.IsAgentTarget,
            CanGoBack = page.CanGoBack,
            CanGoForward = page.CanGoForward,
            IsLoading = page.IsLoading,
        };
    }

    public static BrowserContextListDescriptor FromContexts(DesktopContexts contexts)
    {
        ArgumentNullException.ThrowIfNull(contexts);
        return new BrowserContextListDescriptor
        {
            Contexts = contexts.Contexts
                .Select(context => new BrowserContextDescriptor
                {
                    ContextId = context.ContextId,
                    // 契约没有 UserDataDirectory：如实空串，不伪造路径。
                    UserDataDirectory = string.Empty,
                    PageCount = context.PageCount,
                })
                .ToArray(),
        };
    }

    public static BrowserPageListDescriptor FromPageList(DesktopContexts contexts)
    {
        ArgumentNullException.ThrowIfNull(contexts);
        return new BrowserPageListDescriptor
        {
            Pages = contexts.Contexts
                .SelectMany(context => context.Pages)
                .Select(FromPageInfo)
                .ToArray(),
        };
    }
}
