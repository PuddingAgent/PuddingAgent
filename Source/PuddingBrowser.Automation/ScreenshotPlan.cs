using System.Text.Json;

namespace PuddingBrowser.Automation;

/// <summary>
/// 截图的 **CDP 参数构造与结果解码**（设计方案 §6 的视觉观察）。
///
/// 抽成纯函数的原因：真正会出错的地方不是"调 CDP"，而是参数语义——
/// ① 有损格式才有 <c>quality</c>：给 PNG 传 quality 是无效输入，必须**明确拒绝**而不是忽略；
/// ② 整页截图必须用**内容尺寸**做 clip 并 <c>captureBeyondViewport</c>，否则拿到的只是当前视口
///    （调用方以为拿到了整页——这正是"看起来对"的典型失败）；
/// ③ 返回体是 base64，缺 <c>data</c> 字段要明确报错，不能解出 0 字节还当成功。
/// </summary>
public static class ScreenshotPlan
{
    /// <summary>Capture a planned viewport/region, translating CSS viewport coordinates to CDP document DIPs.</summary>
    public static string BuildForObservation(BrowserScreenshotPlan plan, ScreenshotLayoutFacts facts, string format)
    {
        if (format is not (PngFormat or JpegFormat))
            throw new ArgumentException("Screenshot format must be png or jpeg.", nameof(format));
        var sourceEdge = Math.Max(plan.CropWidth, plan.CropHeight) * facts.DeviceScaleFactor;
        var scale = plan.Detail == BrowserScreenshotDetail.Low
            ? Math.Min(1, BrowserScreenshotTransfer.LowDetailLongestEdge / sourceEdge)
            : 1;
        var payload = new Dictionary<string, object?>
        {
            ["format"] = format,
            ["fromSurface"] = true,
            ["captureBeyondViewport"] = false,
            ["clip"] = new
            {
                x = (facts.ScrollX + plan.CropX) * facts.PageZoomFactor,
                y = (facts.ScrollY + plan.CropY) * facts.PageZoomFactor,
                width = plan.CropWidth * facts.PageZoomFactor,
                height = plan.CropHeight * facts.PageZoomFactor,
                scale,
            },
        };
        if (format == JpegFormat) payload["quality"] = 80;
        return JsonSerializer.Serialize(payload);
    }

    /// <summary>PNG（无损，无 quality）。</summary>
    public const string PngFormat = "png";

    /// <summary>JPEG（有损，带 quality）。</summary>
    public const string JpegFormat = "jpeg";

    /// <summary>CDP <c>Page.getLayoutMetrics</c> 返回里的内容尺寸（CSS 像素）。</summary>
    public readonly record struct ContentSize(double X, double Y, double Width, double Height);

    /// <summary>
    /// 构造 <c>Page.captureScreenshot</c> 的参数。
    /// <paramref name="fullPage"/> 为真时必须给出 <paramref name="contentSize"/>（来自布局指标）——
    /// 拿不到就不要假装整页。
    /// </summary>
    public static string Build(
        int? quality = null,
        bool fullPage = false,
        ContentSize? contentSize = null)
    {
        if (quality is { } value and (< 0 or > 100))
        {
            throw new ArgumentOutOfRangeException(
                nameof(quality), value, "screenshot quality must be between 0 and 100");
        }

        if (fullPage && contentSize is null)
        {
            throw new ArgumentException(
                "full-page capture needs the content size from layout metrics; refusing to guess it",
                nameof(contentSize));
        }

        var payload = new Dictionary<string, object?>
        {
            // quality 只对有损格式有意义 ⇒ 只在这里决定格式，别让调用方各写一套。
            ["format"] = quality is null ? PngFormat : JpegFormat,
            ["captureBeyondViewport"] = fullPage,
        };

        if (quality is { } jpegQuality)
        {
            payload["quality"] = jpegQuality;
        }

        if (fullPage && contentSize is { } size)
        {
            payload["clip"] = new Dictionary<string, object?>
            {
                ["x"] = size.X,
                ["y"] = size.Y,
                ["width"] = size.Width,
                ["height"] = size.Height,
                ["scale"] = 1,
            };
        }

        return JsonSerializer.Serialize(payload);
    }

