using PuddingBrowser.Automation;
namespace PuddingBrowser.AutomationTests;

/// <summary>
/// **截图载荷（桌面一侧）**的边界测试：设计 §8.1 要求 Desktop 返回"有界媒体载荷/分块 + 元数据"，
/// 默认 detail=low（最长边 ≤512px）、单张上限 8 MiB、超限**明确**返回原因；
/// 元数据必须报告源/输出尺寸、设备缩放、CSS viewport、scroll offset、裁剪变换与三代修订号。
///
/// 这些用例只断言**这一半边**能证明的事（不涉及落库、不涉及模型）。
/// </summary>
[TestClass]
public sealed class BrowserScreenshotTransferTests
{
    private static BrowserScreenshotMetadata Metadata(
        string mime = "image/png",
        int byteCount = 64 * 1024,
        int outputWidth = 512,
        int outputHeight = 320,
        BrowserScreenshotDetail detail = BrowserScreenshotDetail.Low,
        string observationId = "obs-1",
        long pageVersion = 7,
        long domRevision = 11,
        long viewportRevision = 3,
        double? deviceScale = 1.25,
        double? viewportWidth = 1280,
        double? viewportHeight = 800,
        double? scrollX = 0,
        double? scrollY = 120,
        double? cropWidth = 512,
        double? cropHeight = 320) =>
        new(
            observationId,
            mime,
            byteCount,
            1280,
            800,
            outputWidth,
            outputHeight,
            deviceScale,
            viewportWidth,
            viewportHeight,
            scrollX,
            scrollY,
            0,
            0,
            cropWidth,
            cropHeight,
            pageVersion,
            domRevision,
            viewportRevision,
            detail);

    [TestMethod]
    public void AConformingLowDetailPayloadIsAccepted()
    {
        Assert.IsTrue(
            BrowserScreenshotTransfer.TryValidate(Metadata(), out var rejection, out var reason),
            reason);
        Assert.AreEqual(BrowserScreenshotRejection.None, rejection);
        Assert.AreEqual(string.Empty, reason);
    }

    [TestMethod]
    public void TheSpecLimitsAreTheOnesTheDesignDocumentStates()
    {
        // 与权威文档对表：数字若被改动而文档没改（或反之），这条会红。
        Assert.AreEqual(8 * 1024 * 1024, BrowserScreenshotTransfer.MaxImageBytes, "设计 §8.1：8 MiB/张");
        Assert.AreEqual(512, BrowserScreenshotTransfer.LowDetailLongestEdge, "设计 §8.1：detail=low 最长边 512px");

        var documentPath = Path.Combine(RepoRoot(), "Docs", "12_features",
            "浏览器自动化可靠性与渐进阅读设计方案-2026-10-08.md");
        Assert.IsTrue(File.Exists(documentPath), $"找不到设计文档：{documentPath}");
        var document = File.ReadAllText(documentPath);
        Assert.IsTrue(document.Contains("8 MiB", StringComparison.Ordinal), "设计文档里应写明 8 MiB 上限");
        Assert.IsTrue(document.Contains("512px", StringComparison.Ordinal), "设计文档里应写明 512px 低清上限");
    }

    [TestMethod]
    public void OverTheEightMebibyteLimitIsRefusedWithAnActionableReason()
    {
        var over = Metadata(byteCount: BrowserScreenshotTransfer.MaxImageBytes + 1);
        Assert.IsFalse(BrowserScreenshotTransfer.TryValidate(over, out var rejection, out var reason));
        Assert.AreEqual(BrowserScreenshotRejection.OverLimit, rejection);
        Assert.IsTrue(reason.Contains("8 MiB", StringComparison.Ordinal), reason);
    }

    [TestMethod]
    public void AnUnsupportedMimeTypeIsRefusedInsteadOfBeingAssumed()
    {
        foreach (var mime in new[] { "image/webp", "text/html", "application/octet-stream", "" })
        {
            Assert.IsFalse(
                BrowserScreenshotTransfer.TryValidate(Metadata(mime: mime), out var rejection, out _),
                $"'{mime}' 不应被接受");
            Assert.AreEqual(BrowserScreenshotRejection.UnsupportedMime, rejection);
        }
    }

    [TestMethod]
    public void LowDetailWithATooLongEdgeIsRefusedBecauseThePromiseWouldBeFalse()
    {
        var lying = Metadata(outputWidth: 1024, outputHeight: 512, detail: BrowserScreenshotDetail.Low);
        Assert.IsFalse(BrowserScreenshotTransfer.TryValidate(lying, out var rejection, out var reason));
        Assert.AreEqual(BrowserScreenshotRejection.DetailViolation, rejection);
        Assert.IsTrue(reason.Contains("512", StringComparison.Ordinal), reason);
    }

