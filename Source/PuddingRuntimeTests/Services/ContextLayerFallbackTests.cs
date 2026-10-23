using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using PuddingCode.Abstractions;
using PuddingCode.Platform;
using PuddingCode.Runtime;
using PuddingMemoryEngine.Data;
using PuddingRuntime.Services;

namespace PuddingRuntimeTests.Services;

/// <summary>
/// 上下文用量分层回退（落账行 → 分层六桶）。
///
/// 背景：分层六桶只在请求组装点（内存态快照）算得出，进程重启即失；此前 DB 回退源
/// 只填 UsedTokens/MessageTokens/HistoryMessageTokens，于是面板在重启后退化成单色条。
/// 本组用例锁定两件事：
/// 1. 账本行带完整六桶时，回退源必须回填（面板恢复分段彩色条）；
/// 2. 旧行（本次改动前的写入，只有旧四字段）一律不得用于分层 —— 否则会渲染出
///    「对话消息 0%」而会话显然有消息的误导性占比。
/// </summary>
[TestClass]
public sealed class ContextLayerFallbackTests
{
    private const string SessionId = "session-layers";

    [TestMethod]
    public async Task GetHealthAsync_FillsLayerBuckets_FromPersistedLedgerRow()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<MemoryDbContext>()
            .UseSqlite(connection)
            .Options;
        await using (var db = new MemoryDbContext(options))
        {
            await db.Database.EnsureCreatedAsync();
        }

        var service = new ContextCompactionService(
            new TestMemoryDbContextFactory(options),
            new FixedSummaryGenerator("summary"),
            NullLogger<ContextCompactionService>.Instance,
            contentSummaryService: null,
            tokenUsageRepo: new FakeTokenUsageRepository(
                Stats(),
                new SessionTokenDiagnostics
                {
                    SessionId = SessionId,
                    OccurredAtUtc = DateTimeOffset.UtcNow,
                    MessageTokens = 1_000,
                    ToolDefinitionTokens = 200,
                    SystemMessageTokens = 300,
                    HistoryMessageTokens = 700,
                    SystemPromptTokens = 300,
                    CompactionSummaryTokens = 400,
                    ConversationTokens = 150,
                    ToolResultTokens = 120,
                    ReasoningTokens = 30,
                }),
            messageStore: null,
            sessionSummaryStore: null,
            contextUsageSnapshotStore: null);

        var health = await service.GetHealthAsync(
            SessionId,
            CancellationToken.None,
            contextWindowTokens: 1_000_000);

