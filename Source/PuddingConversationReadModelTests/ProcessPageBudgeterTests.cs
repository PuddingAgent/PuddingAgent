#pragma warning disable MSTEST0032 // frozen contract constant assertions are intentional

namespace PuddingConversationReadModelTests;

/// <summary>
/// 过程明细页预算器（Plan §5.3、§5.4；冻结合同 <see cref="ProcessPageBudgeter"/> 及其 DTO 的 XML 文档）。
/// </summary>
[TestClass]
public sealed class ProcessPageBudgeterTests
{
    private static ProcessItemCandidate Item(long sequence, string preview = "p", bool displayable = true, int payloadBytes = 1)
        => new()
        {
            Sequence = sequence,
            EventId = $"evt-{sequence}",
            Kind = "tool.call.completed",
            Preview = preview,
            PayloadBytes = payloadBytes,
            IsDisplayable = displayable,
        };

    private static ProcessItemCandidate[] Items(int count)
        => Enumerable.Range(1, count).Select(i => Item(i)).ToArray();

    [TestMethod]
    public void Bound_DefaultLimit_ReturnsAtMostOneHundredItemsAscending()
    {
        var page = ProcessPageBudgeter.Bound(Items(150));

        Assert.HasCount(100, page.Items);
        Assert.AreEqual(51L, page.Items[0].Sequence);
        Assert.AreEqual(150L, page.Items[99].Sequence);
        Assert.AreEqual(DetailPageTruncation.ItemLimit, page.Truncation);
        Assert.AreEqual(51L, page.ScannedBackThroughSequence);
        Assert.AreEqual(51L, page.NextBeforeSequence);
        Assert.IsTrue(page.HasMoreBefore);
        Assert.AreEqual(0, page.OmittedNonDisplayableCount);
        Assert.AreEqual(100, page.TotalPreviewBytes);
    }

    [TestMethod]
    public void Bound_RequestedLimit_ClampsToFrozenMaximum()
    {
        var page = ProcessPageBudgeter.Bound(Items(250), requestedLimit: 999);

        Assert.HasCount(200, page.Items);
        Assert.AreEqual(51L, page.Items[0].Sequence);
        Assert.AreEqual(250L, page.Items[199].Sequence);
        Assert.AreEqual(DetailPageTruncation.ItemLimit, page.Truncation);
        Assert.AreEqual(51L, page.NextBeforeSequence);
        Assert.IsTrue(page.HasMoreBefore);
    }

    [TestMethod]
    public void Bound_EmptyCandidateWindow_ReportsNoScanAndNoCursor()
    {
        var page = ProcessPageBudgeter.Bound(Array.Empty<ProcessItemCandidate>());

        Assert.IsEmpty(page.Items);
        Assert.AreEqual(0L, page.ScannedBackThroughSequence);
        Assert.IsNull(page.NextBeforeSequence);
        Assert.IsFalse(page.HasMoreBefore);
        Assert.AreEqual(DetailPageTruncation.None, page.Truncation);
        Assert.AreEqual(0, page.TotalPreviewBytes);
        Assert.AreEqual(0, page.OmittedNonDisplayableCount);
    }

    [TestMethod]
    public void Bound_TruncatesItemPreviewToSixteenKiB_OnUtf8Boundary()
    {
        var preview = new string('文', 7_000); // 21000 bytes

        var page = ProcessPageBudgeter.Bound([Item(1, preview, payloadBytes: 21_000)]);

        Assert.HasCount(1, page.Items);
        var item = page.Items[0];
        Assert.IsTrue(item.PreviewTruncated);
        Assert.AreEqual(16_383, TestData.Utf8Bytes(item.Preview));
        Assert.AreEqual(new string('文', 5_461), item.Preview);
        Assert.AreEqual(21_000, item.OriginalPayloadBytes);
        Assert.AreEqual(1L, page.ScannedBackThroughSequence);
        Assert.IsNull(page.NextBeforeSequence);
        Assert.IsFalse(page.HasMoreBefore);
        Assert.AreEqual(DetailPageTruncation.None, page.Truncation);
    }

    [TestMethod]
    public void Bound_StopsAtPageByteBudget()
    {
        // 每项 16384 字节预览 + 128 字节 JSON 开销 = 16512；256 KiB 最多容纳 15 项。
        var preview = new string('x', 20_000);
        var candidates = Enumerable.Range(1, 20).Select(i => Item(i, preview)).ToArray();

        var page = ProcessPageBudgeter.Bound(candidates);

        Assert.HasCount(15, page.Items);
        Assert.AreEqual(6L, page.Items[0].Sequence);
        Assert.AreEqual(20L, page.Items[14].Sequence);
        Assert.AreEqual(DetailPageTruncation.ByteBudget, page.Truncation);
        Assert.AreEqual(6L, page.ScannedBackThroughSequence);
        Assert.AreEqual(6L, page.NextBeforeSequence);
        Assert.IsTrue(page.HasMoreBefore);
        Assert.AreEqual(15 * 16_384, page.TotalPreviewBytes);
    }

    [TestMethod]
    public void Bound_FirstCandidateRejectedByByteBudget_ReportsRefetchableBoundary()
    {
        // ProcessItemJsonOverheadBytes(128) 已超过 100 字节页预算：第一个候选就被拒绝，尚未消费任何候选。
        var limits = ConversationReadModelLimits.Default with { DetailPageMaxBytes = 100 };

        var page = ProcessPageBudgeter.Bound([Item(7, "p")], requestedLimit: null, limits);

        Assert.IsEmpty(page.Items);
        Assert.AreEqual(DetailPageTruncation.ByteBudget, page.Truncation);
        Assert.IsTrue(page.HasMoreBefore);
        Assert.AreEqual(7L, page.ScannedBackThroughSequence);
        Assert.AreEqual(8L, page.NextBeforeSequence,
            "尚未消费任何候选时边界必须能重新取回该候选（< seq+1），不能停在 0 导致永远取不到");
        Assert.AreEqual(0, page.TotalPreviewBytes);
    }

