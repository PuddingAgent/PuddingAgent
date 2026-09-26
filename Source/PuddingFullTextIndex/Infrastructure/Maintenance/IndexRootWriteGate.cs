using System.Collections.Concurrent;

namespace PuddingFullTextIndex.Infrastructure.Maintenance;

/// <summary>
/// **index-root 级全局写者 gate**（方案 §4.2 末条：<c>多 scope 增量提交默认全局串行</c>）。
/// <para>
/// <b>为什么需要它</b>：预算判定式是<b>集合口径</b>（<c>全部 live scope 字节 + 本批增长 ≤ MaxIndexBytes</c>），
/// 而「本批开始前实测的 live 字节」是调用方在<b>进锁之前</b>量的。两个 scope 并发提交时，双方手里都是
/// 「对方尚未写入」的旧值 ⇒ 两块批次各按旧值消费同一份剩余额度，合计必然突破预算。因此 §4.2 要求
/// 多 scope 增量提交全局串行，本片进一步在临界区内<b>重测</b> live 字节（见
/// <c>LuceneFullTextIndexMaintenanceEngine.ApplyUnderLeaseAsync</c>），把「同一份剩余额度被消费两次」从根上关掉。
/// </para>
/// <para>
/// <b>为什么键是 index-root 而不是 scope</b>：集合口径的预算分母就是「一个索引根下全部 live scope」；
/// 每个 <c>index-root</c> 一把闸门 ⇒ 不同索引根（例如测试里各自独立的 <c>%TEMP%</c> 根）互不阻塞。
/// </para>
/// <para>
/// <b>为什么是 static（进程级）</b>：同一进程里既可能「一个引擎实例服务多个 scope」，也可能「每个 scope
/// 一个引擎实例」（两种装配都必须串行），因此闸门必须挂在进程级、按索引根分桶。分桶数被「索引根个数」界定
/// （生产上是 1），不会无界增长。
/// </para>
/// <para>
/// ⚠️ 本类是**进程内**互斥，不替代跨进程租约（<c>FileSupplyLease</c>）：两者解决的是不同层的竞争
/// （进程内多 scope 预算 vs 跨进程同 scope 写者）。
/// </para>
/// </summary>
internal static class IndexRootWriteGate
{
    /// <summary>索引根规范键 → 闸门。键口径复用 <see cref="FullTextChangeCoalescer.NormalizeComparisonKey"/>（不另立一套）。</summary>
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new(StringComparer.Ordinal);

    /// <summary>
    /// 取某个索引根的全局写者闸门（调用方负责 <c>WaitAsync</c> 与 <c>Release</c>）。
    /// <para>键的规范化失败（空值 / 非法路径）⇒ 抛异常：调用方传错路径时必须立刻可见，不静默退化成「无闸门」。</para>
    /// </summary>
    internal static SemaphoreSlim For(string indexRootDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(indexRootDirectory);

        var key = FullTextChangeCoalescer.NormalizeComparisonKey(indexRootDirectory);
        return Gates.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
    }

    /// <summary>已建闸门数（诊断用：证明分桶数受「索引根个数」界定）。</summary>
    internal static int BucketCount => Gates.Count;
}
