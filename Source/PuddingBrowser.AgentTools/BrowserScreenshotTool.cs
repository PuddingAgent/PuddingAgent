using Pudding.Contracts;
using Pudding.Contracts.Desktop;
using PuddingBrowser.Abstractions;
using PuddingCode.Models;
using PuddingCode.Tools;

namespace PuddingBrowser.AgentTools;

/// <summary>截图参数（§8.1）。坐标一律 CSS viewport 像素；**必须钉住页面版本**。</summary>
public sealed record BrowserScreenshotArgs
{
    [ToolParam("Browser page id to capture.")]
    public required string PageId { get; init; }

    [ToolParam("Optional browser context id.")]
    public string? ContextId { get; init; }

    /// <summary>
    /// 要截取的页面版本（**必填**）：截图会签发 observationId，而基于它换算的坐标只在**同一页面世代**内有效；
    /// 不钉版本就无法判断"这张图是不是当前文档"，因此缺失即**明确拒绝**（不猜"就用当前版本"）。
    /// </summary>
    [ToolParam("Page version this capture is pinned to (required; take it from the latest snapshot).")]
    public long? ExpectedPageVersion { get; init; }

    [ToolParam("Capture scope: viewport (default), region, or full_page.")]
    public string? Scope { get; init; }

    [ToolParam("Detail: low (default, longest edge <=512px), high, or original.")]
    public string? Detail { get; init; }

    [ToolParam("Image format: png (default) or jpeg.")]
    public string? Format { get; init; }

    [ToolParam("Region x (CSS viewport pixels; region scope only).")]
    public double? RegionX { get; init; }

    [ToolParam("Region y (CSS viewport pixels; region scope only).")]
    public double? RegionY { get; init; }

    [ToolParam("Region width (region scope only; must be positive).")]
    public double? RegionWidth { get; init; }

    [ToolParam("Region height (region scope only; must be positive).")]
    public double? RegionHeight { get; init; }
}

[Tool(
    id: BrowserAgentToolIds.Screenshot,
    name: "Browser screenshot",
    description: "Capture a bounded screenshot of a visible Desktop browser tab and return the observation id that later coordinate operations refer to.",
    category: ToolCategory.Network,
    permission: ToolPermissionLevel.Low,
    safety: ToolSafetyFlags.ConcurrencySafe)]
public sealed class BrowserScreenshotTool(
    IDesktopBrowserCapabilitySurface surface,
    IDesktopCapabilityCallContextFactory callContexts,
    IBrowserOperationOriginAccessor originAccessor) : BrowserAgentToolBase<BrowserScreenshotArgs>(originAccessor)
{
    private static ToolExecutionResult Failure(DesktopCapabilityError error) =>
        BrowserToolResponse.Failure("browser_capability_failed", error.Message);

    protected override async Task<ToolExecutionResult> ExecuteBrowserCoreAsync(
        BrowserScreenshotArgs args, ToolExecutionContext context, CancellationToken ct)
    {
        if (args.ExpectedPageVersion is not { } expectedPageVersion || expectedPageVersion <= 0)
        {
            return BrowserToolResponse.Failure(
                "browser_invalid_arguments",
                "expectedPageVersion is required: a screenshot signs an observation id whose coordinates are only valid within the captured page version");
        }

        var scope = DesktopScreenshotScope.Viewport;
        if (!string.IsNullOrWhiteSpace(args.Scope))
        {
            if (!DesktopScreenshotScopeWire.TryParse(args.Scope, out scope))
            {
                return BrowserToolResponse.Failure(
                    "browser_invalid_arguments", "screenshot scope is not registered (viewport, region, full_page)");
            }
        }

        var detail = DesktopScreenshotDetail.Low;
        if (!string.IsNullOrWhiteSpace(args.Detail)
            && !DesktopScreenshotDetailWire.TryParse(args.Detail, out detail))
        {
            return BrowserToolResponse.Failure(
                "browser_invalid_arguments", "screenshot detail is not registered (low, high, original)");
        }

        var format = string.IsNullOrWhiteSpace(args.Format) ? "png" : args.Format!.Trim().ToLowerInvariant();
        if (format is not ("png" or "jpeg"))
        {
            return BrowserToolResponse.Failure(
                "browser_invalid_arguments", "screenshot format is not registered (png, jpeg)");
        }

        try
        {
            if (callContexts.TryCreate(CurrentPermissionEvidenceSummary()) is not { } call)
            {
                return BrowserToolResponse.Failure(
                    "browser_not_connected", "No Desktop is connected for browser capabilities");
            }

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
            var capture = await surface.ScreenshotAsync(
                new BrowserScreenshotRequest(
                    target,
                    DesktopPageVersion.Require(expectedPageVersion),
                    scope,
                    detail,
                    locator: null,
                    args.RegionX,
                    args.RegionY,
                    args.RegionWidth,
                    args.RegionHeight,
                    format),
                call,
                ct);
            if (capture.IsFailure)
            {
                return Failure(capture.Error!);
            }

            // 观察 id 必须交给调用方：后续 browser_interact 的截图坐标靠它解析。
            return BrowserToolResponse.Success(new
            {
                observationId = capture.Value.Metadata.ObservationId,
                mimeType = capture.Value.Metadata.MimeType,
                byteCount = capture.Value.Metadata.ByteCount,
                outputWidth = capture.Value.Metadata.OutputWidth,
                outputHeight = capture.Value.Metadata.OutputHeight,
                pageVersion = capture.Value.Metadata.PageVersion,
                detail = DesktopScreenshotDetailWire.NameOf(capture.Value.Metadata.Detail),
                chunks = capture.Value.Chunks.Count,
            });
        }
        catch (ArgumentException ex)
        {
            return BrowserToolResponse.Failure(
                "browser_invalid_arguments", $"screenshot arguments are not usable ({ex.ParamName})");
        }
    }
}
