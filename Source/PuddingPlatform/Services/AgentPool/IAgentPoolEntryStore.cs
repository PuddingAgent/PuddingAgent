namespace PuddingPlatform.Services.AgentPool;

/// <summary>
/// 持久子代理池条目快照（S2：存储层自己的投影）。
/// <para>
/// <b>为什么不复用 Core 的 <c>PooledSubAgent</c>：</b>那是运行时的池对象，带执行句柄、取消源、
/// 内存统计等只有活进程才有的东西；把存储层的读结果绑到它上面，等于让「从库读一条记录」这种
/// 纯数据操作依赖 Core 的运行时类型（反向依赖）。这里只暴露落库的列，映射回运行时对象是调用方的事。
/// </para>
/// <para>
/// 时间列在库里是 ISO-8601 文本，读出来已还原成 <see cref="DateTimeOffset"/>，
/// 调用方不必再关心存储编码。
/// </para>
/// </summary>
public sealed record AgentPoolEntrySnapshot(
    string PoolId,
    string Name,
    string SubSessionId,
    string TemplateId,
    string? Role,
    string OwnerWorkspaceId,
    string? OwnerSessionId,
    string? OwnerAgentId,
    string? ActiveExecutionId,
    string Status,
    string? StatusReason,
    int TaskCount,
    bool? LastSuccess,
    DateTimeOffset CreatedAt,
    DateTimeOffset LastUsedAt);

/// <summary>
/// 插入一条池条目的入参。
/// <para>
/// <see cref="CreatedAt"/> 由调用方给：池条目的创建时刻应与「它承载的子会话被创建的时刻」一致，
/// 存储层自己取 <c>UtcNow</c> 会把两者错开，事后按时间对齐日志就会对不上。
/// </para>
/// </summary>
public sealed record AgentPoolInsertRequest(
    string PoolId,
    string Name,
    string SubSessionId,
    string TemplateId,
    string? Role,
    string OwnerWorkspaceId,
    string? OwnerSessionId,
    string? OwnerAgentId,
    DateTimeOffset CreatedAt);

/// <summary>
/// <c>agent_pool</c> 表的数据访问契约（S2：只做存储，不改任何运行时行为）。
/// <para>
/// <b>为什么所有写操作都返回 <c>int</c>（受影响行数）而不是 <c>bool</c>：</b>
/// 池的每一次状态变更都是「有条件的抢占」：谁先抢到谁负责收尾。调用方真正需要知道的是
/// 「我这次是否真的改到了那一行」，0 行意味着条件不成立（被别人抢先、状态已变、条目已软删），
/// 与「操作失败」是两件不同的事——用 bool 会把这两者压成同一个值，调用方就只能猜。
/// 返回行数还让「批量纠正」（例如 <see cref="ResetStaleBusyAsync"/>）能把数量报给上层。
/// </para>
/// <para>
/// <b>为什么本阶段不接任何消费者：</b>先把存储语义独立钉死并用测试锁住，再让运行时接入；
/// 顺序反过来（先接进 <c>SubAgentPool</c> 再补测试）的话，失败的到底是存储还是池逻辑就分不清了。
/// </para>
/// </summary>
public interface IAgentPoolEntryStore
{
    /// <summary>
    /// 容量检查 + 插入，在<b>同一事务</b>内完成。
    /// <para>
    /// <paramref name="capacity"/> &lt;= 0 表示不限制。池满时返回 0，且<b>不得留下半写行</b>：
    /// 半写行（插进去又被上层当作没插）在软删语义下会永久占用 <c>IX_agent_pool_name</c> 与
    /// <c>IX_agent_pool_sub_session</c> 两个唯一索引，等于把该名字/该会话永久占死。
    /// </para>
    /// </summary>
    Task<int> TryInsertAsync(AgentPoolInsertRequest request, int capacity, CancellationToken ct);

