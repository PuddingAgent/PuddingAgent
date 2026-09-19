using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PuddingCode.Abstractions;
using PuddingCode.Models;
using PuddingCode.Platform;
using PuddingPlatform.Data;
using PuddingPlatform.Data.Entities;
using PuddingPlatform.Services;

namespace PuddingPlatformTests.Services;

/// <summary>
/// S01-A（2026-09-11）：usage 账本与月度聚合的并发幂等集成回归。
/// 使用临时 SQLite 文件（每个 context 独立连接），复现「两个连接同读同一
/// 聚合、各写回 +1」的丢失更新窗口与重复 source 并发插入，要求事件数、
/// Token、RequestCount、金额完全对齐。
/// </summary>
[TestClass]
public sealed class TokenUsageRecorderConcurrencyTests
{
    /// <summary>BaseUsage 在固定价格下的精确单条成本（decimal，不经浮点）。</summary>
    private const decimal PerEventCost = 0.00164m;

    private static readonly TokenUsageDto BaseUsage = new()
    {
        PromptTokens = 1000,
        CompletionTokens = 500,
        TotalTokens = 1500,
        PromptCacheHitTokens = 400,
        PromptCacheMissTokens = 600,
    };

    private static TokenUsageDto Usage(int promptTokens) => BaseUsage with
    {
        PromptTokens = promptTokens,
        TotalTokens = promptTokens + (BaseUsage.CompletionTokens ?? 0),
    };

    [TestMethod]
    public async Task FiftyConcurrentDistinctSources_AlignLedgerAndAggregateExactly()
    {
        await using var harness = await CreateHarnessAsync();
        const int concurrency = 50;
        var barrier = new TaskCompletionSource();
        var recorders = Enumerable.Range(0, concurrency)
            .Select(_ => harness.Provider.GetRequiredService<TokenUsageRecorder>())
            .ToList();

        var tasks = recorders.Select((recorder, index) => Task.Run(async () =>
        {
            // 先全部就绪，再由 barrier 放行，最大化聚合读改写的并发交错。
            await barrier.Task;
            await recorder.RecordRequiredAsync(
                Usage(1000 + index),
                "test-request",
                $"src-{index}",
                "ws-default",
                "session-x",
                "prov-x",
                "model-x",
                occurredAtUtc: new DateTimeOffset(2026, 9, 11, 0, 0, 0, TimeSpan.Zero));
        })).ToList();

        tasks.Add(Task.Run(() => barrier.SetResult()));
        await Task.WhenAll(tasks);

        await using var db = await harness.OpenDbContextAsync();
        var events = await db.TokenUsageEvents
            .Where(e => e.SourceType == "test-request")
            .ToListAsync();
        Assert.AreEqual(concurrency, events.Count);
        Assert.AreEqual(concurrency, events.Select(e => e.SourceId).Distinct().Count());
        Assert.AreEqual(
            concurrency * (BaseUsage.PromptCacheHitTokens ?? 0),
            events.Sum(e => e.CacheHitTokens));
        Assert.AreEqual(
            concurrency * (BaseUsage.CompletionTokens ?? 0),
            events.Sum(e => e.CompletionTokens));

        var stats = await db.TokenUsageStats.SingleAsync();
        Assert.AreEqual(concurrency, stats.RequestCount);
        Assert.AreEqual(
            concurrency * (BaseUsage.PromptCacheHitTokens ?? 0),
            stats.CacheHitTokens);
        Assert.AreEqual(
            concurrency * (BaseUsage.CompletionTokens ?? 0),
            stats.CompletionTokens);

        // 金额按 decimal 精确对账：miss 600/1M×1 + completion 500/1M×2
        // + hit 400/1M×0.1 = 0.00164/条；50 条 = 0.082。
        var expectedCost = 50 * PerEventCost;
        Assert.AreEqual(expectedCost, stats.TotalCost, "aggregate TotalCost must equal the exact decimal sum of the ledger");
        Assert.AreEqual(
            events.Sum(e => e.TotalCost),
            stats.TotalCost,
            "aggregate TotalCost must equal the sum of event TotalCost");
    }

