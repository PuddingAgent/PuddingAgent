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

    [TestMethod]
    public void ImageAndCssCoordinatesRoundTripAcrossAGridOfGeometries()
    {
        // 属性式用例（round-144）：手挑的几组点只能覆盖"我想到的情况"。
        // 这里对**组合网格**做往返：正向（CSS→图片像素）之后再走被测变换（图片→CSS），必须回到原点。
        // 缺哪一步（少除设备缩放、少减滚动、漏乘低清比、裁剪符号反）都会在这里成片变红。
        var geometries = new (int Source, int Output, double Scale, double CropX, double CropY, double ScrollX, double ScrollY)[]
        {
            (1280, 512, 1.0, 0, 0, 0, 0),
            (1280, 512, 2.0, 0, 0, 0, 0),
            (1280, 512, 1.5, 100, 200, 0, 0),
            (1920, 768, 1.0, 0, 0, 40, 90),
            (800, 800, 2.0, 25, 35, 10, 15),
            (2560, 512, 1.25, 300, 120, 0, 400),
        };

        var checkedPoints = 0;
        foreach (var (source, output, scale, cropX, cropY, scrollX, scrollY) in geometries)
        {
            var downscale = (double)output / source;
            var observation = new BrowserScreenshotMetadata(
                ObservationId, "image/png", 32 * 1024,
                source, source, output, source * output / source,
                scale,
                1280, 800, scrollX, scrollY,
                cropX, cropY, source / scale, source / scale,
                5, 9, 2,
                BrowserScreenshotDetail.Low);

            for (var step = 1; step <= 4; step++)
            {
                var cssX = cropX + (step * 37);
                var cssY = cropY + (step * 23);

                // 正向：CSS viewport → 图片像素（测试自己实现，用来喂被测变换）。
                var imageX = (cssX + scrollX - cropX) * scale * downscale;
                var imageY = (cssY + scrollY - cropY) * scale * downscale;

                if (imageX < 0 || imageY < 0 || imageX > output || imageY > source * output / source)
                {
                    continue;
                }

                Assert.IsTrue(
                    BrowserScreenshotCoordinates.TryToCssViewport(
                        observation, ObservationId, imageX, imageY, Current(), out var target, out var rejection, out var reason),
                    $"几何 {source}/{output}/DPR{scale}/crop({cropX},{cropY})/scroll({scrollX},{scrollY}) 点 ({imageX},{imageY})：{rejection} {reason}");

                Assert.AreEqual(cssX, target!.CssX, 0.001, $"往返 X 不一致（几何 {source}/{output}/DPR{scale}）");
                Assert.AreEqual(cssY, target.CssY, 0.001, $"往返 Y 不一致（几何 {source}/{output}/DPR{scale}）");
                checkedPoints++;
            }
        }

        Assert.IsTrue(checkedPoints >= 12, $"实际只校验了 {checkedPoints} 个点，网格写坏了？");
    }

    [TestMethod]
    public void TheRoundTripPropertyHasTeethAgainstAWrongForwardTransform()
    {
        // 防止"属性用例假绿"：故意漏掉设备缩放的正向变换，在 DPR≠1 时**必须**往返失败。
        var source = 1280;
        var output = 512;
        var scale = 2.0;
        var downscale = (double)output / source;
        var observation = Observation(sourceWidth: source, sourceHeight: source, outputWidth: output, outputHeight: source * output / source, deviceScale: scale);

        const double cssX = 300;
        // 错误的正向变换：把设备缩放漏了（少乘 scale）。
        var wrongImageX = (cssX - 0) * downscale;

        Assert.IsTrue(
            BrowserScreenshotCoordinates.TryToCssViewport(
                observation, ObservationId, wrongImageX, 0, Current(), out var target, out _, out _));
        Assert.AreNotEqual(cssX, target!.CssX, 0.001, "若这条成立，说明属性用例对'漏乘设备缩放'没有牙齿");
    }

    [TestMethod]
    public void OneImagePixelAlwaysMovesTheSameAmountInCssPixels()
    {
        // 线性与步长：图片 X 每 +1，CSS X 必须正好 +1/(downscale*deviceScale)。
        var observation = Observation(sourceWidth: 1280, sourceHeight: 800, outputWidth: 512, outputHeight: 320, deviceScale: 2.0);
        var expectedStep = 1d / ((512d / 1280d) * 2.0);

        Assert.IsTrue(BrowserScreenshotCoordinates.TryToCssViewport(observation, ObservationId, 100, 100, Current(), out var first, out _, out _));
        Assert.IsTrue(BrowserScreenshotCoordinates.TryToCssViewport(observation, ObservationId, 101, 100, Current(), out var second, out _, out _));
        Assert.AreEqual(expectedStep, second!.CssX - first!.CssX, 0.0001);
    }

    [TestMethod]
    public void PointsJustOutsideTheCropAreRefusedRatherThanClampedIntoRange()
    {
        // 边界外一点点也必须拒绝：夹到边界会让"点在图片外"变成"点在边缘上"，操作就落到了错误的元素上。
        var observation = Observation(sourceWidth: 1280, sourceHeight: 800, outputWidth: 512, outputHeight: 320);
        foreach (var (x, y) in new[] { (-1.0, 10.0), (513.0, 10.0), (10.0, -1.0), (10.0, 321.0) })
        {
            Assert.IsFalse(
                BrowserScreenshotCoordinates.TryToCssViewport(observation, ObservationId, x, y, Current(), out _, out var rejection, out _),
                $"({x},{y}) 应被拒");
            Assert.AreEqual(BrowserCoordinateRejection.OutOfImageBounds, rejection);
        }
    }
}