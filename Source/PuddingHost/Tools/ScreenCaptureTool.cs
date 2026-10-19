using System.Security.Cryptography;
using System.Text.Json;
using PuddingCode.Core;
using PuddingCode.Models;
using PuddingCode.Tools;
using PuddingHost.ScreenCapture;
using PuddingPlatform.Services;

namespace PuddingAgent.Tools;

/// <summary>
/// 屏幕采集工具（Agent 自用）：枚举显示器与窗口、按显示器 / 区域 / 窗口三种口径截图，
/// 并把结果落成 workspace 内的 Vision Artifact，以**原生图像部件**交给模型。
///
/// <para>与 <c>browser_screenshot</c> 的分工：那个只能看浏览器标签页内部；
/// 这个看的是**整台机器的桌面**（含 Pudding 自己的界面、其它应用、多显示器布局）。</para>
///
/// <para>不变量：坐标为物理像素；区域越界**明确拒绝或明确回报裁剪**而不是静默变卦；
/// 多窗口命中回报命中数量；编码失败/超限**不谎报成功**。</para>
/// </summary>
[Tool(
    id: "screen_capture",
    name: "Screen capture",
    description:
        "Capture the desktop: list monitors and windows (action=displays), or capture one monitor, an explicit region, " +
        "a named window, or the whole virtual desktop (displayIndex=-1). Coordinates are PHYSICAL pixels in the " +
        "virtual-desktop space reported by action=displays. Returns an artifact id usable as an image reference.",
    category: ToolCategory.General,
    permission: ToolPermissionLevel.Medium,
    safety: ToolSafetyFlags.ReadOnly | ToolSafetyFlags.ConcurrencySafe,
    SortOrder = 19)]