    [TestMethod]
    public void HighDetailMayExceedTheLowDetailEdgeButStillObeysTheByteLimit()
    {
        var high = Metadata(outputWidth: 2048, outputHeight: 1536, detail: BrowserScreenshotDetail.High);
        Assert.IsTrue(BrowserScreenshotTransfer.TryValidate(high, out _, out var reason), reason);
    }

    [TestMethod]
    public void MissingObservationIdentityBlocksThePayload()
    {
        // 坐标操作要携带截屏 observationId（§8.2）：没有身份就没法安全使用这张图。
        Assert.IsFalse(
            BrowserScreenshotTransfer.TryValidate(Metadata(observationId: "   "), out var rejection, out _));
        Assert.AreEqual(BrowserScreenshotRejection.MissingObservationIdentity, rejection);
    }

    [TestMethod]
    public void MissingRevisionsBlockThePayload()
    {
        foreach (var (page, dom, viewport) in new[] { (0L, 11L, 3L), (7L, 0L, 3L), (7L, 11L, 0L) })
        {
            Assert.IsFalse(
                BrowserScreenshotTransfer.TryValidate(
                    Metadata(pageVersion: page, domRevision: dom, viewportRevision: viewport),
                    out var rejection,
                    out _),
                $"修订号缺失不应被接受：{page}/{dom}/{viewport}");
            Assert.AreEqual(BrowserScreenshotRejection.MissingRevisions, rejection);
        }
    }

    [TestMethod]
    public void UnreportedViewportGeometryIsRefusedRatherThanDefaultedToZero()
    {
        // "未上报"用可空表达；补 0 冒充事实会让调用方算出错误坐标。
        foreach (var broken in new[]
        {
            Metadata(deviceScale: null),
            Metadata(viewportWidth: null),
            Metadata(viewportHeight: null),
            Metadata(scrollX: null),
            Metadata(scrollY: null),
            Metadata(cropWidth: null),
            Metadata(cropHeight: null),
        })
        {
            Assert.IsFalse(BrowserScreenshotTransfer.TryValidate(broken, out var rejection, out _));
            Assert.AreEqual(BrowserScreenshotRejection.MissingViewportGeometry, rejection);
        }
    }

    [TestMethod]
    public void SplittingAndAssemblingRoundTripsWithoutLoss()
    {
        var chunks = BrowserScreenshotTransfer.Split(totalBytes: 150_000, chunkBytes: 64 * 1024);
        Assert.AreEqual(3, chunks.Count);
        Assert.IsTrue(chunks[0].IsLast == false && chunks[1].IsLast == false && chunks[2].IsLast);
        Assert.AreEqual(64 * 1024, chunks[0].Data.Length);
        Assert.AreEqual(150_000 - (2 * 64 * 1024), chunks[2].Data.Length);

        Assert.IsTrue(
            BrowserScreenshotTransfer.TryAssemble(chunks, 150_000, out var payload, out var reason),
            reason);
        Assert.AreEqual(150_000, payload!.Length);
    }

    [TestMethod]
    public void AGapOrATruncatedTailIsRefusedInsteadOfReturningAPartialPayload()
    {
        var chunks = BrowserScreenshotTransfer.Split(totalBytes: 150_000, chunkBytes: 64 * 1024).ToList();

        // 缺口：去掉中间一片。
        var withGap = chunks.Where(chunk => chunk.Index != 1).ToArray();
        Assert.IsFalse(BrowserScreenshotTransfer.TryAssemble(withGap, 150_000, out var noPayload, out var gapReason));
        Assert.IsNull(noPayload);
        Assert.IsTrue(gapReason.Contains("gap", StringComparison.Ordinal), gapReason);

        // 尾巴没标 last：可能被截断。
        var withoutLast = chunks
            .Select(chunk => chunk.Index == chunks.Count - 1 ? chunk with { IsLast = false } : chunk)
            .ToArray();
        Assert.IsFalse(BrowserScreenshotTransfer.TryAssemble(withoutLast, 150_000, out _, out var lastReason));
        Assert.IsTrue(lastReason.Contains("last", StringComparison.Ordinal), lastReason);

        // 声明总量与实际不符。
        Assert.IsFalse(BrowserScreenshotTransfer.TryAssemble(chunks, 150_001, out _, out var sizeReason));
        Assert.IsTrue(sizeReason.Contains("150001", StringComparison.Ordinal), sizeReason);
    }

    [TestMethod]
    public void ATotallyAbsentPayloadIsRefused()
    {
        Assert.IsFalse(BrowserScreenshotTransfer.TryAssemble(null, 10, out var payload, out var reason));
        Assert.IsNull(payload);
        Assert.IsTrue(reason.Length > 0);

        Assert.IsFalse(BrowserScreenshotTransfer.TryAssemble([], 10, out _, out reason));
        Assert.IsTrue(reason.Length > 0);
    }

    private static string RepoRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "PuddingAgentNetwork.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException("repository root not found");
    }
}