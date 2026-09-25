namespace PuddingHost.Hosting;

/// <summary>
/// U4-7：<see cref="FullTextIndexSupplyOptions.MinRebuildInterval"/> 的**消费点** ——
/// 「已有索引足够新则跳过重建」。纯函数，可独立单测（不碰时钟、不碰磁盘）。
/// <para>
/// ⚠️ **仪器口径（如实登记）**：宿主能观测到的「索引新旧」只有**索引根目录**的 mtime
/// （<c>IFullTextSearchEngine</c> 只暴露 <c>HasIndex/RemoveIndex/Build/Search</c>，
/// per-scope 索引目录映射在 <c>PuddingFullTextIndex</c> 内部且是 <c>internal</c>）。
/// 因此这个判据是**粗粒度代理**：根目录在建出新的 scope 子目录时才会变。
/// 精确到 scope 的 freshness 需要组件暴露端口，属**留白**（见交付报告）。
/// </para>
/// <para>方向性：读不到时间 / 时间早于纪元 ⇒ age 极大 ⇒ **重建**（安全方向，绝不因读不到就跳过）。</para>
/// </summary>
public static class IndexPrebuildFreshness
{
    /// <summary>是否应当（重新）构建该 scope 的索引。</summary>
    /// <param name="hasIndex">引擎自报是否已有该 scope 的索引。</param>
    /// <param name="indexLastWriteUtc">索引（根）的最后写入时间（UTC）；读不到时传 <see cref="DateTimeOffset.MinValue"/>。</param>
    /// <param name="nowUtc">当前时间（UTC），由调用方注入以便测试。</param>
    /// <param name="minRebuildInterval">最小重建间隔；<c>&lt;= Zero</c> 表示每次都重建。</param>
    public static bool ShouldRebuild(
        bool hasIndex,
        DateTimeOffset indexLastWriteUtc,
        DateTimeOffset nowUtc,
        TimeSpan minRebuildInterval)
    {
        // 没有索引 ⇒ 必须建（与间隔无关）。
        if (!hasIndex)
            return true;

        // 间隔为 0（或异常负值）⇒ 不跳过。
        if (minRebuildInterval <= TimeSpan.Zero)
            return true;

        // 已有索引且「年龄」达到间隔 ⇒ 重建；否则跳过。
        return nowUtc - indexLastWriteUtc >= minRebuildInterval;
    }
}
