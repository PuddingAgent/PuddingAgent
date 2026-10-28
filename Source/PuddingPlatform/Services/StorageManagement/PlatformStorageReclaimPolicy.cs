namespace PuddingPlatform.Services.StorageManagement;

/// <summary>
/// 为什么这一轮不回收空闲页。
/// </summary>
/// <remarks>
/// 每一项都必须能独立上报：把「模式不对」误报成「写者忙」，会让下一个人去等空闲窗口 ——
/// 而窗口等到了也同样无效。<see cref="NotIncrementalMode"/> 永远优先。
/// </remarks>
internal enum ReclaimSkipReason
{
    /// <summary>本轮要执行回收。</summary>
    None = 0,

    /// <summary>
    /// 库的 <c>auto_vacuum</c> 不是 INCREMENTAL。<c>PRAGMA incremental_vacuum</c> 在这种库上
    /// <b>静默无效</b>（返回成功但没有回收任何页），因此必须拒绝执行并如实上报。
    /// </summary>
    NotIncrementalMode = 1,

    /// <summary>写者持有数据库；本轮的 <c>incremental_vacuum</c> 会因 busy 而无效。</summary>
    WriterBusy = 2,

    /// <summary>freelist 为空，没有可回收的整页。</summary>
    NoFreePages = 3,

    /// <summary>批大小非正，调用方参数错误。</summary>
    InvalidBatchSize = 4,
}

/// <summary>
/// 一轮页回收的决策结果：要么给出本批要归还的页数，要么给出跳过原因（二者互斥）。
/// </summary>
internal readonly record struct ReclaimDecision(int PagesToReclaim, ReclaimSkipReason SkipReason)
{
    /// <summary>本批是否真的应该执行回收。</summary>
    internal bool ShouldReclaim => PagesToReclaim > 0;

    internal static ReclaimDecision Skip(ReclaimSkipReason reason) => new(0, reason);

    internal static ReclaimDecision Reclaim(int pages) => new(pages, ReclaimSkipReason.None);
}

/// <summary>
/// 平台库（<c>pudding_platform.db</c> / <c>pudding_memory.db</c>）空闲页回收的决策核心。
/// </summary>
/// <remarks>
/// <para>
/// 纯函数：不做 IO、不读时钟、不依赖数据库连接。这样「什么情况下必须拒绝回收」可以用普通
/// 单元测试钉死，不需要真实库文件。
/// </para>
/// <para>
/// 背景（2026-10-28 实测）：平台库 11.95 GB 里有 5.24 GB 是 freelist 上的空闲页，
/// 因为它建库时是 <c>auto_vacuum=NONE</c> —— 空闲页永不归还文件系统。
/// 代码索引库有完整的回收治理（INCREMENTAL + 分批 <c>incremental_vacuum</c>），平台库一处都没有。
/// 本类型把那一侧已验证的决策语义搬到平台库侧。
/// </para>
/// <para>
/// <b>顺序是契约</b>：先判模式，再判写者忙。模式不对时无论忙不忙都不会有任何回收，
/// 此时报「写者忙」属于误导性归因。
/// </para>
/// </remarks>
internal static class PlatformStorageReclaimPolicy
{
    /// <summary><c>PRAGMA auto_vacuum</c> 取此值表示 INCREMENTAL（可分批归还整页）。</summary>
    internal const int IncrementalAutoVacuumMode = 2;

    /// <summary>
    /// 默认每批归还的页数（4096 字节/页 ⇒ 4 MiB）。刻意小：维护是背景行为，
    /// 单批占用写锁的时间必须短到不影响前台写入。
    /// </summary>
    internal const int DefaultBatchPages = 1024;

    /// <summary>
    /// 决定这一轮该归还多少页，或者为什么不该做。
    /// </summary>
    /// <param name="freelistCount">当前 freelist 上的页数（<c>PRAGMA freelist_count</c>）。</param>
    /// <param name="autoVacuumMode">当前模式（<c>PRAGMA auto_vacuum</c>：0=NONE 1=FULL 2=INCREMENTAL）。</param>
    /// <param name="batchPages">本批页数上界；必须为正。</param>
    /// <param name="writerBusy">是否已观测到写者持有数据库。</param>
    /// <returns>执行或跳过，二者互斥。</returns>
    internal static ReclaimDecision Evaluate(
        long freelistCount,
        int autoVacuumMode,
        int batchPages = DefaultBatchPages,
        bool writerBusy = false)
    {
        // 顺序有意为之：不是 INCREMENTAL 时 incremental_vacuum 是静默 no-op，
        // 此时唯一正确的动作是如实上报模式，而不是去排队等一个也不会有用的空闲窗口。
        if (autoVacuumMode != IncrementalAutoVacuumMode)
            return ReclaimDecision.Skip(ReclaimSkipReason.NotIncrementalMode);

        if (writerBusy)
            return ReclaimDecision.Skip(ReclaimSkipReason.WriterBusy);

        if (freelistCount <= 0)
            return ReclaimDecision.Skip(ReclaimSkipReason.NoFreePages);

        if (batchPages <= 0)
            return ReclaimDecision.Skip(ReclaimSkipReason.InvalidBatchSize);

        // 有界：一次最多归还 batchPages 页，绝不把整个 freelist 一口气吃下去。
        var pages = batchPages;
        if (freelistCount < batchPages)
            pages = (int)freelistCount;

        return ReclaimDecision.Reclaim(pages);
    }
}
