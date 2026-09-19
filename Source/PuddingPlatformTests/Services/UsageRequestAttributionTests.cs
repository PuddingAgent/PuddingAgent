using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using PuddingCode.Abstractions;
using PuddingCode.Models;
using PuddingCode.Platform;
using PuddingPlatform.Data;
using PuddingPlatform.Services;

namespace PuddingPlatformTests.Services;

/// <summary>
/// S01-B：请求级用量归因与迟到落账恢复。
/// 覆盖验收条目：A 延迟落 attribution 且 B 已准备请求时 A 仍保留 A 的 shape；
/// 次日补录昨日 usage 后闭日聚合失效并可重算。
/// </summary>
[TestClass]
public sealed class UsageRequestAttributionTests
{
    [TestMethod]
    public async Task LateAttribution_KeepsRequestAShape_AfterSessionSnapshotOverwrittenByB()
    {
        var assemblyStore = new ContextAssemblyStore();
        var usageStore = new ContextUsageSnapshotStore();
        await using var harness = await Harness.CreateAsync(assemblyStore, usageStore);

        // A 请求准备边界：L0-STATIC=100、工具定义层=10。
        assemblyStore.Set(new ContextAssemblySnapshot
        {
            SessionId = "session-a",
            Layers =
            [
                new ContextLayerInfo { LayerName = "L0-STATIC", TokenCount = 100, ContentPreview = "shape-A" },
            ],
        });
        usageStore.Set(new ContextUsageSnapshot
        {
            SessionId = "session-a",
            MessageTokens = 100,
            ToolDefinitionTokens = 10,
            ToolCount = 1,
            ToolDefinitionHash = "hash-A",
        });
        var frozenA = RequestContextAttribution.Capture(assemblyStore, usageStore, "session-a");
        Assert.IsNotNull(frozenA, "请求准备边界必须能冻结 attribution");
        Assert.AreEqual(RequestContextAttributionSources.RequestPrepareFrozen, frozenA!.Source);

        // B 请求已经准备并覆盖了同一 session 的最新调试快照。
        assemblyStore.Set(new ContextAssemblySnapshot
        {
            SessionId = "session-a",
            Layers =
            [
                new ContextLayerInfo { LayerName = "L0-STATIC", TokenCount = 900, ContentPreview = "shape-B" },
            ],
        });
        usageStore.Set(new ContextUsageSnapshot
        {
            SessionId = "session-a",
            MessageTokens = 900,
            ToolDefinitionTokens = 90,
            ToolCount = 9,
            ToolDefinitionHash = "hash-B",
        });

        // A 的 usage 迟到落账，携带 A 自己的冻结归因。
        await harness.Recorder.RecordAttributedRequiredAsync(
            Usage(100, 20),
            sourceType: "agent_llm",
            sourceId: "session-a:run-a:1",
            workspaceId: "w1",
            sessionId: "session-a",
            providerId: "deepseek",
            modelId: "m1",
            attribution: new TokenUsageAttribution
            {
                InvocationId = "inv-a",
                AttemptId = "att-a",
                Context = frozenA,
            });

        await using var db = await harness.Factory.CreateDbContextAsync();
        var rows = await db.ContextLayerMetricEvents
            .Where(e => e.SourceId == "session-a:run-a:1")
            .OrderBy(e => e.LayerOrder)
            .ToListAsync();

        Assert.AreEqual(2, rows.Count, "L0-STATIC 与工具定义层都应落库");
        var staticLayer = rows.Single(r => r.LayerName == "L0-STATIC");
        Assert.AreEqual(100L, staticLayer.TokenCount,
            "迟到落账必须使用 A 的冻结 shape，而不是 B 已覆盖的 session 最新快照");
        var toolLayer = rows.Single(r => r.LayerName == "L1-TOOL-DEFINITIONS");
        Assert.AreEqual(10L, toolLayer.TokenCount, "工具定义层同样必须使用 A 的冻结值");
    }

    [TestMethod]
    public async Task LateUsage_InvalidatesClosedDayAggregate_AndNextReadRecomputes()
    {
        var assemblyStore = new ContextAssemblyStore();
        var usageStore = new ContextUsageSnapshotStore();
        await using var harness = await Harness.CreateAsync(assemblyStore, usageStore);
        var daily = new TokenUsageDailyAggregateService(
            harness.Factory,
            NullLogger<TokenUsageDailyAggregateService>.Instance);

        // 先把 2026-06-02 构造成“无数据闭日”缓存（写入完成标记，后续不再重扫）。
        var before = await daily.GetClosedDaysAsync(new DateTime(2026, 6, 1), new DateTime(2026, 6, 3));
        Assert.AreEqual(0, before.Count);

        // 次日补录归属 2026-06-02 的迟到 usage。
        await harness.Recorder.RecordAttributedRequiredAsync(
            Usage(1000, 100),
            sourceType: "agent_llm",
            sourceId: "session-late:run-late:1",
            workspaceId: "w1",
            sessionId: "session-late",
            providerId: "deepseek",
            modelId: "m1",
            attribution: new TokenUsageAttribution { InvocationId = "inv-late", AttemptId = "att-late" },
            prefixSnapshot: null,
            occurredAtUtc: new DateTimeOffset(2026, 6, 2, 5, 0, 0, TimeSpan.Zero));

        var after = await daily.GetClosedDaysAsync(new DateTime(2026, 6, 1), new DateTime(2026, 6, 3));
        var row = after.Single(r => r.DayUtc == "2026-06-02");
        Assert.AreEqual(1L, row.RequestCount, "迟到补录后闭日聚合必须失效并按账本重算");
        Assert.AreEqual(1000L, row.PromptTokens);
    }

