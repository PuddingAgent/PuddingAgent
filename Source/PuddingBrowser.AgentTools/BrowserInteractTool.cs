using PuddingBrowser.Abstractions;
using PuddingCode.Models;
using PuddingCode.Tools;

namespace PuddingBrowser.AgentTools;

public sealed record BrowserInteractArgs
{
    [ToolParam("Action: click, fill, type, press, hover, scroll, select, or check.")]
    public required string Action { get; init; }
    [ToolParam("Browser page id to operate.")]
    public required string PageId { get; init; }
    public string? ContextId { get; init; }
    public BrowserLocatorInput? Locator { get; init; }
    [ToolParam("Text for fill/type, or key for press. Values are never returned in activity logs.")]
    public string? Text { get; init; }
    public IReadOnlyList<string>? Values { get; init; }
    public bool? Checked { get; init; }
    public double? DeltaX { get; init; }
    public double? DeltaY { get; init; }
}

[Tool(
    id: "browser_interact",
    name: "Browser interact",
    description: "Click, fill, type, press, hover, scroll, select, or check an element in a visible Desktop browser tab.",
    category: ToolCategory.Network,
    permission: ToolPermissionLevel.Low,
    safety: ToolSafetyFlags.RequiresNetwork)]
public sealed class BrowserInteractTool(IBrowserRuntime runtime) : PuddingToolBase<BrowserInteractArgs>
{
    protected override async Task<ToolExecutionResult> ExecuteCoreAsync(
        BrowserInteractArgs args, ToolExecutionContext context, CancellationToken ct)
    {
        var action = args.Action?.Trim().ToLowerInvariant();
        if (action is not ("click" or "fill" or "type" or "press" or "hover" or "scroll" or "select" or "check"))
            return BrowserToolResponse.Failure("browser_invalid_arguments", "unsupported interaction action");
        try
        {
            var locator = args.Locator is null ? null : BrowserLocatorInputMapper.ToLocator(args.Locator);
            if (action != "scroll" && locator is null)
                return BrowserToolResponse.Failure("browser_invalid_arguments", "locator is required for this action");
            var resolved = await BrowserToolRuntimeResolver.ResolvePageAsync(runtime, args.ContextId, args.PageId, ct);
            if (resolved is null)
                return BrowserToolResponse.Failure("browser_page_not_found", "Browser page not found");
            var page = resolved.Value.Page;
            switch (action)
            {
                case "click": await page.ClickAsync(locator!, new ClickOptions(), ct); break;
                case "fill": await page.FillAsync(locator!, args.Text ?? string.Empty, new FillOptions(), ct); break;
                case "type": await page.TypeAsync(locator!, args.Text ?? string.Empty, new TypeOptions(), ct); break;
                case "press" when !string.IsNullOrWhiteSpace(args.Text): await page.PressAsync(locator!, args.Text, new KeyOptions(), ct); break;
                case "press": return BrowserToolResponse.Failure("browser_invalid_arguments", "text key is required for press");
                case "hover": await page.HoverAsync(locator!, new PointerOptions(), ct); break;
                case "scroll": await page.ScrollAsync(new ScrollOptions { DeltaX = args.DeltaX, DeltaY = args.DeltaY }, ct); break;
                case "select" when args.Values is { Count: > 0 }: await page.SelectAsync(locator!, args.Values, ct); break;
                case "select": return BrowserToolResponse.Failure("browser_invalid_arguments", "values are required for select");
                case "check" when args.Checked is not null: await page.CheckAsync(locator!, args.Checked.Value, ct); break;
                case "check": return BrowserToolResponse.Failure("browser_invalid_arguments", "checked is required for check");
            }
            return BrowserToolResponse.Success(new BrowserInteractionToolValue
            {
                Action = action,
                Page = BrowserToolResponse.Page(page.Info),
                // An interaction can commit navigation or replace the target node.
                // Re-querying the old locator here would turn a successful action into
                // a stale/not-found failure and could cause the agent to repeat it.
                Element = null
            }, page.ContextId, page.Id, page.PageVersion);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (BrowserOperationException ex) { return BrowserToolResponse.FromException(ex); }
    }
}
