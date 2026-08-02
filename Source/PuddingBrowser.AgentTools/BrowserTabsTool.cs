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
    id: "browser_tabs",
    name: "Browser tabs",
    description: "Create, list, activate, or close visible Desktop browser tabs.",
    category: ToolCategory.Network,
    permission: ToolPermissionLevel.Low,
    safety: ToolSafetyFlags.ConcurrencySafe | ToolSafetyFlags.RequiresNetwork)]
public sealed class BrowserTabsTool(IBrowserRuntime runtime) : PuddingToolBase<BrowserTabsArgs>
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
                "activate" => await ActivateAsync(args, ct),
                "close" => await CloseAsync(args, ct),
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

        var browserContext = await BrowserToolRuntimeResolver.ResolveContextAsync(
            runtime, args.ContextId, createIfMissing: true, ct: ct);
        if (browserContext is null)
            return BrowserToolResponse.Failure("browser_context_not_found", "Browser context not found");

        var page = await browserContext.NewPageAsync(new PageCreateOptions
        {
            InitialUrl = initialUrl,
            Activate = args.Activate
        }, ct);
        return BrowserToolResponse.Success(
            BrowserToolResponse.Page(page.Info),
            page.ContextId,
            page.Id,
            page.PageVersion);
    }

    private async Task<ToolExecutionResult> ListAsync(BrowserTabsArgs args, CancellationToken ct)
    {
        var values = new List<BrowserTabToolValue>();
        if (!string.IsNullOrWhiteSpace(args.ContextId))
        {
            var browserContext = await runtime.GetContextAsync(
                new BrowserContextId(args.ContextId.Trim()), ct);
            if (browserContext is null)
                return BrowserToolResponse.Failure("browser_context_not_found", "Browser context not found");
            values.AddRange((await browserContext.ListPagesAsync(ct)).Select(BrowserToolResponse.Page));
        }
        else
        {
            foreach (var contextInfo in await runtime.ListContextsAsync(ct))
            {
                var browserContext = await runtime.GetContextAsync(contextInfo.Id, ct);
                if (browserContext is not null)
                    values.AddRange((await browserContext.ListPagesAsync(ct)).Select(BrowserToolResponse.Page));
            }
        }

        return BrowserToolResponse.Success(values);
    }

    private async Task<ToolExecutionResult> ActivateAsync(BrowserTabsArgs args, CancellationToken ct)
    {
        var resolved = await BrowserToolRuntimeResolver.ResolvePageAsync(
            runtime, args.ContextId, args.PageId, ct);
        if (resolved is null)
            return BrowserToolResponse.Failure("browser_page_not_found", "Browser page not found");

        await resolved.Value.Page.BringToFrontAsync(ct);
        var page = resolved.Value.Page;
        return BrowserToolResponse.Success(
            BrowserToolResponse.Page(page.Info), page.ContextId, page.Id, page.PageVersion);
    }

    private async Task<ToolExecutionResult> CloseAsync(BrowserTabsArgs args, CancellationToken ct)
    {
        var resolved = await BrowserToolRuntimeResolver.ResolvePageAsync(
            runtime, args.ContextId, args.PageId, ct);
        if (resolved is null)
            return BrowserToolResponse.Failure("browser_page_not_found", "Browser page not found");

        var contextId = resolved.Value.Context.Id;
        var pageId = resolved.Value.Page.Id;
        await resolved.Value.Context.ClosePageAsync(pageId, ct);
        return BrowserToolResponse.Success(new { closed = true }, contextId, pageId);
    }
}
