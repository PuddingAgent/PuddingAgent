using System.Reflection;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using PuddingCode.Abstractions;
using PuddingCode.Configuration;
using PuddingCode.Events;
using PuddingCode.Observability;
using PuddingCode.Platform;
using PuddingCode.Serialization;
using PuddingCode.Services;
using PuddingCode.SubAgents;
using PuddingPlatform.Data;
using PuddingPlatform.Data.Entities;
using PuddingPlatform.Services;
using PuddingPlatform.Services.Diagnostics;

namespace PuddingPlatformTests.Services;

/// <summary>
/// P0-b1：<c>paused</c> 作为「终态 + 可续跑」的运行状态落地。
/// <para>
/// 锁住四组事，每一组都对应一条曾经会静默失效的路径（各用例文档注释里标注了它对应任务书的第几条判据 1–11）：
/// ① 词表 —— 终态与可续跑的单一真源（<see cref="SubAgentRunEntity"/>）；
/// ② 终态 → 事件映射 —— 含未知状态 fail-fast 的**反向对照**（防止后来人加兜底）；
/// ③ 重启恢复 —— 不得把 paused 翻成 interrupted / failed（本任务要防的缺陷）；
/// ④ 活动状态投影 —— 「暂停等裁决」不得显示成「失败」。
/// </para>
/// <para>
/// 设计依据：<c>Docs/07_architecture/子代理生命周期护栏-2026-11-21.md</c> §五（动作阶梯与
/// <c>paused</c> vs <c>sleeping</c> 的区分）、§六（需要新增的部件）。
/// </para>
/// </summary>
[TestClass]
public sealed class SubAgentPausedStatusTests
{
    // ───────────────────────── ① 词表单一真源 ─────────────────────────

    /// <summary>
    /// 判据 1：<c>paused</c> 在终态词表中，且终态词表不得混入任何活动态。
    /// 后半句是这条用例的重点：把 running 误当终态会让池恢复去重置一条正在被驱动的条目。
    /// </summary>
    [TestMethod]
    public void TerminalStatuses_Contains_Paused_And_Excludes_Running()
    {
        Assert.IsTrue(
            SubAgentRunEntity.TerminalStatuses.Contains("paused", StringComparer.Ordinal),
            "paused 必须是终态：检测器叫停后已无轮次在跑，池恢复不能再把它当成「还在跑」。");

        Assert.IsFalse(
            SubAgentRunEntity.TerminalStatuses.Contains(SubAgentRunEntity.RunningStatus, StringComparer.Ordinal),
            "running 是活动态，绝不能进终态词表。");
        Assert.IsFalse(
            SubAgentRunEntity.TerminalStatuses.Contains("queued", StringComparer.Ordinal),
            "queued 是活动态，绝不能进终态词表。");

        Assert.HasCount(1, SubAgentRunEntity.NonTerminalStatuses);
    }

    /// <summary>
    /// 判据 3（不变式）：可续跑 ⊆ 终态 —— 可续跑的前提是执行已经停止。
    /// <para>
    /// 这是「新增一个可续态却漏改某处」这类缺陷的通用捕手：只要某个值进了
    /// <c>ResumableStatuses</c> 却没进 <c>TerminalStatuses</c>，恢复扫描就会把它当成
    /// 还在跑，两个执行于是同时驱动同一个子会话。
    /// </para>
    /// </summary>
    [TestMethod]
    public void ResumableStatuses_Is_Subset_Of_TerminalStatuses()
    {
        Assert.IsGreaterThan(
            0,
            SubAgentRunEntity.ResumableStatuses.Count,
            "可续跑集合不应为空，否则本不变量是空循环、等于没测。");
        foreach (var resumable in SubAgentRunEntity.ResumableStatuses)
        {
            Assert.IsTrue(
                SubAgentRunEntity.IsTerminalStatus(resumable),
                $"{resumable} 被列为可续跑，却不在终态词表内 —— 两者必须同源。");
        }
    }

