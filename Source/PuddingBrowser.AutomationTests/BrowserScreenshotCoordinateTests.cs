using PuddingBrowser.Automation;

namespace PuddingBrowser.AutomationTests;

/// <summary>
/// **截图坐标 → CSS viewport 的精确变换**（设计 §8.2）：坐标必须携带截屏 observationId、
/// 世代变化必须拒绝旧坐标、换算结果必须落在视口内。
/// 这些用例把每一步变换都钉住（低清缩放、设备缩放、裁剪原点、滚动量），
/// 因为**少乘或多减一项**在这里都只会表现为"点偏了"，不会报错。
/// </summary>
[TestClass]
public sealed class BrowserScreenshotCoordinateTests
{
    private const string ObservationId = "obs-42";

    private static BrowserScreenshotMetadata Observation(
        int sourceWidth = 1280,
        int sourceHeight = 800,
        int outputWidth = 512,
        int outputHeight = 320,
        double deviceScale = 1.0,
        double viewportWidth = 1280,
        double viewportHeight = 800,
        double scrollX = 0,
        double scrollY = 0,
        double cropX = 0,
        double cropY = 0,
        long pageVersion = 5,
        long domRevision = 9,
        long viewportRevision = 2) =>
        new(
            ObservationId,
            "image/png",
            64 * 1024,
            sourceWidth,
            sourceHeight,
            outputWidth,
            outputHeight,
            deviceScale,
            viewportWidth,
            viewportHeight,
            scrollX,
            scrollY,
            cropX,
            cropY,
            sourceWidth / deviceScale,
            sourceHeight / deviceScale,
            pageVersion,
            domRevision,
            viewportRevision,
            BrowserScreenshotDetail.Low);

    private static BrowserPageGenerations Current(
        long pageVersion = 5,
        long domRevision = 9,
        long viewportRevision = 2) => new(pageVersion, domRevision, viewportRevision);

    [TestMethod]
    public void APointOnALowDetailScreenshotMapsBackToCssPixels()
    {
        // 源 1280×800、输出 512×320（缩放比 0.4）、设备缩放 1、无裁剪无滚动：
        // 图片上 (256,160) 应回到 CSS (640,400) —— 正好是画面中心。
        Assert.IsTrue(
            BrowserScreenshotCoordinates.TryToCssViewport(
                Observation(), ObservationId, 256, 160, Current(), out var target, out _, out var reason),
            reason);

        Assert.AreEqual(640d, target!.CssX, 0.001);
        Assert.AreEqual(400d, target.CssY, 0.001);
        Assert.AreEqual(ObservationId, target.ObservationId);
    }

    [TestMethod]
    public void DeviceScaleFactorIsDividedOutNotIgnored()
    {
        // 设备缩放 2（Retina）：图片上的设备像素要**除以 2** 才是 CSS 像素。
        // 若漏掉这一步，坐标会整体翻倍 —— 只会"点偏"，不会报错，所以必须钉住。
        var observation = Observation(deviceScale: 2.0);
        Assert.IsTrue(
            BrowserScreenshotCoordinates.TryToCssViewport(
                observation, ObservationId, 256, 160, Current(), out var target, out _, out var reason),
            reason);

        Assert.AreEqual(320d, target!.CssX, 0.001);
        Assert.AreEqual(200d, target.CssY, 0.001);
    }

    [TestMethod]
    public void CropOriginAndScrollOffsetAreBothApplied()
    {
        // 裁剪从文档 (100,200) 开始、页面已向下滚 50：CSS viewport x = 100 + 图片换算 - 0，y = 200 + … - 50。
        var observation = Observation(cropX: 100, cropY: 200, scrollY: 50);
        Assert.IsTrue(
            BrowserScreenshotCoordinates.TryToCssViewport(
                observation, ObservationId, 256, 160, Current(), out var target, out _, out var reason),
            reason);

        Assert.AreEqual(100d + 640d, target!.CssX, 0.001);
        Assert.AreEqual(200d + 400d - 50d, target.CssY, 0.001);
    }

