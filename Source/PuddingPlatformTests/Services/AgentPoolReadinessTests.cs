using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using PuddingCode.Abstractions;
using PuddingCode.Configuration;
using PuddingCode.Models;
using PuddingCode.Platform;
using PuddingCode.Runtime;
using PuddingPlatform.Data;
using PuddingPlatform.Data.Entities;
using PuddingPlatform.Services;
using PuddingPlatform.Services.AgentPool;

namespace PuddingPlatformTests.Services;

/// <summary>
/// 持久子代理池的「就绪门」验收（S4 / D-P0-2）。
/// <para>
/// 这一层要锁的不是「加载成功了」，而是<b>「还没加载完」与「确实没有成员」在行为上必须可区分</b>。
/// 池集合有一处被当成删除白名单使用（<see cref="SubAgentTransientDirectoryGcService"/>），
/// 一旦未就绪时返回空集合，那些池化目录就会被当成孤儿隔离——代价是删数据。
/// 所以每个用例都在替「不返回空集合」这件事取红，而不只是断言加载路径 happy path。
/// </para>
/// </summary>
[TestClass]
public sealed class AgentPoolReadinessTests
{
    private static readonly DateTimeOffset BaseTime = new(2026, 11, 21, 2, 0, 0, TimeSpan.Zero);

    // ═══════════════════════════════════════════════════════
    // 1. 未就绪的 List() 必须抛，不得返回空集合
    // ═══════════════════════════════════════════════════════

    [TestMethod]
    public void List_WhenNotReady_ThrowsPoolNotReadyException()
    {
        // 配了 store 但从未触发加载：这正是「进程刚起来」的真实状态。
        var pool = CreatePool(new FakePoolStore(loadResult: []));

        Assert.AreEqual(PoolReadiness.NotStarted, pool.Readiness,
            "有存储层时，未触发加载前必须是 NotStarted（不是 Ready，也不是空池）");

        var ex = Assert.ThrowsExactly<PoolNotReadyException>(() => pool.List());

        Console.WriteLine($"[evidence] List() threw {ex.GetType().Name}: readiness={ex.Readiness}, msg={ex.Message}");

        Assert.AreEqual(PoolReadiness.NotStarted, ex.Readiness, "异常必须带出抛错时的就绪状态");
    }

    // ═══════════════════════════════════════════════════════
    // 2. Ready + 0 条是合法的空池，不能当成未就绪
    // ═══════════════════════════════════════════════════════

    [TestMethod]
    public async Task ReadyEmptyPool_IsNotEmpty_ListReturnsEmpty()
    {
        var store = new FakePoolStore(loadResult: []);
        var pool = CreatePool(store);

        await pool.EnsureReadyAsync();

        var listed = pool.List();

        Console.WriteLine($"[evidence] readiness={pool.Readiness}, listed={listed.Count}, loadCalls={store.LoadAllCalls}");

        Assert.AreEqual(PoolReadiness.Ready, pool.Readiness);
        Assert.HasCount(0, listed, "就绪且库为空时，List() 必须正常返回空集合（不抛）——这正是「确实没有成员」");
        Assert.AreEqual(1, store.LoadAllCalls, "一次性加载：并发/重复调用不应重复读库");
    }

    // ═══════════════════════════════════════════════════════
    // 3. 加载失败 → Failed 终态，且绝不降级为空池
    // ═══════════════════════════════════════════════════════

