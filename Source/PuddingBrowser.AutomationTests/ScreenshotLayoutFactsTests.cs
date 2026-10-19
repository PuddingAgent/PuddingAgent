using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PuddingBrowser.Automation;

namespace PuddingBrowser.AutomationTests;

/// <summary>§8.1/§8.2 几何事实投影：读得到 ⇒ 值如实；**缺字段/非法值 ⇒ 拒绝**；几何变化 ⇒ 视口修订变化。</summary>
[TestClass]
public sealed class ScreenshotLayoutFactsTests
{
    private const string Metrics = """
        {
          "cssVisualViewport": { "clientWidth": 1280, "clientHeight": 720, "scale": 2, "pageX": 0, "pageY": 120 },
          "cssContentSize": { "x": 0, "y": 0, "width": 1280, "height": 4000 }
        }
        """;

    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

    [TestMethod]
    public void GeometryIsReadFaithfully()
    {
        Assert.IsTrue(ScreenshotLayoutFactsReader.TryRead(Parse(Metrics), 2, out var facts, out var reason), reason);
        Assert.AreEqual(1280, facts!.ViewportWidth);
        Assert.AreEqual(720, facts.ViewportHeight);
        Assert.AreEqual(2, facts.DeviceScaleFactor);
        Assert.AreEqual(120, facts.ScrollY);
        Assert.AreEqual(4000, facts.ContentHeight);
    }

    [TestMethod]
    public void MissingOrIllegalFieldsAreRefused()
    {
        Assert.IsFalse(ScreenshotLayoutFactsReader.TryRead(Parse("{}"), 2, out _, out var reason1));
        Assert.IsTrue(reason1.Length > 0);

        // 视口存在但缺 clientHeight
        Assert.IsFalse(ScreenshotLayoutFactsReader.TryRead(
            Parse("""{ "cssVisualViewport": { "clientWidth": 100 }, "cssContentSize": { "width": 1, "height": 1 } }"""),
            2, out _, out _));

        // 缩放非正
        Assert.IsFalse(ScreenshotLayoutFactsReader.TryRead(
            Parse("""{ "cssVisualViewport": { "clientWidth": 100, "clientHeight": 50, "scale": 0 }, "cssContentSize": { "width": 1, "height": 1 } }"""),
            2, out _, out _));

        // 缺内容尺寸（不得假装整页）
        Assert.IsFalse(ScreenshotLayoutFactsReader.TryRead(
            Parse("""{ "cssVisualViewport": { "clientWidth": 100, "clientHeight": 50 } }"""),
            2, out _, out _));
    }

    [TestMethod]
    public void AnyGeometryChangeChangesTheViewportRevision()
    {
        Assert.IsTrue(ScreenshotLayoutFactsReader.TryRead(Parse(Metrics), 2, out var baseline, out _));

        var scrolled = Metrics.Replace("\"pageY\": 120", "\"pageY\": 121");
        Assert.IsTrue(ScreenshotLayoutFactsReader.TryRead(Parse(scrolled), 2, out var afterScroll, out _));

        var resized = Metrics.Replace("\"clientWidth\": 1280", "\"clientWidth\": 1279");
        Assert.IsTrue(ScreenshotLayoutFactsReader.TryRead(Parse(resized), 2, out var afterResize, out _));

        Assert.AreNotEqual(baseline!.ViewportRevision, afterScroll!.ViewportRevision, "滚动变化必须改变视口修订（§8.2 失效键）");
        Assert.AreNotEqual(baseline.ViewportRevision, afterResize!.ViewportRevision, "布局变化必须改变视口修订");
        Assert.AreEqual(baseline.ViewportRevision, ScreenshotLayoutFactsReader.TryRead(Parse(Metrics), 2, out var again, out _) ? again!.ViewportRevision : -1, "同样的事实必须得到同样的修订");
    }
}
