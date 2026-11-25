using System.Collections.Concurrent;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using PuddingCode.Abstractions;
using PuddingCode.Platform;
using PuddingPlatform.Data.Entities;
using PuddingPlatform.Services.AgentPool;

namespace PuddingPlatform.Services;

/// <summary>
/// 子代理池 — 池化复用子代理以最大化 KV-cache 命中率。
///
/// 生命周期：
///   Create → Idle → Busy → Sleeping → Busy → Sleeping → ... → Dead
///
/// 线程安全：使用 ConcurrentDictionary + SemaphoreSlim 保护状态变更。
/// 向后兼容：不修改现有 spawn_sub_agent 行为，作为可选新增功能。
/// </summary>
public sealed class SubAgentPool : ISubAgentPool
{
    private readonly ISubAgentManager _subAgentManager;
    private readonly ILogger<SubAgentPool> _logger;
    private readonly int _maxCapacity;

    /// <summary>
    /// 池内条目的可变内部状态。通过 SemaphoreSlim 保护状态变更。
    /// </summary>
    private sealed class PoolEntry
    {
        /// <summary>
        /// 库里的稳定主键（<c>agent_pool.pool_id</c>）。
        /// <para>
        /// 名字可以撞、条目可以被 destroy → create 换成另一个对象，只有 pool_id 不变。
        /// 所有条件写库都必须凭它定位那一行：按 name 写会把结果落到墓碑或新条目上
        /// （软删后同名可被新条目占用，唯一索引带 <c>deleted_at</c> 过滤）。
        /// </para>
        /// <para>
        /// 纯内存模式（没有 store）也照样生成：字段非空让日志与诊断始终有一个可引用的身份，
        /// 不必在每条日志里再写一遍「有 store 吗」。
        /// </para>
        /// </summary>
        public string PoolId { get; }

        public string Name { get; }
        public string SubSessionId { get; }
        public string TemplateId { get; }
        public string? Role { get; }
        public DateTimeOffset CreatedAt { get; }
        public DateTimeOffset LastUsedAt { get; set; }
        public PooledSubAgentStatus Status { get; set; }
        public int TaskCount { get; set; }
        public bool? LastSuccess { get; set; }

        /// <summary>
        /// 本次执行的令牌。收尾时必须与它比对：同名条目可能在「置 Busy」与「收尾」之间
        /// 被 destroy → create 替换成另一个对象，迟到的收尾若按名字回查就会把结果写到
        /// 新条目上（D-P0-3 的 stale completion）。
        /// </summary>
        public string? ActiveExecutionId { get; set; }

        /// <summary>创建者父会话。持久化后条目会活过创建它的那次执行，归属必须落在这里。</summary>
        public string? OwnerSessionId { get; }

        /// <summary>创建者工作区。跨工作区复用同样会污染投影，故一并记录。</summary>
        public string? OwnerWorkspaceId { get; }

        public PoolEntry(
            string poolId,
            string name,
            string subSessionId,
            string templateId,
            string? role,
            string? ownerSessionId,
            string? ownerWorkspaceId)
        {
            PoolId = poolId;
            Name = name;
            SubSessionId = subSessionId;
            TemplateId = templateId;
            Role = role;
            OwnerSessionId = ownerSessionId;
            OwnerWorkspaceId = ownerWorkspaceId;
            CreatedAt = DateTimeOffset.UtcNow;
            LastUsedAt = CreatedAt;
            Status = PooledSubAgentStatus.Idle;
            TaskCount = 0;
            LastSuccess = null;
        }

        /// <summary>
        /// 从持久化快照重建条目（进程重启后把库里的池恢复成内存视图）。
        /// <para>
        /// 时间与计数全部以库为准：用 <c>UtcNow</c> 重建会让「创建于上周」的条目看起来是刚生的，
        /// LRU 选型、保留策略、诊断时间线都会因此错位。
        /// </para>
        /// </summary>
        public PoolEntry(
            string poolId,
            string name,
            string subSessionId,
            string templateId,
            string? role,
            string? ownerSessionId,
            string? ownerWorkspaceId,
            DateTimeOffset createdAt,
            DateTimeOffset lastUsedAt,
            PooledSubAgentStatus status,
            int taskCount,
            bool? lastSuccess,
            string? activeExecutionId)
        {
            PoolId = poolId;
            Name = name;
            SubSessionId = subSessionId;
            TemplateId = templateId;
            Role = role;
            OwnerSessionId = ownerSessionId;
            OwnerWorkspaceId = ownerWorkspaceId;
            CreatedAt = createdAt;
            LastUsedAt = lastUsedAt;
            Status = status;
            TaskCount = taskCount;
            LastSuccess = lastSuccess;
            ActiveExecutionId = activeExecutionId;
        }

        public PooledSubAgent ToSnapshot() => new()
        {
            Name = Name,
            SubSessionId = SubSessionId,
            TemplateId = TemplateId,
            Role = Role,
            CreatedAt = CreatedAt,
            LastUsedAt = LastUsedAt,
            Status = Status,
            TaskCount = TaskCount,
            LastSuccess = LastSuccess,
        };
    }

    private readonly ConcurrentDictionary<string, PoolEntry> _pool = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _stateLock = new(1, 1);

    /// <summary>
    /// 持久化存储。<c>null</c> 表示纯内存模式（既有行为，测试与未接存储的宿主都走这条路）。
    /// 非空时池必须先就绪才能读写：未就绪就去写，会与库里的既有条目撞唯一索引，
    /// 而那时候池本身还看不到那些条目。
    /// </summary>
    private readonly IAgentPoolEntryStore? _entryStore;

    /// <summary>一次性加载的临界区：并发调用只会真的读一次库。</summary>
    private readonly SemaphoreSlim _readyGate = new(1, 1);

    /// <summary>就绪状态（由 <see cref="EnsureReadyAsync"/> 驱动；无 store 时恒为 Ready）。</summary>
    private volatile PoolReadiness _readiness;

    /// <summary>上次加载失败的原始异常。失败是终态，错误文案里必须能说出原因。</summary>
    private Exception? _loadFailure;