    [TestMethod]
    public async Task ConcurrentDuplicateSource_CountsExactlyOnce()
    {
        await using var harness = await CreateHarnessAsync();
        const int concurrency = 8;
        var barrier = new TaskCompletionSource();
        var recorders = Enumerable.Range(0, concurrency)
            .Select(_ => harness.Provider.GetRequiredService<TokenUsageRecorder>())
            .ToList();

        var tasks = recorders.Select(recorder => Task.Run(async () =>
        {
            await barrier.Task;
            await recorder.RecordRequiredAsync(
                BaseUsage,
                "test-request",
                "dup-source",
                "ws-default",
                "session-x",
                "prov-x",
                "model-x",
                occurredAtUtc: new DateTimeOffset(2026, 9, 11, 0, 0, 0, TimeSpan.Zero));
        })).ToList();
        tasks.Add(Task.Run(() => barrier.SetResult()));
        await Task.WhenAll(tasks);

        await using var db = await harness.OpenDbContextAsync();
        Assert.AreEqual(1, await db.TokenUsageEvents.CountAsync());
        var stats = await db.TokenUsageStats.SingleAsync();
        Assert.AreEqual(1, stats.RequestCount);
        Assert.AreEqual(PerEventCost, stats.TotalCost);
    }

    [TestMethod]
    public async Task SameSourceWithDifferentPayload_Required_ThrowsAndKeepsLedgerIntact()
    {
        await using var harness = await CreateHarnessAsync();
        var recorder = harness.Provider.GetRequiredService<TokenUsageRecorder>();

        await recorder.RecordRequiredAsync(
            BaseUsage,
            "test-request",
            "conflict-source",
            "ws-default",
            "session-x",
            "prov-x",
            "model-x",
            occurredAtUtc: new DateTimeOffset(2026, 9, 11, 0, 0, 0, TimeSpan.Zero));

        // 同 source 身份、不同 usage 内容：拒绝，不得静默覆盖或双计。
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            recorder.RecordRequiredAsync(
                Usage(9000),
                "test-request",
                "conflict-source",
                "ws-default",
                "session-x",
                "prov-x",
                "model-x",
                occurredAtUtc: new DateTimeOffset(2026, 9, 11, 0, 0, 5, TimeSpan.Zero)));

