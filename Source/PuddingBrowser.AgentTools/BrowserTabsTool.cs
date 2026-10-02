using Pudding.Contracts;
using Pudding.Contracts.Desktop;
using PuddingBrowser.Abstractions;
using PuddingCode.Models;
using PuddingCode.Tools;

namespace PuddingBrowser.AgentTools;

public sealed record BrowserTabsArgs
{
    [ToolParam("Action: new, list, activate, or close.")]
    public required string Action { get; init; }

    [ToolParam("Optional browser context id. The first available context is used when omitted.")]
    public string? ContextId { get; init; }

    [ToolParam("Page id. Required for activate and close.")]
    public string? PageId { get; init; }

    [ToolParam("Optional absolute http/https URL for a new tab.")]
    public string? Url { get; init; }

    [ToolParam("Whether a new tab becomes the visible Agent target. Defaults to true.")]
    public bool Activate { get; init; } = true;
}

[Tool(
    id: BrowserAgentToolIds.Tabs,
    name: "Browser tabs",
    description: "Create, list, activate, or close visible Desktop browser tabs.",
    category: ToolCategory.Network,
    permission: ToolPermissionLevel.Low,
    safety: ToolSafetyFlags.ConcurrencySafe | ToolSafetyFlags.RequiresNetwork)]
