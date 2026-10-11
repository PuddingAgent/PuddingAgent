using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PuddingBrowser.Automation;

namespace PuddingBrowser.AutomationTests;

/// <summary>§8.2 页内变更计数器：安装脚本幂等且真装观察器；读取拒绝缺失/非法值。</summary>
[TestClass]
public sealed class ScreenshotDomRevisionTests
{
    [TestMethod]
    public void TheInstallScriptIsIdempotentAndInstallsAnObserver()
    {
        var script = ScreenshotDomRevision.InstallScript;

        StringAssert.Contains(script, "MutationObserver");
        StringAssert.Contains(script, ScreenshotDomRevision.CounterProperty);
        // 幂等：再次调用**不重复安装**（否则会漏计/重计）。
        StringAssert.Contains(script, "__puddingDomRevisionInstalled");
        StringAssert.Contains(script, "if (window.__puddingDomRevisionInstalled)");
    }

    [TestMethod]
    public void ACountIsReadFaithfully()
    {
        Assert.IsTrue(ScreenshotDomRevision.TryRead(JsonDocument.Parse("42").RootElement, out var revision, out var reason), reason);
        Assert.AreEqual(42, revision);

        Assert.IsTrue(ScreenshotDomRevision.TryRead(JsonDocument.Parse("0").RootElement, out var zero, out _));
        Assert.AreEqual(0, zero);
    }

    [TestMethod]
    public void MissingOrIllegalCountsAreRefused()
    {
        Assert.IsFalse(ScreenshotDomRevision.TryRead(JsonDocument.Parse("null").RootElement, out _, out var reason1));
        Assert.IsTrue(reason1.Length > 0);

        Assert.IsFalse(ScreenshotDomRevision.TryRead(JsonDocument.Parse("\"42\"").RootElement, out _, out _));
        Assert.IsFalse(ScreenshotDomRevision.TryRead(JsonDocument.Parse("-1").RootElement, out _, out _));
        Assert.IsFalse(ScreenshotDomRevision.TryRead(JsonDocument.Parse("1.5").RootElement, out _, out _));
    }
    [TestMethod]
    public void TheCounterIsReadFromARuntimeEvaluateResult()
    {
        var payload = JsonDocument.Parse("""{ "result": { "type": "number", "value": 12 } }""").RootElement;

        Assert.IsTrue(ScreenshotDomRevision.TryReadEvaluationResult(payload, out var revision, out var reason), reason);
        Assert.AreEqual(12, revision);
        StringAssert.Contains(ScreenshotDomRevision.CounterExpression, ScreenshotDomRevision.CounterProperty);
    }

    [TestMethod]
    public void AnUnreadableCounterIsRefusedRatherThanAssumedUnchanged()
    {
        // undefined ⇒ 没有 result.value（观察尚未由截图建立）
        Assert.IsFalse(ScreenshotDomRevision.TryReadEvaluationResult(
            JsonDocument.Parse("""{ "result": { "type": "undefined" } }""").RootElement, out _, out var reason1));
        Assert.IsTrue(reason1.Contains("screenshot"), reason1);

        Assert.IsFalse(ScreenshotDomRevision.TryReadEvaluationResult(
            JsonDocument.Parse("""{ "result": { "type": "number", "value": -3 } }""").RootElement, out _, out _));
        Assert.IsFalse(ScreenshotDomRevision.TryReadEvaluationResult(JsonDocument.Parse("{}").RootElement, out _, out _));
    }
}