namespace PuddingFullTextIndex.Contracts;

/// <summary>
/// 语料清点结果（只读；Plan 与 job 的 Discovering 阶段共用同一口径）。
/// </summary>
/// <param name="FileCount">按索引口径可索引的文件数。</param>
/// <param name="TotalBytes">这些文件的字节总量。</param>
/// <param name="BytesByExtension">按扩展名（含点、小写）聚合的字节数，用于定位体积来源。</param>
public sealed record SupplyInventory(
    int FileCount,
    long TotalBytes,
    IReadOnlyDictionary<string, long> BytesByExtension);

/// <summary>
/// 可注入的语料清点端口。
/// <para>
/// 真实实现<b>必须复用</b> <see cref="FullTextIndexOptions.IsIndexableExtension"/> /
/// <see cref="FullTextIndexOptions.IsExcludedPath"/> 与 <c>PathNoiseRules</c> 派生的排除清单
/// （禁止自造第二套排除规则）；替身实现用于让 Plan/job 计数确定化。
/// </para>
/// </summary>
public interface IFullTextIndexSupplyInventory
{
    /// <summary>清点 <paramref name="rootPath"/> 下的可索引语料。<paramref name="rootPath"/> 必须存在且是目录。</summary>
    Task<SupplyInventory> MeasureAsync(string rootPath, CancellationToken ct = default);
}