public sealed class BrowserTabsTool(
    IDesktopBrowserCapabilitySurface surface,
    IDesktopCapabilityCallContextFactory callContexts,
    IBrowserOperationOriginAccessor originAccessor) : BrowserAgentToolBase<BrowserTabsArgs>(originAccessor)
{
    protected override async Task<ToolExecutionResult> ExecuteCoreAsync(
        BrowserTabsArgs args,
        ToolExecutionContext context,
        CancellationToken ct)
    {
        var action = args.Action?.Trim().ToLowerInvariant();
        try
        {
            return action switch
            {
                "new" => await NewAsync(args, ct),
                "list" => await ListAsync(args, ct),
                "activate" => await ChangeTabAsync(args, DesktopTabAction.Activate, ct),
                "close" => await ChangeTabAsync(args, DesktopTabAction.Close, ct),
                _ => BrowserToolResponse.Failure(
                    "browser_invalid_arguments",
                    "action must be one of: new, list, activate, close")
            };
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

    private async Task<ToolExecutionResult> NewAsync(BrowserTabsArgs args, CancellationToken ct)
    {
        Uri? initialUrl = null;
        if (!string.IsNullOrWhiteSpace(args.Url)
            && (!Uri.TryCreate(args.Url.Trim(), UriKind.Absolute, out initialUrl)
                || (initialUrl.Scheme != Uri.UriSchemeHttp && initialUrl.Scheme != Uri.UriSchemeHttps)))
        {
            return BrowserToolResponse.Failure(
                "browser_invalid_arguments", "url must be an absolute http/https URL");
        }

        if (callContexts.TryCreate(CurrentPermissionEvidenceSummary()) is not { } call)
        {
            return BrowserToolResponse.Failure(
                "browser_not_connected", "No Desktop is connected for browser capabilities");
        }

        // 新建时上下文可省略：沿用「用第一个可用上下文」的既有语义。
        // （迁移前会 createIfMissing，但能力契约没有「创建上下文」的能力 ⇒ 缺上下文时如实报错，不猜。）
        var contextId = string.IsNullOrWhiteSpace(args.ContextId) ? null : args.ContextId!.Trim();
        if (contextId is null)
        {
            var contexts = await surface.GetContextsAsync(call, ct);
            if (contexts.IsFailure)
            {
                return Failure(contexts.Error!);
            }

            contextId = contexts.Value.Contexts.Count > 0 ? contexts.Value.Contexts[0].ContextId : null;
        }

        if (string.IsNullOrWhiteSpace(contextId))
        {
            return BrowserToolResponse.Failure("browser_context_not_found", "Browser context not found");
        }

        var created = await surface.TabsAsync(
            BrowserTabsRequest.New(contextId, initialUrl, args.Activate), call, ct);
        return created.IsFailure ? Failure(created.Error!) : Success(created.Value);
    }

    private async Task<ToolExecutionResult> ListAsync(BrowserTabsArgs args, CancellationToken ct)
    {
        if (callContexts.TryCreate(CurrentPermissionEvidenceSummary()) is not { } call)
        {
            return BrowserToolResponse.Failure(
                "browser_not_connected", "No Desktop is connected for browser capabilities");
        }

        var contexts = await surface.GetContextsAsync(call, ct);
        if (contexts.IsFailure)
        {
            return Failure(contexts.Error!);
        }

        var filter = string.IsNullOrWhiteSpace(args.ContextId) ? null : args.ContextId!.Trim();
        if (filter is not null
            && contexts.Value.Contexts.All(c => !string.Equals(c.ContextId, filter, StringComparison.Ordinal)))
        {
            return BrowserToolResponse.Failure("browser_context_not_found", "Browser context not found");
        }

        var values = contexts.Value.Contexts
            .Where(c => filter is null || string.Equals(c.ContextId, filter, StringComparison.Ordinal))
            .SelectMany(c => c.Pages.Select(page => new BrowserTabToolValue
            {
                ContextId = page.Target.ContextId,
                PageId = page.Target.PageId,
                Title = page.Title ?? string.Empty,
                Url = page.Url?.AbsoluteUri ?? string.Empty,
                PageVersion = page.Version.IsKnown ? page.Version.Value : 0,
            }))
            .ToArray();

        return BrowserToolResponse.Success(values);
    }

    private async Task<ToolExecutionResult> ChangeTabAsync(
        BrowserTabsArgs args, DesktopTabAction action, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(args.PageId))
        {
            return BrowserToolResponse.Failure("browser_page_not_found", "Browser page not found");
        }

        if (callContexts.TryCreate(CurrentPermissionEvidenceSummary()) is not { } call)
        {
            return BrowserToolResponse.Failure(
                "browser_not_connected", "No Desktop is connected for browser capabilities");
        }

        var contextId = string.IsNullOrWhiteSpace(args.ContextId) ? null : args.ContextId!.Trim();
        DesktopPageTarget? target = contextId is null
            ? null
            : new DesktopPageTarget(contextId, args.PageId.Trim());
        if (target is null)
        {
            // 未给上下文时在清单里找出该页面（与迁移前的解析语义一致）。
            var contexts = await surface.GetContextsAsync(call, ct);
            if (contexts.IsFailure)
            {
                return Failure(contexts.Error!);
            }

            target = contexts.Value.Contexts
                .SelectMany(c => c.Pages)
                .FirstOrDefault(p => string.Equals(p.Target.PageId, args.PageId!.Trim(), StringComparison.Ordinal))
                ?.Target;
            if (target is null)
            {
                return BrowserToolResponse.Failure("browser_page_not_found", "Browser page not found");
            }
        }

        // 变更类必须固定版本 ⇒ 先读一次当前状态作为基准（与迁移前「直接作用于当前页」语义等价）。
        var state = await surface.GetPageStateAsync(target, call, ct);
        if (state.IsFailure)
        {
            return Failure(state.Error!);
        }

        if (!state.Value.Version.IsKnown)
        {
            return BrowserToolResponse.Failure("browser_page_not_found", "Page has no live version to act on");
        }

        var changed = await surface.TabsAsync(
            new BrowserTabsRequest(target, action, state.Value.Version), call, ct);
        if (changed.IsFailure)
        {
            return Failure(changed.Error!);
        }

        // 关闭沿用迁移前的可见形状：{ closed: true } + context/page id。
        return action == DesktopTabAction.Close
            ? BrowserToolResponse.Success(
                new { closed = true },
                new BrowserContextId(target.ContextId),
                new PageId(target.PageId))
            : Success(changed.Value);
    }

    private static ToolExecutionResult Success(DesktopTabsResult tabs) => BrowserToolResponse.Success(
        new BrowserTabToolValue
        {
            ContextId = tabs.Page.Target.ContextId,
            PageId = tabs.Page.Target.PageId,
            // 标题来自页状态（缺口 #12 已补）；未知时回退空串，不编造。
            Title = tabs.Page.Title ?? string.Empty,
            Url = tabs.Page.Url?.AbsoluteUri ?? string.Empty,
            PageVersion = tabs.Page.Version.IsKnown ? tabs.Page.Version.Value : 0,
        },
        new BrowserContextId(tabs.Page.Target.ContextId),
        new PageId(tabs.Page.Target.PageId),
        tabs.Page.Version.IsKnown ? tabs.Page.Version.Value : null);

    private static ToolExecutionResult Failure(DesktopCapabilityError error) =>
        BrowserCapabilityFailure.From(error, "browser_tabs_failed", "stale_page_version");
}
