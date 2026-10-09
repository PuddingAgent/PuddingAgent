using PuddingBrowser.Automation;

namespace PuddingBrowser.AutomationTests;

/// <summary>
/// **截图能力的只读投影**（设计 §8.1/§8.2）：先视口/区域、全页明确不支持、截屏前验证授权与就绪、
/// 区域不得越出 CSS 视口、detail=low 缩到最长边 ≤512px。
/// 这些前提**任何一条不满足都必须拒绝并说明原因** —— 它们正是真机上"截了个导航前的画面"
/// 或"拿着越界区域去裁一半"的成因。
/// </summary>
[TestClass]
public sealed class BrowserScreenshotPlannerTests
{
    private static BrowserScreenshotIntent Intent(
        BrowserScreenshotScope scope = BrowserScreenshotScope.Viewport,
        BrowserScreenshotDetail detail = BrowserScreenshotDetail.Low,
        bool grant = true,
        bool ready = true,
        bool current = true,
        double? viewportWidth = 1280,
        double? viewportHeight = 800,
        double? deviceScale = 1.0,
        double? regionX = null,
        double? regionY = null,
        double? regionWidth = null,
        double? regionHeight = null) =>
        new(scope, detail, grant, ready, current, viewportWidth, viewportHeight, deviceScale,
            regionX, regionY, regionWidth, regionHeight);

    [TestMethod]
    public void AViewportCaptureLowDetailIsPlannedWithTheLowEdgeLimit()
    {
        // 1280x800 CSS、DPR=1 ⇒ 源 1280x800；low 缩到最长边 512（比例 0.4）⇒ 512x320。
        Assert.IsTrue(
            BrowserScreenshotPlanner.TryPlan(Intent(), out var plan, out var rejection, out var reason),
            reason);

        Assert.AreEqual(BrowserScreenshotPlanning.None, rejection);
        Assert.AreEqual(0d, plan!.CropX);
        Assert.AreEqual(0d, plan.CropY);
        Assert.AreEqual(1280d, plan.CropWidth);
        Assert.AreEqual(800d, plan.CropHeight);
        Assert.AreEqual(512, plan.ExpectedOutputWidth);
        Assert.AreEqual(320, plan.ExpectedOutputHeight);
        Assert.AreEqual(BrowserScreenshotTransfer.MaxImageBytes, plan.MaxBytes);
    }

    [TestMethod]
    public void HighDetailKeepsDevicePixelsAndObeysTheDeviceScale()
    {
        // DPR=2：源是 2560x1600；high 不缩放 ⇒ 输出就是设备像素尺寸。
        Assert.IsTrue(
            BrowserScreenshotPlanner.TryPlan(
                Intent(detail: BrowserScreenshotDetail.High, deviceScale: 2.0),
                out var plan,
                out _,
                out var reason),
            reason);

        Assert.AreEqual(2560, plan!.ExpectedOutputWidth);
        Assert.AreEqual(1600, plan.ExpectedOutputHeight);
    }

    [TestMethod]
    public void LowDetailOnASmallViewportIsNotUpscaled()
    {
        // 400x300 CSS、DPR=1 ⇒ 最长边 400 < 512 ⇒ **不放大**（放大只会凭空造像素）。
        Assert.IsTrue(
            BrowserScreenshotPlanner.TryPlan(
                Intent(viewportWidth: 400, viewportHeight: 300),
                out var plan,
                out _,
                out var reason),
            reason);

        Assert.AreEqual(400, plan!.ExpectedOutputWidth);
        Assert.AreEqual(300, plan.ExpectedOutputHeight);
    }

    [TestMethod]
    public void FullPageCaptureIsExplicitlyUnsupportedRatherThanFaked()
    {
        // 设计：全页不能冒充 viewport；滚动拼接要单独处理固定元素与动态内容。
        Assert.IsFalse(
            BrowserScreenshotPlanner.TryPlan(
                Intent(scope: BrowserScreenshotScope.FullPage), out var plan, out var rejection, out var reason));

        Assert.IsNull(plan);
        Assert.AreEqual(BrowserScreenshotPlanning.FullPageNeedsSeparateHandling, rejection);
        Assert.IsTrue(reason.Contains("viewport", StringComparison.Ordinal), reason);
    }

