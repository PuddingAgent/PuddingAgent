using Microsoft.EntityFrameworkCore;
using PuddingMemoryEngine.Data;

namespace PuddingMemoryEngine.Services;

/// <summary>
/// 把 <see cref="FactMemoryWriteMonitor"/> 接到真实数据源上：读行数与最后写入时间。
/// <para>
/// 只读、只计数，不改任何数据。判定的口径与 <see cref="FactMemoryWriteMonitor"/> 一致：
/// 开关关着时直接返回 <see cref="FactMemoryWriteHealth.Disabled"/>，**连库都不查** ——
/// 健康检查本身不该成为负担。
/// </para>
/// </summary>
public sealed class FactMemoryWriteProbe(IDbContextFactory<MemoryLibraryDbContext> dbFactory)
{
    private readonly IDbContextFactory<MemoryLibraryDbContext> _dbFactory = dbFactory;

    /// <param name="writeEnabled">事实写入开关（通常来自 PUDDING_MEMORY_FACTS_WRITE）。</param>
    /// <param name="now">当前时刻，便于测试注入。</param>
    /// <param name="staleDays">多少天没有新写入视为停滞。</param>
    public async Task<FactMemoryWriteHealth> CaptureAsync(
        bool writeEnabled,
        DateTimeOffset now,
        int staleDays = 7,
        CancellationToken ct = default)
    {
        if (!writeEnabled)
            return FactMemoryWriteHealth.Disabled;

        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        var count = await db.MemoryFacts.LongCountAsync(ct);

        DateTimeOffset? last = null;
        if (count > 0)
        {
            // CreatedAt 是 epoch 毫秒（INTEGER），与库内其它时间列的存法一致。
            var maxCreatedAt = await db.MemoryFacts.MaxAsync(f => (long?)f.CreatedAt, ct);
            if (maxCreatedAt is { } ms)
                last = DateTimeOffset.FromUnixTimeMilliseconds(ms);
        }

        return FactMemoryWriteMonitor.Evaluate(
            new FactMemoryWriteSnapshot(writeEnabled, count, last, now, staleDays));
    }
}