    /// <summary>
    /// **元素级截图**（设计方案 §6 的视觉观察）：把某个元素的外接矩形当作 clip。
    ///
    /// 与整页截图的关键差别：整页的 clip 来自布局指标（内容尺寸），元素级来自**元素的盒子**。
    /// 两种"看起来都对"的错误在这里被拒绝：
    /// ① 0×0 或负尺寸的盒子 ⇒ 那不是一个能截的元素（隐藏/未布局），必须拒绝而不是截一张空图；
    /// ② 坐标是**视口坐标**：滚动位置不同会得到不同区域，因此调用方必须用刚观测到的盒子
    ///    （引用过期由元素身份与页面版本负责，本函数不猜）。
    /// </summary>
    public static string BuildForElement(
        double x,
        double y,
        double width,
        double height,
        int? quality = null)
    {
        if (width <= 0 || height <= 0)
        {
            throw new ArgumentException(
                "an element screenshot needs a box with positive width and height"
                + " (a 0x0 box means the element is hidden or not laid out); refusing to capture an empty area",
                nameof(width));
        }

        ContentSize box = new(x, y, width, height);
        return Build(quality, fullPage: true, contentSize: box);
    }

    /// <summary>从元素盒子构造内容尺寸（供 <see cref="BuildForElement"/> 与调用方复用）。</summary>
    public static bool TryReadElementBox(
        JsonElement boundingBox,
        out ContentSize box)
    {
        box = default;
        if (!boundingBox.TryGetProperty("x", out var x)
            || !boundingBox.TryGetProperty("y", out var y)
            || !boundingBox.TryGetProperty("width", out var width)
            || !boundingBox.TryGetProperty("height", out var height))
        {
            return false;
        }

        box = new ContentSize(x.GetDouble(), y.GetDouble(), width.GetDouble(), height.GetDouble());
        return true;
    }

    /// <summary>从 <c>Page.getLayoutMetrics</c> 的返回里取内容尺寸（缺字段即拒绝，不补 0）。</summary>
    public static bool TryReadContentSize(JsonElement layoutMetrics, out ContentSize size)
    {
        size = default;
        if (!layoutMetrics.TryGetProperty("cssContentSize", out var content)
            || !content.TryGetProperty("width", out var width)
            || !content.TryGetProperty("height", out var height))
        {
            return false;
        }

        size = new ContentSize(
            content.TryGetProperty("x", out var x) ? x.GetDouble() : 0,
            content.TryGetProperty("y", out var y) ? y.GetDouble() : 0,
            width.GetDouble(),
            height.GetDouble());
        return true;
    }

    /// <summary>解码 <c>Page.captureScreenshot</c> 的返回：缺 <c>data</c> 或不是合法 base64 都明确失败。</summary>
    public static byte[] Decode(JsonElement captureResult)
    {
        if (!captureResult.TryGetProperty("data", out var data)
            || data.ValueKind != JsonValueKind.String
            || data.GetString() is not { Length: > 0 } base64)
        {
            throw new InvalidOperationException("the browser returned no screenshot payload");
        }

        return Convert.FromBase64String(base64);
    }

    /// <summary>截图的字节预算判定结果。</summary>
    public enum BudgetVerdict
    {
        /// <summary>没设预算，或没超。</summary>
        Within,

        /// <summary>超了预算：**不能**截断图片（半张图比没有更危险），只能明确报资源耗尽。</summary>
        Exceeded,
    }