    /// <summary>
    /// 条件置忙：<c>status IN ('idle','sleeping')</c> 且未软删且归属匹配时才成功。
    /// <para>
    /// 归属（owner_session_id + owner_workspace_id）必须由 SQL 谓词强制，而不是先查后判：
    /// 先查后判会在两步之间留下窗口，让「另一个会话刚好在这时抢到」变成静默的跨会话驱动。
    /// </para>
    /// </summary>
    Task<int> TryAcquireAsync(
        string poolId,
        string ownerSessionId,
        string ownerWorkspaceId,
        string executionId,
        DateTimeOffset now,
        CancellationToken ct);

    /// <summary>
    /// 条件收尾：只有 <c>status='busy'</c>、执行令牌相同、复用键相同的执行才能写回。
    /// <para>
    /// 令牌（<c>active_execution_id</c>）挡住的是「迟到收尾」：一次执行超时后被重置为 sleeping，
    /// 它的旧回调再醒来时若无令牌校验，就会把这一轮新执行的 busy 状态改成 sleeping，
    /// 让正在跑的条目被别的调度者再抢一次。
    /// </para>
    /// </summary>
    Task<int> TryCompleteAsync(
        string poolId,
        string subSessionId,
        string executionId,
        bool success,
        DateTimeOffset now,
        CancellationToken ct);

    /// <summary>
    /// 通用条件状态迁移（sleep / dead / destroy 都走这里）：要求当前状态等于
    /// <paramref name="expectedStatus"/> 且未软删。
    /// <para>迁到 <c>dead</c> 时同时写 <c>deleted_at</c>，因此是单向的：软删后不再可变更。</para>
    /// </summary>
    Task<int> TrySetStatusAsync(
        string poolId,
        string expectedStatus,
        string newStatus,
        string? statusReason,
        bool clearExecution,
        DateTimeOffset now,
        CancellationToken ct);

    /// <summary>
    /// 淘汰一条最久未使用的 sleeping 条目（置 dead + 写 deleted_at），返回被淘汰条目的 <c>pool_id</c>；
    /// 没有可淘汰条目时返回 <c>null</c>。
    /// <para>
    /// <b>为什么返回 <c>pool_id</c> 而不是受影响行数：</b>淘汰是「库与内存两边一起死」的动作。
    /// 调用方必须知道死的是哪一条，才能把内存视图里同一个 <c>PoolId</c> 踢掉；
    /// 只拿到 1 行的话，调用方要么自己再猜一次 LRU（内存与库的 last_used_at 可能已经分叉），
    /// 要么只改内存——后者会让条目在重启后被 <c>LoadAllAsync</c> 从库里复活。
    /// 没有候选时返回 <c>null</c> 也是最自然的表达：0 行已经不足以区分「没有候选」与「候选被别人抢先」。
    /// </para>
    /// <para>
    /// <b>不会双删：</b>实现必须在事务内「选出唯一一条 → 带 <c>status='sleeping'</c> 与
    /// <c>deleted_at IS NULL</c> 守卫地更新」，并由事务保证两个并发淘汰者加起来最多删一条。
    /// </para>
    /// </summary>
    Task<string?> TryEvictOldestSleepingAsync(DateTimeOffset now, CancellationToken ct);

    /// <summary>
    /// 进程重启后把残留的 busy 复位成 sleeping（reason=recovered_busy）。
    /// <para>
    /// 不改 <c>task_count</c>：那次执行确实发生过，可能跑完了只是收尾没写回；
    /// 抹掉计数会让「复用价值」这一指标比实际偏低。
    /// </para>
    /// </summary>
    Task<int> ResetStaleBusyAsync(DateTimeOffset now, CancellationToken ct);

    /// <summary>载入全部未删除条目（供池在启动时重建内存视图）。</summary>
    Task<IReadOnlyList<AgentPoolEntrySnapshot>> LoadAllAsync(CancellationToken ct);

    /// <summary>按稳定 id 取单条未删除条目；不存在（含已软删）返回 <c>null</c>。</summary>
    Task<AgentPoolEntrySnapshot?> GetByIdAsync(string poolId, CancellationToken ct);

    /// <summary>按复用键（<c>sub_session_id</c>）取单条未删除条目；这是恢复子会话的唯一入口。</summary>
    Task<AgentPoolEntrySnapshot?> GetBySubSessionAsync(string subSessionId, CancellationToken ct);
}