    /// <summary>判据 2：终态判定对 paused / completed 为真、对活动态与 null 为假（走公开路径，不靠反射）。</summary>
    [TestMethod]
    public void IsTerminalStatus_Recognises_Paused_And_Rejects_Running()
    {
        Assert.IsTrue(SubAgentRunEntity.IsTerminalStatus("paused"));
        Assert.IsTrue(
            SubAgentRunEntity.IsTerminalStatus("completed"),
            "completed 是最常见的终态；它若判假，说明词表被改坏了。");
        Assert.IsFalse(SubAgentRunEntity.IsTerminalStatus(SubAgentRunEntity.RunningStatus));
        Assert.IsFalse(SubAgentRunEntity.IsTerminalStatus(null), "null 不是终态（fail-closed 方向）。");
        Assert.IsFalse(
            SubAgentRunEntity.IsTerminalStatus("Paused"),
            "词表是代码写入的精确 token，不做大小写宽容：宽容会让拼错的值静默通过判定。");
    }

    /// <summary>判据 4：可续跑只认 paused / budget_exhausted，其余终态是反向对照。</summary>
    [DataTestMethod]
    [DataRow("paused", true)]
    [DataRow("budget_exhausted", true)]
    [DataRow("completed", false)]
    [DataRow("failed", false)]
    [DataRow("cancelled", false)]
    [DataRow("timed_out", false)]
    public void IsResumableStatus_Matches_Expected_Set(string status, bool expected)
    {
        Assert.AreEqual(expected, SubAgentRunEntity.IsResumableStatus(status), $"status={status}");
    }

    // ─────────────────── ② 终态 → 事件类型（含反向对照） ───────────────────

    /// <summary>
    /// 判据 8：paused 映射到 <c>subagent.run.paused</c>（映射表是私有的，因此走反射）。
    /// </summary>
    [TestMethod]
    public void ToTerminalEventType_Maps_Paused_To_Paused_Event()
    {
        var method = ResolveToTerminalEventTypeMethod();

        Assert.AreEqual(
            ConversationEventTypes.SubAgentRunPaused,
            method.Invoke(null, new object?[] { "paused" }),
            "paused 必须映射到专用事件；映射成 failed 会让父代理收不到「可裁决」信号。");
    }

    /// <summary>
    /// 判据 7（反向对照）：未知状态必须抛 <see cref="ArgumentOutOfRangeException"/>。
    /// <para>
    /// 这条用例的唯一作用是让「顺手加个兜底」的改动立刻变红：默认分支一旦返回某个
    /// 默认事件类型，上游造出的新词就会被写成一条看起来正常的终态事件，事后无法追查
    /// 是哪一步走偏。
    /// </para>
    /// </summary>
    [TestMethod]
    public void ToTerminalEventType_Throws_On_Unknown_Status()
    {
        var method = ResolveToTerminalEventTypeMethod();

        var ex = Assert.ThrowsExactly<TargetInvocationException>(
            () => method.Invoke(null, new object?[] { "bogus" }));
        Assert.IsInstanceOfType<ArgumentOutOfRangeException>(
            ex.InnerException,
            "未知状态必须 fail-fast（ArgumentOutOfRangeException），不得兜底成默认事件类型。");
    }