    /// <summary>
    /// 字节预算判定。截图与文本不同：**不能**「截断到预算内」（半张图会让读者得出错误结论），
    /// 因此超了就如实报资源耗尽，由调用方决定降画质/改视口重拍，还是提高预算。
    /// </summary>
    public static BudgetVerdict CheckBudget(int byteCount, int? maxBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(byteCount);

        if (maxBytes is not { } limit)
        {
            return BudgetVerdict.Within;
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        return byteCount <= limit ? BudgetVerdict.Within : BudgetVerdict.Exceeded;
    }

    /// <summary>超预算时的说明（不含图片内容，只说事实与可行做法）。</summary>
    public static string DescribeBudget(int byteCount, int maxBytes) =>
        $"screenshot is {byteCount} bytes, over the {maxBytes}-byte budget;"
        + " lower the quality, capture the viewport instead of the full page, or raise the budget"
        + " (a screenshot is never truncated to fit: half an image invites wrong conclusions)";

    // ── 低细节档的**质量阶梯**（detail=low 的 512px/40 KiB 承诺必须可满足） ──
    //
    // 存在的问题（2026-10-26 实测）：quality 此前固定 80，而 512px 已是硬上限 ⇒ 密集页面
    // （抖音搜索结果 43502 B、区域截图 56061 B）**必然**撞 40 KiB 预算，detail=low 在真实页面上
    // 有一半时间拿不到图。预算判据本身是对的，错在"固定画质 + 硬预算"这对组合不可能总成立。
    //
    // 做法：画质按**结果**选，而不是按常量选——从调用方给的质量开始，超了就降一档重拍，
    // 第一个满足预算的胜出。到底仍超就交回最小的一次并由调用方如实报 budget_exceeded，
    // 仍然不截断、不冒充可读。

    /// <summary>阶梯下限：不再往下压（再低就只剩色块，宣称"可读"就是撒谎）。</summary>
    public const int MinimumQuality = 38;

    /// <summary>降压候选档（只取比起点小的那些，因此起点越高可选档越多）。</summary>
    private static readonly int[] Ladder = [80, 65, 50, MinimumQuality];

    /// <summary>
    /// 从 <paramref name="start"/> 开始的质量阶梯（含起点）。
    /// 只降不升：调用方要求 q50 时不会先偷偷用 q80 拍一张。
    /// </summary>
    public static IReadOnlyList<int> QualitySteps(int start)
    {
        if (start is < 1 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(start), start, "screenshot quality must be between 1 and 100");
        }

        List<int> steps = [start];
        foreach (var candidate in Ladder)
        {
            if (candidate < start)
            {
                steps.Add(candidate);
            }
        }

        return steps;
    }

    /// <summary>一次受预算约束的拍摄结果（用于把"试了几档、最后多小"如实带回去）。</summary>
    public readonly record struct BudgetedCapture(
        byte[] Bytes,
        int Quality,
        IReadOnlyList<int> AttemptByteCounts,
        bool WithinBudget);

    /// <summary>
    /// 按阶梯拍摄直到满足预算。<paramref name="maxBytes"/> 为空 ⇒ 只拍第一档（不需要为预算做任何取舍）。
    ///
    /// 抽成接收 <paramref name="capture"/> 委托的形式，而不是直接调 CDP：这样"第几档能过""全过不了怎么交回"
    /// 这些**真正的判断**可以脱离 WebView2 单测，执行层只剩"把 quality 发给浏览器"。
    /// </summary>
    public static async Task<BudgetedCapture> CaptureWithinBudgetAsync(
        IReadOnlyList<int> steps,
        int? maxBytes,
        Func<int, CancellationToken, Task<byte[]>> capture,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(steps);
        ArgumentNullException.ThrowIfNull(capture);
        if (steps.Count == 0)
        {
            throw new ArgumentException("at least one quality step is required", nameof(steps));
        }

        List<int> attempts = [];
        byte[]? smallest = null;
        var smallestQuality = steps[0];

        foreach (var quality in steps)
        {
            var bytes = await capture(quality, ct).ConfigureAwait(false)
                ?? throw new InvalidOperationException("the screenshot capture returned no bytes");

            attempts.Add(bytes.Length);
            if (smallest is null || bytes.Length < smallest.Length)
            {
                smallest = bytes;
                smallestQuality = quality;
            }

            if (maxBytes is not { } limit || bytes.Length <= limit)
            {
                return new BudgetedCapture(bytes, quality, attempts, WithinBudget: true);
            }
        }

        // 全部超预算：交回**最小**的一次，由调用方如实报错。降到底仍超说明这个画面装不进预算，
        // 调用方应改局部裁剪或提高预算——继续压只会得到一张看不清的图并误导读图的人。
        return new BudgetedCapture(smallest!, smallestQuality, attempts, WithinBudget: false);
    }
}
