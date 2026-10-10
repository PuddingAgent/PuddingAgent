using Microsoft.VisualStudio.TestTools.UnitTesting;
using PuddingBrowser.Automation;

namespace PuddingBrowser.AutomationTests;

/// <summary>§8.2 截图坐标 → CSS viewport 像素：纯函数，逐项钉住缩放、裁剪与越界拒绝。</summary>
[TestClass]
public sealed class ScreenshotCoordinatesTests
{
    [TestMethod]
    public void WithoutZoomOrCropThePointIsUnchanged()
    {
        var (x, y) = ScreenshotCoordinates.ToViewport(320, 240, imageWidth: 1280, imageHeight: 720, zoom: 1);
        Assert.AreEqual(320, x);
        Assert.AreEqual(240, y);
    }

    [TestMethod]
    public void ZoomScalesImagePixelsBackToCssPixels()
    {
        // 2 倍缩放的截图：图像上的 200px 对应 CSS 的 100px。
        var (x, y) = ScreenshotCoordinates.ToViewport(200, 100, imageWidth: 2560, imageHeight: 1440, zoom: 2);
        Assert.AreEqual(100, x);
        Assert.AreEqual(50, y);
    }

    [TestMethod]
    public void CropOriginIsAddedBack()
    {
        // 裁剪自视口 (100, 50) 处的区域，且截图缩放 2 倍。
        var (x, y) = ScreenshotCoordinates.ToViewport(
            imageX: 50, imageY: 30, imageWidth: 400, imageHeight: 200, zoom: 2,
            cropOriginX: 100, cropOriginY: 50);
        Assert.AreEqual(125, x);
        Assert.AreEqual(65, y);
    }

    [TestMethod]
    public void APointOutsideTheImageIsRefused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            ScreenshotCoordinates.ToViewport(1281, 10, imageWidth: 1280, imageHeight: 720, zoom: 1));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            ScreenshotCoordinates.ToViewport(-1, 10, imageWidth: 1280, imageHeight: 720, zoom: 1));
    }

    [TestMethod]
    public void AnIllegalZoomIsRefused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            ScreenshotCoordinates.ToViewport(10, 10, imageWidth: 1280, imageHeight: 720, zoom: 0));
    }
}