    public SubAgentPool(
        ISubAgentManager subAgentManager,
        IConfiguration configuration,
        ILogger<SubAgentPool> logger,
        IAgentPoolEntryStore? entryStore = null)
    {
        _subAgentManager = subAgentManager;
        _logger = logger;
        _entryStore = entryStore;

        // 没有存储层就没有「加载」这回事：池一开始就是就绪的，
        // 这样既有的 29 个池用例与纯内存宿主无需任何改动。
        _readiness = entryStore is null ? PoolReadiness.Ready : PoolReadiness.NotStarted;

        var configCapacity = configuration["SubAgentPool:MaxCapacity"];
        if (int.TryParse(configCapacity, out var parsed) && parsed > 0)
        {
            _maxCapacity = parsed;
        }
        else
        {
            _maxCapacity = 15;
        }

        _logger.LogInformation(
            "[SubAgentPool] Initialized with MaxCapacity={MaxCapacity}", _maxCapacity);
    }

    /// <summary>当前池中活跃子代理数量（不含 Dead）。</summary>
    public int Count => _pool.Count;

    /// <summary>池容量上限。</summary>
    public int MaxCapacity => _maxCapacity;

    /// <inheritdoc />
    public PoolReadiness Readiness => _readiness;

    // ════════════════════════════════════════════════════════
    // 就绪
    // ════════════════════════════════════════════════════════

    /// <summary>
    /// 确保池内视图已从存储加载（一次性；并发调用只真读一次库）。
    /// <para>
    /// <b>为什么失败不降级为空池：</b>池集合是 GC 的删除白名单（见 <see cref="PoolNotReadyException"/>）。
    /// 库读不到时“降级为空”会让 GC 把全部池化目录当成孤儿隔离，而“报错”只会让本轮扫描跳过。
    /// 两者的代价完全不对称，所以失败必须留成 <see cref="PoolReadiness.Failed"/> 并向上抛。
    /// </para>
    /// <para>
    /// 失败是<b>终态</b>（不自动重试）：库不可用是个环境故障，静默重试只会让每次调用都先超时一次，
    /// 而调用方从异常里拿不到“刚才已经失败过 20 次”这个信息。需要重试就重启进程。
    /// </para>
    /// </summary>
    public async Task EnsureReadyAsync(CancellationToken ct = default)
    {
        // 纯内存模式：没有“未就绪”这回事。
        if (_entryStore is null)
            return;

        // 快路径：已就绪直接返回；已失败不抢锁，但要把原因报出去。
        var observed = _readiness;
        if (observed == PoolReadiness.Ready)
            return;
        if (observed == PoolReadiness.Failed)
            throw BuildNotReadyException();

        await _readyGate.WaitAsync(ct);
        try
        {
            // 双检：等锁期间可能已被另一个调用方加载完成（或已失败）。
            if (_readiness == PoolReadiness.Ready)
                return;
            if (_readiness == PoolReadiness.Failed)
                throw BuildNotReadyException();

            _readiness = PoolReadiness.Loading;

            IReadOnlyList<AgentPoolEntrySnapshot> loaded;
            try
            {
                loaded = await _entryStore.LoadAllAsync(ct);
            }
            catch (Exception ex)
            {
                _loadFailure = ex;
                _readiness = PoolReadiness.Failed;
                _logger.LogError(ex, "[SubAgentPool] Failed to load persisted pool entries; pool marked Failed");
                throw new PoolNotReadyException(
                    $"[SubAgentPool] Pool recovery failed and will not be treated as an empty pool: " +
                    $"{ex.GetType().Name}: {ex.Message}",
                    PoolReadiness.Failed,
                    ex);
            }

            foreach (var snapshot in loaded)
            {
                // 用 TryAdd 而不是索引赋值：ExecuteAsync 的「自动预留」路径不经过就绪门，
                // 可能在加载期间已经建了一个同名条目并正在执行。那个条目带着活的子会话，
                // 用库里的快照覆盖它会把正在执行的收尾丢掉（FinishAsync 是引用相等判定）。
                _pool.TryAdd(snapshot.Name, new PoolEntry(
                    // pool_id 只从快照带回来，绝不在这里重新生成：
                    // 重新生成会让重启后的每个条目都指向一个库里不存在的 id，后续所有条件写都落空。
                    poolId: snapshot.PoolId,
                    name: snapshot.Name,
                    subSessionId: snapshot.SubSessionId,
                    templateId: snapshot.TemplateId,
                    role: snapshot.Role,
                    ownerSessionId: snapshot.OwnerSessionId,
                    ownerWorkspaceId: snapshot.OwnerWorkspaceId,
                    createdAt: snapshot.CreatedAt,
                    lastUsedAt: snapshot.LastUsedAt,
                    status: MapStatus(snapshot.Status),
                    taskCount: snapshot.TaskCount,
                    lastSuccess: snapshot.LastSuccess,
                    activeExecutionId: snapshot.ActiveExecutionId));
            }

            // S5c：先救「重启前残留的 busy」，再对外宣告就绪。
            // 顺序上选在 Ready 之前：Ready 是「可以对外派活」的承诺，把唤醒提前到复位之前，
            // 第一个调用方就会撞上一条尚未救回的 busy（报「当前忙」而不是真 busy）。
            await TryRecoverStaleBusyAsync(loaded, ct);

            _readiness = PoolReadiness.Ready;
            _logger.LogInformation(
                "[SubAgentPool] Restored {Count} pool entries from storage (readiness=Ready)", loaded.Count);
        }
        finally
        {
            _readyGate.Release();
        }
    }

    /// <summary>
    /// 重启恢复（S5c）：把「上一次进程崩溃 / 重启留下的 busy」复位成 sleeping，并同步内存视图。
    /// <para>
    /// <b>为什么需要这一步：</b><see cref="ExecuteAsync"/> 的内存前置检查要求状态不是 Busy，
    /// 库侧 <c>TryAcquireAsync</c> 也只接受 idle/sleeping——一条残留的 busy 会让条目
    /// 「在池里、但永远派不了活」，用户看到的是永远「忙」。
    /// </para>
    /// <para>
    /// <b>为什么写库在前、改内存在后：</b>与 Execute/Sleep/Destroy 同一套口径——库是事实源，
    /// 缓存跟随。复位后重读一次库而不是在内存里按状态自己推断「哪几条该改」，
    /// 是为了不把「哪些算 stale」这个判据抄成 C# 的第二份（第二份迟早与 SQL 那份漂移，
    /// 那时就会出现「库里已 sleeping、内存仍 busy」，而那正是本缺陷的变体重演）。
    /// </para>
    /// <para>
    /// <b>为什么失败不让池整体 Failed：</b>复位只是一次「尽力而为的清理」，而池可用的前提是
    /// 视图已加载（<c>LoadAllAsync</c> 才是硬依赖）。若把复位失败也当成 Failed，
    /// 一次写失败就会让整个池化能力停摆（GC 跳过扫描、所有池调用报错），代价远大于
    /// 「个别条目继续停在 busy」——后者仍有日志可查，且下次重启还会再试一次。
    /// </para>
    /// </summary>
    /// <summary>
    /// 启动期 stale-busy 复位是否值得再试一轮。
    /// <para>
    /// 判据是「还有几条没救回来」（<paramref name="pendingBusy"/> &gt; 已救回数），
    /// 不是「这一轮救到了东西」：连续几轮返回 0 并不等于没希望——那只是
    /// <c>run</c> 尚未被收成终态，下一轮可能就好了；而一旦救回条数追上起始 busy 数，
    /// 剩下的即使仍 busy 也是「有活动 run 的」，再等也不会变。
    /// </para>
    /// <para>
    /// 两种偏差的代价不对称：救少了会让条目卡到下次重启（本方法存在的理由），
    /// 多等一轮只是 <c>Task.Delay</c>（且仅当确实存在 busy 条目时才发生）。
    /// </para>
    /// </summary>
    internal static bool ShouldRetryStaleBusySweep(
        int totalReset, int pendingBusy, int attempt, int maxAttempts)
    {
        if (attempt >= maxAttempts)
            return false;

        return totalReset < pendingBusy;
    }

