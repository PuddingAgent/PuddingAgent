using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using PuddingCode.Models;
using PuddingCode.Platform;
using PuddingCode.Runtime;
using PuddingMemoryEngine.Data;
using PuddingMemoryEngine.Entities;
using PuddingRuntime.Services;

namespace PuddingMemoryEngineTests;

[TestClass]
public sealed class ContextCompactionPressureTests
{
    [TestMethod]
    public void ProviderUsage_DoesNotMislabelAnOverestimate_OrWeakenOutboundGuard()
    {
        var store = new ContextUsageSnapshotStore();
        store.Set(new ContextUsageSnapshot
        {
            SessionId = "s", UsedTokens = 204942, RawEstimatedTokens = 204942,
            MessageTokens = 160778, ToolDefinitionTokens = 44164, ModelId = "deepseek-flash",
            Source = "llm_request", Confidence = "estimated",
        });
        var measured = store.RecordProviderUsage("s", new TokenUsageDto
        {
            PromptTokens = 120653, CompletionTokens = 2663, TotalTokens = 123316,
        });
        Assert.AreEqual(123316, measured.UsedTokens);
        Assert.AreEqual("provider_reported", measured.Confidence);
        Assert.AreEqual(204942, measured.RawEstimatedTokens);
        Assert.AreEqual(44164, measured.ToolDefinitionTokens);
        Assert.AreEqual(1.0, store.GetPromptCalibrationRatio("s", "deepseek-flash"));
    }

    [TestMethod]
    public void MissingUsage_PreservesEstimateAndItsConfidence()
    {
        var store = new ContextUsageSnapshotStore();
        var request = store.CaptureLlmRequest("s", [new ChatMessage(ChatRole.User, "content")], null);
        var measured = store.RecordProviderUsage("s", new TokenUsageDto());
        Assert.AreEqual(request.UsedTokens, measured.UsedTokens);
        Assert.AreSame(request, measured, "Missing usage must not refresh the age of an older snapshot.");
        Assert.AreEqual(request.Source, measured.Source);
        Assert.AreEqual("estimated", measured.Confidence);
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow(0)]
    public void MissingTotal_UsesPromptPlusCompletion(int? total)
    {
        var measured = new ContextUsageSnapshotStore().RecordProviderUsage("s",
            new TokenUsageDto { PromptTokens = 120000, CompletionTokens = 3000, TotalTokens = total });
        Assert.AreEqual(123000, measured.UsedTokens);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Health_RejectsUsageFromBeforeCheckpoint_InMemoryAndAfterRestart(bool restarted)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<MemoryDbContext>().UseSqlite(connection).Options;
        await using var db = new MemoryDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var boundary = DateTimeOffset.UtcNow;
        db.CompactionCoverageManifests.Add(new CompactionCoverageManifestEntity
        {
            SessionId = "s", FinalSummaryId = "summary", CreatedAtUtc = boundary.ToUnixTimeMilliseconds(),
        });
        await db.SaveChangesAsync();
        var store = new ContextUsageSnapshotStore();
        if (!restarted)
            store.Set(new ContextUsageSnapshot
            {
                SessionId = "s", UsedTokens = 600000, Source = "provider_usage",
                RecordedAt = boundary.AddSeconds(-1),
            });
        var service = new ContextCompactionService(new DbFactory(options), new UnusedSummary(),
            NullLogger<ContextCompactionService>.Instance, contextUsageSnapshotStore: store,
            tokenUsageRepo: new UsageRepository(boundary.AddSeconds(-1)));
        var health = await service.GetHealthAsync("s", contextWindowTokens: 1000000, maxOutputTokens: 384000);
        Assert.IsFalse(health.ShouldAutoCompact);
        Assert.AreEqual("active_session_messages", health.UsageSource);

        // A request measured in the new generation must still trigger when truly full.
        store.Set(new ContextUsageSnapshot
        {
            SessionId = "s", UsedTokens = 600000, Source = "llm_request",
            RecordedAt = boundary.AddSeconds(1),
        });
        var full = await service.GetHealthAsync("s", contextWindowTokens: 1000000, maxOutputTokens: 384000);
        Assert.IsTrue(full.ShouldAutoCompact);
        Assert.IsTrue(full.ShouldBlockSend);
    }

    [TestMethod]
    public async Task Health_CurrentRequestTakesPrecedenceOverPreviousDatabaseUsage()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<MemoryDbContext>().UseSqlite(connection).Options;
        await using var db = new MemoryDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var store = new ContextUsageSnapshotStore();
        store.CaptureLlmRequest("s", [new ChatMessage(ChatRole.User, "retained context")], null);
        var service = new ContextCompactionService(new DbFactory(options), new UnusedSummary(),
            NullLogger<ContextCompactionService>.Instance, contextUsageSnapshotStore: store,
            tokenUsageRepo: new UsageRepository(DateTimeOffset.UtcNow.AddMinutes(-1)));
        var health = await service.GetHealthAsync("s", contextWindowTokens: 1000000, maxOutputTokens: 384000);
        Assert.IsFalse(health.ShouldAutoCompact);
        Assert.AreEqual("llm_request", health.UsageSource);
    }

    private sealed class DbFactory(DbContextOptions<MemoryDbContext> options) : IDbContextFactory<MemoryDbContext>
    {
        public MemoryDbContext CreateDbContext() => new(options);
    }

    private sealed class UnusedSummary : IContextCompactionSummaryGenerator
    {
        public Task<string> GenerateSummaryAsync(ContextCompactionSummaryRequest request, CancellationToken ct = default)
            => throw new AssertFailedException("Health queries must not call the LLM.");
    }

    private sealed class UsageRepository(DateTimeOffset recordedAt) : ITokenUsageEventRepository
    {
        public Task<SessionTokenStats?> GetLatestStatsAsync(string sessionId, CancellationToken ct = default)
            => Task.FromResult<SessionTokenStats?>(new SessionTokenStats
            {
                PromptTokens = 599000, CompletionTokens = 1000, TotalTokens = 600000, OccurredAtUtc = recordedAt,
            });
        public Task<TokenUsageEventPage> GetFilteredAsync(string? workspaceId = null, string? sessionId = null,
            string? providerId = null, string? modelId = null, DateTimeOffset? from = null, DateTimeOffset? to = null,
            int page = 1, int pageSize = 50, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<SessionTokenDiagnostics?> GetLatestLayerDiagnosticsAsync(string sessionId, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task<SessionTokenDiagnostics?> GetLatestEntropyDiagnosticsAsync(string sessionId, CancellationToken ct = default)
            => throw new NotSupportedException();
    }
}
