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
                 "owner_session_id", "owner_agent_id", "model_id", "responsibility", "avatar_key",
                 "active_execution_id", "status", "status_reason",
                 "task_count", "last_success", "created_at", "last_used_at", "version", "deleted_at")
            VALUES
                ({request.PoolId}, {request.OwnerWorkspaceId}, {request.Name}, {request.SubSessionId},
                 {request.TemplateId}, {request.Role}, {request.OwnerSessionId}, {request.OwnerAgentId},
                 {request.ModelId}, {request.Responsibility}, {request.AvatarKey},
                 NULL, {AgentPoolEntity.StatusIdle}, NULL, 0, NULL, {createdAt}, {createdAt}, 0, NULL);
            """, ct);

        await transaction.CommitAsync(ct);

        logger.LogDebug(
            "[AgentPool] Inserted entry {PoolId} (name={Name}, subSession={SubSessionId})",
            request.PoolId, request.Name, request.SubSessionId);

        return affected;
    }

    /// <inheritdoc />
    public async Task<int> TryInsertAndAcquireAsync(
        AgentPoolInsertRequest request,
        int capacity,
        string executionId,
        DateTimeOffset now,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrEmpty(request.OwnerSessionId) || string.IsNullOrEmpty(request.OwnerWorkspaceId))
        {
            // 归属不可表达时抢占谓词恒假，本方法的「插完必然能抢到」这个前提就不成立。
            // 不在这里猜一个归属补上：猜错会把条目挂到一个永远不会回来的会话名下。
            throw new ArgumentException(
                "TryInsertAndAcquireAsync requires both owner session and owner workspace; " +
                "use TryInsertAsync when ownership cannot be expressed.",
                nameof(request));
        }

        await using var db = await factory.CreateDbContextAsync(ct);
        var createdAt = FormatTime(request.CreatedAt);
        var usedAt = FormatTime(now);

        // 容量检查、插入、抢占全在同一个事务里：任何一步不成立都整体回滚，
        // 不留「插进去了但没人接管」的行。那行看似无害，其实占着 name 与 sub_session
        // 两个唯一索引，会让同名条目之后的插入永久撞索引并被误报成「池满」。
        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        if (capacity > 0)
        {
            var live = await db.AgentPool.CountAsync(x => x.DeletedAt == null, ct);
            if (live >= capacity)
            {
                await transaction.RollbackAsync(ct);
                logger.LogDebug(
                    "[AgentPool] Insert+acquire rejected: pool full ({Live}/{Capacity}) for {PoolId}",
                    live, capacity, request.PoolId);
                return 0;
            }
        }

        var inserted = await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "agent_pool"
                ("pool_id", "owner_workspace_id", "name", "sub_session_id", "template_id", "role",
                 "owner_session_id", "owner_agent_id", "model_id", "responsibility", "avatar_key",
                 "active_execution_id", "status", "status_reason",
                 "task_count", "last_success", "created_at", "last_used_at", "version", "deleted_at")
            VALUES
                ({request.PoolId}, {request.OwnerWorkspaceId}, {request.Name}, {request.SubSessionId},
                 {request.TemplateId}, {request.Role}, {request.OwnerSessionId}, {request.OwnerAgentId},
                 {request.ModelId}, {request.Responsibility}, {request.AvatarKey},
                 NULL, {AgentPoolEntity.StatusIdle}, NULL, 0, NULL, {createdAt}, {createdAt}, 0, NULL);
            """, ct);

        var acquired = await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE "agent_pool"
               SET "status" = {AgentPoolEntity.StatusBusy},
                   "active_execution_id" = {executionId},
                   "last_used_at" = {usedAt},
                   "version" = "version" + 1
             WHERE "pool_id" = {request.PoolId}
               AND "owner_session_id" = {request.OwnerSessionId}
               AND "owner_workspace_id" = {request.OwnerWorkspaceId}
               AND "deleted_at" IS NULL
               AND "status" IN ({AgentPoolEntity.StatusIdle}, {AgentPoolEntity.StatusSleeping});
            """, ct);

        if (inserted == 0 || acquired == 0)
        {
            // 同事务内刚插入的行只有本事务看得见，状态必为 idle、归属必为已写入的值，
            // 因此「抢不到」在逻辑上不可达。真出现只能是 SQL 层面的意外，
            // 宁可整体回滚上一个异常，也不靠返回 0 把一个不知处于何态的行留给上层。
            await transaction.RollbackAsync(ct);
            throw new InvalidOperationException(
                $"Inserted entry '{request.Name}' (poolId={request.PoolId}) but could not acquire it " +
                $"in the same transaction (inserted={inserted}, acquired={acquired}). " +
                "Rolled back instead of leaving a row nobody owns.");
        }

        await transaction.CommitAsync(ct);

        logger.LogDebug(
            "[AgentPool] Inserted and acquired entry {PoolId} (name={Name}, exec={ExecutionId})",
            request.PoolId, request.Name, executionId);

        return 1;
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
        CancellationToken ct,
        string? expectedOwnerSessionId = null,
        string? expectedOwnerWorkspaceId = null)
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
               AND "deleted_at" IS NULL
               -- 归属闸门：参数为 NULL 表示「不限该维度」（管理面），非 NULL 则要求相等。
               -- 写成 IS NULL OR 而不是拼 SQL：两种语义共用一条参数化语句，不留拼接分支。
               -- 早期行 owner 为 NULL 时不会匹配任何具体会话——「不知道属于谁」
               -- 不能当成「属于所有人」，否则边界会从这里被绕开。
               AND ({expectedOwnerSessionId} IS NULL OR "owner_session_id" = {expectedOwnerSessionId})
               AND ({expectedOwnerWorkspaceId} IS NULL OR "owner_workspace_id" = {expectedOwnerWorkspaceId});
            """, ct);

        return affected;
    }

    /// <inheritdoc />
    public async Task<string?> TryEvictOldestSleepingAsync(DateTimeOffset now, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var stamp = FormatTime(now);

        // 为什么不再用「一条 UPDATE + 子查询」：那种写法只能拿到受影响行数，调用方不知道
        // 被淘汰的是哪一条，内存视图就无法跟随（库死了内存还活着 = 重启前内存里多一个幽灵；
        // 反过来内存死了库还活着 = 重启后条目复活）。所以改为事务内「选人 → 打标」，
        // 并把选中的 pool_id 返回给调用方。
        //
        // 为什么仍然不会双删：Microsoft.Data.Sqlite 的默认事务是非延迟（BEGIN IMMEDIATE），
        // 写锁在事务开始时就持有，两个淘汰者不可能同时进入事务体：第二个会在 BEGIN 处等到
        // 第一个提交，随后 SELECT 已经看不到 sleeping 条目，自然返回 null。
        // 即使隔离退化（有人把这个事务改成延迟的），UPDATE 里的 status / deleted_at 守卫
        // 也会让被抢先的那一次命中 0 行，不会误删别人。
        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        // 重试一次：只有在 SELECT 与 UPDATE 之间被另一个连接改掉选中行时才会走到这里。
        // 不无限重试：淘汰是后台清理动作，失败留给调用方下一轮再触发即可。
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var poolId = await db.Database.SqlQuery<string>($"""
                SELECT "pool_id" AS Value
                  FROM "agent_pool"
                 WHERE "status" = {AgentPoolEntity.StatusSleeping}
                   AND "deleted_at" IS NULL
                 ORDER BY "last_used_at" ASC
                 LIMIT 1
                """).FirstOrDefaultAsync(ct);

            if (poolId is null)
            {
                await transaction.RollbackAsync(ct);
                return null;
            }

            var affected = await db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE "agent_pool"
                   SET "status" = {AgentPoolEntity.StatusDead},
                       "deleted_at" = {stamp},
                       "active_execution_id" = NULL,
                       "version" = "version" + 1
                  WHERE "pool_id" = {poolId}
                    AND "status" = {AgentPoolEntity.StatusSleeping}
                    AND "deleted_at" IS NULL;
                """, ct);

            if (affected == 1)
            {
                await transaction.CommitAsync(ct);
                logger.LogDebug("[AgentPool] Evicted oldest sleeping entry {PoolId}", poolId);
                return poolId;
            }

            logger.LogDebug(
                "[AgentPool] Eviction candidate {PoolId} changed before update (attempt {Attempt}); retrying",
                poolId, attempt + 1);
        }

        await transaction.RollbackAsync(ct);
        return null;
    }

    /// <inheritdoc />
    public async Task<int> ResetStaleBusyAsync(DateTimeOffset now, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var stamp = FormatTime(now);

        // 「哪些 run 算还在跑」只从 SubAgentRunEntity 取（唯一真源），本文件不另写一份状态字面量：
        // 池恢复与 run 归档对「还在跑」的口径必须一致。只改一侧的后果不对称——
        // 把在跑的 run 误判成终态会去重置一条正在被驱动的条目（两个执行同时驱动一个子会话），
        // 而反过来只是少救一条，下次重启还有机会。
        var activeStatuses = SubAgentRunEntity.NonTerminalStatuses;

        if (activeStatuses.Count == 0)
        {
            // 空集合的 Contains 恒为 false ⇒ 判据退化成「所有 busy 都是 stale」。
            // 本函数是「救条目」的，误杀代价远大于不救，所以词表被改坏时宁可什么都不做，
            // 把原因留在日志里，而不是把整个池重置成 sleeping。
            logger.LogError(
                "[AgentPool] ResetStaleBusy skipped: SubAgentRunEntity declares no non-terminal status; " +
                "refusing to treat every busy entry as stale");
            return 0;
        }

        // 判据是「库里还有没有非终态 run」，而不是「启动顺序走到哪一步」：
        // 本函数在宿主启动期执行，而 run 恢复是否已完成只靠注册顺序无法保证
        // （BackgroundService.StartAsync 不 await ExecuteAsync 主体）。把判据换成数据后，
        // 即使执行得比 run 恢复更早，也不会误杀真正在跑的条目——顺序依赖换成了状态依赖。
        //
        // 只动 busy → sleeping，不清 task_count：那次执行确实发生过。
        // last_used_at 推进到现在，是为了让刚救回来的条目不要立刻成为下一次 LRU 淘汰的首选
        // ——它刚才「忙」着，不代表它是最久没用的。
        var affected = await db.AgentPool
            .Where(p => p.Status == AgentPoolEntity.StatusBusy && p.DeletedAt == null)
            .Where(p => !db.SubAgentRuns.Any(r =>
                r.SubSessionId == p.SubSessionId && activeStatuses.Contains(r.Status)))
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(p => p.Status, AgentPoolEntity.StatusSleeping)
                .SetProperty(p => p.StatusReason, AgentPoolEntity.StatusReasonRecoveredBusy)
                .SetProperty(p => p.ActiveExecutionId, (string?)null)
                .SetProperty(p => p.LastUsedAt, stamp)
                .SetProperty(p => p.Version, p => p.Version + 1),
                ct);

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
        ParseTime(row.LastUsedAt),
        row.ModelId);

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
