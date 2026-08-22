using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using PuddingPlatform.Data;
using PuddingPlatform.Data.Entities;
using PuddingPlatform.Services;

namespace PuddingPlatformTests.Services;

[TestClass]
public sealed class ContextLayerDailyRollupServiceTests
{
    [TestMethod]
    public async Task Analysis_MergesClosedDayRollupsExactlyAndSurvivesLedgerDeletion()
    {
        await using var scope = await CreateScopeAsync();
        await using (var db = await scope.Factory.CreateDbContextAsync())
        {
            db.ContextLayerMetricEvents.AddRange(
                CreateLayer("a", "L0-STATIC", 0, 100, "h1", 80, 20, "2026-06-02T01:00:00Z"),
                CreateLayer("b", "L0-STATIC", 0, 300, "h1", 300, 0, "2026-06-03T01:00:00Z", changed: true, reason: "history_changed"),
                CreateLayer("c", "L0-STATIC", 0, 200, "h2", 0, 200, "2026-06-03T02:00:00Z"),
                CreateLayer("d", "L5-RECENT", 1, 60, "r1", 30, 30, "2026-06-03T03:00:00Z", providerId: "other"));
            await db.SaveChangesAsync();
        }

        var service = CreateService(scope);
        var analysis = await service.GetAnalysisAsync("2026-06-01T00:00:00Z", "2026-06-04T00:00:00Z", null, null);

        Assert.IsNotNull(analysis);
        Assert.AreEqual(4, analysis.TotalEvents);

        var staticLayer = analysis.Layers.Single(l => l.LayerName == "L0-STATIC");
        Assert.AreEqual(3, staticLayer.Calls);
        Assert.AreEqual(600, staticLayer.TokenCount);
        Assert.AreEqual(0.6, staticLayer.AvgCacheHitRate);
        // median([100,300,200]) = 200；p95 = 200*0.1 + 300*0.9 = 290
        Assert.AreEqual(200, staticLayer.MedianTokens);
        Assert.AreEqual(290, staticLayer.P95Tokens);
        Assert.AreEqual(2, staticLayer.DistinctHashes);
        Assert.AreEqual(1, staticLayer.ChangeCount);
        var reason = staticLayer.ChangeReasons.Single();
        Assert.AreEqual("history_changed", reason.Reason);
        Assert.AreEqual(1, reason.Count);

        // 删除明细后闭日分析结果不变（rollup 缓存生效）
        await using (var db = await scope.Factory.CreateDbContextAsync())
        {
            db.ContextLayerMetricEvents.RemoveRange(db.ContextLayerMetricEvents);
            await db.SaveChangesAsync();
        }

        var cached = await service.GetAnalysisAsync("2026-06-01T00:00:00Z", "2026-06-04T00:00:00Z", null, null);
        Assert.IsNotNull(cached);
        Assert.AreEqual(4, cached.TotalEvents);
        Assert.AreEqual(200, cached.Layers.Single(l => l.LayerName == "L0-STATIC").MedianTokens);

        // provider 过滤在 rollup 行上生效
        var filtered = await service.GetAnalysisAsync("2026-06-01T00:00:00Z", "2026-06-04T00:00:00Z", "deepseek", null);
        Assert.IsNotNull(filtered);
        Assert.AreEqual(3, filtered.TotalEvents);
        Assert.IsFalse(filtered.Layers.Any(l => l.LayerName == "L5-RECENT"));
    }

    [TestMethod]
    public async Task Analysis_PartialBoundaryDays_ExcludeRowsOutsideTimeRange()
    {
        await using var scope = await CreateScopeAsync();
        await using (var db = await scope.Factory.CreateDbContextAsync())
        {
            db.ContextLayerMetricEvents.AddRange(
                CreateLayer("in", "L0-STATIC", 0, 100, "h1", 80, 20, "2026-06-02T00:30:00Z"),
                CreateLayer("out", "L0-STATIC", 0, 900, "h2", 0, 900, "2026-06-02T01:30:00Z"));
            await db.SaveChangesAsync();
        }

        var service = CreateService(scope);
        var analysis = await service.GetAnalysisAsync("2026-06-02T00:00:00Z", "2026-06-02T01:00:00Z", null, null);

        Assert.IsNotNull(analysis);
        Assert.AreEqual(1, analysis.TotalEvents);
        Assert.AreEqual(100, analysis.Layers.Single().TokenCount);
    }

