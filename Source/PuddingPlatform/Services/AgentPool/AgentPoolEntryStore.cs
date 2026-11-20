using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PuddingPlatform.Data;
using PuddingPlatform.Data.Entities;

namespace PuddingPlatform.Services.AgentPool;

/// <summary>
/// <see cref="IAgentPoolEntryStore"/> 的 EF Core / raw SQL 实现（S2：只做存储）。
/// <para>
/// <b>为什么用「条件 UPDATE + 看受影响行数」而不是「先 SELECT 判断，再 UPDATE」：</b>
/// 池的每一次状态变更都是一次抢占（谁能把 idle 改成 busy，谁就拥有这条子代理）。
/// 先查后改把「判断」和「写」拆成两个时刻，两者之间别的调用方可以合法地改掉同一行，
/// 于是判断结果在写下去的那一刻已经过期——表现就是「两个调度者都认为自己抢到了」。
/// 把这些条件写进同一条 <c>UPDATE ... WHERE</c> 里，判断与写在 SQLite 的同一条语句内原子完成，
/// 受影响行数天然就是「我是否真的抢到」的答案：1 = 抢到，0 = 没抢到（且不需要额外查询解释原因）。
/// </para>
/// <para>
/// <b>为什么每条方法都新建一个 DbContext：</b>store 被注册为 Singleton（调用方是 Singleton 的池），
/// 而 DbContext 不是线程安全的、也不能长活；用工厂「用完即弃」让并发调用各自拿到独立连接，
/// 不会出现「同一个 context 被两个线程同时写」的隐蔽竞态。
/// </para>
/// <para>
/// <b>时间列一律 ISO-8601 文本：</b>S1 建表时时间列是 TEXT（与仓内其它表一致），
/// 让 EF 自己推断类型会把写入变成别的表示。这里显式 <c>ToString("O")</c> 写、显式按
/// <see cref="DateTimeStyles.RoundtripKind"/> 读，保证写进去什么、读出来就是同一个瞬时。
/// </para>
/// </summary>
public sealed class AgentPoolEntryStore(
    IDbContextFactory<PlatformDbContext> factory,
    ILogger<AgentPoolEntryStore> logger) : IAgentPoolEntryStore
{
    /// <inheritdoc />
    public async Task<int> TryInsertAsync(
        AgentPoolInsertRequest request,
        int capacity,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        await using var db = await factory.CreateDbContextAsync(ct);
        var createdAt = FormatTime(request.CreatedAt);

        // 容量检查与插入放在同一事务里：否则「检查通过」到「插入完成」之间进来的插入会让池超容，
        // 而超容之后没有任何一步会把它纠正回来（池只按容量拒绝新插入，不会主动踢出已有条目）。
        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        if (capacity > 0)
        {
            var live = await db.AgentPool.CountAsync(x => x.DeletedAt == null, ct);
            if (live >= capacity)
            {
                // 显式回滚而不是「不写就返回」：事务里可能已经执行过读语句，
                // 留着不提交的事务要靠 Dispose 兜底，语义上不如写明「本次什么都不留」。
                await transaction.RollbackAsync(ct);
                logger.LogDebug(
                    "[AgentPool] Insert rejected: pool full ({Live}/{Capacity}) for {PoolId}",
                    live, capacity, request.PoolId);
                return 0;
            }
        }

        // 直接 INSERT 而不是 EF 实体添加：本表的所有写入都必须走同一组条件语义，
        // 走 EF 变更跟踪会让「插入」带上一套独立的并发与默认值行为，两套语义很难对齐。
        // 唯一索引冲突（同名 / 同复用键）会正常抛出 SqliteException：那是调用方的错，不能吞。
        // 事务由 await using 兜底回滚，因此抛异常也不会留下半写行。
        var affected = await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "agent_pool"
                ("pool_id", "owner_workspace_id", "name", "sub_session_id", "template_id", "role",
                 "owner_session_id", "owner_agent_id", "active_execution_id", "status", "status_reason",
                 "task_count", "last_success", "created_at", "last_used_at", "version", "deleted_at")
            VALUES
                ({request.PoolId}, {request.OwnerWorkspaceId}, {request.Name}, {request.SubSessionId},
                 {request.TemplateId}, {request.Role}, {request.OwnerSessionId}, {request.OwnerAgentId},
                 NULL, {AgentPoolEntity.StatusIdle}, NULL, 0, NULL, {createdAt}, {createdAt}, 0, NULL);
            """, ct);

        await transaction.CommitAsync(ct);

        logger.LogDebug(
            "[AgentPool] Inserted entry {PoolId} (name={Name}, subSession={SubSessionId})",
            request.PoolId, request.Name, request.SubSessionId);

        return affected;
    }

    /// <inheritdoc />
    public async Task<int> TryAcquireAsync(
        string poolId,
        string ownerSessionId,
        string ownerWorkspaceId,
        string executionId,
        DateTimeOffset now,
        CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var usedAt = FormatTime(now);

        // 归属（owner_session_id / owner_workspace_id）写在 WHERE 里而不是先查后比：
        // 这是「谁有权驱动这条子代理」的边界，必须由数据库在写入的同一刻强制。
        // status_reason 故意不在这里清空：收尾方（TryCompleteAsync）会按结果重写它，
        // 抢占阶段写一个中性值反而会与收尾结果打架。
        var affected = await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE "agent_pool"
               SET "status" = {AgentPoolEntity.StatusBusy},
                   "active_execution_id" = {executionId},
                   "last_used_at" = {usedAt},
                   "version" = "version" + 1
             WHERE "pool_id" = {poolId}
               AND "owner_session_id" = {ownerSessionId}
               AND "owner_workspace_id" = {ownerWorkspaceId}
               AND "deleted_at" IS NULL
               AND "status" IN ({AgentPoolEntity.StatusIdle}, {AgentPoolEntity.StatusSleeping});
            """, ct);

        if (affected == 0)
        {
            // 0 行有多种原因（被别人抢先、归属不符、已软删、状态已前进），这里只记录现象，
            // 不臆测原因——调用方若要区分，应自己重新读取一次快照再看状态。
            logger.LogDebug("[AgentPool] Acquire miss for {PoolId} exec={ExecutionId}", poolId, executionId);
        }

        return affected;
    }

    /// <inheritdoc />
    public async Task<int> TryCompleteAsync(
        string poolId,
        string subSessionId,
        string executionId,
        bool success,
        DateTimeOffset now,
        CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var usedAt = FormatTime(now);
        // 显式传 0/1：last_success 是三态 INTEGER（NULL/0/1），bool 参数经 provider 转换虽也是 1/0，
        // 但在 SQL 里还要参与 CASE 判断，写成整数可以避免「两边转换规则不一致」这种只看 SQL 看不出的坑。
        var successFlag = success ? 1 : 0;

        // 三重条件缺一不可：
        // ① status='busy' —— 只收尾「正在进行」的条目；
        // ② active_execution_id=@exec —— 挡住迟到收尾（见接口注释）；
        // ③ sub_session_id=@sid —— 复用键，防止 pool_id 被复用后旧执行认错对象。
        var affected = await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE "agent_pool"
               SET "status" = {AgentPoolEntity.StatusSleeping},
                   "task_count" = "task_count" + 1,
                   "last_success" = {successFlag},
                   "last_used_at" = {usedAt},
                   "active_execution_id" = NULL,
                   "status_reason" = CASE WHEN {successFlag} = 1 THEN NULL ELSE 'execution_failed' END,
                   "version" = "version" + 1
             WHERE "pool_id" = {poolId}
               AND "sub_session_id" = {subSessionId}
               AND "active_execution_id" = {executionId}
               AND "status" = {AgentPoolEntity.StatusBusy};
            """, ct);

        if (affected == 0)
        {
            logger.LogDebug(
                "[AgentPool] Complete miss for {PoolId} exec={ExecutionId} (stale or mismatched token)",
                poolId, executionId);
        }

        return affected;
    }

    /// <inheritdoc />
    public async Task<int> TrySetStatusAsync(
        string poolId,
        string expectedStatus,
        string newStatus,
        string? statusReason,
        bool clearExecution,
        DateTimeOffset now,
        CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var stamp = FormatTime(now);
        var clearFlag = clearExecution ? 1 : 0;

        // expectedStatus 是乐观并发闸门：调用方说的是「我看到的那个状态」，
        // 只要期间别人改过，这里就是 0 行，调用方重读后再决定，绝不会覆盖别人的更新。
        //
        // 这里刻意不写 last_used_at：状态迁移不是「使用」，把它当使用会让 LRU 淘汰顺序失真
        // （所有刚睡下的条目都变成「最近用过」，真正最久没用的反而被保护）。
        // now 只用于 dead 的墓碑时间。
        var affected = await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE "agent_pool"
               SET "status" = {newStatus},
                   "status_reason" = {statusReason},
                   "active_execution_id" = CASE WHEN {clearFlag} = 1 THEN NULL ELSE "active_execution_id" END,
                   "deleted_at" = CASE WHEN {newStatus} = {AgentPoolEntity.StatusDead} THEN {stamp} ELSE "deleted_at" END,
                   "version" = "version" + 1
             WHERE "pool_id" = {poolId}
               AND "status" = {expectedStatus}
               AND "deleted_at" IS NULL;
            """, ct);

        return affected;
    }

    /// <inheritdoc />
    public async Task<int> TryEvictOldestSleepingAsync(DateTimeOffset now, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var stamp = FormatTime(now);

        // 必须是单条语句：子查询在 UPDATE 执行时求值，选人 + 打标在同一事务内完成。
        // 若拆成「SELECT 一条 → UPDATE WHERE status='sleeping'」，第二个调用方会读到同一批 sleeping
        // 并各自更新一条（超量淘汰），或者更新到已经被别人改过的行（重复墓碑）。
        // 这里 WHERE "pool_id" = (子查询) 还有一个好处：子查询返回 NULL 时整个条件为假，
        // 也就是「已经没有 sleeping 条目」时自然得到 0 行，不需要额外的存在性判断。
        var affected = await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE "agent_pool"
               SET "status" = {AgentPoolEntity.StatusDead},
                   "deleted_at" = {stamp},
                   "active_execution_id" = NULL,
                   "version" = "version" + 1
             WHERE "pool_id" = (
                SELECT "pool_id" FROM "agent_pool"
                 WHERE "status" = {AgentPoolEntity.StatusSleeping}
                   AND "deleted_at" IS NULL
                 ORDER BY "last_used_at" ASC
                 LIMIT 1
             );
            """, ct);

        if (affected > 0)
            logger.LogDebug("[AgentPool] Evicted oldest sleeping entry ({Count} row)", affected);

        return affected;
    }

    /// <inheritdoc />
    public async Task<int> ResetStaleBusyAsync(DateTimeOffset now, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var stamp = FormatTime(now);

        // 只动 busy → sleeping，不清 task_count：那次执行确实发生过。
        // last_used_at 推进到现在，是为了让刚救回来的条目不要立刻成为下一次 LRU 淘汰的首选
        // ——它刚才「忙」着，不代表它是最久没用的。
        var affected = await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE "agent_pool"
               SET "status" = {AgentPoolEntity.StatusSleeping},
                   "status_reason" = 'recovered_busy',
                   "active_execution_id" = NULL,
                   "last_used_at" = {stamp},
                   "version" = "version" + 1
             WHERE "status" = {AgentPoolEntity.StatusBusy}
               AND "deleted_at" IS NULL;
            """, ct);

        if (affected > 0)
            logger.LogInformation("[AgentPool] Reset {Count} stale busy entr(y/ies) to sleeping", affected);

        return affected;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<AgentPoolEntrySnapshot>> LoadAllAsync(CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);

        // 只读、不跟踪：调用方拿到的是快照，改快照不应该悄悄写回库。
        // 排序让「重建内存视图」的结果可复现：同一次启动两次读到的顺序必须一致，
        // 否则按序号做的断言/日志比对会随机失败。
        var rows = await db.AgentPool
            .AsNoTracking()
            .Where(x => x.DeletedAt == null)
            .OrderBy(x => x.CreatedAt)
            .ThenBy(x => x.PoolId)
            .ToListAsync(ct);

        return rows.Select(Map).ToList();
    }

    /// <inheritdoc />
    public async Task<AgentPoolEntrySnapshot?> GetByIdAsync(string poolId, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);

        var row = await db.AgentPool
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.PoolId == poolId && x.DeletedAt == null, ct);

        return row is null ? null : Map(row);
    }

    /// <inheritdoc />
    public async Task<AgentPoolEntrySnapshot?> GetBySubSessionAsync(string subSessionId, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);

        // sub_session_id 上有唯一索引，因此 FirstOrDefault 与 Single 等价，
        // 但前者在索引缺失/数据异常时不会抛异常炸掉整条恢复链路。
        var row = await db.AgentPool
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.SubSessionId == subSessionId && x.DeletedAt == null, ct);

        return row is null ? null : Map(row);
    }

    /// <summary>实体 → 快照。时间列在库里是文本，这里统一按往返格式解析。</summary>
    private static AgentPoolEntrySnapshot Map(AgentPoolEntity row) => new(
        row.PoolId,
        row.Name,
        row.SubSessionId,
        row.TemplateId,
        row.Role,
        row.OwnerWorkspaceId,
        row.OwnerSessionId,
        row.OwnerAgentId,
        row.ActiveExecutionId,
        row.Status,
        row.StatusReason,
        row.TaskCount,
        row.LastSuccess,
        ParseTime(row.CreatedAt),
        ParseTime(row.LastUsedAt));

    /// <summary>
    /// 时间写入口径：ISO-8601 往返格式（含偏移量），与 S1 建表的 TEXT 列一致。
    /// 只用 InvariantCulture，避免在有其它区域设置的机器上写出不同分隔符。
    /// </summary>
    private static string FormatTime(DateTimeOffset value) =>
        value.ToString("O", CultureInfo.InvariantCulture);

    /// <summary>
    /// 时间读入口径：<see cref="DateTimeStyles.RoundtripKind"/> 保留原偏移量，
    /// 写进去的瞬时读回来相等（<see cref="DateTimeOffset.EqualsExact"/> 级别），不因本地时区漂移。
    /// </summary>
    private static DateTimeOffset ParseTime(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}
