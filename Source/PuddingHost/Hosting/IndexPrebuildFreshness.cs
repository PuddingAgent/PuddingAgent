namespace PuddingHost.Hosting;

/// <summary>
/// U4-7：<see cref="FullTextIndexSupplyOptions.MinRebuildInterval"/> 的**消费点** ——
/// 「已有索引足够新则跳过重建」。纯函数，可独立单测（不碰时钟、不碰磁盘）。
/// <para>
/// ⚠️ **仪器口径（S5 起，2026-09-25 修正）**：「索引新旧」由调用方传入，口径是
/// **该 scope 自己的 live 索引目录** <c>&lt;IndexRoot&gt;/&lt;64位hex&gt;</c> 的 mtime
/// （宿主经 <c>IFullTextIndexSupplyComposition.LiveIndexLastWriteUtc</c> 取得，
/// 目录映射来自组件内的单一真源 <c>IFullTextIndexRootedEngine.ResolveIndexDirectory</c>）。
/// <para>
/// 历史缺陷（U4-7）：那时读的是**索引根目录** mtime —— 只要建出任何一个 scope 的索引目录，
/// 整个索引根就变新，其余 scope 会被**集体误判为新鲜**而全部跳过。per-scope 口径修掉了它
/// （A scope 新鲜只跳 A，不影响 B）。
/// </para>
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
