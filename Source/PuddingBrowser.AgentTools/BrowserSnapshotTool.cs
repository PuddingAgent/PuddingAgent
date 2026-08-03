using PuddingBrowser.Abstractions;
using PuddingCode.Models;
using PuddingCode.Tools;

namespace PuddingBrowser.AgentTools;

public sealed record BrowserSnapshotArgs
{
    [ToolParam("Browser page id to inspect.")]
    public required string PageId { get; init; }
    [ToolParam("Optional browser context id.")]
    public string? ContextId { get; init; }
    public bool IncludeDom { get; init; } = true;
    public bool IncludeAccessibilityTree { get; init; } = true;
    public bool IncludeHidden { get; init; }
    public bool IncludeIframes { get; init; } = true;
    public bool IncludeShadowDom { get; init; } = true;
    public bool IncludeHtml { get; init; }
    public int MaxNodes { get; init; } = 5_000;
    public int MaxTextLength { get; init; } = 200_000;
    public int MaxDepth { get; init; } = 24;
}

[Tool(
    id: BrowserAgentToolIds.Snapshot,
    name: "Browser snapshot",
    description: "Read a bounded DOM and accessibility snapshot from a visible Desktop browser tab. Interactive nodes include reusable versioned refs.",
    category: ToolCategory.Network,
    permission: ToolPermissionLevel.Low,
    safety: ToolSafetyFlags.ConcurrencySafe)]
public sealed class BrowserSnapshotTool(IBrowserRuntime runtime, IBrowserOperationOriginAccessor originAccessor) : BrowserAgentToolBase<BrowserSnapshotArgs>(originAccessor)
{
    protected override async Task<ToolExecutionResult> ExecuteCoreAsync(
        BrowserSnapshotArgs args, ToolExecutionContext context, CancellationToken ct)
    {
        if (args.MaxNodes is < 1 or > 10_000 || args.MaxTextLength is < 256 or > 500_000
            || args.MaxDepth is < 1 or > 64)
            return BrowserToolResponse.Failure("browser_invalid_arguments", "snapshot budgets are outside allowed ranges");
        try
        {
            var resolved = await BrowserToolRuntimeResolver.ResolvePageAsync(runtime, args.ContextId, args.PageId, ct);
            if (resolved is null)
                return BrowserToolResponse.Failure("browser_page_not_found", "Browser page not found");
            var page = resolved.Value.Page;
            var snapshot = await page.SnapshotAsync(new SnapshotOptions
            {
                IncludeDom = args.IncludeDom,
                IncludeAccessibilityTree = args.IncludeAccessibilityTree,
                IncludeHidden = args.IncludeHidden,
                IncludeIframes = args.IncludeIframes,
                IncludeShadowDom = args.IncludeShadowDom,
                IncludeHtml = args.IncludeHtml,
                MaxNodes = args.MaxNodes,
                MaxTextLength = args.MaxTextLength,
                MaxDepth = args.MaxDepth
            }, ct);
            return BrowserToolResponse.Success(new BrowserSnapshotToolValue
            {
                DomText = snapshot.DomText,
                AccessibilityTree = snapshot.AccessibilityTree,
                Html = snapshot.Html,
                Truncated = snapshot.Truncated,
                NodeCount = snapshot.NodeCount
            }, page.ContextId, page.Id, page.PageVersion,
                snapshot.Truncated ? ["snapshot_truncated"] : null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (BrowserOperationException ex) { return BrowserToolResponse.FromException(ex); }
    }
}