    [TestMethod]
    public void AnyGenerationChangeMakesTheOldCoordinatesStale()
    {
        foreach (var stale in new[]
        {
            Current(pageVersion: 6),
            Current(domRevision: 10),
            Current(viewportRevision: 3),
        })
        {
            Assert.IsFalse(
                BrowserScreenshotCoordinates.TryToCssViewport(
                    Observation(), ObservationId, 256, 160, stale, out var target, out var rejection, out var reason),
                $"世代变化必须拒绝：{stale}");
            Assert.IsNull(target);
            Assert.AreEqual(BrowserCoordinateRejection.StaleObservation, rejection);
            Assert.IsTrue(reason.Contains("new screenshot", StringComparison.Ordinal), reason);
        }
    }

    [TestMethod]
    public void CoordinatesFromAnotherObservationAreRefused()
    {
        // 用另一张图的坐标去点当前界面 ⇒ 拒绝（而不是"照比例凑一个"）。
        Assert.IsFalse(
            BrowserScreenshotCoordinates.TryToCssViewport(
                Observation(), "obs-other", 10, 10, Current(), out _, out var rejection, out var reason));
        Assert.AreEqual(BrowserCoordinateRejection.UnknownObservation, rejection);
        Assert.IsTrue(reason.Contains("obs-other", StringComparison.Ordinal), reason);

        Assert.IsFalse(
            BrowserScreenshotCoordinates.TryToCssViewport(
                Observation(), "  ", 10, 10, Current(), out _, out rejection, out _));
        Assert.AreEqual(BrowserCoordinateRejection.UnknownObservation, rejection);
    }

    [TestMethod]
    public void APointOutsideTheImageOrTheViewportIsRefused()
    {
        // 图片外：y 超出 320。
        Assert.IsFalse(
            BrowserScreenshotCoordinates.TryToCssViewport(
                Observation(), ObservationId, 256, 400, Current(), out _, out var rejection, out _));
        Assert.AreEqual(BrowserCoordinateRejection.OutOfImageBounds, rejection);

        // 视口外：图片点在界内，但换算后落在滚动过头的区域（scrollY 很大 ⇒ CSS y 变负）。
        var scrolled = Observation(scrollY: 900);
        Assert.IsFalse(
            BrowserScreenshotCoordinates.TryToCssViewport(
                scrolled, ObservationId, 256, 160, Current(), out _, out rejection, out var reason));
        Assert.AreEqual(BrowserCoordinateRejection.OutsideViewport, rejection);
        Assert.IsTrue(reason.Contains("outside", StringComparison.Ordinal), reason);
    }

    [TestMethod]
    public void ObservationsWithoutCompleteGeometryCannotBeUsedForCoordinates()
    {
        var incomplete = Observation() with { DeviceScaleFactor = null };
        Assert.IsFalse(
            BrowserScreenshotCoordinates.TryToCssViewport(
                incomplete, ObservationId, 10, 10, Current(), out _, out var rejection, out _));
        Assert.AreEqual(BrowserCoordinateRejection.InvalidObservationGeometry, rejection);

        Assert.IsFalse(
            BrowserScreenshotCoordinates.TryToCssViewport(
                null, ObservationId, 10, 10, Current(), out _, out rejection, out _));
        Assert.AreEqual(BrowserCoordinateRejection.UnknownObservation, rejection);

        Assert.IsFalse(
            BrowserScreenshotCoordinates.TryToCssViewport(
                Observation(), ObservationId, 10, 10, null, out _, out rejection, out _));
        Assert.AreEqual(BrowserCoordinateRejection.UnknownObservation, rejection);
    }

    [TestMethod]
    public void OriginalDetailWithoutDownscaleIsAnIdentityTransform()
    {
        // 输出 = 源尺寸、设备缩放 1、无裁剪无滚动 ⇒ 图片像素就是 CSS 像素。
        var original = Observation(sourceWidth: 512, sourceHeight: 320, outputWidth: 512, outputHeight: 320);
        Assert.IsTrue(
            BrowserScreenshotCoordinates.TryToCssViewport(
                original, ObservationId, 100, 50, Current(), out var target, out _, out var reason),
            reason);

        Assert.AreEqual(100d, target!.CssX, 0.001);
        Assert.AreEqual(50d, target.CssY, 0.001);
    }
}
