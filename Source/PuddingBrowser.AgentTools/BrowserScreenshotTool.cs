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

    [ToolParam("Detail: low (default, <=512px preview); high/original preserve device pixels. Use original or a region for small text and fine visual comparisons. The selected detail also reaches the model image input.")]
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
    IBrowserOperationOriginAccessor originAccessor,
    // §7.1：把字节落成**有界工件**的窄端口（实现归 Host）。未注入时**不谎报**视觉成功：
    // 仍返回元数据，但显式标注 visionUnavailable（外部复核 P1：旧实现只返回 chunks 数量）。
    IBrowserScreenshotPublisher? screenshotPublisher = null) : BrowserAgentToolBase<BrowserScreenshotArgs>(originAccessor)
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

            // §7.1：先把**有序字节块**组装并校验（顺序/末块/累计长度），再决定"发布工件"还是"只给元数据"。
            var assemblyError = VerifyChunkAssembly(capture.Value);
            if (assemblyError is not null)
            {
                return BrowserToolResponse.Failure("browser_screenshot_incomplete", assemblyError);
            }

            var metadata = new
            {
                observationId = capture.Value.Metadata.ObservationId,
                mimeType = capture.Value.Metadata.MimeType,
                byteCount = capture.Value.Metadata.ByteCount,
                outputWidth = capture.Value.Metadata.OutputWidth,
                outputHeight = capture.Value.Metadata.OutputHeight,
                pageVersion = capture.Value.Metadata.PageVersion,
                detail = DesktopScreenshotDetailWire.NameOf(capture.Value.Metadata.Detail),
                chunks = capture.Value.Chunks.Count,
            };

            var summaryJson = System.Text.Json.JsonSerializer.Serialize(metadata);

            if (screenshotPublisher is null)
            {
                // **不得** silent success：没有发布端 ⇒ 模型并没有看见图片，必须显式说明。
                return BrowserToolResponse.Success(new
                {
                    metadata.observationId,
                    metadata.mimeType,
                    metadata.byteCount,
                    metadata.outputWidth,
                    metadata.outputHeight,
                    metadata.pageVersion,
                    metadata.detail,
                    metadata.chunks,
                    visionUnavailable = true,
                    visionUnavailableReason =
                        "no screenshot publisher is wired in this deployment: metadata only, the image bytes are not sent to the model",
                });
            }

            var published = await screenshotPublisher.PublishAsync(
                new BrowserScreenshotPublishRequest(
                    context.WorkspaceId,
                    capture.Value.Metadata.ObservationId,
                    capture.Value.Metadata.MimeType,
                    capture.Value.Metadata.ByteCount,
                    capture.Value.Metadata.OutputWidth,
                    capture.Value.Metadata.OutputHeight,
                    DesktopScreenshotDetailWire.NameOf(capture.Value.Metadata.Detail),
                    [.. capture.Value.Chunks.Select(chunk =>
                        new BrowserScreenshotPublishChunk(chunk.Index, chunk.IsLast, chunk.Data))]),
                ct);

            // §7.1：native image part 用**工件 id**（不是把 base64 塞进 JSON 文本）。
            return ToolExecutionResult.OkWithParts(summaryJson,
                [new LlmImagePart(published.ArtifactId, metadata.detail)]);
        }
        catch (ArgumentException ex)
        {
            return BrowserToolResponse.Failure(
                "browser_invalid_arguments", $"screenshot arguments are not usable ({ex.ParamName})");
        }
    }

    /// <summary>
    /// §7.2：**分块完整性必须在这里先判**（顺序、末块、累计长度与元数据一致）——
    /// 不完整就明确失败，而不是把残缺字节当"成功截图"交给视觉通道。
    /// 返回 <c>null</c> 表示通过；否则返回具体原因。
    /// </summary>
    private static string? VerifyChunkAssembly(BrowserScreenshotResult capture)
    {
        long total = 0;
        var expectedIndex = 0;

        foreach (var chunk in capture.Chunks)
        {
            if (chunk.Index != expectedIndex)
            {
                return $"screenshot chunks are out of order: expected index {expectedIndex}, got {chunk.Index}";
            }

            expectedIndex++;
            total += chunk.Data.Length;
        }

        if (capture.Chunks.Count == 0)
        {
            return "screenshot returned no chunks";
        }

        if (!capture.Chunks[^1].IsLast)
        {
            return "the last screenshot chunk is not marked IsLast: the capture is incomplete";
        }

        if (total != capture.Metadata.ByteCount)
        {
            return $"screenshot byte count mismatch: metadata says {capture.Metadata.ByteCount}, chunks carry {total}";
        }

        return null;
    }
}
