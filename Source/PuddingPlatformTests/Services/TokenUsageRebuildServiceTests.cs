using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using PuddingPlatform.Data;
using PuddingPlatform.Data.Entities;
using PuddingPlatform.Services;

namespace PuddingPlatformTests.Services;

[TestClass]
public sealed class TokenUsageRebuildServiceTests
{
    [TestMethod]
    public async Task RebuildAsync_RebuildsMonthlyStatsFromExistingEvents()
    {
        await using var scope = await CreateScopeAsync();
        await using (var db = await scope.Factory.CreateDbContextAsync())
        {
            db.TokenUsageEvents.Add(new TokenUsageEventEntity
            {
                SourceType = "chat_message",
                SourceId = "m1",
                WorkspaceId = "w1",
                SessionId = "s1",
                ProviderId = "deepseek",
                ModelId = "deepseek-chat",
                OccurredAtUtc = DateTimeOffset.Parse("2026-06-03T08:00:00Z"),
                YearMonth = "2026-06",
                PromptTokens = 1000,
                CompletionTokens = 200,
                TotalTokens = 1200,
                CacheHitTokens = 600,
                CacheMissTokens = 400,
                CacheEligibleTokens = 1000,
                CacheHitRate = 0.6,
                InputCost = 0.0001m,
                OutputCost = 0.0002m,
                CacheHitCost = 0.0003m,
                TotalCost = 0.0006m,
                CreatedAtUtc = DateTimeOffset.Parse("2026-06-03T08:00:00Z"),
            });
            await db.SaveChangesAsync();
        }

        var service = new TokenUsageRebuildService(
            scope.Factory,
            new TokenUsageNormalizer(),
            null,
            NullLogger<TokenUsageRebuildService>.Instance);

        await service.RebuildAsync("2026-06");

        await using var verifyDb = await scope.Factory.CreateDbContextAsync();
        var stats = await verifyDb.TokenUsageStats.SingleAsync();
        Assert.AreEqual("2026-06", stats.YearMonth);
        Assert.AreEqual("deepseek", stats.ProviderId);
        Assert.AreEqual("deepseek-chat", stats.ModelId);
        Assert.AreEqual(1000, stats.PromptTokens);
        Assert.AreEqual(200, stats.CompletionTokens);
        Assert.AreEqual(600, stats.CacheHitTokens);
        Assert.AreEqual(400, stats.CacheMissTokens);
        Assert.AreEqual(1, stats.RequestCount);
        Assert.AreEqual(0.0006m, stats.TotalCost);
    }

