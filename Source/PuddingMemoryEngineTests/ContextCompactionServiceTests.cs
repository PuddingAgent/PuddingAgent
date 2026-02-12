using Microsoft.EntityFrameworkCore;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using PuddingCode.Models;
using PuddingCode.Platform;
using PuddingCode.Runtime;
using PuddingMemoryEngine.Data;
using PuddingMemoryEngine.Entities;
using PuddingRuntime.Services;

namespace PuddingMemoryEngineTests;

[TestClass]
public sealed class ContextCompactionServiceTests
{
    [TestMethod]
    public void ContextHealthEvaluator_ReturnsCriticalWhenUsageExceedsCriticalThreshold()
    {
        var evaluator = new ContextHealthEvaluator();

        var health = evaluator.Evaluate(
            sessionId: "session-1",
            usedTokens: 160_000,
            contextWindowTokens: 200_000,
            maxOutputTokens: 20_000);

        Assert.AreEqual(ContextHealthState.Critical, health.State);
        Assert.AreEqual(200_000, health.EffectiveWindowTokens);
        Assert.AreEqual(40_000, health.RemainingTokens);
        Assert.IsTrue(health.ShouldAutoCompact);
        Assert.IsFalse(health.ShouldBlockSend);
    }

    [TestMethod]
    public async Task GetHealthAsync_UsesLatestOutboundContextUsageSnapshot_WhenAvailable()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = CreateOptions(connection);
        await using var db = new MemoryDbContext(options);
        await db.Database.EnsureCreatedAsync();
        await SeedMessagesAsync(db, "session-usage", messageCount: 2);

        var usageStore = new ContextUsageSnapshotStore();
        usageStore.Set(new ContextUsageSnapshot
        {
            SessionId = "session-usage",
            UsedTokens = 90_000,
            Confidence = "estimated",
            Source = "llm_request",
            RecordedAt = DateTimeOffset.UtcNow,
        });
        var service = new ContextCompactionService(
            new TestMemoryDbContextFactory(options),
            new FixedSummaryGenerator("summary"),
            NullLogger<ContextCompactionService>.Instance,
            contextUsageSnapshotStore: usageStore);

        var health = await service.GetHealthAsync(
            "session-usage",
            contextWindowTokens: 130_000,
            maxOutputTokens: 20_000);

