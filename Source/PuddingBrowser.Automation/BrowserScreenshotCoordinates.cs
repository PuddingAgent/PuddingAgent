namespace PuddingBrowser.Automation;

/// <summary>
/// 页面当前的"世代"三元组（设计 §8.2：滚动/缩放/布局或页面世代改变时**拒绝旧坐标**，要求重新观察）。
/// </summary>
public sealed record BrowserPageGenerations(long PageVersion, long DomRevision, long ViewportRevision);

/// <summary>坐标被拒的**明确原因**（绝不"就近取一个点"或按旧坐标硬点）。</summary>
public enum BrowserCoordinateRejection
{
    None,
    UnknownObservation,
    StaleObservation,
    OutOfImageBounds,
    OutsideViewport,
    InvalidObservationGeometry,
}

/// <summary>换算结果：CSS viewport 像素（设计 §8.2 规定坐标统一为 CSS viewport 像素）。</summary>
public sealed record BrowserCoordinateTarget(string ObservationId, double CssX, double CssY);

/// <summary>
/// **截图坐标 → CSS viewport 的精确变换（桌面一侧）**，来自设计 §8.2：
///
/// <list type="bullet">
///   <item><c>browser_interact</c> 的坐标统一为 **CSS viewport 像素**；</item>
///   <item>截图坐标输入**必须携带截屏 observationId**，由驱动用**精确变换**转换；</item>
///   <item>滚动/缩放/布局或**页面世代**改变时**拒绝旧坐标**，要求重新观察；</item>
///   <item>坐标仍受视口边界约束（不能越过工作台隔离）。</item>
/// </list>
///
/// 变换链（每一步都必须有据可依，缺一即拒）：
/// 图片像素 → 去低清缩放 → 设备像素 → 除设备缩放得 CSS 像素 → 加裁剪原点 → 减滚动量 = CSS viewport 像素。
/// </summary>
public static class BrowserScreenshotCoordinates
{
    /// <summary>浮点比较容差（像素级；太小会因 JPEG/缩放取整被误拒，太大就成了"就近取点"）。</summary>
    public const double Tolerance = 0.5;

    /// <summary>
    /// 把截图上的点换算成 CSS viewport 像素。任何不满足前提的情况**明确拒绝并给出原因**。
    /// </summary>
    public static bool TryToCssViewport(
        BrowserScreenshotMetadata? observation,
        string? requestedObservationId,
        double imageX,
        double imageY,
        BrowserPageGenerations? current,
        out BrowserCoordinateTarget? target,
        out BrowserCoordinateRejection rejection,
        out string reason)
    {
        target = null;

        if (observation is null)
        {
            rejection = BrowserCoordinateRejection.UnknownObservation;
            reason = "no screenshot observation is known for the requested observation id";
            return false;
        }

        if (string.IsNullOrWhiteSpace(requestedObservationId)
            || !string.Equals(requestedObservationId, observation.ObservationId, StringComparison.Ordinal))
        {
            // 用 A 张图的坐标去点 B 张图对应的界面 —— 必须拒绝，不能"猜一个"。
            rejection = BrowserCoordinateRejection.UnknownObservation;
            reason = string.IsNullOrWhiteSpace(requestedObservationId)
                ? "coordinate input must name the screenshot observation it came from"
                : $"coordinate input names observation '{requestedObservationId}'"
                    + $" but the known observation is '{observation.ObservationId}'";
            return false;
        }

        if (current is null)
        {
            rejection = BrowserCoordinateRejection.UnknownObservation;
            reason = "the current page generations must be provided to check whether the coordinates are stale";
            return false;
        }

        if (current.PageVersion != observation.PageVersion
            || current.DomRevision != observation.DomRevision
            || current.ViewportRevision != observation.ViewportRevision)
        {
            // §8.2：滚动/缩放/布局或世代改变 ⇒ 旧坐标失效。
            rejection = BrowserCoordinateRejection.StaleObservation;
            reason = "the page changed after the screenshot was taken"
                + $" (observed {observation.PageVersion}/{observation.DomRevision}/{observation.ViewportRevision},"
                + $" now {current.PageVersion}/{current.DomRevision}/{current.ViewportRevision});"
                + " take a new screenshot and use its observation id";
            return false;
        }

        if (observation.DeviceScaleFactor is not > 0
            || observation.ViewportWidth is not > 0
            || observation.ViewportHeight is not > 0
            || observation.ScrollX is null
            || observation.ScrollY is null
            || observation.CropX is null
            || observation.CropY is null
            || observation.OutputWidth <= 0
            || observation.OutputHeight <= 0
            || observation.SourceWidth <= 0
            || observation.SourceHeight <= 0)
        {
            // 几何信息不全时**算不出**精确坐标：拒绝，绝不假设。
            rejection = BrowserCoordinateRejection.InvalidObservationGeometry;
            reason = "the screenshot observation is missing the geometry needed for an exact transform";
            return false;
        }

        if (imageX < -Tolerance || imageY < -Tolerance
            || imageX > observation.OutputWidth + Tolerance
            || imageY > observation.OutputHeight + Tolerance)
        {
            rejection = BrowserCoordinateRejection.OutOfImageBounds;
            reason = $"point ({imageX}, {imageY}) is outside the screenshot image"
                + $" ({observation.OutputWidth}x{observation.OutputHeight})";
            return false;
        }

        // ① 去低清缩放：输出图相对源图的缩放比。
        var downscale = (double)observation.OutputWidth / observation.SourceWidth;
        var deviceX = imageX / downscale;
        var deviceY = imageY / downscale;

        // ② 设备像素 → CSS 像素。
        var scale = observation.DeviceScaleFactor.Value;
        var cssInCropX = deviceX / scale;
        var cssInCropY = deviceY / scale;

        // ③ 加裁剪原点、④ 减滚动量 ⇒ CSS viewport 像素。
        var cssX = observation.CropX.Value + cssInCropX - observation.ScrollX.Value;
        var cssY = observation.CropY.Value + cssInCropY - observation.ScrollY.Value;

        if (cssX < -Tolerance || cssY < -Tolerance
            || cssX > observation.ViewportWidth.Value + Tolerance
            || cssY > observation.ViewportHeight.Value + Tolerance)
        {
            // 视口边界约束（§8.2）：换算结果落在视口外 ⇒ 拒绝，而不是交给驱动去点空处。
            rejection = BrowserCoordinateRejection.OutsideViewport;
            reason = $"point maps to CSS viewport ({cssX:0.##}, {cssY:0.##}) which is outside"
                + $" {observation.ViewportWidth:0.##}x{observation.ViewportHeight:0.##}";
            return false;
        }

        target = new BrowserCoordinateTarget(observation.ObservationId, cssX, cssY);
        rejection = BrowserCoordinateRejection.None;
        reason = string.Empty;
        return true;
    }
}
