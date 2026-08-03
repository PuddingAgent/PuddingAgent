using PuddingBrowser.Abstractions;
using PuddingCode.Models;
using PuddingCode.Tools;

namespace PuddingBrowser.AgentTools;

public sealed record BrowserLocateArgs
{
    [ToolParam("Browser page id to search.")]
    public required string PageId { get; init; }
    public string? ContextId { get; init; }
    public required BrowserLocatorInput Locator { get; init; }
}

[Tool(
    id: BrowserAgentToolIds.Locate,
    name: "Browser locate",
    description: "Resolve a ref, CSS, XPath, text, role, label, placeholder, alt text, title, or test-id locator in a Desktop browser tab.",
    category: ToolCategory.Network,
    permission: ToolPermissionLevel.Low,
    safety: ToolSafetyFlags.ConcurrencySafe)]
public sealed class BrowserLocateTool(IBrowserRuntime runtime, IBrowserOperationOriginAccessor originAccessor) : BrowserAgentToolBase<BrowserLocateArgs>(originAccessor)
{
    protected override async Task<ToolExecutionResult> ExecuteCoreAsync(
        BrowserLocateArgs args, ToolExecutionContext context, CancellationToken ct)
    {
        try
        {
            var locator = BrowserLocatorInputMapper.ToLocator(args.Locator);
            var resolved = await BrowserToolRuntimeResolver.ResolvePageAsync(runtime, args.ContextId, args.PageId, ct);
            if (resolved is null)
                return BrowserToolResponse.Failure("browser_page_not_found", "Browser page not found");
            var page = resolved.Value.Page;
            var handles = await page.QueryAllAsync(locator, ct);
            var values = handles.Take(100).Select(handle => BrowserToolResponse.Element(handle.Info)).ToArray();
            foreach (var handle in handles)
                await handle.DisposeAsync();
            return BrowserToolResponse.Success(new BrowserLocateToolValue
            {
                Count = handles.Count,
                Elements = values
            }, page.ContextId, page.Id, page.PageVersion,
                handles.Count > values.Length ? ["locator_results_truncated"] : null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (BrowserOperationException ex) { return BrowserToolResponse.FromException(ex); }
    }
}
