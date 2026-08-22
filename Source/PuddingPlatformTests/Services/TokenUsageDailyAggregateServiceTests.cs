using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using PuddingPlatform.Data;
using PuddingPlatform.Data.Entities;
using PuddingPlatform.Services;

namespace PuddingPlatformTests.Services;

[TestClass]
public sealed class TokenUsageDailyAggregateServiceTests
{
    [TestMethod]
    public async Task ClosedDays_AggregateOnceThenServeFromCacheAfterLedgerDeleted()
    {
        await using var scope = await CreateScopeAsync();
        await using (var db = await scope.Factory.CreateDbContextAsync())
        {
            db.LlmGatewayUsageEvents.Add(CreateGateway("g-1", "2026-06-02T05:00:00Z", 100, 20));
            db.TokenUsageEvents.Add(CreateLegacy("l-1", "2026-06-02T06:00:00Z", 30, 10));
            await db.SaveChangesAsync();
        }

        var service = CreateService(scope);
        var first = await service.GetClosedDaysAsync(new DateTime(2026, 6, 1), new DateTime(2026, 6, 3));

        var gateway = first.Single(r => r.Source == LlmUsageAggregateSources.Gateway);
        Assert.AreEqual("2026-06-02", gateway.DayUtc);
        Assert.AreEqual("deepseek", gateway.ProviderId);
        Assert.AreEqual(100, gateway.PromptTokens);
        Assert.AreEqual(50, gateway.CacheHitTokens);
        Assert.AreEqual(50, gateway.CacheMissTokens);
        Assert.AreEqual(20, gateway.CompletionTokens);
        Assert.AreEqual(1, gateway.RequestCount);

        var legacy = first.Single(r => r.Source == LlmUsageAggregateSources.Legacy);
        Assert.AreEqual("unknown", legacy.ProviderId);
        Assert.AreEqual(30, legacy.PromptTokens);

        // 删除源账本后仍返回相同结果 → 证明闭日结果来自缓存而非重扫
        await using (var db = await scope.Factory.CreateDbContextAsync())
        {
            db.LlmGatewayUsageEvents.RemoveRange(db.LlmGatewayUsageEvents);
            db.TokenUsageEvents.RemoveRange(db.TokenUsageEvents);
            await db.SaveChangesAsync();
        }

        var second = await service.GetClosedDaysAsync(new DateTime(2026, 6, 1), new DateTime(2026, 6, 3));
        Assert.AreEqual(first.Count, second.Count);
        Assert.AreEqual(100, second.Single(r => r.Source == LlmUsageAggregateSources.Gateway).PromptTokens);
    }

    [TestMethod]
    public async Task ClosedDays_WriteCompletionMarkersForEmptyDays()
    {
        await using var scope = await CreateScopeAsync();
        var service = CreateService(scope);

        var rows = await service.GetClosedDaysAsync(new DateTime(2026, 6, 1), new DateTime(2026, 6, 4));
        Assert.AreEqual(0, rows.Count);

        await using var db = await scope.Factory.CreateDbContextAsync();
        var markers = await db.StatsDailyCacheDays
            .Where(d => d.CacheKey == "token_usage" && d.DayUtc.StartsWith("2026-06"))
            .ToListAsync();
        // 空数据闭日同样有完成标记，后续请求不再重扫事件表
        Assert.AreEqual(3, markers.Count);
        Assert.IsTrue(markers.All(m => m.DayUtc is "2026-06-01" or "2026-06-02" or "2026-06-03"));
    }

    [TestMethod]
    public async Task LiveToday_ReflectsTodayEventsWithoutWritingCache()
    {
        await using var scope = await CreateScopeAsync();
        var now = DateTimeOffset.UtcNow;
        await using (var db = await scope.Factory.CreateDbContextAsync())
        {
            db.LlmGatewayUsageEvents.Add(CreateGateway("g-today", now.ToString("o"), 10, 5));
            await db.SaveChangesAsync();
        }

        var service = CreateService(scope);
        var live = await service.GetLiveTodayAsync();

        var row = live.Single(r => r.Source == LlmUsageAggregateSources.Gateway);
        Assert.AreEqual(now.UtcDateTime.Date.ToString("yyyy-MM-dd"), row.DayUtc);
        Assert.AreEqual(10, row.PromptTokens);

        await using var db2 = await scope.Factory.CreateDbContextAsync();
        var todayHasMarker = await db2.StatsDailyCacheDays.AnyAsync(
            d => d.CacheKey == "token_usage" && d.DayUtc == now.UtcDateTime.Date.ToString("yyyy-MM-dd"));
        Assert.IsFalse(todayHasMarker, "今天尚未结束，不应写入闭日缓存标记");
    }

