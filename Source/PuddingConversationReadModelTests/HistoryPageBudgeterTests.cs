namespace PuddingConversationReadModelTests;

/// <summary>
/// 历史页预算器（Plan §5.2、§5.4；冻结合同 <see cref="HistoryPageBudgeter"/> 及其 DTO 的 XML 文档）。
/// </summary>
[TestClass]
public sealed class HistoryPageBudgeterTests
{
    private static HistoryPageCandidate Candidate(string messageId, string content = "x", bool displayable = true, string role = "user")
        => new()
        {
            MessageId = messageId,
            Role = role,
            Content = content,
            CreatedAt = DateTimeOffset.UnixEpoch,
            IsDisplayable = displayable,
        };

    private static HistoryPageCandidate[] Candidates(int count)
        => Enumerable.Range(0, count).Select(i => Candidate($"m{i}")).ToArray();

    [TestMethod]
    public void Bound_DefaultLimit_ReturnsAtMostTwentyNewestFirst()
    {
        var page = HistoryPageBudgeter.Bound(Candidates(30));

        Assert.HasCount(20, page.Items);
        Assert.AreEqual("m0", page.Items[0].MessageId);
        Assert.AreEqual("m19", page.Items[19].MessageId);
        Assert.AreEqual(30, page.CandidateCount);
        Assert.AreEqual(10, page.OmittedForItemLimitCount);
        Assert.AreEqual(0, page.OmittedForByteBudgetCount);
        Assert.AreEqual(0, page.SkippedNonDisplayableCount);
        Assert.AreEqual(0, page.DuplicateCount);
        Assert.AreEqual(20, page.TotalPreviewBytes);
        Assert.IsTrue(page.HasMore);
    }

    [TestMethod]
    public void Bound_RequestedLimit_ClampsToFrozenMaximum()
    {
        var page = HistoryPageBudgeter.Bound(Candidates(60), requestedLimit: 999);

        Assert.HasCount(50, page.Items);
        Assert.AreEqual(10, page.OmittedForItemLimitCount);
        Assert.IsTrue(page.HasMore);
    }

    [TestMethod]
    public void Bound_ConfiguredLimits_DriveDefaultAndMaximum()
    {
        var limits = ConversationReadModelLimits.Default with { OpenMessageLimit = 5, MaxOpenMessageLimit = 10 };
        var candidates = Candidates(20);

        var defaultPage = HistoryPageBudgeter.Bound(candidates, requestedLimit: null, limits);
        Assert.HasCount(5, defaultPage.Items);

        var clampedPage = HistoryPageBudgeter.Bound(candidates, requestedLimit: 999, limits);
        Assert.HasCount(10, clampedPage.Items);
    }

    [TestMethod]
    public void Bound_TruncatesPreviewToEightKiB_OnUtf8Boundary()
    {
        var content = new string('文', 4_000); // 12000 UTF-8 bytes

        var page = HistoryPageBudgeter.Bound([Candidate("m0", content)]);

        Assert.HasCount(1, page.Items);
        var item = page.Items[0];
        Assert.IsTrue(item.PreviewTruncated);
        Assert.AreEqual(8_190, TestData.Utf8Bytes(item.Preview));
        Assert.AreEqual(new string('文', 2_730), item.Preview);
        Assert.AreEqual(12_000, item.OriginalContentBytes);
        TestData.AssertNoReplacementCharacters(item.Preview);
        Assert.AreEqual(8_190, page.TotalPreviewBytes);
    }

    [TestMethod]
    public void Bound_DoesNotTruncateShortPreview()
    {
        var page = HistoryPageBudgeter.Bound([Candidate("m0", "你好")]);

        var item = page.Items[0];
        Assert.IsFalse(item.PreviewTruncated);
        Assert.AreEqual("你好", item.Preview);
        Assert.AreEqual(6, item.OriginalContentBytes);
        Assert.AreEqual(6, page.TotalPreviewBytes);
    }

    [TestMethod]
    public void Bound_ContentExactlyAtPreviewLimit_IsNotTruncated()
    {
        var content = new string('x', 8_192);

        var page = HistoryPageBudgeter.Bound([Candidate("m0", content)]);

        var item = page.Items[0];
        Assert.IsFalse(item.PreviewTruncated);
        Assert.AreEqual(8_192, TestData.Utf8Bytes(item.Preview));
        Assert.AreEqual(8_192, item.OriginalContentBytes);
    }

    [TestMethod]
    public void Bound_PreviewTruncation_DoesNotSplitEmoji()
    {
        var content = "a" + TestData.Repeat("😀", 5_000); // 20001 bytes, emoji run starts at byte 1

        var page = HistoryPageBudgeter.Bound([Candidate("m0", content)]);

        var item = page.Items[0];
        Assert.IsTrue(item.PreviewTruncated);
        Assert.AreEqual(8_189, TestData.Utf8Bytes(item.Preview));
        Assert.AreEqual("a" + TestData.Repeat("😀", 2_047), item.Preview);
        TestData.AssertNoReplacementCharacters(item.Preview);
    }

    [TestMethod]
    public void Bound_SkipsNonDisplayableCandidates()
    {
        HistoryPageCandidate[] candidates =
        [
            Candidate("m0"),
            Candidate("m1", displayable: false),
            Candidate("m2"),
            Candidate("m3", displayable: false),
            Candidate("m4"),
        ];

        var page = HistoryPageBudgeter.Bound(candidates);

        Assert.HasCount(3, page.Items);
        Assert.AreEqual("m0", page.Items[0].MessageId);
        Assert.AreEqual("m2", page.Items[1].MessageId);
        Assert.AreEqual("m4", page.Items[2].MessageId);
        Assert.AreEqual(5, page.CandidateCount);
        Assert.AreEqual(2, page.SkippedNonDisplayableCount);
        Assert.AreEqual(0, page.OmittedForItemLimitCount);
        Assert.AreEqual(0, page.OmittedForByteBudgetCount);
        Assert.IsFalse(page.HasMore);
    }

