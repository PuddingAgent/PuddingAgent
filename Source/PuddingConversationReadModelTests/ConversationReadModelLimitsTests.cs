#pragma warning disable MSTEST0032 // frozen contract constant assertions are intentional

namespace PuddingConversationReadModelTests;

/// <summary>
/// 冻结预算与配置校验测试（Plan §5.4 / §3.3；冻结合同 <see cref="ConversationReadModelLimits"/>）。
/// </summary>
[TestClass]
public sealed class ConversationReadModelLimitsTests
{
    [TestMethod]
    public void Defaults_MatchFrozenBudgetTable()
    {
        var limits = ConversationReadModelLimits.Default;

        Assert.AreEqual(1, ConversationReadModelLimits.CurrentReadModelVersion);
        Assert.AreEqual(1, ConversationReadModelLimits.HistoryOrderVersion);
        Assert.AreEqual(256, ConversationReadModelLimits.MessageJsonOverheadBytes);
        Assert.AreEqual(128, ConversationReadModelLimits.ProcessItemJsonOverheadBytes);

        Assert.AreEqual(20, limits.OpenMessageLimit);
        Assert.AreEqual(50, limits.MaxOpenMessageLimit);
        Assert.AreEqual(128 * 1024, limits.OpenPageMaxBytes);
        Assert.AreEqual(8 * 1024, limits.SingleMessagePreviewBytes);
        Assert.AreEqual(16 * 1024, limits.ActivePreviewMaxBytes);
        Assert.AreEqual(16 * 1024, limits.GiantEventPreviewBytes);

        Assert.AreEqual(100, limits.DetailPageItemLimit);
        Assert.AreEqual(200, limits.MaxDetailPageItems);
        Assert.AreEqual(256 * 1024, limits.DetailPageMaxBytes);
        Assert.AreEqual(400, limits.DetailPageCandidateScanLimit);

        Assert.AreEqual(20, limits.SubAgentSummaryLimit);
        Assert.AreEqual(32 * 1024, limits.SubAgentSummaryMaxBytes);
        Assert.AreEqual(200, limits.HistoryPageCandidateScanLimit);

        Assert.AreEqual(2_000, limits.SseCatchUpMaxEvents);
        Assert.AreEqual(2 * 1024 * 1024, limits.SseCatchUpMaxBytes);
        Assert.AreEqual(TimeSpan.FromSeconds(1), limits.SseCatchUpMaxDuration);

        Assert.AreEqual(5, limits.CacheSessionLimit);
        Assert.AreEqual(5L * 1024 * 1024, limits.CacheMaxBytes);
        Assert.AreEqual(50, limits.BackfillBatchSize);
        Assert.AreEqual(TimeSpan.FromMilliseconds(50), limits.BackfillBatchWorkBudget);
        Assert.AreEqual(TimeSpan.FromMilliseconds(200), limits.BackfillBatchDelay);
        Assert.AreEqual(TimeSpan.FromSeconds(60), limits.BackfillReadyGracePeriod);
    }

    [TestMethod]
    [DataRow(null, 20)]
    [DataRow(0, 1)]
    [DataRow(-7, 1)]
    [DataRow(1, 1)]
    [DataRow(20, 20)]
    [DataRow(50, 50)]
    [DataRow(999, 50)]
    public void NormalizeOpenMessageLimit_ClampsIntoFrozenRange(int? requested, int expected)
        => Assert.AreEqual(expected, ConversationReadModelLimits.Default.NormalizeOpenMessageLimit(requested));

    [TestMethod]
    public void NormalizeOpenMessageLimit_UsesConfiguredDefaultsAndMaximum()
    {
        var limits = ConversationReadModelLimits.Default with { OpenMessageLimit = 10, MaxOpenMessageLimit = 30 };

        Assert.AreEqual(10, limits.NormalizeOpenMessageLimit(null));
        Assert.AreEqual(30, limits.NormalizeOpenMessageLimit(999));
    }

    [TestMethod]
    [DataRow(null, 100)]
    [DataRow(0, 1)]
    [DataRow(-3, 1)]
    [DataRow(1, 1)]
    [DataRow(100, 100)]
    [DataRow(200, 200)]
    [DataRow(999, 200)]
    public void NormalizeDetailItemLimit_ClampsIntoFrozenRange(int? requested, int expected)
        => Assert.AreEqual(expected, ConversationReadModelLimits.Default.NormalizeDetailItemLimit(requested));

    [TestMethod]
    public void NormalizeDetailItemLimit_UsesConfiguredDefaultsAndMaximum()
    {
        var limits = ConversationReadModelLimits.Default with { DetailPageItemLimit = 40, MaxDetailPageItems = 60 };

        Assert.AreEqual(40, limits.NormalizeDetailItemLimit(null));
        Assert.AreEqual(60, limits.NormalizeDetailItemLimit(999));
    }