        Assert.AreEqual(
            "provider_usage_db_layers",
            health.UsageSource,
            "分层取自落账行时必须如实标注来源（总量仍为 provider 报数）。");
        Assert.AreEqual(12_345, health.UsedTokens, "上下文占用仍按 provider 的 prompt 口径。");
        Assert.AreEqual(300, health.SystemPromptTokens);
        Assert.AreEqual(400, health.CompactionSummaryTokens);
        Assert.AreEqual(150, health.ConversationTokens);
        Assert.AreEqual(120, health.ToolResultTokens);
        Assert.AreEqual(30, health.ReasoningTokens);
    }

    [TestMethod]
    public async Task GetHealthAsync_LeavesLayerBucketsEmpty_WhenLedgerRowPredatesLayerCluster()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<MemoryDbContext>()
            .UseSqlite(connection)
            .Options;
        await using (var db = new MemoryDbContext(options))
        {
            await db.Database.EnsureCreatedAsync();
        }

        var service = new ContextCompactionService(
            new TestMemoryDbContextFactory(options),
            new FixedSummaryGenerator("summary"),
            NullLogger<ContextCompactionService>.Instance,
            contentSummaryService: null,
            tokenUsageRepo: new FakeTokenUsageRepository(
                Stats(),
                // 旧行：只持久化了旧四字段，五个新桶为 null。
                new SessionTokenDiagnostics
                {
                    SessionId = SessionId,
                    OccurredAtUtc = DateTimeOffset.UtcNow,
                    MessageTokens = 1_000,
                    ToolDefinitionTokens = 200,
                    SystemMessageTokens = 300,
                    HistoryMessageTokens = 700,
                }),
            messageStore: null,
            sessionSummaryStore: null,
            contextUsageSnapshotStore: null);

        var health = await service.GetHealthAsync(
            SessionId,
            CancellationToken.None,
            contextWindowTokens: 1_000_000);

        Assert.AreEqual(
            "provider_usage_db",
            health.UsageSource,
            "旧行不得被当成六桶齐备，来源不应标注为分层可用。");
        Assert.AreEqual(12_345, health.UsedTokens);
        Assert.AreEqual(0, health.SystemPromptTokens, "旧行不得填充分层：宁可回落单色条也不显示残缺分层。");
        Assert.AreEqual(0, health.CompactionSummaryTokens);
        Assert.AreEqual(0, health.ConversationTokens);
        Assert.AreEqual(0, health.ToolResultTokens);
        Assert.AreEqual(0, health.ReasoningTokens);
    }

    /// <summary>
    /// 健康口径的「预留输出」必须等于本轮真实申请的输出上限（LlmReplyBudget.PerTurnOutputCapTokens
    /// 收敛为 64,000），而不是资源池登记的模型最大输出能力（deepseek-flash 登记 393,216）。
    /// 否则面板报 605,760 有效输入，执行路径却按 934,976 判定压缩 —— 同一次请求两套水位（诊断 §4.4）。
    /// </summary>
    [TestMethod]
    public async Task GetHealthAsync_CapsOutputReserve_AtPerTurnBudget()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<MemoryDbContext>()
            .UseSqlite(connection)
            .Options;
        await using (var db = new MemoryDbContext(options))
        {
            await db.Database.EnsureCreatedAsync();
        }

        var service = new ContextCompactionService(
            new TestMemoryDbContextFactory(options),
            new FixedSummaryGenerator("summary"),
            NullLogger<ContextCompactionService>.Instance,
            contentSummaryService: null,
            tokenUsageRepo: new FakeTokenUsageRepository(Stats(), null),
            messageStore: null,
            sessionSummaryStore: null,
            contextUsageSnapshotStore: null);

        // 入参模拟 SessionEventsController：直接传资源池登记的「模型最大输出能力」。
        var health = await service.GetHealthAsync(
            SessionId,
            CancellationToken.None,
            contextWindowTokens: 1_000_000,
            maxOutputTokens: 393_216);

        // 1,000,000 − 64,000（单轮上限）− 1,024（安全余量）= 934,976
        Assert.AreEqual(
            934_976,
            health.EffectiveWindowTokens,
            "预留输出须收敛到单轮上限；若退回登记值 393,216，此处会变成 605,760。");
        Assert.AreNotEqual(
            605_760,
            health.EffectiveWindowTokens,
            "605,760 是「把模型最大输出能力当预留」的旧口径，不得回归。");
    }
    private static SessionTokenStats Stats() => new()
    {
        PromptTokens = 12_345,
        CompletionTokens = 678,
        TotalTokens = 13_023,
        OccurredAtUtc = DateTimeOffset.UtcNow,
    };

    private sealed class TestMemoryDbContextFactory(DbContextOptions<MemoryDbContext> options)
        : IDbContextFactory<MemoryDbContext>
    {
        public MemoryDbContext CreateDbContext() => new(options);

        public Task<MemoryDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }

    private sealed class FixedSummaryGenerator(string summary) : IContextCompactionSummaryGenerator
    {
        public Task<string> GenerateSummaryAsync(
            ContextCompactionSummaryRequest request,
            CancellationToken ct = default) =>
            Task.FromResult(summary);
    }

    private sealed class FakeTokenUsageRepository(
        SessionTokenStats stats,
        SessionTokenDiagnostics? diagnostics) : ITokenUsageEventRepository
    {
        public Task<TokenUsageEventPage> GetFilteredAsync(
            string? workspaceId = null,
            string? sessionId = null,
            string? providerId = null,
            string? modelId = null,
            DateTimeOffset? from = null,
            DateTimeOffset? to = null,
            int page = 1,
            int pageSize = 50,
            CancellationToken ct = default) =>
            Task.FromResult(new TokenUsageEventPage());

        public Task<SessionTokenStats?> GetLatestStatsAsync(
            string sessionId,
            CancellationToken ct = default) =>
            Task.FromResult<SessionTokenStats?>(stats);

        public Task<SessionTokenDiagnostics?> GetLatestLayerDiagnosticsAsync(
            string sessionId,
            CancellationToken ct = default) =>
            Task.FromResult(diagnostics);

        public Task<SessionTokenDiagnostics?> GetLatestEntropyDiagnosticsAsync(
            string sessionId,
            CancellationToken ct = default) =>
            Task.FromResult<SessionTokenDiagnostics?>(null);
    }
}
