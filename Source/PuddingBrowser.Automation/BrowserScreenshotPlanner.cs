namespace PuddingBrowser.Automation;

/// <summary>截图范围（设计 §8.1：**先**视口与区域/元素裁剪，全页捕获是后续工作）。</summary>
public enum BrowserScreenshotScope
{
    /// <summary>视口（`CapturePreviewAsync` 路径）。</summary>
    Viewport,

    /// <summary>视口内的区域裁剪（CSS viewport 像素）。</summary>
    Region,

    /// <summary>全页：设计明确"不能冒充 viewport；滚动拼接需单独处理固定元素与动态内容"。</summary>
    FullPage,
}

/// <summary>规划被拒的原因（每一条都对应设计里的一句硬要求）。</summary>
public enum BrowserScreenshotPlanning
{
    None,

    /// <summary>没有 page read grant —— 截屏前必须验证（§8.1）。</summary>
    NoReadGrant,

    /// <summary>页面还没到可采集状态（仍在加载/导航中）—— 否则会返回导航前的画面（§8.1）。</summary>
    PageNotReady,

    /// <summary>目标不是当前文档 —— 截图不切换到未授权页面（§8.1）。</summary>
    TargetDocumentNotCurrent,

    /// <summary>区域超出 CSS 视口（§8.2 的视口边界约束）。</summary>
    RegionOutOfViewport,

    /// <summary>全页截图需要单独处理（滚动拼接/固定元素/动态内容）—— 现阶段**明确不支持**。</summary>
    FullPageNeedsSeparateHandling,

    /// <summary>请求本身不合法（尺寸/区域未上报等），不猜默认值。</summary>
    InvalidIntent,
}

/// <summary>
/// 一次截图的意图（调用方给的**事实**；规划器不做任何假设）。
/// 可空字段表示"未上报"，缺了就拒绝 —— 与仓库的 fail-closed 约定一致。
/// </summary>
public sealed record BrowserScreenshotIntent(
    BrowserScreenshotScope Scope,
    BrowserScreenshotDetail Detail,
    bool HasPageReadGrant,
    bool PageReady,
    bool IsCurrentDocument,
    double? ViewportWidth,
    double? ViewportHeight,
    double? DeviceScaleFactor,
    double? RegionX = null,
    double? RegionY = null,
    double? RegionWidth = null,
    double? RegionHeight = null);

/// <summary>规划结果：要采什么（CSS 像素）、预期输出尺寸与上限、将来必须上报的元数据要点。</summary>
public sealed record BrowserScreenshotPlan(
    BrowserScreenshotScope Scope,
    BrowserScreenshotDetail Detail,
    double CropX,
    double CropY,
    double CropWidth,
    double CropHeight,
    int ExpectedOutputWidth,
    int ExpectedOutputHeight,
    int MaxBytes,
    string ObservationRequirements);