    [TestMethod]
    public void Validate_DefaultLimits_Passes()
        => Assert.IsEmpty(ConversationReadModelLimits.Default.Validate());

    [TestMethod]
    public void Validate_ValidCustomLimits_Passes()
    {
        var limits = ConversationReadModelLimits.Default with
        {
            OpenMessageLimit = 30,
            MaxOpenMessageLimit = 40,
            DetailPageItemLimit = 150,
            MaxDetailPageItems = 160,
            SseCatchUpMaxEvents = 100,
        };

        Assert.IsEmpty(limits.Validate());
    }

    [TestMethod]
    public void Validate_ReportsEveryInconsistentBudget()
    {
        var cases = new (ConversationReadModelLimits Limits, string Field)[]
        {
            (ConversationReadModelLimits.Default with { OpenMessageLimit = 60 }, "OpenMessageLimit"),
            (ConversationReadModelLimits.Default with { OpenMessageLimit = 0 }, "OpenMessageLimit"),
            (ConversationReadModelLimits.Default with { MaxOpenMessageLimit = 0 }, "MaxOpenMessageLimit"),
            (ConversationReadModelLimits.Default with { OpenPageMaxBytes = -1 }, "OpenPageMaxBytes"),
            (ConversationReadModelLimits.Default with { SingleMessagePreviewBytes = 0 }, "SingleMessagePreviewBytes"),
            (ConversationReadModelLimits.Default with { ActivePreviewMaxBytes = -1 }, "ActivePreviewMaxBytes"),
            (ConversationReadModelLimits.Default with { GiantEventPreviewBytes = -1 }, "GiantEventPreviewBytes"),
            (ConversationReadModelLimits.Default with { DetailPageItemLimit = 300 }, "DetailPageItemLimit"),
            (ConversationReadModelLimits.Default with { MaxDetailPageItems = 0 }, "MaxDetailPageItems"),
            (ConversationReadModelLimits.Default with { DetailPageMaxBytes = -1 }, "DetailPageMaxBytes"),
            (ConversationReadModelLimits.Default with { DetailPageCandidateScanLimit = 0 }, "DetailPageCandidateScanLimit"),
            (ConversationReadModelLimits.Default with { HistoryPageCandidateScanLimit = 0 }, "HistoryPageCandidateScanLimit"),
            (ConversationReadModelLimits.Default with { SubAgentSummaryLimit = 0 }, "SubAgentSummaryLimit"),
            (ConversationReadModelLimits.Default with { SubAgentSummaryMaxBytes = -1 }, "SubAgentSummaryMaxBytes"),
            (ConversationReadModelLimits.Default with { SseCatchUpMaxEvents = 0 }, "SseCatchUpMaxEvents"),
            (ConversationReadModelLimits.Default with { SseCatchUpMaxBytes = -1 }, "SseCatchUpMaxBytes"),
            (ConversationReadModelLimits.Default with { SseCatchUpMaxDuration = TimeSpan.FromMilliseconds(-1) }, "SseCatchUpMaxDuration"),
            (ConversationReadModelLimits.Default with { CacheSessionLimit = 0 }, "CacheSessionLimit"),
            (ConversationReadModelLimits.Default with { CacheMaxBytes = -1 }, "CacheMaxBytes"),
            (ConversationReadModelLimits.Default with { BackfillBatchSize = 0 }, "BackfillBatchSize"),
            (ConversationReadModelLimits.Default with { BackfillBatchWorkBudget = TimeSpan.FromMilliseconds(-1) }, "BackfillBatchWorkBudget"),
            (ConversationReadModelLimits.Default with { BackfillBatchDelay = TimeSpan.FromMilliseconds(-1) }, "BackfillBatchDelay"),
            (ConversationReadModelLimits.Default with { BackfillReadyGracePeriod = TimeSpan.FromMilliseconds(-1) }, "BackfillReadyGracePeriod"),
        };

        foreach (var (limits, field) in cases)
        {
            var violations = limits.Validate();
            Assert.IsNotEmpty(violations, $"expected a violation for {field}");
            Assert.IsTrue(
                violations.Any(v => v.Contains(field, StringComparison.Ordinal)),
                $"violations must name '{field}': {string.Join(" | ", violations)}");
        }
    }

    [TestMethod]
    public void Validate_ReportsMultipleViolations()
    {
        var limits = ConversationReadModelLimits.Default with
        {
            OpenMessageLimit = 60,
            DetailPageCandidateScanLimit = 0,
            OpenPageMaxBytes = -1,
        };

        var violations = limits.Validate();

        Assert.IsGreaterThanOrEqualTo(3, violations.Count);
    }
}
