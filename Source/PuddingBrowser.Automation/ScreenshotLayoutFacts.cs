using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PuddingBrowser.Automation;

/// <summary>
/// 截图所需的**页面几何事实**（设计 §8.1/§8.2）：视口尺寸、设备缩放、滚动位置、内容尺寸。
/// **缺任一必需字段即拒绝**（不补 0、不猜窗口大小）。
/// </summary>
public sealed record ScreenshotLayoutFacts(
    double ViewportWidth,
    double ViewportHeight,
    double DeviceScaleFactor,
    double ScrollX,
    double ScrollY,
    double ContentWidth,
    double ContentHeight,
    double PageZoomFactor = 1)
{
    /// <summary>
    /// **视口修订**（§8.2 的失效键之一）：上面任一几何事实变化 ⇒ 值变化。
    /// 语义明确说明：它是**几何指纹**，不是"DOM 变更计数"；页内 DOM 变更由 `PageVersion` 覆盖。
    /// </summary>
    public long ViewportRevision => ComputeRevision();

    private long ComputeRevision()
    {
        var canonical = string.Join(
            '|',
            ViewportWidth.ToString("R", CultureInfo.InvariantCulture),
            ViewportHeight.ToString("R", CultureInfo.InvariantCulture),
            DeviceScaleFactor.ToString("R", CultureInfo.InvariantCulture),
            ScrollX.ToString("R", CultureInfo.InvariantCulture),
            ScrollY.ToString("R", CultureInfo.InvariantCulture),
            ContentWidth.ToString("R", CultureInfo.InvariantCulture),
            ContentHeight.ToString("R", CultureInfo.InvariantCulture),
            PageZoomFactor.ToString("R", CultureInfo.InvariantCulture));
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        return BitConverter.ToInt64(hash, 0) & long.MaxValue;
    }
}

/// <summary>从 CDP `Page.getLayoutMetrics` 的返回里读几何事实（缺字段即拒绝）。</summary>
public static class ScreenshotLayoutFactsReader
{
    public static bool TryRead(JsonElement layoutMetrics, double deviceScaleFactor, out ScreenshotLayoutFacts? facts, out string reason)
    {
        facts = null;
        reason = string.Empty;

        // cssVisualViewport.scale is page/pinch scale, NOT devicePixelRatio.
        if (!double.IsFinite(deviceScaleFactor) || deviceScaleFactor <= 0)
        {
            reason = "the device pixel ratio must be a finite positive number";
            return false;
        }

        if (layoutMetrics.ValueKind != JsonValueKind.Object)
        {
            reason = "layout metrics must be an object";
            return false;
        }

        if (!layoutMetrics.TryGetProperty("cssVisualViewport", out var viewport)
            || viewport.ValueKind != JsonValueKind.Object)
        {
            reason = "layout metrics are missing cssVisualViewport";
            return false;
        }

        if (!TryReadNumber(viewport, "clientWidth", out var viewportWidth)
            || !TryReadNumber(viewport, "clientHeight", out var viewportHeight))
        {
            reason = "the visual viewport is missing its client size";
            return false;
        }

        if (viewportWidth <= 0 || viewportHeight <= 0)
        {
            reason = "the visual viewport size must be positive";
            return false;
        }

        // scale 缺省按 1（CDP 在无缩放时可能不返回），但**显式 0 或负数即拒绝**。
        var scale = 1.0;
        if (viewport.TryGetProperty("scale", out var scaleElement))
        {
            if (scaleElement.ValueKind != JsonValueKind.Number || !scaleElement.TryGetDouble(out scale) || scale <= 0)
            {
                reason = "the visual viewport scale must be a positive number";
                return false;
            }
        }

        var zoom = 1.0;
        if (viewport.TryGetProperty("zoom", out var zoomElement)
            && (zoomElement.ValueKind != JsonValueKind.Number
                || !zoomElement.TryGetDouble(out zoom) || !double.IsFinite(zoom) || zoom <= 0))
        {
            reason = "the page zoom must be a finite positive number";
            return false;
        }

        // 滚动位置：缺省视为 0（CDP 在未滚动时可能省略 pageX/pageY）。
        var scrollX = 0.0;
        var scrollY = 0.0;
        if (viewport.TryGetProperty("pageX", out var pageX) && pageX.ValueKind == JsonValueKind.Number)
        {
            pageX.TryGetDouble(out scrollX);
        }

        if (viewport.TryGetProperty("pageY", out var pageY) && pageY.ValueKind == JsonValueKind.Number)
        {
            pageY.TryGetDouble(out scrollY);
        }

        if (!ScreenshotPlan.TryReadContentSize(layoutMetrics, out var contentSize))
        {
            reason = "layout metrics are missing the content size";
            return false;
        }

        facts = new ScreenshotLayoutFacts(
            viewportWidth, viewportHeight, deviceScaleFactor, scrollX, scrollY, contentSize.Width, contentSize.Height, zoom);
        return true;
    }

    private static bool TryReadNumber(JsonElement parent, string name, out double value)
    {
        value = 0;
        return parent.TryGetProperty(name, out var element)
            && element.ValueKind == JsonValueKind.Number
            && element.TryGetDouble(out value);
    }
}