    [TestMethod]
    public async Task RebuildAsync_SkipsTranscriptWhenUsageEventAlreadyExistsWithDifferentSourceId()
    {
        await using var scope = await CreateScopeAsync();
        var occurredAt = DateTimeOffset.Parse("2026-06-03T08:00:00Z");
        const string usageJson = "{\"promptTokens\":1000,\"completionTokens\":200,\"totalTokens\":1200,\"contextWindowTokens\":1048576,\"promptCacheHitTokens\":600,\"promptCacheMissTokens\":400}";

        await using (var db = await scope.Factory.CreateDbContextAsync())
        {
            db.ChatMessages.Add(new ChatMessageEntity
            {
                Id = 42,
                SessionId = "s1",
                Role = "agent",
                Content = "ok",
                UsageJson = usageJson,
                CreatedAt = occurredAt.ToUnixTimeMilliseconds(),
            });
            db.TokenUsageEvents.AddRange(
                new TokenUsageEventEntity
                {
                    SourceType = "chat_message",
                    SourceId = "runtime-message-id",
                    WorkspaceId = "w1",
                    SessionId = "s1",
                    ProviderId = "default-openai",
                    ModelId = null,
                    OccurredAtUtc = occurredAt.AddMilliseconds(20),
                    YearMonth = "2026-06",
                    PromptTokens = 1000,
                    CompletionTokens = 200,
                    TotalTokens = 1200,
                    CacheHitTokens = 600,
                    CacheMissTokens = 400,
                    CacheEligibleTokens = 1000,
                    CacheHitRate = 0.6,
                    RawUsageJson = usageJson,
                    CreatedAtUtc = occurredAt,
                },
                new TokenUsageEventEntity
                {
                    SourceType = "chat_message",
                    SourceId = "42",
                    SessionId = "s1",
                    ProviderId = null,
                    ModelId = null,
                    OccurredAtUtc = occurredAt,
                    YearMonth = "2026-06",
                    PromptTokens = 1000,
                    CompletionTokens = 200,
                    TotalTokens = 1200,
                    CacheHitTokens = 600,
                    CacheMissTokens = 400,
                    CacheEligibleTokens = 1000,
                    CacheHitRate = 0.6,
                    RawUsageJson = usageJson,
                    CreatedAtUtc = occurredAt,
                });
            await db.SaveChangesAsync();
        }

        var service = new TokenUsageRebuildService(
            scope.Factory,
            new TokenUsageNormalizer(),
            null,
            NullLogger<TokenUsageRebuildService>.Instance);

        var result = await service.RebuildAsync("2026-06");

        await using var verifyDb = await scope.Factory.CreateDbContextAsync();
        Assert.AreEqual(0, result.EventsCreated);
        Assert.AreEqual(1, result.EventsDeleted);
        Assert.AreEqual(1, result.SkippedDuplicates);
        Assert.AreEqual(1, await verifyDb.TokenUsageEvents.CountAsync());

        var stats = await verifyDb.TokenUsageStats.SingleAsync();
        Assert.AreEqual(1, stats.RequestCount);
        Assert.AreEqual(1000, stats.PromptTokens);
        Assert.AreEqual(600, stats.CacheHitTokens);
    }

    [TestMethod]
    public async Task RebuildAsync_SkipsUsageJsonWithoutTokenValues()
    {
        await using var scope = await CreateScopeAsync();

        await using (var db = await scope.Factory.CreateDbContextAsync())
        {
            db.ChatMessages.Add(new ChatMessageEntity
            {
                Id = 42,
                SessionId = "s1",
                Role = "agent",
                Content = "ok",
                UsageJson = "{\"promptTokens\":null,\"completionTokens\":null,\"totalTokens\":null,\"contextWindowTokens\":1048576,\"promptCacheHitTokens\":null,\"promptCacheMissTokens\":null}",
                CreatedAt = DateTimeOffset.Parse("2026-06-03T08:00:00Z").ToUnixTimeMilliseconds(),
            });
            db.TokenUsageEvents.Add(new TokenUsageEventEntity
            {
                SourceType = "chat_message",
                SourceId = "42",
                SessionId = "s1",
                OccurredAtUtc = DateTimeOffset.Parse("2026-06-03T08:00:00Z"),
                YearMonth = "2026-06",
                PromptTokens = 0,
                CompletionTokens = 0,
                TotalTokens = 0,
                CacheHitTokens = 0,
                CacheMissTokens = 0,
                CacheEligibleTokens = 0,
                CacheHitRate = 0,
                CreatedAtUtc = DateTimeOffset.Parse("2026-06-03T08:00:00Z"),
            });
            await db.SaveChangesAsync();
        }

        var service = new TokenUsageRebuildService(
            scope.Factory,
            new TokenUsageNormalizer(),
            null,
            NullLogger<TokenUsageRebuildService>.Instance);

        var result = await service.RebuildAsync("2026-06");

        await using var verifyDb = await scope.Factory.CreateDbContextAsync();
        Assert.AreEqual(0, result.EventsCreated);
        Assert.AreEqual(1, result.EventsDeleted);
        Assert.AreEqual(0, await verifyDb.TokenUsageEvents.CountAsync());
        Assert.AreEqual(0, await verifyDb.TokenUsageStats.CountAsync());
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
        public TestDbContextFactory Factory { get; } = factory;

        public async ValueTask DisposeAsync()
        {
            await connection.DisposeAsync();
        }
    }
}
