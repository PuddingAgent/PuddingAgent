using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using PuddingCode.Models;
using PuddingCode.Platform;
using PuddingCode.Runtime;
using PuddingPlatform.Data;
using PuddingPlatform.Services;

namespace PuddingPlatformTests.Services;

[TestClass]
public sealed class TokenUsageRecorderPrefixDiagnosticsTests
{
    [TestMethod]
    public async Task RecordAsync_WhenToolSpecHashChanges_StoresToolSpecChangedReason()
    {
        await using var scope = await CreateScopeAsync();
        var recorder = new TokenUsageRecorder(
            scope.Provider.GetRequiredService<IServiceScopeFactory>(),
            new TokenUsageNormalizer(),
            NullLogger<TokenUsageRecorder>.Instance);
        var usage = new TokenUsageDto
        {
            PromptTokens = 100,
            CompletionTokens = 10,
            TotalTokens = 110,
            PromptCacheHitTokens = 80,
            PromptCacheMissTokens = 20,
        };

        await recorder.RecordAsync(
            usage,
            sourceType: "chat_message",
            sourceId: "m1",
            workspaceId: "w1",
            sessionId: "s1",
            providerId: "deepseek",
            modelId: "deepseek-chat",
            prefixSnapshot: CreateSnapshot("prefix-a", "system-a", "tool-a"),
            occurredAtUtc: DateTimeOffset.Parse("2026-05-25T00:00:00Z"));

        await recorder.RecordAsync(
            usage,
            sourceType: "chat_message",
            sourceId: "m2",
            workspaceId: "w1",
            sessionId: "s1",
            providerId: "deepseek",
            modelId: "deepseek-chat",
            prefixSnapshot: CreateSnapshot("prefix-b", "system-a", "tool-b"),
            occurredAtUtc: DateTimeOffset.Parse("2026-05-25T00:01:00Z"));

        var db = scope.Provider.GetRequiredService<PlatformDbContext>();
        var second = await db.TokenUsageEvents.SingleAsync(e => e.SourceId == "m2");

        Assert.AreEqual("tool_spec_changed", second.PrefixChangeReason);
    }

    [TestMethod]
    public async Task RecordAsync_WhenContextSnapshotExists_StoresContextLayerMetrics()
    {
        await using var scope = await CreateScopeAsync();
        var contextStore = scope.Provider.GetRequiredService<ContextAssemblyStore>();
        contextStore.Set(new ContextAssemblySnapshot
        {
            SessionId = "s-layer",
            AssembledAt = DateTimeOffset.Parse("2026-06-06T00:00:00Z"),
            TotalTokens = 100,
            Layers =
            [
                new ContextLayerInfo
                {
                    LayerName = "L0-STATIC",
                    TokenCount = 40,
                    ContentPreview = "stable system prompt",
                },
                new ContextLayerInfo
                {
                    LayerName = "L5-RECENT",
                    TokenCount = 60,
                    ContentPreview = "recent conversation",
                },
            ],
        });
        var recorder = new TokenUsageRecorder(
            scope.Provider.GetRequiredService<IServiceScopeFactory>(),
            new TokenUsageNormalizer(),
            NullLogger<TokenUsageRecorder>.Instance,
            contextAssemblyStore: contextStore);

        await recorder.RecordAsync(
            new TokenUsageDto
            {
                PromptTokens = 100,
                CompletionTokens = 10,
                TotalTokens = 110,
                PromptCacheHitTokens = 70,
                PromptCacheMissTokens = 30,
            },
            sourceType: "chat_message",
            sourceId: "layer-m1",
            workspaceId: "w1",
            sessionId: "s-layer",
            providerId: "deepseek",
            modelId: "deepseek-chat",
            prefixSnapshot: CreateSnapshot("prefix-a", "system-a", "tool-a"),
            occurredAtUtc: DateTimeOffset.Parse("2026-06-06T00:01:00Z"));

        var db = scope.Provider.GetRequiredService<PlatformDbContext>();
        var layers = await db.ContextLayerMetricEvents
            .OrderBy(e => e.LayerOrder)
            .ToListAsync();

        Assert.AreEqual(2, layers.Count);
        Assert.AreEqual("L0-STATIC", layers[0].LayerName);
        Assert.AreEqual(40, layers[0].TokenCount);
        Assert.AreEqual(0, layers[0].StartsAtToken);
        Assert.AreEqual(40, layers[0].EndsAtToken);
        Assert.IsTrue(layers[0].IsCacheEligible);
        Assert.AreEqual(40, layers[0].EstimatedCacheHitTokens);
        Assert.AreEqual(0, layers[0].EstimatedCacheMissTokens);
        Assert.AreEqual(1.0, layers[0].EstimatedCacheHitRate);
        Assert.AreEqual("estimated", layers[0].Confidence);
        Assert.AreEqual("L5-RECENT", layers[1].LayerName);
        Assert.AreEqual(30, layers[1].EstimatedCacheHitTokens);
        Assert.AreEqual(30, layers[1].EstimatedCacheMissTokens);
        Assert.AreEqual(0.5, layers[1].EstimatedCacheHitRate);
    }

    private static PromptPrefixSnapshot CreateSnapshot(
        string prefixHash,
        string systemPromptHash,
        string toolSpecHash) => new()
        {
            PrefixHash = prefixHash,
            SystemPromptHash = systemPromptHash,
            ToolSpecHash = toolSpecHash,
            MessageCount = 3,
            ToolCount = 2,
        };

    private static async Task<TestScope> CreateScopeAsync()
    {
        var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();
        var services = new ServiceCollection();
        services.AddDbContext<PlatformDbContext>(options => options.UseSqlite(connection));
        services.AddSingleton<ContextAssemblyStore>();
        var provider = services.BuildServiceProvider();
        var db = provider.GetRequiredService<PlatformDbContext>();
        await db.Database.EnsureCreatedAsync();
        return new TestScope(connection, provider);
    }

    private sealed class TestScope(SqliteConnection connection, ServiceProvider provider) : IAsyncDisposable
    {
        public ServiceProvider Provider { get; } = provider;

        public async ValueTask DisposeAsync()
        {
            await Provider.DisposeAsync();
            await connection.DisposeAsync();
        }
    }
}
