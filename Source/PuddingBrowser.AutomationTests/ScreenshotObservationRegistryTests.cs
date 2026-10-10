using Microsoft.VisualStudio.TestTools.UnitTesting;
using PuddingBrowser.Automation;

namespace PuddingBrowser.AutomationTests;

/// <summary>§8.2 观察登记处：登记/解析、**世代任一变化即过期并移除**、空 id 与未登记 id 拒绝。</summary>
[TestClass]
public sealed class ScreenshotObservationRegistryTests
{
    private static BrowserScreenshotMetadata Metadata(
        string id = "obs-1", long pageVersion = 5, long domRevision = 7, long viewportRevision = 9) => new(
        id, "image/png", 1024, 1280, 720, 512, 288,
        DeviceScaleFactor: 2, ViewportWidth: 1280, ViewportHeight: 720,
        ScrollX: 0, ScrollY: 120, CropX: null, CropY: null, CropWidth: null, CropHeight: null,
        PageVersion: pageVersion, DomRevision: domRevision, ViewportRevision: viewportRevision,
        BrowserScreenshotDetail.Low);

    [TestMethod]
    public void RegisteredObservationResolvesWithinItsGenerations()
    {
        var registry = new InMemoryScreenshotObservationRegistry();
        var id = registry.Register(Metadata());

        Assert.AreEqual("obs-1", id);
        Assert.IsTrue(registry.TryResolve("obs-1", new BrowserPageGenerations(5, 7, 9), out var found));
        Assert.AreEqual(120, found.ScrollY);
    }

    [TestMethod]
    public void AnyGenerationChangeExpiresAndRemovesTheObservation()
    {
        var registry = new InMemoryScreenshotObservationRegistry();
        registry.Register(Metadata());

        // 页面版本变化
        Assert.IsFalse(registry.TryResolve("obs-1", new BrowserPageGenerations(6, 7, 9), out _));
        Assert.AreEqual(0, registry.Count);

        // 文档修订变化
        registry.Register(Metadata());
        Assert.IsFalse(registry.TryResolve("obs-1", new BrowserPageGenerations(5, 8, 9), out _));
        Assert.AreEqual(0, registry.Count);

        // 视口修订变化（滚动/缩放变化的代理）
        registry.Register(Metadata());
        Assert.IsFalse(registry.TryResolve("obs-1", new BrowserPageGenerations(5, 7, 10), out _));
        Assert.AreEqual(0, registry.Count);
    }

    [TestMethod]
    public void UnknownOrEmptyIdsNeverResolve()
    {
        var registry = new InMemoryScreenshotObservationRegistry();
        registry.Register(Metadata());

        Assert.IsFalse(registry.TryResolve("never-registered", new BrowserPageGenerations(5, 7, 9), out _));
        Assert.IsFalse(registry.TryResolve("  ", new BrowserPageGenerations(5, 7, 9), out _));
        Assert.IsFalse(registry.TryResolve(null, new BrowserPageGenerations(5, 7, 9), out _));
    }

    [TestMethod]
    public void AnEmptyObservationIdIsRefusedAtRegistration()
    {
        var registry = new InMemoryScreenshotObservationRegistry();

        var refused = false;
        try
        {
            registry.Register(Metadata(id: "  "));
        }
        catch (ArgumentException)
        {
            refused = true;
        }

        Assert.IsTrue(refused, "登记空 id 必须明确失败（身份来源不得含混）");
        Assert.AreEqual(0, registry.Count);
    }
}