    private async Task TryRecoverStaleBusyAsync(
        IReadOnlyList<AgentPoolEntrySnapshot> loaded, CancellationToken ct)
    {
        if (_entryStore is null)
            return;

        // 只有「确实存在 busy 条目」时才值得重试：正常启动没有 busy，多睡这几轮是白等。
        // 计数而不是布尔：重试判据是「还有几条没救回来」，需要起始待救条数。
        var pendingBusy = loaded.Count(static row =>
            string.Equals(row.Status, AgentPoolEntity.StatusBusy, StringComparison.Ordinal));
        var hasBusy = pendingBusy > 0;

        // 重试的理由是顺序竞态，不是「上一次失败」：本方法在池就绪之前跑，而把上一进程遗留的
        // running 收成终态的是 SubAgentConversationProjectionWorker 的启动恢复——那是
        // BackgroundService，StartAsync 不 await 其主体，所以「注册在它之后」并不等于
        // 「它恢复完了」。复位先落地时判据还看得见 running，于是返回 0、什么也没改；
        // 收成终态之后没人再叫一次，条目就停到下次重启。这几轮有限重试就是在补那一次。
        // 250ms / 500ms / 1000ms：最多多等 1.75 秒，且仅当确实存在 busy 条目时才会走到这里。
        var retryDelays = new[] { 250, 500, 1000 };
        var maxAttempts = hasBusy ? retryDelays.Length + 1 : 1;
        var reset = 0;
        var totalReset = 0;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                reset = await _entryStore.ResetStaleBusyAsync(DateTimeOffset.UtcNow, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // 不设 Failed：加载已成功，池可用；只是这次的「救条目」没做成。
                _logger.LogWarning(
                    ex,
                    "[SubAgentPool] Stale-busy recovery failed; pool still becomes Ready " +
                    "(loaded entries are usable, stale busy entries stay stuck until next restart)");
                return;
            }

            // 判据是「还剩几条没救回来」，不是「这一轮是否救到了东西」：
            // 两条残留 busy 时分批转终态，第一条救回后第二条仍未就绪；
            // 而单条时连续两轮 0 行也只是 run 还没收完，不能就此收手。
            // 旧写法 reset > 0 在第一种形态下少救一条；
            // 只看「本轮 reset」又会在第二种形态下过早放弃——两者都不对。
            totalReset += reset;
            if (!ShouldRetryStaleBusySweep(totalReset, pendingBusy, attempt, maxAttempts))
                break;

            // 日志说「这一轮扫到几条」而不是「什么都没扫到」：本轮可能确实救回了条目，
            // 但还有其它 busy 没轮到（它们的 run 尚未终态），所以仍需再试一轮。
            _logger.LogDebug(
                "[SubAgentPool] Stale-busy sweep attempt {Attempt}/{Max} reset {Reset} this attempt; " +
                "retrying after interrupted-run recovery has a chance to finish",
                attempt, maxAttempts, reset);
            await Task.Delay(retryDelays[attempt - 1], ct);
        }

        // 用累计值而不是本轮的 reset：最后一轮可能返回 0（前面已经救回过），
        // 用本轮值会误判成「一条也没救」而跳过下面的内存同步，
        // 留下「库里已 sleeping、内存仍 busy」——那正是本方法要消灭的那种状态。
        if (totalReset <= 0)
        {
            if (hasBusy)
            {
                // 重试也用尽：run 还没被收成终态，本轮救不回来。留在日志里，别静默。
                _logger.LogWarning(
                    "[SubAgentPool] Stale-busy sweep exhausted {Attempts} attempts without resetting; " +
                    "entries stay Busy until the next restart",
                    maxAttempts);
            }

            return;
        }

        IReadOnlyList<AgentPoolEntrySnapshot> rows;
        try
        {
            rows = await _entryStore.LoadAllAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 复位已在库里生效，内存没跟上只是缓存陈旧（下一次重启会重新加载）；不让池 Failed。
            _logger.LogWarning(
                ex,
                "[SubAgentPool] Stale-busy reset applied ({Count}) but re-read failed; " +
                "in-memory entries may still report Busy", totalReset);
            return;
        }

        // 按 pool_id 对齐而不是按 name：name 可以被软删后复用，pool_id 才是稳定身份。
        var byPoolId = rows.ToDictionary(row => row.PoolId, StringComparer.Ordinal);

        // 取 _stateLock：List()/GetAsync() 不经就绪门，用同一把锁让它们不会读到写了一半的中间态。
        // 锁序安全：本方法在 _readyGate 内调用，而其余路径都是先拿 _readyGate（EnsureReadyAsync）
        // 再拿 _stateLock，不存在反向嵌套。
        var synced = 0;
        await _stateLock.WaitAsync(ct);
        try
        {
            foreach (var entry in _pool.Values)
            {
                if (entry.Status != PooledSubAgentStatus.Busy)
                    continue;
                if (!byPoolId.TryGetValue(entry.PoolId, out var row))
                    continue;

                var status = MapStatus(row.Status);
                if (status == PooledSubAgentStatus.Busy)
                    continue; // 库里有非终态 run，复位没有动它：内存也得继续显示 busy

                entry.Status = status;
                entry.ActiveExecutionId = row.ActiveExecutionId;
                entry.LastUsedAt = row.LastUsedAt;
                synced++;
            }
        }
        finally
        {
            _stateLock.Release();
        }