    [TestMethod]
    public async Task EnsureReady_LoadFailure_SetsFailedAndDoesNotDegradeToEmpty()
    {
        var store = new FakePoolStore(loadFailure: new InvalidOperationException("disk I/O error"));
        var pool = CreatePool(store);

        var loadError = await Assert.ThrowsExactlyAsync<PoolNotReadyException>(
            () => pool.EnsureReadyAsync());

        Console.WriteLine($"[evidence] load threw {loadError.GetType().Name}: readiness={loadError.Readiness}, msg={loadError.Message}");

        Assert.AreEqual(PoolReadiness.Failed, pool.Readiness, "加载失败后状态必须是 Failed");
        Assert.AreEqual(PoolReadiness.Failed, loadError.Readiness);
        Assert.IsTrue(loadError.InnerException is InvalidOperationException,
            "原始异常必须留在异常链里，否则上层只能看到「池不可用」而不知道「为什么」");
        StringAssert.Contains(loadError.Message, "disk I/O error", "错误文案必须能说出恢复失败的原因");

        // 关键断言：失败之后 List() 必须继续抛，绝不能变成「空池」。
        var listError = Assert.ThrowsExactly<PoolNotReadyException>(() => pool.List());
        Assert.AreEqual(PoolReadiness.Failed, listError.Readiness);

        // 失败是终态：再问一次也只报错，不会偷偷重试成 Ready。
        var again = await Assert.ThrowsExactlyAsync<PoolNotReadyException>(() => pool.EnsureReadyAsync());
        Assert.AreEqual(PoolReadiness.Failed, again.Readiness);
        Assert.AreEqual(1, store.LoadAllCalls, "失败后不得静默重试（重试由重启进程决定，不由每次调用决定）");
    }

    // ═══════════════════════════════════════════════════════
    // 4. 就绪时把库里的条目加载进内存视图
    // ═══════════════════════════════════════════════════════

    [TestMethod]
    public async Task EnsureReady_LoadsEntriesFromStore()
    {
        var later = BaseTime.AddMinutes(30);
        var store = new FakePoolStore(loadResult:
        [
            Snapshot("pool-a", name: "agent-a", subSessionId: "session-a-sub-1111aaaa",
                status: AgentPoolEntity.StatusSleeping, taskCount: 2, lastSuccess: true,
                createdAt: BaseTime, lastUsedAt: later, role: "worker"),
            Snapshot("pool-b", name: "agent-b", subSessionId: "session-b-sub-2222bbbb",
                status: AgentPoolEntity.StatusIdle, taskCount: 0, lastSuccess: null,
                createdAt: BaseTime, lastUsedAt: BaseTime, role: null),
        ]);
        var pool = CreatePool(store);

        var loaded = await pool.GetSnapshotAsync();

        Console.WriteLine($"[evidence] readiness={pool.Readiness}, loaded={loaded.Count}, names={string.Join(",", loaded.Select(x => x.Name))}");

        Assert.AreEqual(PoolReadiness.Ready, pool.Readiness);
        Assert.HasCount(2, loaded);

        var a = loaded.Single(x => x.Name == "agent-a");
        Assert.AreEqual("session-a-sub-1111aaaa", a.SubSessionId);
        Assert.AreEqual("workspace-task-agent", a.TemplateId);
        Assert.AreEqual("worker", a.Role);
        Assert.AreEqual(PooledSubAgentStatus.Sleeping, a.Status);
        Assert.AreEqual(2, a.TaskCount, "task_count 必须来自库，否则复用价值指标会在重启后归零");
        Assert.AreEqual(true, a.LastSuccess);
        Assert.AreEqual(BaseTime, a.CreatedAt, "创建时刻必须来自库（用 UtcNow 重建会让旧条目看起来是刚生的）");
        Assert.AreEqual(later, a.LastUsedAt, "最近使用时刻必须来自库（LRU 选型依赖它）");

        var b = loaded.Single(x => x.Name == "agent-b");
        Assert.AreEqual(PooledSubAgentStatus.Idle, b.Status);
        Assert.IsNull(b.LastSuccess, "从未执行过的条目 last_success 必须是 null（三态）");
    }

    // ═══════════════════════════════════════════════════════
    // 5. CreateAsync 真的写库（真实 SQLite + 真实 store）
    // ═══════════════════════════════════════════════════════