    [TestMethod]
    public async Task SameSourceResubmission_DoesNotDoubleCount_AndDifferentPayloadIsConflict()
    {
        var assemblyStore = new ContextAssemblyStore();
        var usageStore = new ContextUsageSnapshotStore();
        await using var harness = await Harness.CreateAsync(assemblyStore, usageStore);

        // Provider 已成功、本地提交失败后只允许重投同一用量事实，不得再计一次。
        await harness.Recorder.RecordAttributedRequiredAsync(
            Usage(500, 50), "agent_llm", "src-idem", "w1", "s1", "deepseek", "m1",
            new TokenUsageAttribution { InvocationId = "inv-1", AttemptId = "att-1" });
        await harness.Recorder.RecordAttributedRequiredAsync(
            Usage(500, 50), "agent_llm", "src-idem", "w1", "s1", "deepseek", "m1",
            new TokenUsageAttribution { InvocationId = "inv-1", AttemptId = "att-1" });

        await using (var db = await harness.Factory.CreateDbContextAsync())
        {
            Assert.AreEqual(1, await db.TokenUsageEvents.CountAsync(e => e.SourceId == "src-idem"));
        }

        // 同一 source 身份、不同 usage payload：不得静默覆盖或双计。
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () =>
            await harness.Recorder.RecordAttributedRequiredAsync(
                Usage(777, 77), "agent_llm", "src-idem", "w1", "s1", "deepseek", "m1",
                new TokenUsageAttribution { InvocationId = "inv-1", AttemptId = "att-2" }));
    }

    [TestMethod]
    public async Task LayerBreakdown_IsPersistedWithUsage_AndReadBackForFallback()
    {
        var assemblyStore = new ContextAssemblyStore();
        var usageStore = new ContextUsageSnapshotStore();
        await using var harness = await Harness.CreateAsync(assemblyStore, usageStore);

        // 请求组装点算出的分层六桶（只存在于内存态快照，重启即失）。
        usageStore.Set(new ContextUsageSnapshot
        {
            SessionId = "session-layers",
            MessageTokens = 1_000,
            ToolDefinitionTokens = 200,
            SystemMessageTokens = 300,
            HistoryMessageTokens = 700,
            SystemPromptTokens = 300,
            CompactionSummaryTokens = 400,
            ConversationTokens = 150,
            ToolResultTokens = 120,
            ReasoningTokens = 30,
        });

        await harness.Recorder.RecordAttributedRequiredAsync(
            Usage(5_000, 500),
            sourceType: "agent_llm",
            sourceId: "session-layers:run-1:1",
            workspaceId: "w1",
            sessionId: "session-layers",
            providerId: "deepseek",
            modelId: "m1",
            attribution: new TokenUsageAttribution { InvocationId = "inv-layers", AttemptId = "att-1" });

        // 账本行必须携带分层六桶；否则进程重启后 DB 回退源拿不到分层，
        // 上下文面板会退化成单色条（用户 2026-09-19 反馈）。
        await using (var db = await harness.Factory.CreateDbContextAsync())
        {
            var row = await db.TokenUsageEvents.SingleAsync(e => e.SourceId == "session-layers:run-1:1");
            Assert.AreEqual(300, row.SystemPromptTokens);
            Assert.AreEqual(400, row.CompactionSummaryTokens);
            Assert.AreEqual(150, row.ConversationTokens);
            Assert.AreEqual(120, row.ToolResultTokens);
            Assert.AreEqual(30, row.ReasoningTokens);
        }

        // 读取契约：回退源（ContextCompactionService）据此回填六桶。
        var repository = new TokenUsageEventRepository(harness.Factory);
        var diagnostics = await repository.GetLatestLayerDiagnosticsAsync("session-layers");
        Assert.IsNotNull(diagnostics);
        Assert.AreEqual(300, diagnostics!.SystemPromptTokens);
        Assert.AreEqual(400, diagnostics.CompactionSummaryTokens);
        Assert.AreEqual(150, diagnostics.ConversationTokens);
        Assert.AreEqual(120, diagnostics.ToolResultTokens);
        Assert.AreEqual(30, diagnostics.ReasoningTokens);
    }

    [TestMethod]
    public async Task TokenUsageRepository_AllReadPaths_AreTranslatableOnSqlite()
    {
        // 回归守衡：四个读取路径都曾对 DateTimeOffset 列做 ORDER BY，SQLite 在翻译期
        // 抛 NotSupportedException，而调用方的 catch(Exception) 只写 Debug 日志，
        // 于是 DB 回退源（provider_usage_db / 分层诊断 / 熵诊断）静默失效。
        var assemblyStore = new ContextAssemblyStore();
        var usageStore = new ContextUsageSnapshotStore();
        await using var harness = await Harness.CreateAsync(assemblyStore, usageStore);
        usageStore.Set(new ContextUsageSnapshot
        {
            SessionId = "session-repo",
            MessageTokens = 10,
            ToolDefinitionTokens = 2,
            SystemMessageTokens = 5,
            HistoryMessageTokens = 5,
            SystemPromptTokens = 5,
            CompactionSummaryTokens = 1,
            ConversationTokens = 2,
            ToolResultTokens = 1,
            ReasoningTokens = 1,
        });
        var occurredAt = new DateTimeOffset(2026, 6, 2, 5, 0, 0, TimeSpan.Zero);
        await harness.Recorder.RecordAttributedRequiredAsync(
            Usage(1_000, 100),
            sourceType: "agent_llm",
            sourceId: "session-repo:run-1:1",
            workspaceId: "w1",
            sessionId: "session-repo",
            providerId: "deepseek",
            modelId: "m1",
            attribution: new TokenUsageAttribution { InvocationId = "inv-repo", AttemptId = "att-1" },
            occurredAtUtc: occurredAt);

        var repository = new TokenUsageEventRepository(harness.Factory);

        var stats = await repository.GetLatestStatsAsync("session-repo");
        Assert.IsNotNull(stats, "GetLatestStatsAsync 必须在 SQLite 上可翻译。");
        Assert.AreEqual(1_000, stats!.PromptTokens);

        var layers = await repository.GetLatestLayerDiagnosticsAsync("session-repo");
        Assert.IsNotNull(layers, "GetLatestLayerDiagnosticsAsync 必须在 SQLite 上可翻译。");
        Assert.AreEqual(5, layers!.SystemPromptTokens);

        // 熵未记录：允许返回 null，但不得抛异常（WHERE 过滤在无命中时不得触发翻译失败）。
        var entropy = await repository.GetLatestEntropyDiagnosticsAsync("session-repo");
        Assert.IsNull(entropy);

        // 分页查询（无时间过滤）：排序已改按主键，故可用。
        // 已知限制：from/to 走 DateTimeOffset 比较，SQLite 同样不可翻译（不只是 ORDER BY），
        // 该方法当前无调用方，缺陷单独登记，不在本回归断言范围内。
        var page = await repository.GetFilteredAsync(sessionId: "session-repo");
        Assert.AreEqual(1, page.TotalCount);
        Assert.HasCount(1, page.Events);
    }

    private static TokenUsageDto Usage(int prompt, int completion) => new()
    {
        PromptTokens = prompt,
        CompletionTokens = completion,
        TotalTokens = prompt + completion,
        PromptCacheHitTokens = prompt / 2,
        PromptCacheMissTokens = prompt - (prompt / 2),
    };

    private sealed class Harness : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;
        private readonly ServiceProvider _provider;

        private Harness(
            SqliteConnection connection,
            ServiceProvider provider,
            TestDbContextFactory factory,
            TokenUsageRecorder recorder)
        {
            _connection = connection;
            _provider = provider;
            Factory = factory;
            Recorder = recorder;
        }

        public TestDbContextFactory Factory { get; }

        public TokenUsageRecorder Recorder { get; }

        public static async Task<Harness> CreateAsync(
            ContextAssemblyStore assemblyStore,
            ContextUsageSnapshotStore usageStore)
        {
            var connection = new SqliteConnection("DataSource=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<PlatformDbContext>()
                .UseSqlite(connection)
                .Options;
            var factory = new TestDbContextFactory(options);
            await using (var db = await factory.CreateDbContextAsync())
            {
                await db.Database.EnsureCreatedAsync();
            }

            var services = new ServiceCollection();
            services.AddSingleton<IDbContextFactory<PlatformDbContext>>(factory);
            services.AddScoped(_ => factory.CreateDbContext());
            services.AddScoped(sp => new TokenUsageDailyAggregateService(
                sp.GetRequiredService<IDbContextFactory<PlatformDbContext>>(),
                NullLogger<TokenUsageDailyAggregateService>.Instance));
            var provider = services.BuildServiceProvider();

            var recorder = new TokenUsageRecorder(
                provider.GetRequiredService<IServiceScopeFactory>(),
                new TokenUsageNormalizer(),
                NullLogger<TokenUsageRecorder>.Instance,
                telemetrySink: null,
                contextAssemblyStore: assemblyStore,
                contextUsageSnapshotStore: usageStore);

            return new Harness(connection, provider, factory, recorder);
        }

        public async ValueTask DisposeAsync()
        {
            await _provider.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }

    private sealed class TestDbContextFactory(DbContextOptions<PlatformDbContext> options)
        : IDbContextFactory<PlatformDbContext>
    {
        public PlatformDbContext CreateDbContext() => new(options);

        public Task<PlatformDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(CreateDbContext());
    }
}