    [TestMethod]
    public async Task InvalidateAsync_RemovesCachedMonthSoNextReadRecomputes()
    {
        await using var scope = await CreateScopeAsync();
        await using (var db = await scope.Factory.CreateDbContextAsync())
        {
            db.LlmGatewayUsageEvents.Add(CreateGateway("g-1", "2026-06-02T05:00:00Z", 100, 20));
            await db.SaveChangesAsync();
        }

        var service = CreateService(scope);
        var first = await service.GetClosedDaysAsync(new DateTime(2026, 6, 1), new DateTime(2026, 6, 3));
        Assert.AreEqual(1, first.Count);

        // 模拟重建：账本内容变化 + 按月失效
        await using (var db = await scope.Factory.CreateDbContextAsync())
        {
            db.LlmGatewayUsageEvents.RemoveRange(db.LlmGatewayUsageEvents);
            db.LlmGatewayUsageEvents.Add(CreateGateway("g-2", "2026-06-02T07:00:00Z", 42, 7));
            await db.SaveChangesAsync();
        }

        await service.InvalidateAsync("2026-06");

        var second = await service.GetClosedDaysAsync(new DateTime(2026, 6, 1), new DateTime(2026, 6, 3));
        var row = second.Single();
        Assert.AreEqual(42, row.PromptTokens);
        Assert.AreEqual(7, row.CompletionTokens);
    }

    private static TokenUsageDailyAggregateService CreateService(TestScope scope)
        => new(scope.Factory, NullLogger<TokenUsageDailyAggregateService>.Instance);

    private static LlmGatewayUsageEventEntity CreateGateway(
        string sourceId,
        string occurredAt,
        long promptTokens,
        long completionTokens)
    {
        var occurredAtUtc = DateTimeOffset.Parse(occurredAt, System.Globalization.CultureInfo.InvariantCulture);
        return new LlmGatewayUsageEventEntity
        {
            SourceId = sourceId,
            Operation = "chat_stream",
            WorkspaceId = "w1",
            SessionId = "s1",
            ProviderId = "deepseek",
            ModelId = "shared-model",
            OccurredAtUtc = occurredAtUtc,
            YearMonth = occurredAtUtc.ToString("yyyy-MM"),
            PromptTokens = promptTokens,
            CompletionTokens = completionTokens,
            TotalTokens = promptTokens + completionTokens,
            CacheHitTokens = promptTokens / 2,
            CacheMissTokens = promptTokens - (promptTokens / 2),
            InputCost = promptTokens / 1_000_000m,
            OutputCost = completionTokens * 2m / 1_000_000m,
            TotalCost = 1m,
        };
    }

    private static TokenUsageEventEntity CreateLegacy(
        string sourceId,
        string occurredAt,
        long promptTokens,
        long completionTokens)
    {
        var occurredAtUtc = DateTimeOffset.Parse(occurredAt, System.Globalization.CultureInfo.InvariantCulture);
        return new TokenUsageEventEntity
        {
            SourceType = "chat_message",
            SourceId = sourceId,
            ProviderId = null,
            ModelId = null,
            OccurredAtUtc = occurredAtUtc,
            YearMonth = occurredAtUtc.ToString("yyyy-MM"),
            PromptTokens = promptTokens,
            CompletionTokens = completionTokens,
            TotalTokens = promptTokens + completionTokens,
            InputCost = promptTokens / 1_000_000m,
            OutputCost = completionTokens / 1_000_000m,
            TotalCost = promptTokens / 500_000m,
        };
    }

    private static async Task<TestScope> CreateScopeAsync()
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

        return new TestScope(connection, factory);
    }

    private sealed class TestDbContextFactory(DbContextOptions<PlatformDbContext> options)
        : IDbContextFactory<PlatformDbContext>
    {
        public PlatformDbContext CreateDbContext() => new(options);

        public Task<PlatformDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(CreateDbContext());
    }

    private sealed class TestScope(SqliteConnection connection, TestDbContextFactory factory) : IAsyncDisposable
    {
        public SqliteConnection Connection { get; } = connection;
        public TestDbContextFactory Factory { get; } = factory;

        public async ValueTask DisposeAsync()
        {
            await Connection.DisposeAsync();
        }
    }
}