        _logger.LogInformation(
            "[SubAgentPool] Recovered {Reset} stale busy entr(y/ies); synced {Synced} in-memory",
            reset, synced);
    }

    /// <summary>
    /// 库内状态串 → 运行时状态。未知值一律当成 <see cref="PooledSubAgentStatus.Dead"/>
    /// （不可复用），而不是猜成 Idle：认错方向的代价是「一个不该被复用的子代理被派活」。
    /// </summary>
    private static PooledSubAgentStatus MapStatus(string status) => status switch
    {
        AgentPoolEntity.StatusIdle => PooledSubAgentStatus.Idle,
        AgentPoolEntity.StatusBusy => PooledSubAgentStatus.Busy,
        AgentPoolEntity.StatusSleeping => PooledSubAgentStatus.Sleeping,
        AgentPoolEntity.StatusDead => PooledSubAgentStatus.Dead,
        _ => PooledSubAgentStatus.Dead,
    };

    /// <summary>
    /// 运行时状态 → 库内状态串（<see cref="MapStatus"/> 的反方向）。
    /// <para>
    /// 两个方向刻意不共用一张表：恢复路径遇到「未知值」映射成 Dead 是安全的（不可复用），
    /// 而写入路径遇到未知值必须在写库前就报错——不得静默化简成 idle，
    /// 否则一个新增的运行时状态会在库里表现为「刚创建、从未执行过」。
    /// </para>
    /// </summary>
    private static string ToStoreStatus(PooledSubAgentStatus status) => status switch
    {
        PooledSubAgentStatus.Idle => AgentPoolEntity.StatusIdle,
        PooledSubAgentStatus.Busy => AgentPoolEntity.StatusBusy,
        PooledSubAgentStatus.Sleeping => AgentPoolEntity.StatusSleeping,
        PooledSubAgentStatus.Dead => AgentPoolEntity.StatusDead,
        _ => throw new ArgumentOutOfRangeException(
            nameof(status), status, "Unknown pooled status; refusing to write an unmapped value to agent_pool.status"),
    };

    private PoolNotReadyException BuildNotReadyException() => new(
        $"[SubAgentPool] Pool is not ready (readiness={_readiness}); " +
        $"refusing to report an empty pool. " +
        (_loadFailure is null
            ? "It has not finished loading yet."
            : $"Last load error: {_loadFailure.GetType().Name}: {_loadFailure.Message}"),
        _readiness,
        _loadFailure);

    // ════════════════════════════════════════════════════════
    // 创建
    // ════════════════════════════════════════════════════════

    /// <summary>
    /// 创建命名子代理，初始化上下文。
    /// 池满时抛出 InvalidOperationException。
    /// 已存在同名子代理时抛出 InvalidOperationException。
    /// </summary>
    public async Task<PooledSubAgent> CreateAsync(
        string name,
        SubAgentSpawnRequest request,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Sub-agent pool name cannot be null or empty.", nameof(name));

        // 先把视图从库加载好：没加载就写库，会与库里的既有条目撞唯一索引（name / sub_session_id），
        // 而那时池自己还看不到它们——错会变成“约束报错”，而不是“名字已存在”这种可理解的拒绝。
        await EnsureReadyAsync(ct);

        PoolEntry entry;
        await _stateLock.WaitAsync(ct);
        try
        {
            if (_pool.ContainsKey(name))
            {
                throw new InvalidOperationException(
                    $"Sub-agent '{name}' already exists in the pool.");
            }

            if (_pool.Count >= _maxCapacity)
            {
                throw new InvalidOperationException(
                    $"Sub-agent pool is full (capacity={_maxCapacity}). " +
                    $"Cannot create '{name}'. Consider calling DestroyAsync on unused agents.");
            }

            // 池创建只预留稳定会话标识，不创建 run，也不执行任务。
            // 真正执行统一由 ExecuteSyncAsync 完成，否则 create → execute 会对同一
            // SubSessionId 重叠启动两次，并污染当前状态投影与 runId 映射。
            //
            // pool_id 在创建处一次性生成并同时交给「写库」与「内存条目」：
            // 两处各生成一个的话，内存里的 id 永远指向一行不存在的记录，
            // 之后每一次条件写库都会静默落空（0 行），而内存看上去一切正常。
            var poolId = Guid.NewGuid().ToString("N");
            entry = new PoolEntry(
                poolId: poolId,
                name: name,
                subSessionId: SubAgentSessionId.Create(request.ParentSessionId),
                templateId: request.TemplateId,
                role: request.RoleInPlan,
                ownerSessionId: request.ParentSessionId,
                ownerWorkspaceId: request.WorkspaceId);

            if (_entryStore is not null)
            {
                // 先落库再进内存：写库失败时内存里不得留下「看起来存在但库里没有」的条目，
                // 否则重启后它会凭空消失，而调用方已经拿到了它的 SubSessionId。
                var inserted = await _entryStore.TryInsertAsync(
                    new AgentPoolInsertRequest(
                        PoolId: poolId,
                        Name: name,
                        SubSessionId: entry.SubSessionId,
                        TemplateId: request.TemplateId,
                        Role: request.RoleInPlan,
                        OwnerWorkspaceId: request.WorkspaceId,
                        OwnerSessionId: request.ParentSessionId,
                        OwnerAgentId: request.ParentAgentId,
                        CreatedAt: entry.CreatedAt,
                        // 模型写完整路由，与 List DTO 的「providerId/modelId」口径一致。
                        // ProviderId 为空时不编造一个「/model」出来。
                        ModelId: string.IsNullOrWhiteSpace(request.LlmProfile.ProviderId)
                            ? null
                            : $"{request.LlmProfile.ProviderId}/{request.LlmProfile.ModelId}",
                        Responsibility: request.Responsibility,
                        AvatarKey: request.AvatarKey),
                    capacity: _maxCapacity,
                    ct);

                if (inserted == 0)
                {
                    // store 按库里的未软删行数判定池满。与内存池满用同一种异常与文案：
                    // 两种“满”对调用方的处置（先 destroy 不用的条目）完全一样。
                    throw new InvalidOperationException(
                        $"Sub-agent pool is full (capacity={_maxCapacity}). " +
                        $"Cannot create '{name}'. Consider calling DestroyAsync on unused agents.");
                }
            }

            _pool[name] = entry;
        }
        finally
        {
            _stateLock.Release();
        }

        _logger.LogInformation(
            "[SubAgentPool] Reserved sub-agent '{Name}' subSessionId={Sub} template={Template}",
            name, entry.SubSessionId, request.TemplateId);

        return entry.ToSnapshot();
    }

    // ════════════════════════════════════════════════════════
    // 获取
    // ════════════════════════════════════════════════════════

    /// <summary>获取指定子代理的信息快照。不存在时返回 null。</summary>
    public Task<PooledSubAgent?> GetAsync(string name, CancellationToken ct = default)
    {
        if (_pool.TryGetValue(name, out var entry))
        {
            return Task.FromResult<PooledSubAgent?>(entry.ToSnapshot());
        }
        return Task.FromResult<PooledSubAgent?>(null);
    }

    /// <summary>
    /// 读取条目声明的模型路由。只读一次库（内存视图不持有 model_id，它是展示/声明列）。
    /// <para>
    /// 刻意不缓存：这一列可被 PATCH 改，缓存会让「刚改完就该生效」变成「重启才生效」。
    /// 一次按主键的读换正确性，比省略一次查重要便宜。
    /// </para>
    /// </summary>
    public async Task<string?> GetDeclaredModelIdAsync(string name, CancellationToken ct = default)
    {
        if (_entryStore is null)
            return null; // 纯内存模式：没有持久化列，就没有可声明的模型
        if (!_pool.TryGetValue(name, out var entry))
            return null;

        try
        {
            var row = await _entryStore.GetByIdAsync(entry.PoolId, ct);
            return string.IsNullOrWhiteSpace(row?.ModelId) ? null : row!.ModelId;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 继承是增强：读不到就用平台默认，不把库抖动升级成派活失败。
            _logger.LogDebug(ex, "[SubAgentPool] 读取条目 {Name} 的声明模型失败，本次不继承", name);
            return null;
        }
    }

    // ════════════════════════════════════════════════════════
    // 执行
    // ════════════════════════════════════════════════════════

    /// <summary>
    /// 执行收尾：只在「条目仍是被执行的那个对象」且「执行令牌仍匹配」时应用结果。
    /// <para>
    /// 判据刻意不用名字：同名条目可能在执行期间被 destroy → create 换成另一个对象，
    /// 此时按名字回查会把旧执行的结果写到新条目上（task_count 被加、last_success 被覆盖、
    /// status 被打回 Sleeping）。引用相等 + 令牌双判，任一不符即丢弃并记一条 warning。
    /// </para>
    /// </summary>
    /// <summary>异常与日志文案里只展示会话 id 前 8 位，避免整串 id 刷屏。</summary>
    private static string Shorten(string value) =>
        value.Length <= 8 ? value : value[..8];

    private async Task FinishAsync(
        string name,
        PoolEntry executedEntry,
        string executionId,
        bool success,
        CancellationToken ct)
    {
        await _stateLock.WaitAsync(ct);
        try
        {
            if (!_pool.TryGetValue(name, out var current) ||
                !ReferenceEquals(current, executedEntry) ||
                !string.Equals(current.ActiveExecutionId, executionId, StringComparison.Ordinal))
            {
                // stale completion：丢弃，不碰任何计数与状态
                _logger.LogWarning(
                    "[SubAgentPool] Stale completion dropped name={Name} sub={Sub} exec={Exec}",
                    name, executedEntry.SubSessionId, executionId);
                return;
            }

            // 第二道判定（S5a，DB 为准）：内存令牌只在本进程内有效——
            // 条目可能在另一路径被写成 sleeping（sleep / 重启后的 busy 复位），
            // 那时内存里的 ActiveExecutionId 仍与本次令牌相等，但库里那行已经不归这次执行了。
            // 因此必须再由库条件（status='busy' 且 active_execution_id 匹配）确认一次。
            //
            // 为什么 0 行时连内存也不改：内存是库的缓存，不是事实源。
            // 库已经判定「这次收尾不成立」时如果只改内存，会出现内存 task_count=1 /
            // status=sleeping 而库里 task_count=0 / status=busy 的分叉：
            // 下一次 ExecuteAsync 的内存前置检查会放行，DB 侧却抢不到——错误要到那时才暴露，
            // 而且看起来像另一个无关模块的 bug。
            if (_entryStore is not null)
            {
                var affected = await _entryStore.TryCompleteAsync(
                    executedEntry.PoolId,
                    executedEntry.SubSessionId,
                    executionId,
                    success,
                    DateTimeOffset.UtcNow,
                    ct);

                if (affected == 0)
                {
                    _logger.LogWarning(
                        "[SubAgentPool] Stale completion dropped by store name={Name} poolId={PoolId} sub={Sub} exec={Exec}",
                        name, executedEntry.PoolId, executedEntry.SubSessionId, executionId);
                    return;
                }
            }

            current.Status = PooledSubAgentStatus.Sleeping;
            current.TaskCount++;
            current.LastSuccess = success;
            current.LastUsedAt = DateTimeOffset.UtcNow;
            current.ActiveExecutionId = null;
        }
        finally
        {
            _stateLock.Release();
        }
    }

    /// <summary>
    /// 条件抢占库里的那一行（S5a：所有执行都必须先过库这一道）。
    /// <para>
    /// 0 行不是异常而是结论：那一行已被别的执行占着、归属不符、状态已前进或已软删。
    /// 此时必须原地放弃——继续执行会真的建子会话并消费父级预算，而收尾时
    /// <c>TryCompleteAsync</c> 又会因令牌不匹配把结果丢掉，等于白跑一轮还把内存状态搞乱。
    /// </para>
    /// <para>
    /// 纯内存模式（<c>_entryStore == null</c>）直接放行：没有库就没有「抢占」这回事，
    /// 既有 38 个池用例走的就是这条路。
    /// </para>
    /// <para>
    /// 归属谓词无法表达时（遗留行 <c>owner_session_id</c> 为 NULL）也放行：
    /// SQL 里 <c>NULL = @p</c> 恒为假，硬要调用只会把一条本可复用的条目永久判成「被占用」。
    /// </para>
    /// </summary>
    private async Task AcquireInStoreAsync(
        PoolEntry entry,
        SubAgentSpawnRequest request,
        string executionId,
        CancellationToken ct)
    {
        if (_entryStore is null)
            return;

        if (string.IsNullOrEmpty(entry.OwnerSessionId))
        {
            _logger.LogDebug(
                "[SubAgentPool] Skipping store acquire for '{Name}' poolId={PoolId}: no owner session recorded",
                entry.Name, entry.PoolId);
            return;
        }

        var affected = await _entryStore.TryAcquireAsync(
            entry.PoolId,
            request.ParentSessionId,
            request.WorkspaceId,
            executionId,
            DateTimeOffset.UtcNow,
            ct);

        if (affected == 0)
        {
            throw new InvalidOperationException(
                $"Sub-agent '{entry.Name}' could not be acquired in the persistent store " +
                $"(poolId={entry.PoolId}): it is busy, owned by another session/workspace, " +
                "or already deleted. Refusing to run a task the store has not handed to this execution.");
        }
    }

    /// <summary>
    /// 向已有子代理发送新任务，追加到已有会话。
    /// 若子代理不存在则自动创建。
    /// 执行期间状态从 Sleeping/Idle → Busy，完成后 → Sleeping。
    /// </summary>
    public async Task<SubAgentExecuteResult> ExecuteAsync(
        string name,
        SubAgentSpawnRequest request,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Sub-agent pool name cannot be null or empty.", nameof(name));

        // 每次执行生成一个令牌，收尾时与条目上的令牌比对；不匹配即视为迟到收尾并丢弃。
        var executionId = Guid.NewGuid().ToString("N");

        // 先把库里的视图加载好再写库（S5a）：未加载时内存里看不到库里已有的行，
        // 条件更新会命中一个本地根本不知道的行，于是「本地看上去空闲」与
        // 「库里已经被别的执行占着」会得出相反结论，而且两边都不会报错。
        await EnsureReadyAsync(ct);

        // 获取或创建池条目
        PoolEntry entry;
        bool autoReserved = false;

        await _stateLock.WaitAsync(ct);
        try
        {
            if (_pool.TryGetValue(name, out var existing))
            {
                if (existing.Status == PooledSubAgentStatus.Busy)
                {
                    throw new InvalidOperationException(
                        $"Sub-agent '{name}' is currently busy. Wait for the current task to complete.");
                }
                if (existing.Status == PooledSubAgentStatus.Dead)
                {
                    throw new InvalidOperationException(
                        $"Sub-agent '{name}' has been destroyed and cannot be reused.");
                }

                // 归属校验（D-P0-1）：只允许创建者父会话在同一工作区驱动该条目。
                // 必须放在置 Busy 之前——跨父复用会先建 run、再在会话投影上抛错，
                // 那时只能把 run 终结成 failed 并污染池计数，无法撤回。
                var ownerSession = existing.OwnerSessionId;
                if (!string.IsNullOrEmpty(ownerSession) &&
                    !string.Equals(ownerSession, request.ParentSessionId, StringComparison.Ordinal))
                {
                    throw new PoolOwnershipMismatchException(
                        $"Sub-agent '{name}' belongs to parent session '{Shorten(ownerSession)}' " +
                        $"but was requested by '{Shorten(request.ParentSessionId)}'.",
                        name, ownerSession, request.ParentSessionId,
                        PoolOwnershipMismatchException.OwnerSessionReason);
                }

                var ownerWorkspace = existing.OwnerWorkspaceId;
                if (!string.IsNullOrEmpty(ownerWorkspace) &&
                    !string.Equals(ownerWorkspace, request.WorkspaceId, StringComparison.Ordinal))
                {
                    throw new PoolOwnershipMismatchException(
                        $"Sub-agent '{name}' belongs to workspace '{ownerWorkspace}' " +
                        $"but was requested from '{request.WorkspaceId}'.",
                        name, ownerSession, request.ParentSessionId,
                        PoolOwnershipMismatchException.OwnerWorkspaceReason);
                }

                // 标记为 Busy 之前先抢库里的那一行（S5a，DB 是第一道真相）。
                // 顺序不能倒：先改内存再写库时，若库拒绝（被别的执行占着 / 已软删 / 状态已前进），
                // 内存就会停在 Busy 而那条执行根本不存在，之后同名的每次调用都会被自己卡死。
                await AcquireInStoreAsync(existing, request, executionId, ct);

                // 标记为 Busy（在锁内，确保状态变更原子性）
                existing.Status = PooledSubAgentStatus.Busy;
                existing.LastUsedAt = DateTimeOffset.UtcNow;
                entry = existing;
            }
            else
            {
                if (_pool.Count >= _maxCapacity)
                {
                    throw new InvalidOperationException(
                        $"Sub-agent pool is full (capacity={_maxCapacity}). " +
                        $"Cannot create '{name}'. Consider calling DestroyAsync on unused agents.");
                }

                var poolId = Guid.NewGuid().ToString("N");
                entry = new PoolEntry(
                    poolId: poolId,
                    name: name,
                    subSessionId: SubAgentSessionId.Create(request.ParentSessionId),
                    templateId: request.TemplateId,
                    role: request.RoleInPlan,
                    ownerSessionId: request.ParentSessionId,
                    ownerWorkspaceId: request.WorkspaceId)
                {
                    Status = PooledSubAgentStatus.Busy,
                    LastUsedAt = DateTimeOffset.UtcNow,
                };

                if (_entryStore is not null)
                {
                    // 自动预留的条目此刻库里还没有行。必须先插成 idle 再走与 CreateAsync 同一道
                    // TryAcquire：两条路径落成同一种库语义（插入 → 条件抢占），
                    // 而不是在打开路径里直接写一个 busy 行——后者会让重启恢复读到一个
                    // 从未被 TryAcquire 校验过归属的 busy 行。
                    var inserted = await _entryStore.TryInsertAsync(
                        new AgentPoolInsertRequest(
                            PoolId: poolId,
                            Name: name,
                            SubSessionId: entry.SubSessionId,
                            TemplateId: request.TemplateId,
                            Role: request.RoleInPlan,
                            OwnerWorkspaceId: request.WorkspaceId,
                            OwnerSessionId: request.ParentSessionId,
                            OwnerAgentId: request.ParentAgentId,
                            CreatedAt: entry.CreatedAt),
                        capacity: _maxCapacity,
                        ct);

                    if (inserted == 0)
                    {
                        // 与 CreateAsync 同一文案：两种「满」对调用方的处置完全一样。
                        throw new InvalidOperationException(
                            $"Sub-agent pool is full (capacity={_maxCapacity}). " +
                            $"Cannot create '{name}'. Consider calling DestroyAsync on unused agents.");
                    }

                    // 抢不到就抛：这行刚由本方法插入且是 idle，抢不到只能说明前提判断错了，
                    // 此时宁可报错也不要把一个未接管的条目塞进内存。
                    await AcquireInStoreAsync(entry, request, executionId, ct);
                }

                // 先落库再进内存：库拒绝时内存里不得留下「看起来存在但库里不可抢」的条目。
                _pool[name] = entry;
                autoReserved = true;
            }

            // 置 Busy 的同时写入执行令牌：收尾必须凭它判定「这次执行是否还属于这个条目」。
            entry.ActiveExecutionId = executionId;
        }
        finally
        {
            _stateLock.Release();
        }

        if (autoReserved)
        {
            _logger.LogInformation(
                "[SubAgentPool] Auto-reserved sub-agent '{Name}' subSessionId={Sub}",
                name, entry.SubSessionId);
        }

        // 构建复用会话的请求：保持相同的 SubSessionId 以复用会话历史
        // 通过 ReuseSubSessionId 传入 pool entry 的 SubSessionId 以复用会话
        //
        // 角色同理继承：池复用的意义是「同一个成员、同一套配置」，显式传的字段优先
        // （允许单次覆盖），没传的字段继承成员保存的声明。否则「advisor 永远当审阅者」
        // 这类约定会退化成每次派活都要重新指定一遍，而漏指定时执行器看到的是空值。
        var executeRequest = request with
        {
            ReuseSubSessionId = entry.SubSessionId,
            RoleInPlan = string.IsNullOrWhiteSpace(request.RoleInPlan) ? entry.Role : request.RoleInPlan,
        };

        SubAgentExecuteResult result;
        try
        {
            result = await _subAgentManager.ExecuteSyncAsync(executeRequest, ct);
        }
        catch (Exception ex)
        {
            // 执行失败，更新状态（迟到的收尾会被 FinishAsync 丢弃）
            await FinishAsync(name, entry, executionId, success: false, CancellationToken.None);

            _logger.LogError(ex,
                "[SubAgentPool] Execute failed for '{Name}' subSessionId={Sub}",
                name, entry.SubSessionId);

            throw;
        }

        // 执行成功，更新状态（迟到的收尾会被 FinishAsync 丢弃）
        await FinishAsync(name, entry, executionId, success: result.Success, CancellationToken.None);

        _logger.LogInformation(
            "[SubAgentPool] Execute completed for '{Name}' success={Success} taskCount={TaskCount}",
            name, result.Success, entry.TaskCount);

        return result;
    }

    // ════════════════════════════════════════════════════════
    // 睡眠
    // ════════════════════════════════════════════════════════

    /// <summary>
    /// 标记子代理为空闲（Sleeping）。
    /// 不存在或已销毁时返回 false。
    /// </summary>
    public async Task<bool> SleepAsync(string name, CancellationToken ct = default)
    {
        await _stateLock.WaitAsync(ct);
        try
        {
            if (!_pool.TryGetValue(name, out var entry))
            {
                _logger.LogWarning("[SubAgentPool] SleepAsync: '{Name}' not found", name);
                return false;
            }

            if (entry.Status == PooledSubAgentStatus.Dead)
            {
                _logger.LogWarning("[SubAgentPool] SleepAsync: '{Name}' is already dead", name);
                return false;
            }

            if (entry.Status == PooledSubAgentStatus.Busy)
            {
                // 与缺陷 D1 同型：busy 时只改 Status=Sleeping 会让执行继续在后台烧 token，
                // 而日志里的 force-sleeping 会让人以为已经停下（日志在说谎）。
                // 先真取消执行链，再标记休眠。
                _logger.LogInformation(
                    "[SubAgentPool] SleepAsync: '{Name}' is busy, cancelling the in-flight run before sleeping", name);
                await _subAgentManager.CancelAsync(
                    entry.SubSessionId,
                    "Pooled sub-agent put to sleep",
                    ct);
            }

            // DB 为准（S5a）：先把库里那一行迁到 sleeping，成功才改缓存。
            //
            // 为什么 0 行时不改内存：库拒绝意味着「我看到的那个前置状态已经不成立」
            // （被别的执行改成 busy、已被置 dead、行已软删）。此时把内存改成 Sleeping
            // 会让下次 ExecuteAsync 的内存前置检查放行，而 DB 侧又抢不到——
            // 错会推迟到下一次派活才暴露，届时已分不清是谁改的。
            //
            // 取舍（见 RISKS）：0 行不回读校正——本阶段只把 DB 当唯一真相并停下来，
            // 回读需要额外的读路径与它自己的一致性错误处理，而那属于 S5c 的恢复语义。
            if (_entryStore is not null)
            {
                var expected = ToStoreStatus(entry.Status);
                var affected = await _entryStore.TrySetStatusAsync(
                    entry.PoolId,
                    expected,
                    AgentPoolEntity.StatusSleeping,
                    statusReason: null,
                    clearExecution: true,
                    now: DateTimeOffset.UtcNow,
                    ct);

                if (affected == 0)
                {
                    _logger.LogWarning(
                        "[SubAgentPool] SleepAsync: store rejected transition for '{Name}' poolId={PoolId} " +
                        "expected={Expected}; memory left untouched (store is the source of truth)",
                        name, entry.PoolId, expected);
                    return false;
                }

                // 缓存跟随库：库已经把执行令牌清空，内存再留着它会让之后的迟到收尾
                // 在内存这一关「判定通过」，白跑一次 DB 回写。
                entry.ActiveExecutionId = null;
            }

            entry.Status = PooledSubAgentStatus.Sleeping;
            entry.LastUsedAt = DateTimeOffset.UtcNow;

            _logger.LogInformation("[SubAgentPool] '{Name}' set to Sleeping", name);
            return true;
        }
        finally
        {
            _stateLock.Release();
        }
    }

    // ════════════════════════════════════════════════════════
    // 销毁
    // ════════════════════════════════════════════════════════

    /// <summary>
    /// 销毁子代理，释放资源。
    /// 不存在时返回 false（不抛异常）。
    /// </summary>
    public async Task<bool> DestroyAsync(string name, CancellationToken ct = default)
    {
        await _stateLock.WaitAsync(ct);
        try
        {
            if (!_pool.TryGetValue(name, out var entry))
            {
                _logger.LogWarning("[SubAgentPool] DestroyAsync: '{Name}' not found", name);
                return false;
            }

            // 同 SleepAsync：先让库迁到 dead（store 会同时写 deleted_at），成功后才动内存。
            // 两种侧重点不同的错误在这里对称：
            //   • 先删内存再写库 → 库拒绝时库里还活着、内存没了，重启后它又“活”回来，
            //     而调用方已经收到「已销毁」；
            //   • 库拒绝却删内存 → 同一个名字又能被 create 成新条目，但库里旧行还占着唯一索引
            //     （软删后同名才可被新条目占用），新条目会在插库时撞约束。
            // 所以 0 行时一律保留内存条目并返回 false，让调用方重试或先查清状态。
            if (_entryStore is not null)
            {
                var expected = ToStoreStatus(entry.Status);
                var affected = await _entryStore.TrySetStatusAsync(
                    entry.PoolId,
                    expected,
                    AgentPoolEntity.StatusDead,
                    statusReason: null,
                    clearExecution: true,
                    now: DateTimeOffset.UtcNow,
                    ct);

                if (affected == 0)
                {
                    _logger.LogWarning(
                        "[SubAgentPool] DestroyAsync: store rejected transition for '{Name}' poolId={PoolId} " +
                        "expected={Expected}; entry kept in memory",
                        name, entry.PoolId, expected);
                    return false;
                }
            }

            _pool.TryRemove(name, out _);

            var wasBusy = entry.Status == PooledSubAgentStatus.Busy;
            entry.Status = PooledSubAgentStatus.Dead;

            // 同 SleepAsync：条目已死并不代表执行循环已停。先真取消，避免「已销毁但仍在烧 token」。
            if (wasBusy)
            {
                await _subAgentManager.CancelAsync(
                    entry.SubSessionId,
                    "Pooled sub-agent destroyed",
                    ct);
            }

            _logger.LogInformation(
                "[SubAgentPool] Destroyed '{Name}' subSessionId={Sub} taskCount={TaskCount}",
                name, entry.SubSessionId, entry.TaskCount);

            return true;
        }
        finally
        {
            _stateLock.Release();
        }
    }

    // ════════════════════════════════════════════════════════
    // 列表
    // ════════════════════════════════════════════════════════

    /// <summary>
    /// 列出池中所有子代理的状态快照。
    /// <para>
    /// <b>未就绪时必须抛错，不能返回空集合：</b>本方法的调用方之一是
    /// <c>SubAgentTransientDirectoryGcService</c>，它拿这个集合当「哪些目录受池保护」的删除白名单。
    /// 「还没加载完」返回空集合 = 告诉 GC「没有任何池化目录需要保护」，那些目录会被当成孤儿隔离。
    /// 把「不知道」伪装成「没有」会直接导致丢数据；抛错只会让本轮扫描跳过。
    /// </para>
    /// </summary>
    public IReadOnlyList<PooledSubAgent> List()
    {
        if (_readiness != PoolReadiness.Ready)
            throw BuildNotReadyException();

        return _pool.Values
            .Select(e => e.ToSnapshot())
            .OrderByDescending(e => e.LastUsedAt)
            .ToList();
    }

    /// <inheritdoc />
    /// <remarks>
    /// 与 <see cref="List"/> 的差别只在「会不会先等加载」：同步方法无法 await，
    /// 所以需要「等到就绪再取」的调用方用这个。未就绪且加载失败时同样抛错，
    /// 不回退成空集合。
    /// </remarks>
    public async Task<IReadOnlyList<PooledSubAgent>> GetSnapshotAsync(CancellationToken ct = default)
    {
        await EnsureReadyAsync(ct);
        return List();
    }

    // ════════════════════════════════════════════════════════
    // LRU 淘汰（可选）
    // ════════════════════════════════════════════════════════

    /// <summary>
    /// 尝试淘汰最久未使用的 Sleeping 子代理。
    /// 返回被淘汰的子代理名称，无可用候选时返回 null。
    /// <para>
    /// <b>有存储层时淘汰必须以库为准：</b>淘汰语义是「这条条目永久不可复用」，
    /// 只改内存会让库里那一行继续保持 <c>sleeping</c> 且未软删，重启后
    /// <see cref="EnsureReadyAsync"/> 会把它从库重新加载回来（复活缺陷）。
    /// 因此顺序固定为「先写库 → 成功后再踢内存」，与 <see cref="DestroyAsync"/> 同形。
    /// </para>
    /// </summary>
    public async Task<string?> EvictLeastRecentlyUsedAsync(CancellationToken ct = default)
    {
        await _stateLock.WaitAsync(ct);
        try
        {
            // 纯内存模式（无 store）：没有「重启复活」这回事，保持原有 LRU 行为。
            if (_entryStore is null)
            {
                var candidate = _pool.Values
                    .Where(e => e.Status == PooledSubAgentStatus.Sleeping)
                    .OrderBy(e => e.LastUsedAt)
                    .FirstOrDefault();

                if (candidate == null)
                {
                    _logger.LogDebug("[SubAgentPool] EvictLRU: no sleeping candidate found");
                    return null;
                }

                _pool.TryRemove(candidate.Name, out _);
                candidate.Status = PooledSubAgentStatus.Dead;

                _logger.LogInformation(
                    "[SubAgentPool] Evicted LRU sub-agent '{Name}' lastUsed={LastUsed}",
                    candidate.Name, candidate.LastUsedAt);

                return candidate.Name;
            }

            // 有 store 时选人交给库：LRU 的顺序必须与库里 <c>last_used_at</c> 一致，
            // 由内存再猜一次容易与库分叉（两者的时间可能不是同一份）。
            var evictedPoolId = await _entryStore.TryEvictOldestSleepingAsync(DateTimeOffset.UtcNow, ct);
            if (evictedPoolId is null)
            {
                _logger.LogDebug("[SubAgentPool] EvictLRU: store reports no sleeping entry to evict");
                return null;
            }

            // 按 PoolId 而不是按名字回查：名字可以被 destroy → create 换成另一个对象，
            // 只有 PoolId 与库里那一行一一对应。
            var entry = _pool.Values.FirstOrDefault(e => e.PoolId == evictedPoolId);
            if (entry is null)
            {
                // 库淘汰成功但内存没有对应条目：说明两边已经分叉（例如本进程从未走过
                // EnsureReadyAsync，或条目只存在于库里）。库已经改了，抛异常只会让
                // 调用方误以为可以重试；这里记 warning 暴露分叉即可。
                _logger.LogWarning(
                    "[SubAgentPool] EvictLRU: store evicted poolId={PoolId} but memory has no matching entry; " +
                    "views have diverged",
                    evictedPoolId);
                return null;
            }

            _pool.TryRemove(entry.Name, out _);
            entry.Status = PooledSubAgentStatus.Dead;

            _logger.LogInformation(
                "[SubAgentPool] Evicted LRU sub-agent '{Name}' poolId={PoolId} lastUsed={LastUsed}",
                entry.Name, entry.PoolId, entry.LastUsedAt);

            return entry.Name;
        }
        finally
        {
            _stateLock.Release();
        }
    }
}