    [TestMethod]
    public async Task Create_WithStore_WritesThrough()
    {
        await using var scope = await PoolDb.CreateAsync();
        var pool = CreatePool(scope.Store);

        var created = await pool.CreateAsync("dev-agent", CreateSpawnRequest());

        var rows = await scope.Store.LoadAllAsync(default);

        Console.WriteLine($"[evidence] pool.Count={pool.Count}, rows={rows.Count}, " +
                          $"name={rows.SingleOrDefault()?.Name}, sub={rows.SingleOrDefault()?.SubSessionId}, " +
                          $"status={rows.SingleOrDefault()?.Status}");

        Assert.HasCount(1, rows, "CreateAsync 必须落一行到 agent_pool");
        var row = rows[0];
        Assert.AreEqual("dev-agent", row.Name);
        Assert.AreEqual(created.SubSessionId, row.SubSessionId, "库里的复用键必须与返回给调用方的一致");
        Assert.AreEqual("workspace-task-agent", row.TemplateId);
        Assert.AreEqual("parent-session", row.OwnerSessionId, "归属必须落库：持久化后条目会活过创建它的那次执行");
        Assert.AreEqual("default", row.OwnerWorkspaceId);
        Assert.AreEqual(AgentPoolEntity.StatusIdle, row.Status);
        Assert.AreEqual(0, row.TaskCount);

        // 内存视图与库两侧都要有：只写库不写内存会让刚创建的条目在本次进程里看不见。
        Assert.AreEqual(1, pool.Count);
        Assert.IsNotNull(await pool.GetAsync("dev-agent"));
    }

    // ═══════════════════════════════════════════════════════
    // 6. GC 门控：池未就绪时不得枚举/移动目录
    // ═══════════════════════════════════════════════════════

    [TestMethod]
    public async Task GcSweep_WhenPoolNotReady_SkipsWithoutScanningOrMoving()
    {
        using var temp = new TempDataRoot();
        var now = new DateTimeOffset(2026, 11, 21, 0, 0, 0, TimeSpan.Zero);
        const string subSessionId = "parent-session-sub-9999ffff";

        // 造一个「本可被隔离」的候选：空脚手架 + 两天前的 completed run + 非池化。
        // 这样若池门被放到枚举之后（或未就绪被当成空池），本轮就会真的隔离它。
        CreateEmptySkillScaffold(temp.Paths, subSessionId, now.AddDays(-2));
        await temp.AddRunAsync(subSessionId, "completed", now.AddDays(-2), now.AddDays(-2));

        var service = temp.CreateService(new NotReadyPool());
        var result = await service.SweepOnceAsync(now);

        Console.WriteLine($"[evidence] scanned={result.Scanned}, quarantined={result.Quarantined}, " +
                          $"errors={result.Errors}, dirStillExists={Directory.Exists(temp.Paths.AgentInstanceRoot(subSessionId))}");

        Assert.AreEqual(0, result.Scanned, "未就绪时不得枚举候选目录（Scanned 必须为 0）");
        Assert.AreEqual(0, result.Quarantined, "未就绪时不得隔离任何目录");
        Assert.AreEqual(1, result.Errors, "未就绪必须记一条错误，让这一轮在诊断里可见");
        Assert.IsTrue(Directory.Exists(temp.Paths.AgentInstanceRoot(subSessionId)),
            "池未就绪时目录必须原地不动");
        Assert.IsFalse(
            Directory.Exists(temp.Paths.SubAgentTransientDirectoryQuarantineRoot) &&
            Directory.EnumerateDirectories(temp.Paths.SubAgentTransientDirectoryQuarantineRoot).Any(),
            "未就绪时不得写 quarantine");
    }

    // ── 测试脚手架 ──────────────────────────────────────────