public sealed class ScreenCaptureTool(
    IScreenCaptureBackend backend,
    IServiceProvider services,
    ILogger<ScreenCaptureTool> logger)
    : PuddingToolBase<ScreenCaptureArgs>
{
    private const int MaxWindowsForListing = 40;

    private static readonly JsonSerializerOptions JsonOptions = ScreenCaptureWire.JsonOptions;

    protected override async Task<ToolExecutionResult> ExecuteCoreAsync(
        ScreenCaptureArgs args,
        ToolExecutionContext context,
        CancellationToken ct)
    {
        var action = string.IsNullOrWhiteSpace(args.Action) ? "capture" : args.Action!.Trim().ToLowerInvariant();
        if (action is not ("capture" or "displays"))
        {
            return ToolExecutionResult.Fail("action must be 'capture' or 'displays'.");
        }

        try
        {
            var displays = backend.GetDisplays();

            if (action == "displays")
            {
                return ToolExecutionResult.Ok(JsonSerializer.Serialize(
                    new
                    {
                        action,
                        coordinateSpace = "physical pixels, origin at the virtual desktop's top-left corner",
                        virtualDesktop = ScreenCaptureGeometry.GetVirtualDesktop(displays),
                        displays,
                        windows = ScreenCaptureWire.ProjectWindows(
                            backend.GetWindows(Math.Clamp(args.MaxWindows ?? 15, 1, MaxWindowsForListing))),
                    },
                    JsonOptions));
            }

            var plan = Plan(args, displays, out var planError);
            if (plan is null)
            {
                return ToolExecutionResult.Fail(planError!);
            }

            var format = string.IsNullOrWhiteSpace(args.Format) ? "png" : args.Format!.Trim().ToLowerInvariant();
            if (!ScreenCaptureEncoder.SupportedFormats.Contains(format) && format != "jpg")
            {
                return ToolExecutionResult.Fail("format must be png or jpeg.");
            }

            var image = plan.Window is { } window
                ? backend.CaptureWindow(window.Handle, window.Bounds)
                : backend.Capture(plan.Rect);

            var bytes = ScreenCaptureEncoder.Encode(image, format);
            var mimeType = format == "png" ? "image/png" : "image/jpeg";

            if (bytes.LongLength > VisionImageInspector.MaxCanonicalImageBytes)
            {
                return ToolExecutionResult.Fail(
                    $"captured image is {bytes.LongLength} bytes, above the {VisionImageInspector.MaxCanonicalImageBytes}-byte " +
                    "canonical limit; narrow the region, pick a single monitor, or use format=jpeg.");
            }

            var artifactId = await PublishAsync(bytes, mimeType, context.WorkspaceId, ct);

            var summary = JsonSerializer.Serialize(
                new
                {
                    action,
                    scope = plan.Scope,
                    displayIndex = plan.Display.Index,
                    displayBounds = plan.Display.Bounds,
                    capturedRect = plan.Rect,
                    imageWidth = image.Width,
                    imageHeight = image.Height,
                    mimeType,
                    byteCount = bytes.LongLength,
                    artifactId,
                    windowMatchCount = plan.WindowMatchCount,
                    notes = plan.Notes,
                    visionUnavailable = artifactId is null
                        ? "artifact storage is not registered in this host; metadata only"
                        : null,
                },
                JsonOptions);

            // 落盘通道不可用时不谎报"图片已可见"：元数据照给，但不附图像部件。
            return artifactId is null
                ? ToolExecutionResult.Ok(summary)
                : ToolExecutionResult.OkWithParts(summary, [new LlmImagePart(artifactId, "original")]);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "[ScreenCaptureTool] capture failed action={Action}", action);
            return ToolExecutionResult.Fail($"screen capture failed: {ex.Message}");
        }
    }

    /// <summary>
    /// 把参数解析成一次确定的采集计划。口径优先级：**窗口 &gt; 显式区域 &gt; 显示器**。
    /// 判定顺序写死在这里，避免"同时给了窗口和区域"时静默二选一。
    /// </summary>
    private CapturePlan? Plan(ScreenCaptureArgs args, IReadOnlyList<ScreenDisplay> displays, out string? error)
    {
        if (!string.IsNullOrWhiteSpace(args.WindowTitle) || !string.IsNullOrWhiteSpace(args.WindowHandle))
        {
            if (!ScreenCaptureGeometry.TryMatchWindow(
                    backend.GetWindows(MaxWindowsForListing),
                    args.WindowTitle,
                    args.WindowHandle,
                    out var window,
                    out var matchCount,
                    out var matchError))
            {
                error = matchError;
                return null;
            }

            var notes = matchCount > 1
                ? new[] { $"{matchCount} windows matched; captured the largest visible one (\"{window.Title}\")" }
                : Array.Empty<string>();

            error = null;
            return new CapturePlan("window", DisplayContaining(displays, window.Bounds), window.Bounds, window, matchCount, notes);
        }

        if (ScreenCaptureGeometry.TryResolveRegion(args.X, args.Y, args.Width, args.Height, out var requested, out var regionError))
        {
            var virtualDesktop = ScreenCaptureGeometry.GetVirtualDesktop(displays);
            var clamped = requested.Intersect(virtualDesktop);
            if (clamped.IsEmpty)
            {
                error = $"region {requested} does not intersect the virtual desktop {virtualDesktop}";
                return null;
            }

            var notes = clamped.Equals(requested)
                ? Array.Empty<string>()
                : new[] { $"region was clamped from {requested} to {clamped} (virtual desktop {virtualDesktop})" };

            error = null;
            return new CapturePlan("region", DisplayContaining(displays, clamped), clamped, null, 0, notes);
        }

        if (regionError is not null)
        {
            error = regionError;
            return null;
        }

        if (!ScreenCaptureGeometry.TrySelectDisplay(displays, args.DisplayIndex, out var display, out var displayError))
        {
            error = displayError;
            return null;
        }

        error = null;
        return new CapturePlan(
            display.Index == ScreenCaptureGeometry.VirtualDesktopIndex ? "virtual-desktop" : "display",
            display,
            display.Bounds,
            null,
            0,
            Array.Empty<string>());
    }

    /// <summary>找到与目标矩形交集最大的显示器（矩形跨屏时给出"主要落点"）。</summary>
    private static ScreenDisplay DisplayContaining(IReadOnlyList<ScreenDisplay> displays, PixelRect rect)
    {
        if (displays.Count == 0)
        {
            return new ScreenDisplay(-1, "unknown", rect, false);
        }

        var best = displays[0];
        var bestArea = -1L;
        foreach (var display in displays)
        {
            var overlap = display.Bounds.Intersect(rect).Area;
            if (overlap > bestArea)
            {
                best = display;
                bestArea = overlap;
            }
        }

        return best;
    }

    private async Task<string?> PublishAsync(byte[] bytes, string mimeType, string workspaceId, CancellationToken ct)
    {
        if (services.GetService(typeof(VisionArtifactStorageService)) is not VisionArtifactStorageService storage)
        {
            return null;
        }

        using var memory = new MemoryStream(bytes, writable: false);
        var id = "vision-" + Convert.ToHexString(await SHA256.HashDataAsync(memory, ct)).ToLowerInvariant()[..32];
        memory.Position = 0;

        await storage.SaveIdempotentAsync(workspaceId, id, memory, mimeType, ct: ct).ConfigureAwait(false);
        return id;
    }

    private sealed record CapturePlan(
        string Scope,
        ScreenDisplay Display,
        PixelRect Rect,
        ScreenWindow? Window,
        int WindowMatchCount,
        IReadOnlyList<string> Notes);
}

/// <summary>屏幕采集参数。除 <c>action</c> 外全部可选，具体组合决定采集口径。</summary>
public sealed record ScreenCaptureArgs
{
    [ToolParam("'capture' (default) or 'displays' (list monitors and top windows; captures nothing).")]
    public string? Action { get; init; }

    [ToolParam("Monitor index from action=displays (0-based). Use -1 for the entire virtual desktop. Default: the primary monitor.")]
    public int? DisplayIndex { get; init; }

    [ToolParam("Region left edge in physical virtual-desktop pixels. Requires y/width/height.")]
    public int? X { get; init; }

    [ToolParam("Region top edge in physical virtual-desktop pixels.")]
    public int? Y { get; init; }

    [ToolParam("Region width in physical pixels (positive).")]
    public int? Width { get; init; }

    [ToolParam("Region height in physical pixels (positive).")]
    public int? Height { get; init; }

    [ToolParam("Capture the first window whose title contains this text (case-insensitive).")]
    public string? WindowTitle { get; init; }

    [ToolParam("Capture this exact window handle (decimal or 0x-prefixed hex), as reported by action=displays.")]
    public string? WindowHandle { get; init; }

    [ToolParam("Image format: png (default) or jpeg.")]
    public string? Format { get; init; }

    [ToolParam("For action=displays: how many windows to list (1-40, default 15).")]
    public int? MaxWindows { get; init; }
}