        await using var db = await harness.OpenDbContextAsync();
        Assert.AreEqual(1, await db.TokenUsageEvents.CountAsync());
        var stats = await db.TokenUsageStats.SingleAsync();
        Assert.AreEqual(1, stats.RequestCount);
        Assert.AreEqual(PerEventCost, stats.TotalCost);
    }

    [TestMethod]
    public async Task SameSourceWithDifferentPayload_BestEffort_SkipsWithoutThrowing()
    {
        await using var harness = await CreateHarnessAsync();
        var recorder = harness.Provider.GetRequiredService<TokenUsageRecorder>();

        await recorder.RecordRequiredAsync(
            BaseUsage,
            "test-request",
            "besteffort-source",
            "ws-default",
            "session-x",
            "prov-x",
            "model-x",
            occurredAtUtc: new DateTimeOffset(2026, 9, 11, 0, 0, 0, TimeSpan.Zero));

        await recorder.RecordAsync( // best-effort：冲突只记录告警，不向上抛
            Usage(9000),
            "test-request",
            "besteffort-source",
            "ws-default",
            "session-x",
            "prov-x",
            "model-x",
            occurredAtUtc: new DateTimeOffset(2026, 9, 11, 0, 0, 5, TimeSpan.Zero));

        await using var db = await harness.OpenDbContextAsync();
        Assert.AreEqual(1, await db.TokenUsageEvents.CountAsync());
        Assert.AreEqual(1, (await db.TokenUsageStats.SingleAsync()).RequestCount);
    }

    [TestMethod]
    public async Task ConcurrentDuplicateSource_ToleratesProvidersWithPreexistingRow()
    {
        // 重复调用（payload 一致）必须在第二次调用时按幂等成功跳过。
        await using var harness = await CreateHarnessAsync();
        var recorder = harness.Provider.GetRequiredService<TokenUsageRecorder>();
        var occurredAt = new DateTimeOffset(2026, 9, 11, 0, 0, 0, TimeSpan.Zero);

        await recorder.RecordRequiredAsync(
            BaseUsage, "test-request", "same-source", "ws-default", "session-x", "prov-x", "model-x", occurredAtUtc: occurredAt);
        await recorder.RecordRequiredAsync(
            BaseUsage, "test-request", "same-source", "ws-default", "session-x", "prov-x", "model-x", occurredAtUtc: occurredAt);

        await using var db = await harness.OpenDbContextAsync();
        Assert.AreEqual(1, await db.TokenUsageEvents.CountAsync());
        Assert.AreEqual(1, (await db.TokenUsageStats.SingleAsync()).RequestCount);
    }

    private static async Task<TestHarness> CreateHarnessAsync()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "pudding-usage-concurrency-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var dbPath = Path.Combine(root, "platform.db");

        var services = new ServiceCollection();
        // Factory 注册：options/Factory 单例、context 每作用域新建；
        // 验证查询用 factory 自建实例，避免 root 作用域缓存实例被提前释放。
        services.AddDbContextFactory<PlatformDbContext>(options =>
            options.UseSqlite($"Data Source={dbPath}"));
        services.AddSingleton<TokenUsageNormalizer>();
        services.AddSingleton<ILlmConfigService>(new FixedLlmConfigService());
        services.AddSingleton<ILogger<TokenUsageRecorder>>(NullLogger<TokenUsageRecorder>.Instance);
        services.AddScoped<TokenUsageRecorder>();
        var provider = services.BuildServiceProvider();

        await using (var db = await provider
            .GetRequiredService<IDbContextFactory<PlatformDbContext>>()
            .CreateDbContextAsync())
        {
            await db.Database.EnsureCreatedAsync();
        }

        return new TestHarness(root, provider);
    }

    private sealed class TestHarness(string root, ServiceProvider provider) : IAsyncDisposable
    {
        public ServiceProvider Provider { get; } = provider;

        public Task<PlatformDbContext> OpenDbContextAsync()
            => Provider.GetRequiredService<IDbContextFactory<PlatformDbContext>>()
                .CreateDbContextAsync();

        public async ValueTask DisposeAsync()
        {
            await Provider.DisposeAsync();
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    /// <summary>固定价格的 ILlmConfigService 打桩：只提供价格匹配所需的最小面。</summary>
    private sealed class FixedLlmConfigService : ILlmConfigService
    {
        private static readonly LlmModelInfo Model = new()
        {
            ProviderId = "prov-x",
            ModelId = "model-x",
            InputPricePer1MTokens = 1m,
            OutputPricePer1MTokens = 2m,
            CacheHitPricePer1MTokens = 0.1m,
        };

        public IReadOnlyList<LlmProviderInfo> GetEnabledProviders() => [];
        public IReadOnlyList<LlmModelInfo> GetAllModels() => [Model];
        public LlmConfig? Resolve(string providerId, string modelId) => null;
        public LlmProfileInfo? ResolveProfile(string profileId) => null;
        public LlmConfig? GetMemoryConfig() => null;
        public LlmConfig? GetEmbeddingConfig() => null;
        public LlmProviderStrategy? GetProviderStrategy(string providerId) => null;
        public LlmProviderStrategy? GetModelStrategy(string providerId, string modelId) => null;
        public void Reload(object config) { }
    }
}
