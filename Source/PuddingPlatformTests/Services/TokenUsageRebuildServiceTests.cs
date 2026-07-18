using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using PuddingCode.Platform;
using PuddingPlatform.Data;
using PuddingPlatform.Data.Entities;
using PuddingPlatform.Services;

namespace PuddingPlatformTests.Services;

[TestClass]
public sealed class TokenUsageRebuildServiceTests
{
    [TestMethod]
    public async Task RebuildAsync_RebuildsMonthlyStatsFromNonConversationUsageEvents()
    {
        await using var scope = await CreateScopeAsync();
        await using (var db = await scope.Factory.CreateDbContextAsync())
        {
            db.TokenUsageEvents.Add(CreateLedgerEvent(
                sourceType: "subconscious_memory",
                sourceId: "m1",
                providerId: "deepseek",
                modelId: "deepseek-chat"));
            await db.SaveChangesAsync();
        }

        var result = await CreateService(scope).RebuildAsync("2026-06");

        await using var verifyDb = await scope.Factory.CreateDbContextAsync();
        var stats = await verifyDb.TokenUsageStats.SingleAsync();
        Assert.AreEqual(0, result.EventsDeleted);
        Assert.AreEqual("2026-06", stats.YearMonth);
        Assert.AreEqual("deepseek", stats.ProviderId);
        Assert.AreEqual("deepseek-chat", stats.ModelId);
        Assert.AreEqual(1000, stats.PromptTokens);
        Assert.AreEqual(200, stats.CompletionTokens);
        Assert.AreEqual(1, stats.RequestCount);
    }

    [TestMethod]
    public async Task RebuildAsync_ProjectsAttributedConversationUsageAndIsIdempotent()
    {
        await using var scope = await CreateScopeAsync();
        await using (var db = await scope.Factory.CreateDbContextAsync())
        {
            db.ConversationEvents.Add(CreateConversationUsageEvent(
                eventId: "usage-event-1",
                payload: """
                    {
                      "usage": {
                        "promptTokens": 1000,
                        "completionTokens": 200,
                        "totalTokens": 1200,
                        "promptCacheHitTokens": 600,
                        "promptCacheMissTokens": 400
                      },
                      "providerId": "moonshot",
                      "profileId": "agent:a1:conscious",
                      "modelId": "kimi-k3",
                      "role": "conscious",
                      "invocationIndex": 1
                    }
                    """));
            await db.SaveChangesAsync();
        }

        var service = CreateService(scope);
        var first = await service.RebuildAsync("2026-06");
        var second = await service.RebuildAsync("2026-06");

        await using var verifyDb = await scope.Factory.CreateDbContextAsync();
        var usageEvent = await verifyDb.TokenUsageEvents.SingleAsync();
        var stats = await verifyDb.TokenUsageStats.SingleAsync();

        Assert.AreEqual(1, first.EventsCreated);
        Assert.AreEqual(1, second.EventsDeleted);
        Assert.AreEqual(1, second.EventsCreated);
        Assert.AreEqual("agent_llm", usageEvent.SourceType);
        Assert.AreEqual("usage-event-1", usageEvent.SourceId);
        Assert.AreEqual("moonshot", usageEvent.ProviderId);
        Assert.AreEqual("kimi-k3", usageEvent.ModelId);
        Assert.AreEqual(1000, stats.PromptTokens);
        Assert.AreEqual(600, stats.CacheHitTokens);
        Assert.AreEqual(1, stats.RequestCount);
    }

    [TestMethod]
    public async Task RebuildAsync_SkipsUnattributedLegacyUsageWithoutDeletingExistingLedgerFacts()
    {
        await using var scope = await CreateScopeAsync();
        await using (var db = await scope.Factory.CreateDbContextAsync())
        {
            db.ConversationEvents.Add(CreateConversationUsageEvent(
                eventId: "legacy-usage",
                schemaVersion: 1,
                payload: """
                    {
                      "promptTokens": 1000,
                      "completionTokens": 200,
                      "totalTokens": 1200,
                      "promptCacheHitTokens": 600,
                      "promptCacheMissTokens": 400
                    }
                    """));
            db.TokenUsageEvents.Add(CreateLedgerEvent(
                sourceType: "chat_message",
                sourceId: "guessed-row",
                providerId: "default-provider",
                modelId: "default-model"));
            await db.SaveChangesAsync();
        }

        var result = await CreateService(scope).RebuildAsync("2026-06");

        await using var verifyDb = await scope.Factory.CreateDbContextAsync();
        Assert.AreEqual(0, result.EventsDeleted);
        Assert.AreEqual(1, result.UnattributedEventsSkipped);
        Assert.AreEqual(0, result.EventsCreated);
        Assert.AreEqual(1, await verifyDb.TokenUsageEvents.CountAsync());
        Assert.AreEqual(1, await verifyDb.TokenUsageStats.CountAsync());
    }

    private static TokenUsageRebuildService CreateService(TestScope scope)
        => new(
            scope.Factory,
            new TokenUsageNormalizer(),
            null,
            NullLogger<TokenUsageRebuildService>.Instance);

    private static ConversationEventEntity CreateConversationUsageEvent(
        string eventId,
        string payload,
        int schemaVersion = 2)
        => new()
        {
            ConversationId = "conversation-1",
            Sequence = 1,
            EventId = eventId,
            WorkspaceId = "workspace-1",
            TurnId = "turn-1",
            CommandId = "command-1",
            RunId = "run-1",
            MessageId = "message-1",
            Type = ConversationEventTypes.UsageRecorded,
            SchemaVersion = schemaVersion,
            Payload = payload,
            OccurredAt = "2026-06-03T08:00:00+00:00",
            CommittedAt = "2026-06-03T08:00:01+00:00",
        };

    private static TokenUsageEventEntity CreateLedgerEvent(
        string sourceType,
        string sourceId,
        string providerId,
        string modelId)
        => new()
        {
            SourceType = sourceType,
            SourceId = sourceId,
            WorkspaceId = "w1",
            SessionId = "s1",
            ProviderId = providerId,
            ModelId = modelId,
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

        public Task<PlatformDbContext> CreateDbContextAsync(
            CancellationToken cancellationToken = default)
            => Task.FromResult(CreateDbContext());
    }

    private sealed class TestScope(
        SqliteConnection connection,
        TestDbContextFactory factory) : IAsyncDisposable
    {
        public TestDbContextFactory Factory { get; } = factory;

        public async ValueTask DisposeAsync()
        {
            await connection.DisposeAsync();
        }
    }
}
