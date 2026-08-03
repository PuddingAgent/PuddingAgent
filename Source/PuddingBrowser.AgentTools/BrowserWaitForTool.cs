using PuddingBrowser.Abstractions;
using PuddingCode.Models;
using PuddingCode.Tools;

namespace PuddingBrowser.AgentTools;

public sealed record BrowserWaitForArgs
{
    [ToolParam("Browser page id to wait in.")]
    public required string PageId { get; init; }
    public string? ContextId { get; init; }
    public string? Selector { get; init; }
    public string? SelectorToHide { get; init; }
    public string? UrlPattern { get; init; }
    public int TimeoutMs { get; init; } = 30_000;
}

[Tool(
    id: BrowserAgentToolIds.WaitFor,
    name: "Browser wait for",
    description: "Wait for a CSS selector to appear or hide, or for a wildcard URL pattern in a Desktop browser tab.",
    category: ToolCategory.Network,
    permission: ToolPermissionLevel.Low,
    safety: ToolSafetyFlags.ConcurrencySafe)]
public sealed class BrowserWaitForTool(IBrowserRuntime runtime, IBrowserOperationOriginAccessor originAccessor) : BrowserAgentToolBase<BrowserWaitForArgs>(originAccessor)
{
    protected override async Task<ToolExecutionResult> ExecuteCoreAsync(
        BrowserWaitForArgs args, ToolExecutionContext context, CancellationToken ct)
    {
        if (args.TimeoutMs is < 1 or > 120_000
            || (string.IsNullOrWhiteSpace(args.Selector)
                && string.IsNullOrWhiteSpace(args.SelectorToHide)
                && string.IsNullOrWhiteSpace(args.UrlPattern)))
            return BrowserToolResponse.Failure("browser_invalid_arguments", "wait condition or timeout is invalid");
        try
        {
            var resolved = await BrowserToolRuntimeResolver.ResolvePageAsync(runtime, args.ContextId, args.PageId, ct);
            if (resolved is null)
                return BrowserToolResponse.Failure("browser_page_not_found", "Browser page not found");
            var page = resolved.Value.Page;
            var result = await page.WaitForAsync(new WaitCondition
            {
                Selector = args.Selector,
                SelectorToHide = args.SelectorToHide,
                UrlPattern = args.UrlPattern,
                TimeoutMs = args.TimeoutMs
            }, ct);
            return BrowserToolResponse.Success(new BrowserWaitToolValue
            {
                TimedOut = result.TimedOut,
                Error = result.Error,
                Page = BrowserToolResponse.Page(page.Info)
            }, page.ContextId, page.Id, page.PageVersion);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (BrowserOperationException ex) { return BrowserToolResponse.FromException(ex); }
    }
}
