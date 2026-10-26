using PuddingFinance.Collection;

namespace PuddingFinanceTests;

/// <summary>
/// 选标规则的回归。标题全部取自 2026-10-26 巨潮真实回执，
/// 特别是「A 股版与 H 股版并存」这一组——那是最容易静默选错的场景。
/// </summary>
[TestClass]
public sealed class AnnouncementSelectorTests
{
    [TestMethod]
    public void Classify_SemiAnnualBeforeAnnual()
    {
        // 「半年度报告」也包含「年度报告」子串，顺序错了就会把半年报当年报。
        Assert.AreEqual(AnnouncementKind.SemiAnnualReport, AnnouncementSelector.Classify("工商银行2025年半年度报告"));
        Assert.AreEqual(AnnouncementKind.SemiAnnualReport, AnnouncementSelector.Classify("工商银行2025年中期报告"));
        Assert.AreEqual(AnnouncementKind.AnnualReport, AnnouncementSelector.Classify("工商银行2025年年度报告"));
    }

    [TestMethod]
    public void Classify_OtherKinds()
    {
        Assert.AreEqual(AnnouncementKind.QuarterlyReport, AnnouncementSelector.Classify("工商银行2026年第一季度报告"));
        Assert.AreEqual(AnnouncementKind.Dividend, AnnouncementSelector.Classify("工商银行A股2025年年度分红派息实施公告"));
        Assert.AreEqual(AnnouncementKind.Other, AnnouncementSelector.Classify("工商银行关于召开2025年度股东会的通知"));
        Assert.AreEqual(AnnouncementKind.Other, AnnouncementSelector.Classify(null));
    }

    [TestMethod]
    public void Select_WhenAShareAndHongKongBothExist_PicksAShare()
    {
        var selection = AnnouncementSelector.Select(
        [
            Item("工商银行2025年年度报告", "2026-03-28"),
            Item("工商银行H股公告-2025年度报告", "2026-03-28"),
        ], AnnouncementKind.AnnualReport);

        Assert.IsTrue(selection.Found);
        Assert.AreEqual("工商银行2025年年度报告", selection.Selected!.Title);
        Assert.IsTrue(selection.HasHongKongTwin, "必须知道存在被排除的 H 股版");
        StringAssert.Contains(selection.Describe()!, "排除了 1 份 H 股版");
    }

    [TestMethod]
    public void Select_OnlyHongKongVersion_ReportsExclusionInsteadOfEmpty()
    {
        var selection = AnnouncementSelector.Select(
            [Item("工商银行H股公告-2025年度报告", "2026-04-23")],
            AnnouncementKind.AnnualReport);

        Assert.IsFalse(selection.Found);
        Assert.HasCount(1, selection.ExcludedHongKongVersions);
        StringAssert.Contains(selection.Describe()!, "都被排除");
    }

    [TestMethod]
    public void Select_MultipleAnnualReports_TakesLatest()
    {
        var selection = AnnouncementSelector.Select(
        [
            Item("工商银行2024年年度报告", "2025-03-28"),
            Item("工商银行2025年年度报告", "2026-03-28"),
            Item("工商银行2025年年度报告（更正版）", "2026-04-10"),
        ], AnnouncementKind.AnnualReport);

        Assert.IsTrue(selection.Found);
        // 「更正版」被 NonFullTextMarkers 排除，因此仍选正式版；
        // 这条断言钉的是"排除规则优先于日期排序"。
        Assert.AreEqual("工商银行2025年年度报告", selection.Selected!.Title);
        Assert.HasCount(1, selection.ExcludedOthers);
    }

    [TestMethod]
    public void Select_SummaryIsNotFullText()
    {
        var selection = AnnouncementSelector.Select(
            [Item("工商银行2025年年度报告摘要", "2026-03-28")],
            AnnouncementKind.AnnualReport);

        Assert.IsFalse(selection.Found);
        Assert.HasCount(1, selection.ExcludedOthers);
    }

    [TestMethod]
    public void Select_ItemWithoutPdfIsNotSelected()
    {
        var selection = AnnouncementSelector.Select(
        [
            new CninfoAnnouncement("601398", "工商银行", "jjxt0000019", "1225",
                "工商银行2025年年度报告", DateTimeOffset.Parse("2026-03-28T00:00:00+08:00"),
                PdfUrl: null, SizeKb: 10504, AdjunctType: "PDF"),
        ], AnnouncementKind.AnnualReport);

        Assert.IsFalse(selection.Found, "没有 PDF 就无法穿透，不能当选");
        Assert.HasCount(1, selection.ExcludedOthers);
    }

    [TestMethod]
    public void Select_EmptyBatch_ReportsNoSuchKind()
    {
        var selection = AnnouncementSelector.Select([], AnnouncementKind.AnnualReport);

        Assert.IsFalse(selection.Found);
        StringAssert.Contains(selection.Describe()!, "没有该类");
    }

    private static CninfoAnnouncement Item(string title, string date)
    {
        return new CninfoAnnouncement(
            "601398",
            "工商银行",
            "jjxt0000019",
            Guid.NewGuid().ToString("N")[..10],
            title,
            DateTimeOffset.Parse($"{date}T00:00:00+08:00"),
            $"http://static.cninfo.com.cn/finalpage/{date}/{Guid.NewGuid():N}.PDF",
            SizeKb: 100,
            AdjunctType: "PDF");
    }
}
