using Pudding.Contracts;
using Pudding.Contracts.Desktop;
using PuddingBrowser.Abstractions;
using PuddingCode.Models;
using PuddingCode.Tools;

namespace PuddingBrowser.AgentTools;

public sealed record BrowserNavigateArgs
{
    [ToolParam("Action: goto, back, forward, reload, or stop.")]
    public required string Action { get; init; }

    [ToolParam("Browser page id to navigate.")]
    public required string PageId { get; init; }

    [ToolParam("Optional browser context id. Use it to disambiguate page ids.")]
    public string? ContextId { get; init; }

    [ToolParam("Absolute http/https URL. Required for goto.")]
    public string? Url { get; init; }

    [ToolParam("Navigation timeout in milliseconds. Defaults to 30000.")]
    public int TimeoutMs { get; init; } = 30_000;
}

[Tool(
    id: BrowserAgentToolIds.Navigate,
    name: "Browser navigate",
    description: "Navigate a visible Desktop browser tab with goto, back, forward, reload, or stop.",
    category: ToolCategory.Network,
    permission: ToolPermissionLevel.Low,
    safety: ToolSafetyFlags.ConcurrencySafe | ToolSafetyFlags.RequiresNetwork)]
public sealed class BrowserNavigateTool(
    IDesktopBrowserCapabilitySurface surface,
    IDesktopCapabilityCallContextFactory callContexts,
    IBrowserOperationOriginAccessor originAccessor) : BrowserAgentToolBase<BrowserNavigateArgs>(originAccessor)
{
    protected override async Task<ToolExecutionResult> ExecuteCoreAsync(
        BrowserNavigateArgs args,
        ToolExecutionContext context,
        CancellationToken ct)
    {
        var action = args.Action?.Trim().ToLowerInvariant();
        if (action is not ("goto" or "back" or "forward" or "reload" or "stop"))
        {
            return BrowserToolResponse.Failure(
                "browser_invalid_arguments",
                "action must be one of: goto, back, forward, reload, stop");
        }

        try
        {
            if (callContexts.TryCreate() is not { } call)
            {
                return BrowserToolResponse.Failure(
                    "browser_not_connected", "No Desktop is connected for browser capabilities");
            }

            // 调用方给了上下文就直接用，不要先去列清单（宿主集成测试按调用序列断言会抓到）。
            var contextId = string.IsNullOrWhiteSpace(args.ContextId) ? null : args.ContextId!.Trim();
            if (contextId is null)
            {
                var contexts = await surface.GetContextsAsync(call, ct);
                if (contexts.IsFailure)
                {
                    return Failure(contexts.Error!);
                }

                contextId = contexts.Value.Contexts.Count > 0 ? contexts.Value.Contexts[0].ContextId : null;
                if (string.IsNullOrWhiteSpace(contextId))
                {
                    return BrowserToolResponse.Failure("browser_context_not_found", "No browser context is available");
                }
            }

            var target = new DesktopPageTarget(contextId, args.PageId.Trim());
            var browserContextId = new BrowserContextId(contextId);
            var pageId = new PageId(args.PageId.Trim());

            Uri? url = null;
            if (action == "goto")
            {
                if (!Uri.TryCreate(args.Url, UriKind.Absolute, out url)
                    || (url.Scheme != Uri.UriSchemeHttp && url.Scheme != Uri.UriSchemeHttps))
                {
                    return BrowserToolResponse.Failure(
                        "browser_invalid_arguments", "url must be an absolute http/https URL",
                        browserContextId, pageId);
                }

                if (args.TimeoutMs is < 1 or > 300_000)
                {
                    return BrowserToolResponse.Failure(
                        "browser_invalid_arguments", "timeout_ms must be between 1 and 300000",
                        browserContextId, pageId);
                }
            }

            var navigationAction = action switch
            {
                "back" => DesktopNavigationAction.Back,
                "forward" => DesktopNavigationAction.Forward,
                "reload" => DesktopNavigationAction.Reload,
                "stop" => DesktopNavigationAction.Stop,
                _ => DesktopNavigationAction.Goto,
            };

            // 导航不强钉版本（与迁移前一致）：契约用 Unknown 表达"当前版本即可"。
            var navigated = await surface.NavigateAsync(
                new NavigateRequest(target, url, DesktopPageVersion.Unknown, navigationAction, args.TimeoutMs),
                call,
                ct);
            if (navigated.IsFailure)
            {
                return Failure(navigated.Error!);
            }

            var page = navigated.Value;
            return BrowserToolResponse.Success(
                new BrowserNavigationToolValue
                {
                    Action = action,
                    Page = new BrowserTabToolValue
                    {
                        ContextId = contextId,
                        PageId = args.PageId.Trim(),
                        // 标题由 NavigateResult 回带（缺口 #14 已补）；未知时回退空串，不编造。
                        Title = page.Title ?? string.Empty,
                        Url = page.CurrentUrl?.AbsoluteUri ?? string.Empty,
                        PageVersion = page.PageVersion.IsKnown ? page.PageVersion.Value : 0,
                    },
                    // 导航结果事实：goto 有值；back/forward/reload/stop 在运行时没有等价返回值 ⇒ null（不猜）。
                    NavigationOk = page.Ok,
                    StatusCode = page.StatusCode,
                    ErrorText = page.ErrorText,
                },
                browserContextId,
                pageId,
                page.PageVersion.IsKnown ? page.PageVersion.Value : null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (BrowserOperationException ex)
        {
            return BrowserToolResponse.FromException(ex);
        }
    }

    private static ToolExecutionResult Failure(DesktopCapabilityError error) =>
        BrowserCapabilityFailure.From(error, "browser_navigate_failed", "stale_page_version");
}
