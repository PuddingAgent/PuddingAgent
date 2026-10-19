using System.Buffers.Binary;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PuddingBrowser.Automation;

namespace PuddingBrowser.AutomationTests;

[TestClass]
public sealed class ScreenshotCaptureContractTests
{
    [DataTestMethod]
    [DataRow("png", 1.0, 1.0)]
    [DataRow("jpeg", 2.0, 1.0)]
    [DataRow("jpeg", 2.5, 1.25)]
    public void PlannedCaptureUsesRequestedEncodingActualDprAndDocumentClip(string format, double dpr, double zoom)
    {
        var facts = new ScreenshotLayoutFacts(1280, 720, dpr, 20, 2000, 2000, 4000, zoom);
        Assert.IsTrue(BrowserScreenshotPlanner.TryPlan(new BrowserScreenshotIntent(
            BrowserScreenshotScope.Region, BrowserScreenshotDetail.Low, true, true, true,
            1280, 720, dpr, 100, 50, 1000, 400), out var plan, out _, out _));
        using var json = JsonDocument.Parse(ScreenshotPlan.BuildForObservation(plan!, facts, format));
        Assert.AreEqual(format, json.RootElement.GetProperty("format").GetString());
        Assert.AreEqual(format == "jpeg", json.RootElement.TryGetProperty("quality", out _));
        var clip = json.RootElement.GetProperty("clip");
        Assert.AreEqual(120 * zoom, clip.GetProperty("x").GetDouble());
        Assert.AreEqual(2050 * zoom, clip.GetProperty("y").GetDouble());
        Assert.AreEqual(1000 * zoom, clip.GetProperty("width").GetDouble());
        Assert.AreEqual(512 / (1000 * dpr), clip.GetProperty("scale").GetDouble(), 0.000001);
        Assert.IsFalse(json.RootElement.GetProperty("captureBeyondViewport").GetBoolean());
    }

    [TestMethod]
    public void DevicePixelRatioIsIndependentOfVisualViewportScale()
    {
        using var metrics = JsonDocument.Parse("""{"cssVisualViewport":{"clientWidth":800,"clientHeight":600,"scale":1,"zoom":1.25},"cssContentSize":{"width":800,"height":2000}}""");
        Assert.IsTrue(ScreenshotLayoutFactsReader.TryRead(metrics.RootElement, 2.5, out var facts, out _));
        Assert.AreEqual(2.5, facts!.DeviceScaleFactor);
        Assert.AreEqual(1.25, facts.PageZoomFactor);
        Assert.IsFalse(ScreenshotLayoutFactsReader.TryRead(metrics.RootElement, double.NaN, out _, out _));
        Assert.IsFalse(ScreenshotLayoutFactsReader.TryRead(metrics.RootElement, 0, out _, out _));
    }

    [TestMethod]
    public void HeaderDimensionsComeFromBytesAndMalformedPayloadsAreRejected()
    {
        byte[] png = [137,80,78,71,13,10,26,10,0,0,0,13,73,72,68,82,
            0,0,0,0,0,0,0,0,8,2,0,0,0,0,0,0,0];
        BinaryPrimitives.WriteUInt32BigEndian(png.AsSpan(16), 512);
        BinaryPrimitives.WriteUInt32BigEndian(png.AsSpan(20), 288);
        Assert.AreEqual(new ScreenshotImageHeader("image/png", 512, 288), ScreenshotImageHeader.Read(png));
        byte[] jpeg = [255,216,255,192,0,17,8,1,32,2,0,3,1,17,0,2,17,0,3,17,0,255,217];
        Assert.AreEqual(new ScreenshotImageHeader("image/jpeg", 512, 288), ScreenshotImageHeader.Read(jpeg));
        Assert.IsNull(ScreenshotImageHeader.Read([1,2,3,4]));
        Assert.IsNull(ScreenshotImageHeader.Read(jpeg.AsSpan(0, 12)));
        Assert.IsNull(ScreenshotImageHeader.Read(png.AsSpan(0, 24)));
        BinaryPrimitives.WriteUInt32BigEndian(png.AsSpan(16), 0);
        Assert.IsNull(ScreenshotImageHeader.Read(png));
    }
}