    private static MethodInfo ResolveToTerminalEventTypeMethod()
    {
        var method = typeof(FileSubAgentRunStore).GetMethod(
            "ToTerminalEventType",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.IsNotNull(method, "ToTerminalEventType 必须存在（终态→事件映射的唯一实现）。");
        return method!;
    }

    /// <summary>
    /// 判据 3（公开路径）：以 paused 收尾的 run 必须落一条 <c>subagent.run.paused</c> 终态事件，
    /// 而不是被当成 failed 发出去 —— 否则父代理永远收不到「可裁决」的信号。
    /// </summary>
    [TestMethod]
    public async Task CompleteRunAsync_Paused_WritesPausedTerminalEvent()
    {
        using var harness = await RunStoreHarness.CreateAsync();
        var handle = await harness.CreateRunAsync("paused before restart");

        var applied = await harness.Store.CompleteRunAsync(handle.RunId, new SubAgentRunCompletion
        {
            Status = "paused",
            ErrorMessage = "paused by run anomaly detector (S1 repeated assistant output)",
        });
        Assert.AreEqual(SubAgentRunTerminalWriteResult.Applied, applied);

        var archive = await harness.Store.GetRunArchiveAsync(handle.RunId);
        Assert.IsNotNull(archive);
        Assert.AreEqual("paused", archive.Manifest.Status);

        var terminalEvents = harness.ConversationEvents.Appended
            .Where(item => TerminalEventTypes.Contains(item.Event.Type))
            .ToList();
        Assert.HasCount(1, terminalEvents);
        Assert.AreEqual(ConversationEventTypes.SubAgentRunPaused, terminalEvents[0].Event.Type);
    }

    // ───────────────────────── ③ 重启恢复安全 ─────────────────────────

    /// <summary>
    /// 判据 10（本任务最容易漏的一条）：run.json 已经是 paused、StartedAt 早于恢复截止时间时，
    /// 重启恢复**必须跳过**它，并且不能把 status 改写成 interrupted。
    /// <para>
    /// 为什么专门测：一旦 paused 进入终态词表，恢复判据自动跳过它 —— 这是正确的；
    /// 但如果有人以后把 paused 从词表里挪走（例如误以为它是活动态），这里会立刻变红。
    /// </para>
    /// </summary>
    [TestMethod]
    public async Task RecoverInterruptedRunsAsync_Skips_Paused_Run()
    {
        using var harness = await RunStoreHarness.CreateAsync();
        var handle = await harness.CreateRunAsync("paused by detector before restart");

        // 模拟「检测器已把 run 置为 paused，随后进程退出」：直接改 run.json，不经过 CompleteRunAsync，
        // 这样本用例盯的纯粹是恢复路径的判据，而不是完成路径的校验。
        await harness.RewriteRunStatusAsync(handle.RunId, "paused");

        var recovered = await harness.Store.RecoverInterruptedRunsAsync(
            DateTimeOffset.UtcNow.AddSeconds(1),
            maxRuns: 100);

        Assert.AreEqual(
            0,
            recovered,
            "paused 是终态：重启恢复不得把它当成上一个进程遗留的运行去改成 interrupted。");

        var archive = await harness.Store.GetRunArchiveAsync(handle.RunId);
        Assert.IsNotNull(archive);
        Assert.AreEqual("paused", archive.Manifest.Status, "重启不得把 paused 翻成 interrupted。");
        Assert.IsFalse(
            harness.ConversationEvents.Appended.Any(item =>
                item.Event.Type == ConversationEventTypes.SubAgentRunInterrupted),
            "paused 的 run 不应产生 interrupted 终态事件。");
    }

    /// <summary>
    /// 判据 11：<c>SessionStateManager.RecoverInterruptedSubAgentsAsync</c> 只取 <c>status=="running"</c>，
    /// 天然不碰 paused —— 这条用例把「天然安全」变成断言，避免以后有人把判据放宽成「非终态即恢复」。
    /// </summary>
    [TestMethod]
    public async Task RecoverInterruptedSubAgentsAsync_Leaves_Paused_Row_Untouched()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"test_{Guid.NewGuid():N}.db");
        try
        {
            var sessionState = CreateSessionStateManager(dbPath);
            var cutoff = DateTimeOffset.UtcNow;
            var scopeFactory = CreateScopeFactory(dbPath);

            using (var seedScope = scopeFactory.CreateScope())
            {
                var seedDb = seedScope.ServiceProvider.GetRequiredService<PlatformDbContext>();
                seedDb.SessionSubAgents.AddRange(
                    new SessionSubAgentEntity
                    {
                        ParentSessionId = "parent", SubSessionId = "stale-running", Status = "running",
                        TaskSummary = "probe", SpawnedAt = cutoff.AddDays(-30).ToString("O"),
                    },
                    new SessionSubAgentEntity
                    {
                        ParentSessionId = "parent", SubSessionId = "stale-paused", Status = "paused",
                        TaskSummary = "probe", SpawnedAt = cutoff.AddDays(-30).ToString("O"),
                    });
                await seedDb.SaveChangesAsync();
            }

            var recovered = await sessionState.RecoverInterruptedSubAgentsAsync(cutoff, maxRows: 100);

            Assert.AreEqual(1, recovered, "只应修正 running 那一条；paused 不是「上一个进程遗留的运行」。");

            using var verifyScope = scopeFactory.CreateScope();
            var db = verifyScope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            var paused = await db.SessionSubAgents.SingleAsync(e => e.SubSessionId == "stale-paused");
            var running = await db.SessionSubAgents.SingleAsync(e => e.SubSessionId == "stale-running");

            Assert.AreEqual("paused", paused.Status, "暂停态投影不得被重启恢复改成 failed。");
            Assert.IsNull(paused.CompletedAt, "恢复路径不应给 paused 行补完成时间。");
            Assert.AreEqual("failed", running.Status, "对照组：真正的遗留 running 仍应被恢复为 failed。");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(dbPath)) File.Delete(dbPath);
        }
    }

    /// <summary>
    /// 补充判据（同一条缺陷的另一条路径）：终态写入本身必须保留 paused，且第二次终态写入被忽略。
    /// <para>
    /// 这里曾经会静默出错两次：终态白名单缺 paused ⇒ paused 被改写成 failed（界面显示「失败」）；
    /// 重复终态守卫缺 paused ⇒ 迟到的 completed 覆盖掉暂停标记（resume 入口消失）。
    /// </para>
    /// </summary>
    [TestMethod]
    public async Task TrackSubAgentCompleteAsync_PausedStaysPaused_AndIgnoresSecondTerminalWrite()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"test_{Guid.NewGuid():N}.db");
        try
        {
            var sessionState = CreateSessionStateManager(dbPath);
            var scopeFactory = CreateScopeFactory(dbPath);
            var now = DateTimeOffset.UtcNow;

            using (var seedScope = scopeFactory.CreateScope())
            {
                var seedDb = seedScope.ServiceProvider.GetRequiredService<PlatformDbContext>();
                seedDb.SessionSubAgents.Add(new SessionSubAgentEntity
                {
                    ParentSessionId = "parent", SubSessionId = "sub-paused", Status = "running",
                    TaskSummary = "probe", SpawnedAt = now.AddMinutes(-5).ToString("O"),
                });
                await seedDb.SaveChangesAsync();
            }

            await sessionState.TrackSubAgentCompleteAsync("sub-paused", new SubAgentResult
            {
                Success = false,
                Status = "paused",
                Error = "paused by run anomaly detector",
                CompletedAt = now,
            });

            using (var verifyScope = scopeFactory.CreateScope())
            {
                var db = verifyScope.ServiceProvider.GetRequiredService<PlatformDbContext>();
                var row = await db.SessionSubAgents.SingleAsync(e => e.SubSessionId == "sub-paused");
                Assert.AreEqual(
                    "paused",
                    row.Status,
                    "paused 是可续态终态：写成 failed 会让「等裁决」看起来像执行失败。");
            }

            // 迟到的第二个终态（例如 Runtime 兜底提交 completed）不得覆盖暂停标记。
            await sessionState.TrackSubAgentCompleteAsync("sub-paused", new SubAgentResult
            {
                Success = true,
                Status = "completed",
                Reply = "late terminal write",
                CompletedAt = now.AddMinutes(1),
            });

            using (var verifyScope = scopeFactory.CreateScope())
            {
                var db = verifyScope.ServiceProvider.GetRequiredService<PlatformDbContext>();
                var row = await db.SessionSubAgents.SingleAsync(e => e.SubSessionId == "sub-paused");
                Assert.AreEqual("paused", row.Status, "paused 已在终态词表内，重复终态写入必须被忽略。");
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(dbPath)) File.Delete(dbPath);
        }
    }

    // ─────────────────── ④ 活动状态投影与通知文案 ───────────────────

    /// <summary>
    /// 判据 5：paused 与 budget_exhausted 同为 Deferred，而不是 Failed。
    /// 投影成 Failed 会让界面与告警把「暂停等裁决」当成执行失败，父代理也会误判为不该 resume。
    /// </summary>
    [DataTestMethod]
    [DataRow("paused", RuntimeActivityStatuses.Deferred)]
    [DataRow("budget_exhausted", RuntimeActivityStatuses.Deferred)]
    [DataRow("completed", RuntimeActivityStatuses.Succeeded)]
    [DataRow("failed", RuntimeActivityStatuses.Failed)]
    [DataRow("cancelled", RuntimeActivityStatuses.Failed)]
    public void ToActivityStatus_Maps_Paused_To_Deferred_Not_Failed(string terminalStatus, string expected)
    {
        Assert.AreEqual(expected, SubAgentManager.ToActivityStatus(terminalStatus), $"status={terminalStatus}");
    }

    /// <summary>
    /// 父代理通知文案必须能区分两种原因：预算耗尽要「加预算后 resume」，异常暂停要「裁决三选一」。
    /// 合并成一句话，裁决者就不知道自己该做什么。
    /// </summary>
    [TestMethod]
    public void ToActivitySummary_DistinguishesPausedFromBudgetExhausted()
    {
        var paused = SubAgentManager.ToActivitySummary("paused", "sub-1", "boom", sync: false);
        var budget = SubAgentManager.ToActivitySummary("budget_exhausted", "sub-1", "boom", sync: false);

        Assert.IsNotNull(paused);
        Assert.IsNotNull(budget);
        StringAssert.Contains(paused!, "anomaly detector");
        StringAssert.Contains(budget!, "budget checkpoint");
        Assert.AreNotEqual(budget, paused);

        // 裁决指令必须可操作：文案里要出现 resume / sleep / destroy 之一（小写包含判断）。
        var pausedLower = paused!.ToLowerInvariant();
        Assert.IsTrue(
            pausedLower.Contains("resume", StringComparison.Ordinal)
            || pausedLower.Contains("sleep", StringComparison.Ordinal)
            || pausedLower.Contains("destroy", StringComparison.Ordinal),
            $"paused 的摘要必须告诉裁决者下一步能做什么，实际为：{paused}");

        // 配对不变式：投影成 Deferred 的终态，其摘要必须是「等裁决」，不得回落到 failureMessage。
        // 两个断言放在一起，是为了让「从可续跑词表里漏掉一个」同时打红本判据（而不是只红一处）。
        foreach (var resumable in new[] { "paused", "budget_exhausted" })
        {
            Assert.AreEqual(
                RuntimeActivityStatuses.Deferred,
                SubAgentManager.ToActivityStatus(resumable),
                $"{resumable} 必须先被投影为 Deferred，下面的摘要断言才有意义。");
            Assert.AreNotEqual(
                "boom",
                SubAgentManager.ToActivitySummary(resumable, "sub-1", "boom", sync: false),
                $"{resumable} 的摘要不得回落到 failureMessage。");
        }

        var syncPaused = SubAgentManager.ToActivitySummary("paused", "sub-1", null, sync: true);
        StringAssert.StartsWith(syncPaused!, "Sync sub-agent");
        StringAssert.Contains(syncPaused!, "anomaly detector");
    }

    /// <summary>
    /// 判据 9：事件 schema 注册（设计文档 §六 要求）。
    /// 2026-11-21 实测：未注册时 <c>GetSchemaVersion</c> 返回默认 1、不抛异常，但 <c>IsValidEventType</c> 为 false；
    /// 本条同时锁住「注册存在」与「版本仍是 1」——注册是纯附加，不构成兼容性变更。
    /// </summary>
    [TestMethod]
    public void EventSchemaRegistry_PausedEventIsRegistered()
    {
        Assert.IsTrue(EventSchemaRegistry.IsValidEventType(ConversationEventTypes.SubAgentRunPaused));
        Assert.AreEqual(1, EventSchemaRegistry.GetSchemaVersion(ConversationEventTypes.SubAgentRunPaused));
    }

    // ─────────────────────────────── 测试基建 ───────────────────────────────

    private static readonly HashSet<string> TerminalEventTypes = new(StringComparer.Ordinal)
    {
        ConversationEventTypes.SubAgentRunCompleted,
        ConversationEventTypes.SubAgentRunFailed,
        ConversationEventTypes.SubAgentRunCancelled,
        ConversationEventTypes.SubAgentRunTimedOut,
        ConversationEventTypes.SubAgentRunBudgetExhausted,
        ConversationEventTypes.SubAgentRunInterrupted,
        ConversationEventTypes.SubAgentRunPaused,
    };

    private static IServiceScopeFactory CreateScopeFactory(string dbPath)
    {
        var options = new DbContextOptionsBuilder<PlatformDbContext>()
            .UseSqlite($"Data Source={dbPath}")
            .Options;

        var services = new ServiceCollection();
        services.AddScoped(_ => new PlatformDbContext(options));
        return services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
    }

    private static SessionStateManager CreateSessionStateManager(string dbPath)
    {
        var scopeFactory = CreateScopeFactory(dbPath);
        using (var scope = scopeFactory.CreateScope())
        {
            scope.ServiceProvider.GetRequiredService<PlatformDbContext>().Database.EnsureCreated();
        }

        var dataRoot = Path.Combine(Path.GetTempPath(), $"paused_run_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dataRoot);
        var dataPaths = PuddingDataPaths.FromRoot(dataRoot);

        return new SessionStateManager(
            scopeFactory,
            NullLogger<SessionStateManager>.Instance,
            NullRuntimeActivitySink.Instance,
            new NoOpTraceAccessor(),
            new JsonlSessionWriter(dataRoot),
            new SessionStateStore(dataPaths, NullLogger<SessionStateStore>.Instance),
            new ConversationDiagnosticEventProjector(),
            rawLogMirror: null);
    }

    /// <summary>FileSubAgentRunStore 的最小可运行宿主（文件存储 + SQLite 索引 + 事件记录器）。</summary>
    private sealed class RunStoreHarness : IDisposable
    {
        private TemporaryDirectory _temp = null!;
        private DbContextOptions<PlatformDbContext> _options = null!;
        private PuddingDataPaths _paths = null!;

        public FileSubAgentRunStore Store { get; private set; } = null!;

        public RecordingConversationEventStore ConversationEvents { get; } = new();

        public static async Task<RunStoreHarness> CreateAsync()
        {
            var harness = new RunStoreHarness { _temp = TemporaryDirectory.Create() };
            harness._options = new DbContextOptionsBuilder<PlatformDbContext>()
                .UseSqlite($"Data Source={Path.Combine(harness._temp.Path, "platform.db")}")
                .Options;
            await using (var db = new PlatformDbContext(harness._options))
            {
                await db.Database.EnsureCreatedAsync();
            }

            harness._paths = PuddingDataPaths.FromRoot(harness._temp.Path);
            harness.Store = new FileSubAgentRunStore(
                harness._paths,
                NullLogger<FileSubAgentRunStore>.Instance,
                new TestDbContextFactory(harness._options),
                harness.ConversationEvents);
            return harness;
        }

        public Task<SubAgentRunHandle> CreateRunAsync(string task)
            => Store.CreateRunAsync(new SubAgentRunCreateRequest
            {
                ParentSessionId = "parent-session",
                SubSessionId = "sub-session-paused",
                WorkspaceId = "default",
                AgentInstanceId = "agent-1",
                TemplateId = "workspace-task-agent",
                Task = task,
            });

        /// <summary>直接把 run.json 的 status 改成指定值，模拟检测器落盘后的进程退出。</summary>
        public async Task RewriteRunStatusAsync(string runId, string status)
        {
            var runJsonPath = Directory
                .EnumerateFiles(_paths.WorkspacesRoot, "run.json", SearchOption.AllDirectories)
                .Single(path =>
                    string.Equals(Path.GetFileName(Path.GetDirectoryName(path)), runId, StringComparison.Ordinal));

            var json = await File.ReadAllTextAsync(runJsonPath);
            var manifest = JsonSerializer.Deserialize<SubAgentRunManifest>(
                json,
                PuddingJsonContracts.PrettyJson);
            Assert.IsNotNull(manifest);

            await File.WriteAllTextAsync(
                runJsonPath,
                JsonSerializer.Serialize(manifest with { Status = status }, PuddingJsonContracts.PrettyJson));
        }

        public void Dispose() => _temp.Dispose();
    }

    private sealed class TestDbContextFactory(DbContextOptions<PlatformDbContext> options)
        : IDbContextFactory<PlatformDbContext>
    {
        public PlatformDbContext CreateDbContext() => new(options);

        public Task<PlatformDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }

    private sealed class NullRuntimeActivitySink : IRuntimeActivitySink
    {
        public static readonly NullRuntimeActivitySink Instance = new();

        public Task RecordAsync(RuntimeActivity activity, CancellationToken ct = default) => Task.CompletedTask;

        public Task<IReadOnlyList<RuntimeActivity>> QueryAsync(RuntimeActivityQuery query, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<RuntimeActivity>>(Array.Empty<RuntimeActivity>());
    }

    private sealed class NoOpTraceAccessor : IRuntimeTraceAccessor
    {
        public RuntimeTraceContext? Current { get; set; }
    }

    private sealed class RecordingConversationEventStore : IConversationEventStore
    {
        public List<(string ConversationId, NewConversationEvent Event)> Appended { get; } = [];

        public Task<AppendResult> AppendAsync(
            string conversationId,
            long expectedVersion,
            IReadOnlyList<NewConversationEvent> events,
            EventWriteCondition condition,
            CancellationToken ct)
        {
            foreach (var item in events)
            {
                if (Appended.All(existing => existing.Event.EventId != item.EventId))
                    Appended.Add((conversationId, item));
            }

            var last = Appended.Count;
            return Task.FromResult(new AppendResult(last, last, events.Count));
        }

        public Task<EventPage> ReadForwardAsync(
            string conversationId,
            long afterExclusive,
            long? throughInclusive,
            int limit,
            CancellationToken ct) =>
            Task.FromResult(new EventPage([], null, false));

        public Task<EventPage> ReadBackwardAsync(
            string conversationId,
            long beforeExclusive,
            int limit,
            CancellationToken ct) =>
            Task.FromResult(new EventPage([], null, false));

        public Task<EventPage> ReadByTypePrefixBackwardAsync(
            string conversationId,
            string typePrefix,
            long beforeExclusive,
            int limit,
            CancellationToken ct) =>
            Task.FromResult(new EventPage([], null, false));

        public Task<EventBounds> GetBoundsAsync(
            string conversationId,
            CancellationToken ct) =>
            Task.FromResult(new EventBounds(null, null));

        public Task EnsureTablesAsync(CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        private TemporaryDirectory(string path) => Path = path;

        public string Path { get; }

        public static TemporaryDirectory Create()
        {
            var path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "pudding-paused-status-tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return new TemporaryDirectory(path);
        }

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();

            if (Directory.Exists(Path))
            {
                try
                {
                    Directory.Delete(Path, recursive: true);
                }
                catch (IOException)
                {
                    // 临时目录清理失败不影响断言结论。
                }
            }
        }
    }
}
