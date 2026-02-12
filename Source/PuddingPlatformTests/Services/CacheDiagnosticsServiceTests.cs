using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PuddingPlatform.Data;
using PuddingPlatform.Data.Entities;
using PuddingPlatform.Services;

namespace PuddingPlatformTests.Services;

[TestClass]
public sealed class CacheDiagnosticsServiceTests
{
    [TestMethod]
    public async Task GetSessionReportAsync_StablePrefix_ReturnsStable()
    {
        await using var scope = await CreateScopeAsync();
        scope.Db.TokenUsageEvents.Add(CreateEvent("s1", "1", "prefix-a", hit: 80, miss: 20));
        scope.Db.TokenUsageEvents.Add(CreateEvent("s1", "2", "prefix-a", hit: 90, miss: 10, offsetMinutes: 1));
        await scope.Db.SaveChangesAsync();

        var report = await new CacheDiagnosticsService(scope.Db).GetSessionReportAsync("s1");

        Assert.AreEqual("stable", report.Status);
        Assert.AreEqual(1, report.DistinctPrefixHashCount);
        Assert.AreEqual(0.85, report.AverageCacheHitRate);
    }

    [TestMethod]
    public async Task GetSessionReportAsync_PrefixChurnWithoutReason_ReturnsUnexpectedChurn()
    {
        await using var scope = await CreateScopeAsync();
        scope.Db.TokenUsageEvents.Add(CreateEvent("s1", "1", "prefix-a", hit: 80, miss: 20));
        scope.Db.TokenUsageEvents.Add(CreateEvent("s1", "2", "prefix-b", hit: 0, miss: 100, offsetMinutes: 1));
        await scope.Db.SaveChangesAsync();

        var report = await new CacheDiagnosticsService(scope.Db).GetSessionReportAsync("s1");

        Assert.AreEqual("unexpected_churn", report.Status);
        Assert.AreEqual(2, report.DistinctPrefixHashCount);
        Assert.AreEqual("prefix_hash_changed", report.FirstChurnSource);
    }

    [TestMethod]
    public async Task GetSessionReportAsync_PrefixChurnWithReason_ReturnsExpectedChurn()
    {
        await using var scope = await CreateScopeAsync();
        scope.Db.TokenUsageEvents.Add(CreateEvent("s1", "1", "prefix-a", hit: 80, miss: 20));
        scope.Db.TokenUsageEvents.Add(CreateEvent(
            "s1",
            "2",
            "prefix-b",
            hit: 0,
            miss: 100,
            offsetMinutes: 1,
            reason: "tool_schema_changed"));
        await scope.Db.SaveChangesAsync();

        var report = await new CacheDiagnosticsService(scope.Db).GetSessionReportAsync("s1");

        Assert.AreEqual("expected_churn", report.Status);
        Assert.AreEqual("tool_schema_changed", report.FirstChurnSource);
    }

    private static TokenUsageEventEntity CreateEvent(
        string sessionId,
        string sourceId,
        string prefixHash,
        long hit,
        long miss,
        int offsetMinutes = 0,
        string? reason = null)
    {
        var occurredAt = DateTimeOffset.Parse("2026-05-25T00:00:00Z").AddMinutes(offsetMinutes);
        var eligible = hit + miss;
        return new TokenUsageEventEntity
        {
            SourceType = "chat_message",
            SourceId = sourceId,
            WorkspaceId = "w1",
            SessionId = sessionId,
            ProviderId = "deepseek",
            ModelId = "deepseek-chat",
            OccurredAtUtc = occurredAt,
            YearMonth = "2026-05",
            PromptTokens = eligible,
            CompletionTokens = 10,
            TotalTokens = eligible + 10,
            CacheHitTokens = hit,
            CacheMissTokens = miss,
            CacheEligibleTokens = eligible,
            CacheHitRate = eligible > 0 ? Math.Round((double)hit / eligible, 6) : null,
            PrefixVersion = "prefix-v1",
            PrefixHash = prefixHash,
            SystemPromptHash = "system-a",
            ToolSpecHash = prefixHash,
            PrefixChangeReason = reason,
            CreatedAtUtc = occurredAt,
        };
    }

    private static async Task<TestScope> CreateScopeAsync()
    {
        var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<PlatformDbContext>()
            .UseSqlite(connection)
            .Options;
        var db = new PlatformDbContext(options);
        await db.Database.EnsureCreatedAsync();
        return new TestScope(connection, db);
    }

    private sealed class TestScope(SqliteConnection connection, PlatformDbContext db) : IAsyncDisposable
    {
        public PlatformDbContext Db { get; } = db;

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await connection.DisposeAsync();
        }
    }
}