    [TestMethod]
    public void ThePreconditionsFromTheDesignAllBlockAPlan()
    {
        var cases = new (BrowserScreenshotIntent Intent, BrowserScreenshotPlanning Expected)[]
        {
            (Intent(grant: false), BrowserScreenshotPlanning.NoReadGrant),
            (Intent(current: false), BrowserScreenshotPlanning.TargetDocumentNotCurrent),
            (Intent(ready: false), BrowserScreenshotPlanning.PageNotReady),
        };

        foreach (var (intent, expected) in cases)
        {
            Assert.IsFalse(BrowserScreenshotPlanner.TryPlan(intent, out var plan, out var rejection, out var reason));
            Assert.IsNull(plan);
            Assert.AreEqual(expected, rejection, reason);
            Assert.IsTrue(reason.Length > 0);
        }
    }

    [TestMethod]
    public void ARegionInsideTheViewportIsPlannedWithItsOwnCropAndOutputSize()
    {
        Assert.IsTrue(
            BrowserScreenshotPlanner.TryPlan(
                Intent(
                    scope: BrowserScreenshotScope.Region,
                    detail: BrowserScreenshotDetail.High,
                    regionX: 100,
                    regionY: 50,
                    regionWidth: 400,
                    regionHeight: 200),
                out var plan,
                out _,
                out var reason),
            reason);

        Assert.AreEqual(100d, plan!.CropX);
        Assert.AreEqual(50d, plan.CropY);
        Assert.AreEqual(400d, plan.CropWidth);
        Assert.AreEqual(200d, plan.CropHeight);
        Assert.AreEqual(400, plan.ExpectedOutputWidth);
        Assert.AreEqual(200, plan.ExpectedOutputHeight);
    }

    [TestMethod]
    public void ARegionOutsideTheViewportIsRefusedInsteadOfBeingClipped()
    {
        // 右侧越界 40px、下侧越界 30px：拒绝（"裁一半"会让调用方以为看到了全部）。
        Assert.IsFalse(
            BrowserScreenshotPlanner.TryPlan(
                Intent(
                    scope: BrowserScreenshotScope.Region,
                    regionX: 1000,
                    regionY: 700,
                    regionWidth: 320,
                    regionHeight: 130),
                out var plan,
                out var rejection,
                out var reason));

        Assert.IsNull(plan);
        Assert.AreEqual(BrowserScreenshotPlanning.RegionOutOfViewport, rejection);
        Assert.IsTrue(reason.Contains("not inside", StringComparison.Ordinal), reason);
    }

    [TestMethod]
    public void MissingRegionGeometryOrViewportMetricsIsRefusedRatherThanDefaulted()
    {
        // 区域截图没给区域。
        Assert.IsFalse(
            BrowserScreenshotPlanner.TryPlan(
                Intent(scope: BrowserScreenshotScope.Region), out _, out var rejection, out _));
        Assert.AreEqual(BrowserScreenshotPlanning.InvalidIntent, rejection);

        // 视口尺寸/设备缩放没上报（用可空表达"不知道"）。
        foreach (var incomplete in new[]
        {
            Intent(viewportWidth: null),
            Intent(viewportHeight: null),
            Intent(deviceScale: null),
        })
        {
            Assert.IsFalse(BrowserScreenshotPlanner.TryPlan(incomplete, out _, out rejection, out _));
            Assert.AreEqual(BrowserScreenshotPlanning.InvalidIntent, rejection);
        }

        Assert.IsFalse(BrowserScreenshotPlanner.TryPlan(null, out _, out rejection, out _));
        Assert.AreEqual(BrowserScreenshotPlanning.InvalidIntent, rejection);
    }

    [TestMethod]
    public void ThePlanStatesWhatMetadataTheCallerMustReport()
    {
        Assert.IsTrue(BrowserScreenshotPlanner.TryPlan(Intent(), out var plan, out _, out _));
        foreach (var required in new[] { "device scale", "scroll offset", "crop transform", "PageVersion" })
        {
            Assert.IsTrue(
                plan!.ObservationRequirements.Contains(required, StringComparison.Ordinal),
                $"规划结果应说明必须上报「{required}」");
        }
    }
}
