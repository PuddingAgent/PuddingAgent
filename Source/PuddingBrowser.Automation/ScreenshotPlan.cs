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
}
