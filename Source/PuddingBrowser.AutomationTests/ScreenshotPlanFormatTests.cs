using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PuddingBrowser.Automation;

namespace PuddingBrowser.AutomationTests;

/// <summary>
/// 截图的 CDP 参数语义与结果解码（§6 视觉观察）。
/// 重点是"别假装"：整页必须有内容尺寸、有损格式才有 quality、返回体缺 data 不能当成功。
/// </summary>
[TestClass]
public sealed class ScreenshotPlanTests
{
    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

    [TestMethod]
    public void DefaultsToPngWithoutQuality()
    {
        var root = Parse(ScreenshotPlan.Build());

        Assert.AreEqual("png", root.GetProperty("format").GetString());
        Assert.IsFalse(root.GetProperty("captureBeyondViewport").GetBoolean());
        Assert.IsFalse(root.TryGetProperty("quality", out _));
        Assert.IsFalse(root.TryGetProperty("clip", out _));
    }

    [TestMethod]
    public void QualitySwitchesToJpegAndCarriesTheValue()
    {
        // quality 只对有损格式有意义：给了它就说明要 JPEG，不该再要 PNG。
        var root = Parse(ScreenshotPlan.Build(quality: 80));

        Assert.AreEqual("jpeg", root.GetProperty("format").GetString());
        Assert.AreEqual(80, root.GetProperty("quality").GetInt32());
    }

    [TestMethod]
    public void QualityOutsideRangeIsRejectedInsteadOfClamped()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => ScreenshotPlan.Build(quality: -1));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => ScreenshotPlan.Build(quality: 101));
    }

    [TestMethod]
    public void FullPageNeedsTheContentSize_AndUsesItAsTheClip()
    {
        // 拿不到内容尺寸就不要假装整页（否则调用方以为拿到了整页，实际只有视口）。
        Assert.ThrowsExactly<ArgumentException>(() => ScreenshotPlan.Build(fullPage: true));

        var root = Parse(ScreenshotPlan.Build(
            fullPage: true,
            contentSize: new ScreenshotPlan.ContentSize(0, 0, 1280, 4200)));

        Assert.IsTrue(root.GetProperty("captureBeyondViewport").GetBoolean());
        var clip = root.GetProperty("clip");
        Assert.AreEqual(1280d, clip.GetProperty("width").GetDouble());
        Assert.AreEqual(4200d, clip.GetProperty("height").GetDouble());
        Assert.AreEqual(1, clip.GetProperty("scale").GetInt32());
    }

    [TestMethod]
    public void ContentSizeIsReadFromLayoutMetrics_OrRefusedWhenMissing()
    {
        var metrics = JsonDocument.Parse(
            """{"cssLayoutViewport":{"clientWidth":800,"clientHeight":600},"cssContentSize":{"x":0,"y":0,"width":800,"height":2400}}""");

        Assert.IsTrue(ScreenshotPlan.TryReadContentSize(metrics.RootElement, out var size));
        Assert.AreEqual(2400d, size.Height);

        // 缺 cssContentSize ⇒ 明确失败（不补 0、不假设等于视口）。
        var missing = JsonDocument.Parse("""{"cssLayoutViewport":{"clientWidth":800,"clientHeight":600}}""");
        Assert.IsFalse(ScreenshotPlan.TryReadContentSize(missing.RootElement, out _));
    }

    [TestMethod]
    public void BudgetIsCheckedWithoutTruncatingTheImage()
    {
        // 截图**不能**"截断到预算内"：半张图比没有更危险。超了就报资源耗尽。
        Assert.AreEqual(
            ScreenshotPlan.BudgetVerdict.Within, ScreenshotPlan.CheckBudget(1_000, maxBytes: 1_000));
        Assert.AreEqual(
            ScreenshotPlan.BudgetVerdict.Exceeded, ScreenshotPlan.CheckBudget(1_001, maxBytes: 1_000));
        Assert.AreEqual(
            ScreenshotPlan.BudgetVerdict.Within, ScreenshotPlan.CheckBudget(10_000_000, maxBytes: null));

        var description = ScreenshotPlan.DescribeBudget(2_048, 1_024);
        StringAssert.Contains(description, "2048");
        StringAssert.Contains(description, "never truncated");
    }

    [TestMethod]
    public void BudgetRejectsNonsenseInputs()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => ScreenshotPlan.CheckBudget(-1, 100));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => ScreenshotPlan.CheckBudget(1, maxBytes: 0));
    }

    [TestMethod]
    public void DecodeReturnsTheBytes()
    {
        var payload = Convert.ToBase64String([1, 2, 3, 4]);
        var result = JsonDocument.Parse($$"""{"data":"{{payload}}"}""");

        CollectionAssert.AreEqual(new byte[] { 1, 2, 3, 4 }, ScreenshotPlan.Decode(result.RootElement));
    }

    [TestMethod]
    public void DecodeRefusesMissingOrEmptyOrInvalidPayloads()
    {
        foreach (var json in new[] { "{}", """{"data":""}""", """{"data":null}""", """{"data":"not base64!!"}""" })
        {
            var document = JsonDocument.Parse(json);
            Assert.Throws<Exception>(
                () => ScreenshotPlan.Decode(document.RootElement),
                $"'{json}' 不应被当成有效截图");
        }
    }
}
