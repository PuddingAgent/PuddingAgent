namespace PuddingFinance.Collection;

/// <summary>公告类别。只覆盖研究真正会区分的那几类，不做全量分类。</summary>
public enum AnnouncementKind
{
    /// <summary>年度报告（全文，A 股口径）。</summary>
    AnnualReport,

    /// <summary>半年度报告 / 中期报告。</summary>
    SemiAnnualReport,

    /// <summary>季度报告。</summary>
    QuarterlyReport,

    /// <summary>分红派息。</summary>
    Dividend,

    /// <summary>其余（含股东会通知、债券发行、法律意见书等）。</summary>
    Other,
}

/// <summary>
/// 选标结果。<see cref="ExcludedHongKongVersions"/> 与 <see cref="ExcludedOthers"/> 不是装饰：
/// 巨潮同日会同时挂 A 股版与 H 股版（繁体、按 IFRS），只看标题盲选会选错版本，
/// 而错版本**不会报错**——所以"排除了什么"必须能被调用方看到。
/// </summary>
public sealed record AnnouncementSelection(
    AnnouncementKind Kind,
    CninfoAnnouncement? Selected,
    IReadOnlyList<CninfoAnnouncement> ExcludedHongKongVersions,
    IReadOnlyList<CninfoAnnouncement> ExcludedOthers)
{
    /// <summary>存在被排除的 H 股孪生件。</summary>
    public bool HasHongKongTwin => ExcludedHongKongVersions.Count > 0;

    /// <summary>是否选到了目标。</summary>
    public bool Found => Selected is not null;

    /// <summary>零结果说明：区分"确实没有"与"全被排除了"。</summary>
    public string? Describe()
    {
        if (Selected is not null)
        {
            return HasHongKongTwin
                ? $"已选 A 股版《{Selected.Title}》；同时排除了 {ExcludedHongKongVersions.Count} 份 H 股版"
                : $"已选《{Selected.Title}》";
        }

        if (ExcludedHongKongVersions.Count > 0 || ExcludedOthers.Count > 0)
        {
            return $"未选到 {Kind}：命中都被排除（H 股版 {ExcludedHongKongVersions.Count} 份、其他 {ExcludedOthers.Count} 份）";
        }

        return $"未选到 {Kind}：本批公告里没有该类";
    }
}

/// <summary>
/// 公告选标规则。纯函数：不联网、可断言，所以"哪份才是 A 股年报"这条判断
/// 从"每次看一眼"变成"每次算一遍"。
///
/// <para>
/// 规则来自 2026-10-26 的实测：同日并存「工商银行2026年第一季度报告」(337 KB)
/// 与「工商银行H股公告-2026年第一季度报告」(372 KB)，后者是**繁体 + IFRS 口径**。
/// </para>
/// </summary>
public static class AnnouncementSelector
{
    private static readonly string[] HongKongMarkers = ["H股", "H 股", "香港"];

    private static readonly string[] EnglishMarkers = ["英文", "english"];

    // 摘要不是全文；更正/补充/问询/意见书/通函/会议资料都不是正文报告。
    private static readonly string[] NonFullTextMarkers =
        ["摘要", "更正", "补充", "问询", "意见书", "通函", "会议资料", "提示性公告"];

    /// <summary>按标题判类。注意顺序：「半年度报告」也含「年度报告」，必须先判半年度。</summary>
    public static AnnouncementKind Classify(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return AnnouncementKind.Other;
        }

        var text = title.Trim();

        // 顺序不可颠倒：「半年度报告」的结尾也是「年度报告」。
        // 判据用**结尾**而不是包含：巨潮里存在《关于举行2025年年度报告网上说明会的通知》这种标题，
        // 只看包含会把它当年报——实测踩过（思源电气 002028，2026-10-26）。
        if (EndsWith(text, "半年度报告") || EndsWith(text, "中期报告"))
        {
            return AnnouncementKind.SemiAnnualReport;
        }

        if (EndsWith(text, "年度报告"))
        {
            return AnnouncementKind.AnnualReport;
        }

        if (EndsWith(text, "季度报告"))
        {
            return AnnouncementKind.QuarterlyReport;
        }

        if (Contains(text, "分红") || Contains(text, "派息"))
        {
            return AnnouncementKind.Dividend;
        }

        return AnnouncementKind.Other;
    }

    /// <summary>
    /// 在给定公告集合里选出目标类别的 A 股正文。
    /// 多份命中时取**发布日期最新**的一份（年报存在"更正后重发"的情形）。
    /// </summary>
    public static AnnouncementSelection Select(
        IEnumerable<CninfoAnnouncement>? announcements,
        AnnouncementKind kind)
    {
        ArgumentNullException.ThrowIfNull(announcements);

        var hongKong = new List<CninfoAnnouncement>();
        var others = new List<CninfoAnnouncement>();
        var candidates = new List<CninfoAnnouncement>();

        foreach (var item in announcements)
        {
            if (item is null || Classify(item.Title) != kind)
            {
                continue;
            }

            if (IsHongKongVersion(item.Title))
            {
                hongKong.Add(item);
                continue;
            }

            if (IsNotFullText(item.Title))
            {
                others.Add(item);
                continue;
            }

            if (string.IsNullOrWhiteSpace(item.PdfUrl))
            {
                // 没有 PDF 就没法穿透，归入"其他"而不是当选。
                others.Add(item);
                continue;
            }

            candidates.Add(item);
        }

        var selected = candidates
            .OrderByDescending(item => item.PublishedAt ?? DateTimeOffset.MinValue)
            .ThenByDescending(item => item.AnnouncementId, StringComparer.Ordinal)
            .FirstOrDefault();

        return new AnnouncementSelection(kind, selected, hongKong, others);
    }

    private static bool IsHongKongVersion(string title) =>
        HongKongMarkers.Any(marker => Contains(title, marker));

    private static bool IsNotFullText(string title) =>
        EnglishMarkers.Any(marker => Contains(title, marker))
        || NonFullTextMarkers.Any(marker => Contains(title, marker));

    private static bool Contains(string text, string value) =>
        text.Contains(value, StringComparison.OrdinalIgnoreCase);

    private static bool EndsWith(string text, string value) =>
        text.TrimEnd().EndsWith(value, StringComparison.OrdinalIgnoreCase);
}