        Assert.AreEqual(90_000, health.UsedTokens);
        Assert.AreEqual(130_000, health.EffectiveWindowTokens);
        Assert.AreEqual(40_000, health.RemainingTokens);
        Assert.AreEqual(ContextHealthState.Warning, health.State);
        Assert.IsFalse(health.ShouldAutoCompact);
    }

    [TestMethod]
    public async Task GetHealthAsync_UsesProviderReportedUsage_WhenSnapshotIsUpdated()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = CreateOptions(connection);
        await using var db = new MemoryDbContext(options);
        await db.Database.EnsureCreatedAsync();
        await SeedMessagesAsync(db, "session-provider-usage", messageCount: 2);

        var usageStore = new ContextUsageSnapshotStore();
        usageStore.CaptureLlmRequest(
            "session-provider-usage",
            [new ChatMessage(ChatRole.User, "short local estimate")],
            tools: null);
        usageStore.RecordProviderUsage(
            "session-provider-usage",
            new TokenUsageDto
            {
                PromptTokens = 150_000,
                CompletionTokens = 25_000,
                TotalTokens = 175_000,
            });

        var service = new ContextCompactionService(
            new TestMemoryDbContextFactory(options),
            new FixedSummaryGenerator("summary"),
            NullLogger<ContextCompactionService>.Instance,
            contextUsageSnapshotStore: usageStore);

        var health = await service.GetHealthAsync(
            "session-provider-usage",
            contextWindowTokens: 200_000,
            maxOutputTokens: 20_000);

        Assert.AreEqual(175_000, health.UsedTokens);
        Assert.AreEqual(25_000, health.RemainingTokens);
        Assert.AreEqual("provider_usage", health.UsageSource);
        Assert.AreEqual("provider_reported", health.UsageConfidence);
        Assert.AreEqual(150_000, health.ProviderPromptTokens);
        Assert.AreEqual(175_000, health.ProviderTotalTokens);
        Assert.IsTrue(health.ShouldAutoCompact);
    }

    [TestMethod]
    public async Task FullCompactAsync_WritesSummaryAndMarksOnlyOlderMessages()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = CreateOptions(connection);
        await using var db = new MemoryDbContext(options);
        await db.Database.EnsureCreatedAsync();
        await SeedMessagesAsync(db, "session-1", messageCount: 10);

        var service = new ContextCompactionService(
            new TestMemoryDbContextFactory(options),
            new FixedSummaryGenerator("## 用户目标\n保留早期关键决策。"),
            NullLogger<ContextCompactionService>.Instance);

        var result = await service.CompactAsync(new ContextCompactionRequest(
            WorkspaceId: "workspace-1",
            SessionId: "session-1",
            AgentId: "agent-1",
            Mode: ContextCompactionMode.Manual,
            Level: ContextCompactionLevel.Full,
            Reason: "manual slash command"));

        Assert.AreEqual(ContextCompactionMode.Manual, result.Mode);
        Assert.AreEqual(ContextCompactionLevel.Full, result.Level);
        Assert.AreEqual(4, result.CompactedMessageCount);
        Assert.IsFalse(string.IsNullOrWhiteSpace(result.SummaryMessageId));

        db.ChangeTracker.Clear();
        var messages = await db.Messages
            .AsNoTracking()
            .OrderBy(m => m.Sequence)
            .ToListAsync();
        var summary = messages.Single(m => m.MessageId == result.SummaryMessageId);
        Assert.AreEqual("system", summary.Role);
        Assert.AreEqual("compact_summary", summary.ContentType);
        Assert.AreEqual("context_compaction", summary.Source);
        StringAssert.Contains(summary.Content, "保留早期关键决策");

        var compacted = messages
            .Where(m => m.CompactedBy == result.SummaryMessageId)
            .OrderBy(m => m.Sequence)
            .ToList();
        CollectionAssert.AreEqual(new long[] { 1, 2, 3, 4 }, compacted.Select(m => m.Sequence).ToArray());

        var retained = messages
            .Where(m => m.ContentType == "text" && m.CompactedBy is null)
            .OrderBy(m => m.Sequence)
            .Select(m => m.Sequence)
            .ToArray();
        CollectionAssert.AreEqual(new long[] { 5, 6, 7, 8, 9, 10 }, retained);
    }

    private static async Task SeedMessagesAsync(MemoryDbContext db, string sessionId, int messageCount)
    {
        db.Sessions.Add(new SessionEntity
        {
            SessionId = sessionId,
            WorkspaceId = "workspace-1",
            AgentId = "agent-1",
            Status = "Active",
            CreatedAt = 1,
            LastActivityAt = 1,
        });

        for (var i = 1; i <= messageCount; i++)
        {
            db.Messages.Add(new MessageEntity
            {
                MessageId = $"msg-{i}",
                SessionId = sessionId,
                Sequence = i,
                Role = i % 2 == 0 ? "agent" : "user",
                ContentType = "text",
                Content = $"message {i}",
                CreatedAt = i,
            });
        }

        await db.SaveChangesAsync();
    }

    private static DbContextOptions<MemoryDbContext> CreateOptions(SqliteConnection connection) =>
        new DbContextOptionsBuilder<MemoryDbContext>()
            .UseSqlite(connection)
            .Options;

    private sealed class TestMemoryDbContextFactory : IDbContextFactory<MemoryDbContext>
    {
        private readonly DbContextOptions<MemoryDbContext> _options;

        public TestMemoryDbContextFactory(DbContextOptions<MemoryDbContext> options)
        {
            _options = options;
        }

        public MemoryDbContext CreateDbContext() => new(_options);

        public Task<MemoryDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }

    private sealed class FixedSummaryGenerator : IContextCompactionSummaryGenerator
    {
        private readonly string _summary;

        public FixedSummaryGenerator(string summary)
        {
            _summary = summary;
        }

        public Task<string> GenerateSummaryAsync(
            ContextCompactionSummaryRequest request,
            CancellationToken ct = default) =>
            Task.FromResult(_summary);
    }
}