/// <summary>
/// **截图能力的只读投影（设计 §8.1/§8.2）**：把"这一刻能截什么、截多大、不行时为什么"算清楚，
/// 供将来的工具面直接复用。它**不采集**、**不落库**、**不发授权**，只做纯函数判定：
///
/// <list type="bullet">
///   <item>先做视口与区域裁剪，全页**明确不支持**（"不能冒充 viewport"，拼接要单独处理）；</item>
///   <item>截屏前验证 page read grant、当前文档与 readiness；</item>
///   <item>区域必须落在 CSS 视口内；</item>
///   <item>默认 <c>detail=low</c> ⇒ 最长边缩到 ≤512px；high/original 保持设备像素；</item>
///   <item>所有上限/尺寸都来自代码常量（8 MiB、512px），不手写数字。</item>
/// </list>
/// </summary>
public static class BrowserScreenshotPlanner
{
    /// <summary>规划一次截图；任何前提不满足都**明确拒绝并给出原因**。</summary>
    public static bool TryPlan(
        BrowserScreenshotIntent? intent,
        out BrowserScreenshotPlan? plan,
        out BrowserScreenshotPlanning rejection,
        out string reason)
    {
        plan = null;

        if (intent is null)
        {
            rejection = BrowserScreenshotPlanning.InvalidIntent;
            reason = "no screenshot intent was provided";
            return false;
        }

        if (intent.Scope == BrowserScreenshotScope.FullPage)
        {
            // 诚实的不支持：全页不能冒充视口，滚动拼接要单独处理固定元素与动态内容。
            rejection = BrowserScreenshotPlanning.FullPageNeedsSeparateHandling;
            reason = "full-page capture is not supported yet: it must not masquerade as the viewport, and"
                + " scroll stitching needs separate handling for fixed elements and dynamic content;"
                + " capture the viewport or a region instead";
            return false;
        }

        if (!intent.HasPageReadGrant)
        {
            rejection = BrowserScreenshotPlanning.NoReadGrant;
            reason = "screenshot requires a page read grant for the target page";
            return false;
        }

        if (!intent.IsCurrentDocument)
        {
            rejection = BrowserScreenshotPlanning.TargetDocumentNotCurrent;
            reason = "screenshot must be taken on the current document; capturing would switch pages";
            return false;
        }

        if (!intent.PageReady)
        {
            rejection = BrowserScreenshotPlanning.PageNotReady;
            reason = "the page is not ready (still loading or navigating); capturing now would return the"
                + " previous frame";
            return false;
        }

        if (intent.ViewportWidth is not > 0
            || intent.ViewportHeight is not > 0
            || intent.DeviceScaleFactor is not > 0)
        {
            rejection = BrowserScreenshotPlanning.InvalidIntent;
            reason = "the CSS viewport size and device scale factor must be reported to plan a capture";
            return false;
        }

        double cropX;
        double cropY;
        double cropWidth;
        double cropHeight;

        if (intent.Scope == BrowserScreenshotScope.Viewport)
        {
            cropX = 0;
            cropY = 0;
            cropWidth = intent.ViewportWidth.Value;
            cropHeight = intent.ViewportHeight.Value;
        }
        else
        {
            if (intent.RegionX is null || intent.RegionY is null
                || intent.RegionWidth is not > 0 || intent.RegionHeight is not > 0)
            {
                rejection = BrowserScreenshotPlanning.InvalidIntent;
                reason = "a region capture must report x/y/width/height in CSS viewport pixels";
                return false;
            }

            cropX = intent.RegionX.Value;
            cropY = intent.RegionY.Value;
            cropWidth = intent.RegionWidth.Value;
            cropHeight = intent.RegionHeight.Value;

            if (cropX < 0 || cropY < 0
                || cropX + cropWidth > intent.ViewportWidth.Value + BrowserScreenshotCoordinates.Tolerance
                || cropY + cropHeight > intent.ViewportHeight.Value + BrowserScreenshotCoordinates.Tolerance)
            {
                // §8.2：坐标操作受视口边界约束；越界就拒绝，绝不裁一半。
                rejection = BrowserScreenshotPlanning.RegionOutOfViewport;
                reason = $"region ({cropX}, {cropY}, {cropWidth}x{cropHeight}) is not inside the CSS viewport"
                    + $" {intent.ViewportWidth}x{intent.ViewportHeight}";
                return false;
            }
        }

        // 源（设备像素）尺寸 = CSS 尺寸 × 设备缩放。
        var sourceWidth = cropWidth * intent.DeviceScaleFactor.Value;
        var sourceHeight = cropHeight * intent.DeviceScaleFactor.Value;

        // detail=low ⇒ 等比例缩到最长边 ≤512px；high/original ⇒ 保持设备像素。
        double outputScale = 1;
        if (intent.Detail == BrowserScreenshotDetail.Low)
        {
            var longestEdge = Math.Max(sourceWidth, sourceHeight);
            if (longestEdge > BrowserScreenshotTransfer.LowDetailLongestEdge)
            {
                outputScale = BrowserScreenshotTransfer.LowDetailLongestEdge / longestEdge;
            }
        }

        var outputWidth = Math.Max(1, (int)Math.Round(sourceWidth * outputScale, MidpointRounding.AwayFromZero));
        var outputHeight = Math.Max(1, (int)Math.Round(sourceHeight * outputScale, MidpointRounding.AwayFromZero));

        plan = new BrowserScreenshotPlan(
            intent.Scope,
            intent.Detail,
            cropX,
            cropY,
            cropWidth,
            cropHeight,
            outputWidth,
            outputHeight,
            BrowserScreenshotTransfer.MaxImageBytes,
            "report source/output size, device scale, CSS viewport, scroll offset, crop transform and"
            + " PageVersion/domRevision/viewportRevision (see BrowserScreenshotTransfer)");

        rejection = BrowserScreenshotPlanning.None;
        reason = string.Empty;
        return true;
    }
}