    [TestMethod]
    public void Bound_CandidateScanLimit_StopsWithScanBudget()
    {
        var limits = ConversationReadModelLimits.Default with { DetailPageCandidateScanLimit = 3 };

        var page = ProcessPageBudgeter.Bound(Items(10), requestedLimit: null, limits);

        Assert.HasCount(3, page.Items);
        Assert.AreEqual(8L, page.Items[0].Sequence);
        Assert.AreEqual(10L, page.Items[2].Sequence);
        Assert.AreEqual(DetailPageTruncation.ScanBudget, page.Truncation);
        Assert.AreEqual(8L, page.ScannedBackThroughSequence);
        Assert.AreEqual(8L, page.NextBeforeSequence);
        Assert.IsTrue(page.HasMoreBefore);
    }

    [TestMethod]
    public void Bound_EmptyDisplayPage_StillAdvancesScanBoundary()
    {
        var limits = ConversationReadModelLimits.Default with { DetailPageCandidateScanLimit = 4 };
        var candidates = Enumerable.Range(1, 10).Select(i => Item(i, displayable: false)).ToArray();

        var page = ProcessPageBudgeter.Bound(candidates, requestedLimit: null, limits);

        Assert.IsEmpty(page.Items);
        Assert.AreEqual(4, page.OmittedNonDisplayableCount);
        Assert.AreEqual(7L, page.ScannedBackThroughSequence);
        Assert.AreEqual(7L, page.NextBeforeSequence,
            "空展示页也必须给出可前进的排他边界，调用方不能原地重试");
        Assert.IsTrue(page.HasMoreBefore);
        Assert.AreEqual(DetailPageTruncation.ScanBudget, page.Truncation);
    }

    [TestMethod]
    public void Bound_ExhaustedWindow_WithNoDisplayableItems_ReportsTerminalCursor()
    {
        var candidates = Enumerable.Range(1, 5).Select(i => Item(i, displayable: false)).ToArray();

        var page = ProcessPageBudgeter.Bound(candidates);

        Assert.IsEmpty(page.Items);
        Assert.AreEqual(5, page.OmittedNonDisplayableCount);
        Assert.AreEqual(1L, page.ScannedBackThroughSequence);
        Assert.IsNull(page.NextBeforeSequence);
        Assert.IsFalse(page.HasMoreBefore);
        Assert.AreEqual(DetailPageTruncation.None, page.Truncation);
    }

    [TestMethod]
    public void Bound_ExhaustedWindow_WithDisplayableItems_ReturnsAscendingPage()
    {
        var page = ProcessPageBudgeter.Bound(Items(5));

        Assert.HasCount(5, page.Items);
        for (var i = 0; i < 5; i++)
            Assert.AreEqual(i + 1L, page.Items[i].Sequence);

        Assert.AreEqual(1L, page.ScannedBackThroughSequence);
        Assert.IsNull(page.NextBeforeSequence);
        Assert.IsFalse(page.HasMoreBefore);
        Assert.AreEqual(DetailPageTruncation.None, page.Truncation);
    }

    [TestMethod]
    public void Bound_SkipsNonDisplayableCandidatesMidWindow()
    {
        ProcessItemCandidate[] candidates =
        [
            Item(1),
            Item(2, displayable: false),
            Item(3),
            Item(4, displayable: false),
            Item(5),
        ];

        var page = ProcessPageBudgeter.Bound(candidates);

        Assert.HasCount(3, page.Items);
        Assert.AreEqual(1L, page.Items[0].Sequence);
        Assert.AreEqual(3L, page.Items[1].Sequence);
        Assert.AreEqual(5L, page.Items[2].Sequence);
        Assert.AreEqual(2, page.OmittedNonDisplayableCount);
        Assert.AreEqual(1L, page.ScannedBackThroughSequence);
        Assert.IsNull(page.NextBeforeSequence);
        Assert.IsFalse(page.HasMoreBefore);
        Assert.AreEqual(DetailPageTruncation.None, page.Truncation);
    }

    [TestMethod]
    public void Bound_UsesConfiguredGiantPreviewBudget()
    {
        var limits = ConversationReadModelLimits.Default with { GiantEventPreviewBytes = 4 };

        var page = ProcessPageBudgeter.Bound([Item(1, "abcdef", payloadBytes: 6)], requestedLimit: null, limits);

        var item = page.Items[0];
        Assert.IsTrue(item.PreviewTruncated);
        Assert.AreEqual("abcd", item.Preview);
        Assert.AreEqual(6, item.OriginalPayloadBytes);
    }

    [TestMethod]
    public void Bound_ShortPreview_IsNotTruncated()
    {
        var page = ProcessPageBudgeter.Bound([Item(7, "abc", payloadBytes: 3)]);

        var item = page.Items[0];
        Assert.IsFalse(item.PreviewTruncated);
        Assert.AreEqual("abc", item.Preview);
        Assert.AreEqual(3, item.OriginalPayloadBytes);
        Assert.AreEqual(7L, item.Sequence);
    }

    [TestMethod]
    public void DetailPageTruncation_FrozenEnumValues()
    {
        Assert.AreEqual(0, (int)DetailPageTruncation.None);
        Assert.AreEqual(1, (int)DetailPageTruncation.ItemLimit);
        Assert.AreEqual(2, (int)DetailPageTruncation.ByteBudget);
        Assert.AreEqual(3, (int)DetailPageTruncation.ScanBudget);
    }
}
