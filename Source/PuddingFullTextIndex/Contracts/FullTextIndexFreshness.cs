namespace PuddingFullTextIndex.Contracts;

/// <summary>
/// 某个 scope 的索引**新鲜度读数**的三态（A23）。
/// <para>
/// 三态必须可区分 —— 与 <see cref="IndexDocumentProbe"/> 同一条教训（2026-09-25 事故）：
/// 「索引不存在」「索引存在但新鲜度<b>读不出</b>」「索引存在且新鲜度可读」在判据上完全不同。
/// 把「读不出」伪报成「刚建过」会让调用方**放心使用陈旧索引**（假绿）；
/// 把「读不出」伪报成「很旧」会让它做无谓重建。两者都不得发生。
/// </para>
/// </summary>
public enum FullTextIndexFreshnessState
{
    /// <summary>该 scope 的索引目录不存在（从未构建，或已被删除）。</summary>
    Missing,

    /// <summary>索引目录存在，但 <c>.last_indexed</c> 缺失或损坏 ⇒ <b>读不出</b>（<b>不得</b>伪报时间）。</summary>
    StampUnreadable,

    /// <summary>索引目录存在且 <c>.last_indexed</c> 可解析。</summary>
    Available,
}

/// <summary>
/// 索引新鲜度读数（**只读快照**，不含任何判定策略 —— 「该不该重建」仍由
/// <c>IndexPrebuildFreshness.ShouldRebuild</c> 裁决）。
/// <para>
/// <b>两个时间各有口径，不可混用</b>：
/// <list type="bullet">
/// <item><description><see cref="LastIndexedAtUtc"/> 取自 <c>.last_indexed</c>
/// （<c>{"t":…,"p":…}</c>）里的 <c>t</c>，语义是「上次索引<b>扫描</b>的时刻」——
/// 引擎自身的增量构建比较基准。</description></item>
/// <item><description><see cref="IndexDirectoryLastWriteUtc"/> 是**该 scope 自己的** live 索引目录 mtime，
/// 语义是「索引产物最后一次被写入的时刻」。这是既有 per-scope 新鲜度判定的口径
/// （<c>IFullTextIndexSupplyComposition.LiveIndexLastWriteUtc</c> → <c>IndexPrebuildFreshness.ShouldRebuild</c>），
/// 本读数与之保持一致：只上报事实，不复刻、不改写其语义。
/// ⚠️ 历史缺陷：早期读的是**索引根** mtime ⇒ 任一 scope 建索引会让其余 scope 集体变「新鲜」而全部跳过重建。</description></item>
/// </list>
/// </para>
/// </summary>
/// <param name="State">三态之一。</param>
/// <param name="LastIndexedAtUtc">上次索引扫描时刻（UTC）；读不出时为 <c>null</c>。</param>
/// <param name="PatternFingerprint">上次构建所用文件模式的 12 位指纹；读不出时为 <c>null</c>。</param>
/// <param name="IndexDirectoryLastWriteUtc">该 scope live 索引目录 mtime（UTC）；读不到时为 <c>null</c>。</param>
public readonly record struct FullTextIndexFreshness(
    FullTextIndexFreshnessState State,
    DateTimeOffset? LastIndexedAtUtc,
    string? PatternFingerprint,
    DateTimeOffset? IndexDirectoryLastWriteUtc)
{
    /// <summary>
    /// 「上次索引扫描」距今多久；<see cref="LastIndexedAtUtc"/> 读不出（<c>null</c>）时同样返回 <c>null</c>
    /// —— <b>不</b>用「极大年龄」冒充（那是判定策略，属于调用方）。
    /// </summary>
    /// <param name="nowUtc">当前时间（UTC），由调用方注入以便测试。</param>
    public TimeSpan? StampAgeAt(DateTimeOffset nowUtc) =>
        LastIndexedAtUtc is { } stamp ? nowUtc - stamp : null;

    /// <summary>单行摘要（进消息与断言；不用区域性数字格式，避免消息随区域变化）。</summary>
    public override string ToString() => State switch
    {
        FullTextIndexFreshnessState.Missing => "missing",
        FullTextIndexFreshnessState.StampUnreadable =>
            string.Concat("stamp-unreadable, indexDirMtime=", Format(IndexDirectoryLastWriteUtc)),
        _ => string.Concat(
            "available, lastIndexed=", Format(LastIndexedAtUtc),
            ", pattern=", PatternFingerprint ?? "<null>",
            ", indexDirMtime=", Format(IndexDirectoryLastWriteUtc)),
    };

    private static string Format(DateTimeOffset? value) =>
        value?.UtcDateTime.ToString("O", System.Globalization.CultureInfo.InvariantCulture) ?? "<null>";
}