    [TestMethod]
    public async Task Analysis_TodayRows_AreLiveAndNotCached()
    {
        await using var scope = await CreateScopeAsync();
        var now = DateTimeOffset.UtcNow;
        var todayStart = now.UtcDateTime.Date;
        await using (var db = await scope.Factory.CreateDbContextAsync())
        {
            db.ContextLayerMetricEvents.Add(
                CreateLayer("live", "L0-STATIC", 0, 55, "h1", 55, 0, now.ToString("o")));
            await db.SaveChangesAsync();
        }

        var service = CreateService(scope);
        var analysis = await service.GetAnalysisAsync(
            todayStart.ToString("yyyy-MM-dd") + "T00:00:00Z",
            todayStart.AddDays(1).ToString("yyyy-MM-dd") + "T00:00:00Z",
            null,
            null);

        Assert.IsNotNull(analysis);
        Assert.AreEqual(1, analysis.TotalEvents);
        Assert.AreEqual(55, analysis.Layers.Single().TokenCount);

        await using var db2 = await scope.Factory.CreateDbContextAsync();
        var todayHasMarker = await db2.StatsDailyCacheDays.AnyAsync(
            d => d.CacheKey == "context_layer" && d.DayUtc == todayStart.ToString("yyyy-MM-dd"));
        Assert.IsFalse(todayHasMarker, "今天尚未结束，不应写入 rollup 闭日标记");
    }

    [TestMethod]
    public async Task Analysis_UnparsableRange_ReturnsNull()
    {
        await using var scope = await CreateScopeAsync();
        var service = CreateService(scope);

        Assert.IsNull(await service.GetAnalysisAsync(null, "2026-06-04T00:00:00Z", null, null));
        Assert.IsNull(await service.GetAnalysisAsync("garbage", "2026-06-04T00:00:00Z", null, null));
    }

    private static ContextLayerDailyRollupService CreateService(TestScope scope)
        => new(scope.Factory, NullLogger<ContextLayerDailyRollupService>.Instance);

    private static ContextLayerMetricEventEntity CreateLayer(
        string sourceId,
        string layerName,
        int order,
        long tokens,
        string hash,
        long hit,
        long miss,
        string occurredAt,
        bool changed = false,
        string? reason = null,
        string providerId = "deepseek") => new()
    {
        SourceType = "chat_message",
        SourceId = sourceId,
        WorkspaceId = "w1",
        SessionId = "s1",
        ProviderId = providerId,
        ModelId = "deepseek-chat",
        OccurredAtUtc = DateTimeOffset.Parse(occurredAt, System.Globalization.CultureInfo.InvariantCulture),
        AssemblerVersion = "context-v1",
        LayoutVersion = "layer-v1",
        LayerName = layerName,
        LayerOrder = order,
        LayerRole = layerName.Contains("STATIC", StringComparison.OrdinalIgnoreCase) ? "stable_prefix" : "dynamic_history",
        TokenCount = tokens,
        CharCount = tokens * 4,
        RawUtf8Bytes = tokens * 4,
        GzipBytes = tokens * 2,
        GzipRatio = 2d,
        ContentHash = hash,
        PreviousHash = changed ? "previous" : hash,
        IsChanged = changed,
        ChangeReason = reason,
        StartsAtToken = order == 0 ? 0 : 50,
        EndsAtToken = order == 0 ? tokens : 50 + tokens,
        IsCacheEligible = true,
        EstimatedCacheHitTokens = hit,
        EstimatedCacheMissTokens = miss,
        EstimatedCacheHitRate = (hit + miss) > 0 ? (double)hit / (hit + miss) : null,
        Confidence = "estimated",
    };

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
