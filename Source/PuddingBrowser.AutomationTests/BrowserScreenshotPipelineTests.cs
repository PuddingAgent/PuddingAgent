using PuddingBrowser.Automation;

namespace PuddingBrowser.AutomationTests;

/// <summary>
/// **B4 桌面半边的装配级用例**：规划（能否截/截多大）→ 载荷（有界/守约）→ 坐标（图片点换回 CSS viewport）
/// 三块各自都有单测，但**组合**才是真机路径的形状 —— 单测全绿而组合错位，正是这类代码最容易漏的地方。
/// </summary>
[TestClass]
public sealed class BrowserScreenshotPipelineTests
{
    private const string ObservationId = "obs-pipeline";

    private static BrowserScreenshotIntent Intent(
        BrowserScreenshotScope scope = BrowserScreenshotScope.Viewport,
        BrowserScreenshotDetail detail = BrowserScreenshotDetail.Low,
        double viewportWidth = 1280,
        double viewportHeight = 800,
        double deviceScale = 2.0,
        double? regionX = null,
        double? regionY = null,
        double? regionWidth = null,
        double? regionHeight = null) =>
        new(scope, detail, HasPageReadGrant: true, PageReady: true, IsCurrentDocument: true,
            viewportWidth, viewportHeight, deviceScale, regionX, regionY, regionWidth, regionHeight);

    /// <summary>把规划结果变成一次"真实采集"的元数据（源尺寸=裁剪×设备缩放；输出尺寸=规划预期）。</summary>
    private static BrowserScreenshotMetadata MetadataFrom(BrowserScreenshotPlan plan, int byteCount)
    {
        var scale = 2.0;
        return new BrowserScreenshotMetadata(
            ObservationId,
            "image/jpeg",
            byteCount,
            (int)Math.Round(plan.CropWidth * scale),
            (int)Math.Round(plan.CropHeight * scale),
            plan.ExpectedOutputWidth,
            plan.ExpectedOutputHeight,
            scale,
            plan.CropX + plan.CropWidth,
            plan.CropY + plan.CropHeight,
            0,
            0,
            plan.CropX,
            plan.CropY,
            plan.CropWidth,
            plan.CropHeight,
            11,
            23,
            3,
            plan.Detail);
    }

    [TestMethod]
    public void APlannedLowDetailViewportCapturePassesTransferValidation()
    {
        Assert.IsTrue(
            BrowserScreenshotPlanner.TryPlan(Intent(), out var plan, out var planning, out var planReason),
            $"{planning}: {planReason}");

        var metadata = MetadataFrom(plan!, byteCount: plan!.ByteBudget - 1);
        Assert.IsTrue(
            BrowserScreenshotTransfer.TryValidate(metadata, out var rejection, out var reason),
            $"{rejection}: {reason}");
    }

    [TestMethod]
    public void ThePlannedPayloadSplitsAndReassemblesToTheDeclaredSize()
    {
        Assert.IsTrue(BrowserScreenshotPlanner.TryPlan(Intent(), out var plan, out _, out _));
        var total = plan!.ByteBudget / 4;

        var chunks = BrowserScreenshotTransfer.Split(total, BrowserScreenshotTransfer.DefaultChunkBytes);
        Assert.IsTrue(
            BrowserScreenshotTransfer.TryAssemble(chunks, total, out var payload, out var reason),
            reason);
        Assert.AreEqual(total, payload!.Length);
    }

    [TestMethod]
    public void CoordinatesFromTheSameObservationMapBackIntoTheViewport()
    {
        // 装配视角下最重要的一条：规划出来的裁剪 + 采集回来的元数据，**必须**能被坐标变换用起来，
        // 且图片上的点要落在 CSS 视口内（否则真机上就是"点了但没点到"）。
        Assert.IsTrue(BrowserScreenshotPlanner.TryPlan(Intent(), out var plan, out _, out _));
        var metadata = MetadataFrom(plan!, byteCount: plan!.ByteBudget / 2);
        Assert.IsTrue(BrowserScreenshotTransfer.TryValidate(metadata, out _, out var reason), reason);

        Assert.IsTrue(
            BrowserScreenshotCoordinates.TryToCssViewport(
                metadata,
                ObservationId,
                metadata.OutputWidth / 2.0,
                metadata.OutputHeight / 2.0,
                new BrowserPageGenerations(11, 23, 3),
                out var target,
                out var rejection,
                out var coordinateReason),
            $"{rejection}: {coordinateReason}");

        // 画面中心 ⇒ CSS 视口中心（本例无滚动、裁剪即视口）。
        Assert.AreEqual(metadata.ViewportWidth!.Value / 2, target!.CssX, 0.5);
        Assert.AreEqual(metadata.ViewportHeight!.Value / 2, target.CssY, 0.5);
    }

    [TestMethod]
    public void ARegionCaptureKeepsTheOffsetThroughTheWholePipeline()
    {
        // 区域裁剪：坐标必须带回**裁剪原点**的偏移，否则会整体点偏一块区域。
        var intent = Intent(
            scope: BrowserScreenshotScope.Region,
            regionX: 400,
            regionY: 200,
            regionWidth: 320,
            regionHeight: 180);

        Assert.IsTrue(BrowserScreenshotPlanner.TryPlan(intent, out var plan, out var planning, out var reason),
            $"{planning}: {reason}");
        Assert.AreEqual(400d, plan!.CropX);
        Assert.AreEqual(200d, plan.CropY);

        var metadata = MetadataFrom(plan, byteCount: 8 * 1024);
        Assert.IsTrue(BrowserScreenshotTransfer.TryValidate(metadata, out _, out var transferReason), transferReason);

        // 图片左上角 ⇒ CSS (400, 200)。
        Assert.IsTrue(
            BrowserScreenshotCoordinates.TryToCssViewport(
                metadata, ObservationId, 0, 0, new BrowserPageGenerations(11, 23, 3),
                out var topLeft, out _, out _));
        Assert.AreEqual(400d, topLeft!.CssX, 0.5);
        Assert.AreEqual(200d, topLeft.CssY, 0.5);
    }

    [TestMethod]
    public void ThePipelineRefusesPayloadsThatBreakTheLowDetailPromiseOrTheInputLimit()
    {
        Assert.IsTrue(BrowserScreenshotPlanner.TryPlan(Intent(), out var plan, out _, out _));

        // ① 低细节基准被突破（超过 40 KiB 的输出预算）。
        var overBudget = MetadataFrom(plan!, byteCount: BrowserScreenshotTransfer.LowDetailBaselineBytes + 1);
        Assert.IsFalse(BrowserScreenshotTransfer.TryValidate(overBudget, out var budgetRejection, out _));
        Assert.AreEqual(BrowserScreenshotRejection.LowDetailBudgetExceeded, budgetRejection);

        // ② 单张输入上限被突破（8 MiB）。
        var overInput = MetadataFrom(plan!, byteCount: BrowserScreenshotTransfer.MaxImageBytes + 1);
        Assert.IsFalse(BrowserScreenshotTransfer.TryValidate(overInput, out var inputRejection, out _));
        Assert.AreEqual(BrowserScreenshotRejection.OverLimit, inputRejection);

        // ③ 高保真可以超低细节基准，但仍受输入上限约束 ⇒ 规划里给出的预算也要跟着变。
        Assert.IsTrue(
            BrowserScreenshotPlanner.TryPlan(Intent(detail: BrowserScreenshotDetail.High), out var high, out _, out _));
        Assert.AreEqual(BrowserScreenshotTransfer.MaxImageBytes, high!.ByteBudget);
    }
}