    [TestMethod]
    public void Bound_DeduplicatesMessageIds_KeepingFirstOccurrence()
    {
        HistoryPageCandidate[] candidates =
        [
            Candidate("a"),
            Candidate("b"),
            Candidate("a"),
            Candidate("c"),
            Candidate("b"),
        ];

        var page = HistoryPageBudgeter.Bound(candidates);

        Assert.HasCount(3, page.Items);
        Assert.AreEqual("a", page.Items[0].MessageId);
        Assert.AreEqual("b", page.Items[1].MessageId);
        Assert.AreEqual("c", page.Items[2].MessageId);
        Assert.AreEqual(2, page.DuplicateCount);
        Assert.AreEqual(5, page.CandidateCount);
        Assert.IsFalse(page.HasMore);
    }

    [TestMethod]
    public void Bound_StopsAtPageByteBudget()
    {
        // 每项 4096 字节正文 + 256 字节 JSON 开销 = 4352；128 KiB 最多容纳 30 项。
        var content = new string('x', 4_096);
        var candidates = Enumerable.Range(0, 50).Select(i => Candidate($"m{i}", content)).ToArray();

        var page = HistoryPageBudgeter.Bound(candidates, requestedLimit: 50);

        Assert.HasCount(30, page.Items);
        Assert.AreEqual(0, page.OmittedForItemLimitCount);
        Assert.AreEqual(20, page.OmittedForByteBudgetCount);
        Assert.AreEqual(30 * 4_096, page.TotalPreviewBytes);
        Assert.IsTrue(page.HasMore);
    }

    [TestMethod]
    public void Bound_ByteBudget_IncludesJsonOverheadEstimate()
    {
        var limits = ConversationReadModelLimits.Default with { OpenPageMaxBytes = 1_024 };
        var content = new string('x', 200); // 200 + 256 = 456 每项
        var candidates = Enumerable.Range(0, 3).Select(i => Candidate($"m{i}", content)).ToArray();

        var page = HistoryPageBudgeter.Bound(candidates, requestedLimit: 50, limits);

        Assert.HasCount(2, page.Items);
        Assert.AreEqual(0, page.OmittedForItemLimitCount);
        Assert.AreEqual(1, page.OmittedForByteBudgetCount);
        Assert.AreEqual(400, page.TotalPreviewBytes);
        Assert.IsTrue(page.HasMore);
    }

    [TestMethod]
    public void Bound_ItemLimitReachedBeforeByteBudget_ReportsItemLimitOmissions()
    {
        var content = new string('x', 10_000); // 截断到 8192
        var candidates = Enumerable.Range(0, 3).Select(i => Candidate($"m{i}", content)).ToArray();

        var page = HistoryPageBudgeter.Bound(candidates, requestedLimit: 2);

        Assert.HasCount(2, page.Items);
        Assert.AreEqual(1, page.OmittedForItemLimitCount);
        Assert.AreEqual(0, page.OmittedForByteBudgetCount);
        Assert.IsTrue(page.HasMore);
        Assert.AreEqual(2 * 8_192, page.TotalPreviewBytes);
    }

    [TestMethod]
    public void Bound_EmptyCandidateWindow_ReturnsEmptyPage()
    {
        var page = HistoryPageBudgeter.Bound(Array.Empty<HistoryPageCandidate>());

        Assert.IsEmpty(page.Items);
        Assert.AreEqual(0, page.CandidateCount);
        Assert.AreEqual(0, page.SkippedNonDisplayableCount);
        Assert.AreEqual(0, page.DuplicateCount);
        Assert.AreEqual(0, page.OmittedForItemLimitCount);
        Assert.AreEqual(0, page.OmittedForByteBudgetCount);
        Assert.AreEqual(0, page.TotalPreviewBytes);
        Assert.IsFalse(page.HasMore);
    }

    [TestMethod]
    public void Bound_AllCandidatesNonDisplayable_ReturnsEmptyPageWithoutHasMore()
    {
        HistoryPageCandidate[] candidates =
        [
            Candidate("m0", displayable: false),
            Candidate("m1", displayable: false),
            Candidate("m2", displayable: false),
        ];

        var page = HistoryPageBudgeter.Bound(candidates);

        Assert.IsEmpty(page.Items);
        Assert.AreEqual(3, page.SkippedNonDisplayableCount);
        Assert.AreEqual(3, page.CandidateCount);
        Assert.IsFalse(page.HasMore);
    }

    [TestMethod]
    public void Bound_PreservesCandidateOrderAndMetadata()
    {
        HistoryPageCandidate[] candidates =
        [
            Candidate("m0", role: "assistant"),
            Candidate("m1", role: "user"),
        ];

        var page = HistoryPageBudgeter.Bound(candidates);

        Assert.AreEqual("m0", page.Items[0].MessageId);
        Assert.AreEqual("assistant", page.Items[0].Role);
        Assert.AreEqual("m1", page.Items[1].MessageId);
        Assert.AreEqual("user", page.Items[1].Role);
        Assert.IsNull(page.Items[0].TurnId);
    }
}
