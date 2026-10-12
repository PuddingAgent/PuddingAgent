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
    /// <summary>
    /// R1：主 usage 不得把「准备快照」改成 Provider Total（旧行为），也不得因 Provider 报数**小于**
    /// 估算就削弱出站硬门禁的保守校准。
    /// </summary>
    [TestMethod]
    public void ProviderUsage_DoesNotMislabelAnOverestimate_OrWeakenOutboundGuard()
    {
        var store = new ContextUsageSnapshotStore();
        var prepared = new ContextUsageSnapshot
        {
            SessionId = "s", UsedTokens = 204942, RawEstimatedTokens = 204942,
            MessageTokens = 160778, ToolDefinitionTokens = 44164, ModelId = "deepseek-flash",
            Source = "llm_request", Confidence = "estimated",
        };
        store.PublishPreparedRequest(
            prepared, LlmCallPurposes.Agent, invocationId: "s:trace:1:agent", attemptId: "s:trace:1:agent:a0");

        var returned = store.RecordProviderUsage("s", new TokenUsageDto
        {
            PromptTokens = 120653, CompletionTokens = 2663, TotalTokens = 123316,
        }, LlmCallPurposes.Agent, "s:trace:1:agent", "s:trace:1:agent:a0");

        // 准备快照保持原样：不得被 Provider Total 顶替，也不得丢掉分层归因。
        Assert.AreSame(prepared, returned, "主 usage 不得改写准备快照");
        Assert.IsTrue(store.TryGet("s", out var current));
        Assert.AreSame(prepared, current);
        Assert.AreEqual(204942, current!.UsedTokens);
        Assert.AreEqual(44164, current.ToolDefinitionTokens);

        // Provider 报数小于估算 ⇒ 校准保持 1.0（不得削弱硬门禁）。
        Assert.AreEqual(1.0, store.GetPromptCalibrationRatio("s", "deepseek-flash"));

        // 实测按身份配对，raw 来自它自己那次准备。
        Assert.IsTrue(store.TryGetLastMeasuredRequest("s", out var measured));
        Assert.AreEqual(120653, measured!.ProviderPromptTokens);
        Assert.AreEqual(204942, measured.RawEstimatedTokens);
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

    /// <summary>
    /// Total 缺失时按 prompt+completion 推导 —— R1 后该推导只进**实测槽位**，
    /// 不再改写准备快照（准备槽位由准备/冻结路径独占）。
    /// </summary>
    [TestMethod]
    [DataRow(null)]
    [DataRow(0)]
    public void MissingTotal_DerivesPromptPlusCompletionInMeasurementOnly(int? total)
    {
        var store = new ContextUsageSnapshotStore();
        var identity = "s:trace:1:agent";
        store.PublishPreparedRequest(
            new ContextUsageSnapshot
            {
                SessionId = "s", UsedTokens = 5000, RawEstimatedTokens = 5000, ModelId = "m",
                Source = "llm_request", Confidence = "estimated",
            },
            LlmCallPurposes.Agent,
            invocationId: identity,
            attemptId: identity + ":a0");

        store.RecordProviderUsage(
            "s",
            new TokenUsageDto { PromptTokens = 120000, CompletionTokens = 3000, TotalTokens = total },
            LlmCallPurposes.Agent,
            identity,
            identity + ":a0");

        Assert.IsTrue(store.TryGetLastMeasuredRequest("s", out var measured));
        Assert.AreEqual(120000, measured!.ProviderPromptTokens);
        Assert.AreEqual(123000, measured.ProviderTotalTokens, "Total 缺失时按 prompt+completion 推导");

        // 准备槽位不受影响
        Assert.IsTrue(store.TryGet("s", out var current));
        Assert.AreEqual(5000, current!.UsedTokens, "推导出的 Total 不得写进准备槽位");
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
