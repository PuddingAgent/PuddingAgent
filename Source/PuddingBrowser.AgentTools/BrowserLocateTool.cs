using Pudding.Contracts;
using Pudding.Contracts.Desktop;
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
public sealed class BrowserLocateTool(
    IDesktopBrowserCapabilitySurface surface,
    IDesktopCapabilityCallContextFactory callContexts,
    IBrowserOperationOriginAccessor originAccessor) : BrowserAgentToolBase<BrowserLocateArgs>(originAccessor)
{
    protected override async Task<ToolExecutionResult> ExecuteCoreAsync(
        BrowserLocateArgs args, ToolExecutionContext context, CancellationToken ct)
    {
        try
        {
            var locator = BrowserLocatorInputMapper.ToDesktopLocator(args.Locator);

            if (callContexts.TryCreate() is not { } call)
            {
                return BrowserToolResponse.Failure(
                    "browser_not_connected", "No Desktop is connected for browser capabilities");
            }

            var contexts = await surface.GetContextsAsync(call, ct);
            if (contexts.IsFailure)
            {
                return Failure(contexts.Error!);
            }

            // 上下文可省略：沿用「不给就用第一个可用上下文」的既有语义（与迁移前一致）。
            var contextId = string.IsNullOrWhiteSpace(args.ContextId)
                ? contexts.Value.Contexts.Count > 0 ? contexts.Value.Contexts[0].ContextId : null
                : args.ContextId!.Trim();
            if (string.IsNullOrWhiteSpace(contextId))
            {
                return BrowserToolResponse.Failure("browser_context_not_found", "No browser context is available");
            }

            var target = new DesktopPageTarget(contextId, args.PageId.Trim());

            // Ref 定位必须带上引用来源的版本（引用随版本失效）。工具入参里没有版本，
            // 因此读一次当前状态作为基准——读到的是当前版本，不会自相矛盾，但引用因此有了可比较的版本。
            var expectedPageVersion = DesktopPageVersion.Unknown;
            if (locator.Kind == DesktopLocatorKind.Ref)
            {
                var state = await surface.GetPageStateAsync(target, call, ct);
                if (state.IsFailure)
                {
                    return Failure(state.Error!);
                }

                expectedPageVersion = state.Value.Version;
            }

            var located = await surface.LocateAsync(
                new BrowserLocateRequest(target, locator, expectedPageVersion), call, ct);
            if (located.IsFailure)
            {
                return Failure(located.Error!);
            }

            var elements = located.Value.Elements;
            var values = elements.Take(100).Select(ToValue).ToArray();
            return BrowserToolResponse.Success(
                new BrowserLocateToolValue
                {
                    Count = elements.Count,
                    Elements = values
                },
                new PuddingBrowser.Abstractions.BrowserContextId(contextId),
                new PuddingBrowser.Abstractions.PageId(args.PageId.Trim()),
                located.Value.PageVersion.IsKnown ? located.Value.PageVersion.Value : null,
                located.Value.Truncated || elements.Count > values.Length
                    ? ["locator_results_truncated"]
                    : null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (BrowserOperationException ex) { return BrowserToolResponse.FromException(ex); }
    }

    /// <summary>能力路径的失败 → 工具错误码。**版本不符对定位而言就是"引用过期"**（沿用既有错误码）。</summary>
    private static ToolExecutionResult Failure(DesktopCapabilityError error) => BrowserToolResponse.Failure(
        error.Code switch
        {
            DesktopCapabilityErrorCode.InvalidTarget => "browser_page_not_found",
            DesktopCapabilityErrorCode.PageVersionMismatch => "stale_element_reference",
            DesktopCapabilityErrorCode.InvalidRequest => "browser_invalid_arguments",
            DesktopCapabilityErrorCode.UnsupportedCapability => "browser_unsupported",
            _ => "browser_locate_failed",
        },
        error.Message);

    private static BrowserElementToolValue ToValue(DesktopElementRef element) => new()
    {
        Ref = element.Reference,
        Tag = element.Tag,
        Role = element.Role,
        Name = element.Name,
        Text = element.Text,
        Visible = element.Visible,
        Enabled = element.Enabled,
        Checked = element.IsChecked,
        BoundingBox = element.BoundingBox is { } box
            ? new PuddingBrowser.Abstractions.BoundingBox
            {
                X = box.X,
                Y = box.Y,
                Width = box.Width,
                Height = box.Height,
            }
            : null,
    };
}
