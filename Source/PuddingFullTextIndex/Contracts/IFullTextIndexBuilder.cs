namespace PuddingFullTextIndex.Contracts;

/// <summary>
/// 一次索引构建的度量（与既有 <c>FullTextIndexResult</c> 字段一一对齐，便于上层直接对账）。
/// </summary>
/// <param name="Success">是否成功。</param>
/// <param name="IndexedFileCount">实际写入索引的文件数。</param>
/// <param name="TotalBytes">实际写入索引的语料字节数。</param>
/// <param name="ElapsedMs">构建耗时（毫秒）。</param>
/// <param name="Error">失败原因；成功为 null。</param>
/// <param name="Swap">
/// staged 供给的切换口径快照（A2a R6）；<b>直写模式</b>（<c>UseStaging=false</c>）或
/// 未发生任何切换时为 null。可观察性字段（stagingBytes/liveBytesBefore/liveBytesAfter/budgetBytes/outcome）
/// 在这里结构化给出，协调器再把它折进 job 终态消息，避免只报一句 failed。<br/>
/// ⚠️ 新增可选参数是有意为之：<c>SupplyBuildResult</c> 被 CLI 工程（本切片红线：不可改）按位置构造，
/// 只能**尾部追加带默认值**的字段，不得调整原有字段顺序或语义。
/// </param>
public sealed record SupplyBuildResult(
    bool Success,
    int IndexedFileCount,
    long TotalBytes,
    long ElapsedMs,
    string? Error,
    SupplySwapReport? Swap = null);

/// <summary>
/// 可注入的索引构建端口。协调器只认这个端口，不认具体引擎实现。
/// </summary>
public interface IFullTextIndexBuilder
{
    /// <summary>对给定 scope 执行一次构建（写索引）。调用方保证同一 scope 同一时刻只有一个 builder 在跑。</summary>
    Task<SupplyBuildResult> BuildAsync(SupplyScope scope, CancellationToken ct = default);
}
