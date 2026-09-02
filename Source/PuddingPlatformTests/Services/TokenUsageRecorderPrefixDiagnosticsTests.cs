using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using PuddingCode.Abstractions;
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
    public void ConversationProjectorFallback_UsesPersistentParentAndInvocationIndex()
    {
        var attributed = ConversationProjector.CreateFallbackAttribution(
            "session-child",
            "session-parent",
            invocationIndex: 3);
        var unknown = ConversationProjector.CreateFallbackAttribution(
            "session-main",
            parentSessionId: null,
            invocationIndex: 0);

        Assert.AreEqual("session-parent", attributed.ParentSessionId);
        Assert.AreEqual("session-child", attributed.SubAgentId);
        Assert.AreEqual(2, attributed.TurnRound);
        Assert.IsNull(attributed.ToolCallCount);
        Assert.IsNull(unknown.ParentSessionId);
        Assert.IsNull(unknown.SubAgentId);
        Assert.IsNull(unknown.TurnRound);
    }

    [TestMethod]
    public async Task ConversationProjectorFingerprint_WithCompletionTokens_TranslatesOnSqlite()
    {
        await using var scope = await CreateScopeAsync();
        var db = scope.Provider.GetRequiredService<PlatformDbContext>();
        var occurredAt = DateTimeOffset.Parse("2026-08-29T01:08:54Z");
        db.TokenUsageEvents.Add(new PuddingPlatform.Data.Entities.TokenUsageEventEntity
        {
            SourceType = "agent_llm",
            SourceId = "session-1:trace-1:1",
            WorkspaceId = "default",
            SessionId = "session-1",
            ProviderId = "bigmodel",
            ModelId = "glm-5.3-flash",
            OccurredAtUtc = occurredAt,
            YearMonth = "2026-08",
            PromptTokens = 38994,
            CompletionTokens = 4096,
            TotalTokens = 43090,
        });
        await db.SaveChangesAsync();

        var exists = await ConversationProjector.ApplyDirectUsageFingerprintCandidates(
                db.TokenUsageEvents.AsNoTracking(),
                "session-1",
                "bigmodel",
                "glm-5.3-flash",
                new TokenUsageDto { PromptTokens = 38994, CompletionTokens = 4096 })
            .AnyAsync();

        Assert.IsTrue(exists);
    }

    [TestMethod]
    public async Task RecordAttributedRequiredAsync_PersistsCanonicalAgentLoopAttribution()
    {
        await using var scope = await CreateScopeAsync();
        var recorder = new TokenUsageRecorder(
            scope.Provider.GetRequiredService<IServiceScopeFactory>(),
            new TokenUsageNormalizer(),
            NullLogger<TokenUsageRecorder>.Instance);

        await recorder.RecordAttributedRequiredAsync(
            new TokenUsageDto
            {
                PromptTokens = 100,
                CompletionTokens = 10,
                TotalTokens = 110,
                PromptCacheHitTokens = 80,
                PromptCacheMissTokens = 20,
            },
            sourceType: "agent_llm",
            sourceId: "run-child:trace-1:3",
            workspaceId: "w1",
            sessionId: "session-child",
            providerId: "deepseek",
            modelId: "deepseek-chat",
            attribution: new TokenUsageAttribution
            {
                ParentSessionId = "session-parent",
                SubAgentId = "session-child",
                TurnRound = 2,
                ToolCallCount = 3,
                ToolNames = ["search_grep", "shell", "search_grep"],
            });

        var db = scope.Provider.GetRequiredService<PlatformDbContext>();
        var saved = await db.TokenUsageEvents.SingleAsync();
        Assert.AreEqual("session-parent", saved.ParentSessionId);
        Assert.AreEqual("session-child", saved.SubAgentId);
        Assert.AreEqual(2, saved.TurnRound);
        Assert.AreEqual(3, saved.ToolCallCount);
        Assert.AreEqual("search_grep,shell", saved.ToolNames);
    }

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
    public async Task RecordAsync_WhenOnlyHistoryAnchorChanges_StoresHistoryAnchorChangedReason()
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
            PromptCacheHitTokens = 20,
            PromptCacheMissTokens = 80,
        };

        await recorder.RecordAsync(
            usage,
            "agent_llm",
            "history-m1",
            "w1",
            "history-session",
            "deepseek",
            "deepseek-chat",
            prefixSnapshot: CreateSnapshot("prefix-a", "system-a", "tool-a") with
            {
                HistoryAnchorHash = "anchor-a",
            });
        await recorder.RecordAsync(
            usage,
            "agent_llm",
            "history-m2",
            "w1",
            "history-session",
            "deepseek",
            "deepseek-chat",
            prefixSnapshot: CreateSnapshot("prefix-b", "system-a", "tool-a") with
            {
                HistoryAnchorHash = "anchor-b",
            });

        var db = scope.Provider.GetRequiredService<PlatformDbContext>();
        var second = await db.TokenUsageEvents.SingleAsync(e => e.SourceId == "history-m2");
        Assert.AreEqual(PrefixChangeReasons.HistoryAnchorChanged, second.PrefixChangeReason);
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
        Assert.IsGreaterThan(0L, layers[0].RawUtf8Bytes);
        Assert.IsGreaterThan(0L, layers[0].GzipBytes);
        Assert.IsNotNull(layers[0].GzipRatio);
        Assert.IsGreaterThanOrEqualTo(1.0, layers[0].GzipRatio!.Value);
    }

    [TestMethod]
    public async Task RecordAsync_WhenToolDefinitionsExist_InsertsOrderedLayerAndTracksSchemaChanges()
    {
        await using var scope = await CreateScopeAsync();
        var contextStore = scope.Provider.GetRequiredService<ContextAssemblyStore>();
        var usageStore = scope.Provider.GetRequiredService<ContextUsageSnapshotStore>();
        contextStore.Set(new ContextAssemblySnapshot
        {
            SessionId = "s-tools",
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
            contextAssemblyStore: contextStore,
            contextUsageSnapshotStore: usageStore);
        var messages = new[] { new ChatMessage(ChatRole.System, "You are Pudding.") };
        var usage = new TokenUsageDto
        {
            PromptTokens = 100,
            CompletionTokens = 10,
            TotalTokens = 110,
            PromptCacheHitTokens = 70,
            PromptCacheMissTokens = 30,
        };

        usageStore.CaptureLlmRequest("s-tools", messages, [CreateTool("lookup", "query")]);
        await recorder.RecordAsync(
            usage,
            sourceType: "chat_message",
            sourceId: "tools-m1",
            workspaceId: "w1",
            sessionId: "s-tools",
            providerId: "deepseek",
            modelId: "deepseek-chat",
            occurredAtUtc: DateTimeOffset.Parse("2026-06-06T00:01:00Z"));

        usageStore.CaptureLlmRequest("s-tools", messages, [CreateTool("lookup", "path")]);
        await recorder.RecordAsync(
            usage,
            sourceType: "chat_message",
            sourceId: "tools-m2",
            workspaceId: "w1",
            sessionId: "s-tools",
            providerId: "deepseek",
            modelId: "deepseek-chat",
            occurredAtUtc: DateTimeOffset.Parse("2026-06-06T00:02:00Z"));

        var db = scope.Provider.GetRequiredService<PlatformDbContext>();
        var firstLayers = await db.ContextLayerMetricEvents
            .Where(e => e.SourceId == "tools-m1")
            .OrderBy(e => e.LayerOrder)
            .ToListAsync();
        Assert.HasCount(3, firstLayers);
        CollectionAssert.AreEqual(
            new[] { "L0-STATIC", "L1-TOOL-DEFINITIONS", "L5-RECENT" },
            firstLayers.Select(layer => layer.LayerName).ToArray());
        for (var i = 1; i < firstLayers.Count; i++)
        {
            Assert.AreEqual(firstLayers[i - 1].EndsAtToken, firstLayers[i].StartsAtToken);
        }

        var firstToolLayer = firstLayers[1];
        Assert.AreEqual("stable_prefix", firstToolLayer.LayerRole);
        var secondToolLayer = await db.ContextLayerMetricEvents.SingleAsync(
            e => e.SourceId == "tools-m2" && e.LayerName == "L1-TOOL-DEFINITIONS");
        Assert.AreEqual(firstToolLayer.ContentHash, secondToolLayer.PreviousHash);
        Assert.IsTrue(secondToolLayer.IsChanged);
        Assert.AreEqual("tool_spec_changed", secondToolLayer.ChangeReason);
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

    private static LlmToolDefinition CreateTool(string name, string parameterName) => new()
    {
        Name = name,
        Description = $"Tool {name}",
        Parameters = new ToolParameterSchema(
            [new ToolParameter(parameterName, "string", $"Tool {parameterName}")],
            [parameterName]),
    };

    private static async Task<TestScope> CreateScopeAsync()
    {
        var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();
        var services = new ServiceCollection();
        services.AddDbContext<PlatformDbContext>(options => options.UseSqlite(connection));
        services.AddSingleton<ContextAssemblyStore>();
        services.AddSingleton<ContextUsageSnapshotStore>();
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
