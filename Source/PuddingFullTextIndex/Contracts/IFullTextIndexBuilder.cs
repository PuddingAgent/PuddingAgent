namespace PuddingFullTextIndex.Contracts;

/// <summary>
/// 一次索引构建的度量（与既有 <c>FullTextIndexResult</c> 字段一一对齐，便于上层直接对账）。
/// </summary>
/// <param name="Success">是否成功。</param>
/// <param name="IndexedFileCount">实际写入索引的文件数。</param>
/// <param name="TotalBytes">实际写入索引的语料字节数。</param>
/// <param name="ElapsedMs">构建耗时（毫秒）。</param>
/// <param name="Error">失败原因；成功为 null。</param>
public sealed record SupplyBuildResult(
    bool Success,
    int IndexedFileCount,
    long TotalBytes,
    long ElapsedMs,
    string? Error);

/// <summary>
/// 可注入的索引构建端口。协调器只认这个端口，不认具体引擎实现。
/// </summary>
public interface IFullTextIndexBuilder
{
    /// <summary>对给定 scope 执行一次构建（写索引）。调用方保证同一 scope 同一时刻只有一个 builder 在跑。</summary>
    Task<SupplyBuildResult> BuildAsync(SupplyScope scope, CancellationToken ct = default);
}