    private static SubAgentPool CreatePool(IAgentPoolEntryStore store, int? maxCapacity = null)
    {
        var configBuilder = new ConfigurationBuilder();
        if (maxCapacity.HasValue)
        {
            configBuilder.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["SubAgentPool:MaxCapacity"] = maxCapacity.Value.ToString(),
            });
        }

        return new SubAgentPool(
            new TestSubAgentManager(),
            configBuilder.Build(),
            NullLogger<SubAgentPool>.Instance,
            store);
    }

    private static SubAgentSpawnRequest CreateSpawnRequest(string task = "test task") => new()
    {
        ParentSessionId = "parent-session",
        WorkspaceId = "default",
        TaskDescription = task,
        LlmConfig = new LlmConfig
        {
            ModelId = "test-model",
            KeyVaultId = "test-key",
            Endpoint = "https://test.example.com/v1",
        },
        LlmProfile = new LlmInvocationProfile
        {
            ProviderId = "test-provider",
            ProfileId = "subagent.conscious",
            ModelId = "test-model",
            Role = "conscious",
        },
        TemplateId = "workspace-task-agent",
        RoleInPlan = "worker",
    };

    private static AgentPoolEntrySnapshot Snapshot(
        string poolId,
        string name,
        string subSessionId,
        string status,
        int taskCount,
        bool? lastSuccess,
        DateTimeOffset createdAt,
        DateTimeOffset lastUsedAt,
        string? role) => new(
            PoolId: poolId,
            Name: name,
            SubSessionId: subSessionId,
            TemplateId: "workspace-task-agent",
            Role: role,
            OwnerWorkspaceId: "default",
            OwnerSessionId: "parent-session",
            OwnerAgentId: "agent-1",
            ActiveExecutionId: null,
            Status: status,
            StatusReason: null,
            TaskCount: taskCount,
            LastSuccess: lastSuccess,
            CreatedAt: createdAt,
            LastUsedAt: lastUsedAt);

    /// <summary>
    /// 可控的假存储：只实现本组用例需要的读写，其余「本任务不接」的写路径明确抛错，
    /// 避免出现「调用成功但其实什么也没做」的假绿。
    /// </summary>
    private sealed class FakePoolStore : IAgentPoolEntryStore
    {
        private readonly IReadOnlyList<AgentPoolEntrySnapshot> _loadResult;
        private readonly Exception? _loadFailure;
        private int _loadAllCalls;

        public FakePoolStore(
            IReadOnlyList<AgentPoolEntrySnapshot>? loadResult = null,
            Exception? loadFailure = null)
        {
            _loadResult = loadResult ?? [];
            _loadFailure = loadFailure;
        }

        public int LoadAllCalls => Volatile.Read(ref _loadAllCalls);

        public Task<IReadOnlyList<AgentPoolEntrySnapshot>> LoadAllAsync(CancellationToken ct)
        {
            Interlocked.Increment(ref _loadAllCalls);
            return _loadFailure is not null
                ? Task.FromException<IReadOnlyList<AgentPoolEntrySnapshot>>(_loadFailure)
                : Task.FromResult(_loadResult);
        }

        public Task<int> TryInsertAsync(AgentPoolInsertRequest request, int capacity, CancellationToken ct)
            => Task.FromResult(1);

        public Task<int> TryAcquireAsync(string poolId, string ownerSessionId, string ownerWorkspaceId,
            string executionId, DateTimeOffset now, CancellationToken ct)
            => throw new NotSupportedException("S4 不覆盖抢占写路径");

        public Task<int> TryCompleteAsync(string poolId, string subSessionId, string executionId,
            bool success, DateTimeOffset now, CancellationToken ct)
            => throw new NotSupportedException("S4 不覆盖收尾写路径");

        public Task<int> TrySetStatusAsync(string poolId, string expectedStatus, string newStatus,
            string? statusReason, bool clearExecution, DateTimeOffset now, CancellationToken ct,
            string? expectedOwnerSessionId = null, string? expectedOwnerWorkspaceId = null)
            => throw new NotSupportedException("S4 不覆盖状态迁移写路径");

        public Task<string?> TryEvictOldestSleepingAsync(DateTimeOffset now, CancellationToken ct)
            => throw new NotSupportedException("S4 不覆盖淘汰写路径");

        public Task<int> ResetStaleBusyAsync(DateTimeOffset now, CancellationToken ct)
            => throw new NotSupportedException("S4 不覆盖复位写路径");

        public Task<AgentPoolEntrySnapshot?> GetByIdAsync(string poolId, CancellationToken ct)
            => throw new NotSupportedException("S4 只读 LoadAll");

        public Task<AgentPoolEntrySnapshot?> GetBySubSessionAsync(string subSessionId, CancellationToken ct)
            => throw new NotSupportedException("S4 只读 LoadAll");
    }

    /// <summary>未就绪的池：List() 必须像真实实现那样抛错，而不是返回空集合。</summary>
    private sealed class NotReadyPool : ISubAgentPool
    {
        public int Count => 0;
        public int MaxCapacity => 15;
        public PoolReadiness Readiness => PoolReadiness.Failed;

        public IReadOnlyList<PooledSubAgent> List() => throw new PoolNotReadyException(
            "[SubAgentPool] Pool is not ready (readiness=Failed); refusing to report an empty pool.",
            PoolReadiness.Failed);

        /// <summary>归属过滤不能绕开就绪门：否则先剥掉 scope 就能拿到空集合当「无成员」。</summary>
        public IReadOnlyList<PooledSubAgent> ListOwnedBy(PoolOwnerScope scope) => List();

        public Task<bool> SleepAsync(string name, PoolOwnerScope scope, CancellationToken ct = default)
            => throw new PoolNotReadyException(
                "[SubAgentPool] Pool is not ready (readiness=Failed); refusing to mutate entries.",
                PoolReadiness.Failed);

        public Task<bool> DestroyAsync(string name, PoolOwnerScope scope, CancellationToken ct = default)
            => throw new PoolNotReadyException(
                "[SubAgentPool] Pool is not ready (readiness=Failed); refusing to mutate entries.",
                PoolReadiness.Failed);

        public Task<IReadOnlyList<PooledSubAgent>> GetSnapshotAsync(CancellationToken ct = default)
            => throw new PoolNotReadyException(
                "[SubAgentPool] Pool is not ready (readiness=Failed); refusing to report an empty pool.",
                PoolReadiness.Failed);

        public Task<PooledSubAgent> CreateAsync(string name, SubAgentSpawnRequest request, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task<PooledSubAgent?> GetAsync(string name, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task<SubAgentExecuteResult> ExecuteAsync(string name, SubAgentSpawnRequest request, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task<bool> SleepAsync(string name, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task<bool> DestroyAsync(string name, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task<string?> EvictLeastRecentlyUsedAsync(CancellationToken ct = default)
            => throw new NotSupportedException();
    }

    private static void CreateEmptySkillScaffold(
        PuddingDataPaths paths,
        string subSessionId,
        DateTimeOffset lastWriteAt)
    {
        var root = paths.AgentInstanceRoot(subSessionId);
        var skillsRoot = Path.Combine(root, "skills");
        Directory.CreateDirectory(skillsRoot);
        File.WriteAllText(
            Path.Combine(skillsRoot, "index.json"),
            JsonSerializer.Serialize(new
            {
                agentInstanceId = subSessionId,
                generatedAt = lastWriteAt,
                skills = Array.Empty<object>(),
            }, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        Directory.SetLastWriteTimeUtc(skillsRoot, lastWriteAt.UtcDateTime);
        Directory.SetLastWriteTimeUtc(root, lastWriteAt.UtcDateTime);
    }

    /// <summary>真实临时 SQLite + 真实 store（与 <c>AgentPoolEntryStoreTests</c> 同一套建表路径）。</summary>
    private sealed class PoolDb : IAsyncDisposable
    {
        private PoolDb(string directory, DbContextOptions<PlatformDbContext> options)
        {
            Directory = directory;
            Store = new AgentPoolEntryStore(new TestDbContextFactory(options),
                NullLogger<AgentPoolEntryStore>.Instance);
        }

        public string Directory { get; }
        public IAgentPoolEntryStore Store { get; }

        public static async Task<PoolDb> CreateAsync()
        {
            var directory = Path.Combine(
                Path.GetTempPath(), "pudding-agent-pool-readiness-" + Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(directory);

            var options = new DbContextOptionsBuilder<PlatformDbContext>()
                .UseSqlite($"Data Source={Path.Combine(directory, "platform.db")}")
                .Options;

            await using (var db = new PlatformDbContext(options))
            {
                await AgentPoolSchemaBootstrapper.EnsureCreatedAsync(db);
            }

            return new PoolDb(directory, options);
        }

        public ValueTask DisposeAsync()
        {
            SqliteConnection.ClearAllPools();
            try
            {
                System.IO.Directory.Delete(Directory, recursive: true);
            }
            catch (IOException)
            {
                // 清理失败不影响断言结论。
            }

            return ValueTask.CompletedTask;
        }
    }

    /// <summary>GC 用例用的最小数据根：只需要路径 + 一张真实 run 表。</summary>
    private sealed class TempDataRoot : IDisposable
    {
        private readonly DbContextOptions<PlatformDbContext> _dbOptions;

        public TempDataRoot()
        {
            Root = Path.Combine(
                Path.GetTempPath(), "pudding-agent-pool-gc-gate-" + Guid.NewGuid().ToString("N"));
            Paths = PuddingDataPaths.FromRoot(Root);
            Directory.CreateDirectory(Paths.DatabasesRoot);
            _dbOptions = new DbContextOptionsBuilder<PlatformDbContext>()
                .UseSqlite($"Data Source={Path.Combine(Paths.DatabasesRoot, "platform.db")}")
                .Options;
            using var db = new PlatformDbContext(_dbOptions);
            db.Database.EnsureCreated();
        }

        public string Root { get; }
        public PuddingDataPaths Paths { get; }

        public async Task AddRunAsync(
            string subSessionId,
            string status,
            DateTimeOffset startedAt,
            DateTimeOffset? completedAt)
        {
            await using var db = new PlatformDbContext(_dbOptions);
            db.SubAgentRuns.Add(new SubAgentRunEntity
            {
                RunId = "run-" + Guid.NewGuid().ToString("N"),
                ParentSessionId = "parent-session",
                SubSessionId = subSessionId,
                WorkspaceId = "default",
                AgentInstanceId = "persistent-agent",
                TemplateId = "researcher",
                Status = status,
                StartedAt = startedAt.ToString("O"),
                CompletedAt = completedAt?.ToString("O"),
                ArchivePath = Path.Combine(Root, "runs", Guid.NewGuid().ToString("N")),
            });
            await db.SaveChangesAsync();
        }

        public SubAgentTransientDirectoryGcService CreateService(ISubAgentPool pool) => new(
            Paths,
            new TestDbContextFactory(_dbOptions),
            pool,
            new FixedRuntimeExecutionConfigService(),
            NullLogger<SubAgentTransientDirectoryGcService>.Instance);

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(Root))
                Directory.Delete(Root, recursive: true);
        }
    }

    private sealed class TestDbContextFactory(DbContextOptions<PlatformDbContext> options)
        : IDbContextFactory<PlatformDbContext>
    {
        public PlatformDbContext CreateDbContext() => new(options);

        public Task<PlatformDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(CreateDbContext());
    }

    private sealed class FixedRuntimeExecutionConfigService : IRuntimeExecutionConfigService
    {
        public RuntimeExecutionOptions GetOptions() => new()
        {
            SubAgents = new SubAgentExecutionOptions
            {
                TransientDirectoryRetention = new SubAgentTransientDirectoryRetentionOptions
                {
                    Enabled = true,
                    ScanIntervalMinutes = 360,
                    ScaffoldRetentionHours = 24,
                    OrphanRetentionHours = 168,
                    QuarantineRetentionDays = 7,
                    MaxItemsPerSweep = 200,
                },
            },
        };
    }
}
