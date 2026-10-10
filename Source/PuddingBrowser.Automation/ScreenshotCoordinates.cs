namespace PuddingBrowser.Automation;

/// <summary>
/// §8.2 **截图坐标 → CSS viewport 像素**的纯函数变换。
///
/// 为什么是纯函数：截图坐标系（图像像素，可能带缩放、可能是区域裁剪）与驱动要点的坐标系（CSS viewport 像素）
/// 不同；把它做成纯函数才能**在进程内**用期望值钉住，而不必依赖真机。
///
/// 语义要点：
/// <list type="bullet">
///   <item>图像像素 ÷ <c>zoom</c> 得 CSS 像素（截图可能被缩放）；</item>
///   <item>区域裁剪时加上裁剪原点（CSS 像素）；</item>
///   <item>**不含滚动偏移** —— 目标坐标系是"当前视口"，滚动变化由观察失效规则负责（§8.2 明示）。</item>
/// </list>
/// </summary>
public static class ScreenshotCoordinates
{
    /// <summary>
    /// 把截图图像上的点换算成 **CSS viewport 像素**；越界/非法参数**明确失败**（不猜、不夹取）。
    /// </summary>
    public static (double X, double Y) ToViewport(
        double imageX,
        double imageY,
        double imageWidth,
        double imageHeight,
        double zoom,
        double cropOriginX = 0,
        double cropOriginY = 0)
    {
        if (zoom <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(zoom), zoom, "The screenshot zoom must be positive.");
        }

        if (imageWidth <= 0 || imageHeight <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(imageWidth), (imageWidth, imageHeight), "The screenshot image must have a positive size.");
        }

        if (imageX < 0 || imageY < 0 || imageX > imageWidth || imageY > imageHeight)
        {
            throw new ArgumentOutOfRangeException(
                nameof(imageX),
                (imageX, imageY),
                "The point must lie inside the screenshot image; an outside point cannot be mapped to the page.");
        }

        return (cropOriginX + (imageX / zoom), cropOriginY + (imageY / zoom));
    }
}