using Pudding.Contracts;
using Pudding.Contracts.Desktop;
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
public sealed class BrowserSnapshotTool(
    IDesktopBrowserCapabilitySurface surface,
    IDesktopCapabilityCallContextFactory callContexts,
    IBrowserOperationOriginAccessor originAccessor) : BrowserAgentToolBase<BrowserSnapshotArgs>(originAccessor)
{
    protected override async Task<ToolExecutionResult> ExecuteCoreAsync(
        BrowserSnapshotArgs args, ToolExecutionContext context, CancellationToken ct)
    {
        if (args.MaxNodes is < 1 or > 10_000 || args.MaxTextLength is < 256 or > 500_000
            || args.MaxDepth is < 1 or > 64)
            return BrowserToolResponse.Failure("browser_invalid_arguments", "snapshot budgets are outside allowed ranges");
        try
        {
            if (callContexts.TryCreate() is not { } call)
            {
                return BrowserToolResponse.Failure(
                    "browser_not_connected", "No Desktop is connected for browser capabilities");
            }

            // 调用方给了上下文就直接用，**不要**先去列清单：那是多余且更重的一跳
            // （宿主集成测试按调用序列断言，正好抓出了这一点）。
            var contextId = string.IsNullOrWhiteSpace(args.ContextId) ? null : args.ContextId!.Trim();
            if (contextId is null)
            {
                // 只有省略上下文时，才沿用「用第一个可用上下文」的既有语义。
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
            var snapshot = await surface.SnapshotAsync(
                new BrowserSnapshotRequest(
                    target,
                    // 快照不钉版本：「当前版本即可」由 Unknown 表达（与迁移前"直接用当前页"一致）。
                    DesktopPageVersion.Unknown,
                    new DesktopSnapshotOptions(
                        includeDom: args.IncludeDom,
                        includeAccessibilityTree: args.IncludeAccessibilityTree,
                        includeHtml: args.IncludeHtml,
                        maxNodes: args.MaxNodes,
                        maxTextLength: args.MaxTextLength,
                        includeHidden: args.IncludeHidden,
                        includeIframes: args.IncludeIframes,
                        includeShadowDom: args.IncludeShadowDom,
                        maxDepth: args.MaxDepth)),
                call,
                ct);
            if (snapshot.IsFailure)
            {
                return Failure(snapshot.Error!);
            }

            return BrowserToolResponse.Success(
                new BrowserSnapshotToolValue
                {
                    DomText = snapshot.Value.DomText,
                    AccessibilityTree = snapshot.Value.AccessibilityTree,
                    Html = snapshot.Value.Html,
                    Truncated = snapshot.Value.Truncated,
                    NodeCount = snapshot.Value.NodeCount
                },
                new BrowserContextId(contextId),
                new PageId(args.PageId.Trim()),
                snapshot.Value.PageVersion.IsKnown ? snapshot.Value.PageVersion.Value : null,
                snapshot.Value.Truncated ? ["snapshot_truncated"] : null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (BrowserOperationException ex) { return BrowserToolResponse.FromException(ex); }
    }

    /// <summary>快照的版本不符语义是"页面已变"，不是"引用过期"。</summary>
    private static ToolExecutionResult Failure(DesktopCapabilityError error) =>
        BrowserCapabilityFailure.From(error, "browser_snapshot_failed", "stale_page_version");
}
