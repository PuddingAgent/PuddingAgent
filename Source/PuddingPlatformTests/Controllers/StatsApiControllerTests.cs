using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using PuddingCode.Configuration;
using PuddingPlatform.Controllers.Api;
using PuddingPlatform.Data;
using PuddingPlatform.Data.Entities;
using PuddingPlatform.Services;

namespace PuddingPlatformTests.Controllers;

[TestClass]
public sealed class StatsApiControllerTests
{
    [TestMethod]
    public async Task GetMonthlyTokenStats_UsesRecordedEventCostBreakdown()
    {
        await using var scope = await CreateScopeAsync();
        await using (var db = await scope.Factory.CreateDbContextAsync())
        {
            db.TokenUsageStats.Add(new TokenUsageStatsEntity
            {
                YearMonth = "2026-06",
                ProviderId = "deepseek",
                ModelId = "shared-model",
                PromptTokens = 300,
                CompletionTokens = 300,
                CacheHitTokens = 100,
                CacheMissTokens = 200,
                RequestCount = 1,
            });
            db.TokenUsageEvents.Add(new TokenUsageEventEntity
            {
                SourceType = "chat_message",
                SourceId = "recorded-cost",
                ProviderId = "deepseek",
                ModelId = "shared-model",
                OccurredAtUtc = DateTimeOffset.Parse("2026-06-02T00:00:00Z"),
                YearMonth = "2026-06",
                PromptTokens = 300,
                CompletionTokens = 300,
                TotalTokens = 600,
                CacheHitTokens = 100,
                CacheMissTokens = 200,
                CacheEligibleTokens = 300,
                InputCost = 0.1m,
                CacheHitCost = 0.2m,
                OutputCost = 0.3m,
                TotalCost = 0.6m,
            });
            await db.SaveChangesAsync();
        }

        await using var controllerDb = await scope.Factory.CreateDbContextAsync();
        var controller = CreateController(scope, controllerDb, CreateScopedPriceConfig());

        var response = await controller.GetMonthlyTokenStats("2026-06", null, null, CancellationToken.None);

        var ok = response as OkObjectResult;
        Assert.IsNotNull(ok);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(
            ok.Value,
            new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        Assert.AreEqual(0.1m, json.RootElement.GetProperty("inputCost").GetDecimal());
        Assert.AreEqual(0.2m, json.RootElement.GetProperty("cacheHitCost").GetDecimal());
        Assert.AreEqual(0.3m, json.RootElement.GetProperty("outputCost").GetDecimal());
        Assert.AreEqual(0.6m, json.RootElement.GetProperty("totalCost").GetDecimal());
    }

    [TestMethod]
    public async Task GetMonthlyTokenStats_IncludesConfiguredZeroValueModelsInProviderGroups()
    {
        await using var scope = await CreateScopeAsync();
        await using (var db = await scope.Factory.CreateDbContextAsync())
        {
            db.TokenUsageStats.Add(new TokenUsageStatsEntity
            {
                YearMonth = "2026-06",
                ProviderId = "deepseek",
                ModelId = "deepseek-v4-flash",
                PromptTokens = 100,
                CompletionTokens = 20,
                CacheHitTokens = 40,
                CacheMissTokens = 60,
                RequestCount = 1,
            });
            await db.SaveChangesAsync();
        }

        await using var controllerDb = await scope.Factory.CreateDbContextAsync();
        var controller = CreateController(scope, controllerDb, CreateDeepSeekModelsConfig());

        var response = await controller.GetMonthlyTokenStats("2026-06", null, null, CancellationToken.None);

        var ok = response as OkObjectResult;
        Assert.IsNotNull(ok);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(
            ok.Value,
            new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        var deepseek = json.RootElement.GetProperty("byProvider").EnumerateArray()
            .Single(p => p.GetProperty("providerId").GetString() == "deepseek");
        var models = deepseek.GetProperty("models").EnumerateArray().ToList();

        Assert.IsTrue(models.Any(m => m.GetProperty("modelId").GetString() == "deepseek-v4-flash"));
        var pro = models.Single(m => m.GetProperty("modelId").GetString() == "deepseek-v4-pro");
        Assert.AreEqual(0, pro.GetProperty("requestCount").GetInt64());
        Assert.AreEqual(0, pro.GetProperty("promptTokens").GetInt64());
        Assert.AreEqual(0, pro.GetProperty("completionTokens").GetInt64());
    }

    [TestMethod]
    public async Task GetTokenStatsSeries_ReturnsZeroFilledMonthlyAndDailyTokenBreakdown()
    {
        await using var scope = await CreateScopeAsync();
        await using (var db = await scope.Factory.CreateDbContextAsync())
        {
            db.TokenUsageEvents.AddRange(
                new TokenUsageEventEntity
                {
                    SourceType = "chat_message",
                    SourceId = "january",
                    ProviderId = "deepseek",
                    ModelId = "deepseek-chat",
                    OccurredAtUtc = DateTimeOffset.Parse("2026-01-03T00:00:00Z"),
                    YearMonth = "2026-01",
                    PromptTokens = 15,
                    CompletionTokens = 3,
                    TotalTokens = 18,
                    CacheHitTokens = 10,
                    CacheMissTokens = 5,
                    CacheEligibleTokens = 15,
                    InputCost = 0.01m,
                    CacheHitCost = 0.02m,
                    OutputCost = 0.03m,
                    TotalCost = 0.06m,
                },
                new TokenUsageEventEntity
                {
                    SourceType = "chat_message",
                    SourceId = "june-a",
                    ProviderId = "deepseek",
                    ModelId = "deepseek-chat",
                    OccurredAtUtc = DateTimeOffset.Parse("2026-06-02T00:00:00Z"),
                    YearMonth = "2026-06",
                    PromptTokens = 120,
                    CompletionTokens = 30,
                    TotalTokens = 150,
                    CacheHitTokens = 80,
                    CacheMissTokens = 40,
                    CacheEligibleTokens = 120,
                    InputCost = 0.1m,
                    CacheHitCost = 0.2m,
                    OutputCost = 0.3m,
                    TotalCost = 0.6m,
                },
                new TokenUsageEventEntity
                {
                    SourceType = "chat_message",
                    SourceId = "june-b",
                    ProviderId = "deepseek",
                    ModelId = "deepseek-chat",
                    OccurredAtUtc = DateTimeOffset.Parse("2026-06-02T01:00:00Z"),
                    YearMonth = "2026-06",
                    PromptTokens = 60,
                    CompletionTokens = 10,
                    TotalTokens = 70,
                    CacheHitTokens = 20,
                    CacheMissTokens = 40,
                    CacheEligibleTokens = 60,
                    InputCost = 0.11m,
                    CacheHitCost = 0.22m,
                    OutputCost = 0.33m,
                    TotalCost = 0.66m,
                });
            await db.SaveChangesAsync();
        }

        await using var controllerDb = await scope.Factory.CreateDbContextAsync();
        var controller = CreateController(scope, controllerDb);

        var response = await controller.GetTokenStatsSeries("2026-06", "deepseek", null, CancellationToken.None);

        var ok = response as OkObjectResult;
        Assert.IsNotNull(ok);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(
            ok.Value,
            new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        var root = json.RootElement;
        Assert.AreEqual("2026-06", root.GetProperty("yearMonth").GetString());
        Assert.AreEqual(12, root.GetProperty("monthly").GetArrayLength());
        Assert.AreEqual(30, root.GetProperty("daily").GetArrayLength());

        var january = root.GetProperty("monthly")[0];
        Assert.AreEqual("2026-01", january.GetProperty("period").GetString());
        Assert.AreEqual(5, january.GetProperty("cacheMissTokens").GetInt64());
        Assert.AreEqual(10, january.GetProperty("cacheHitTokens").GetInt64());
        Assert.AreEqual(3, january.GetProperty("completionTokens").GetInt64());
        Assert.AreEqual(0.01m, january.GetProperty("inputCost").GetDecimal());
        Assert.AreEqual(0.02m, january.GetProperty("cacheHitCost").GetDecimal());
        Assert.AreEqual(0.03m, january.GetProperty("outputCost").GetDecimal());
        Assert.AreEqual(0.06m, january.GetProperty("totalCost").GetDecimal());

        var juneSecond = root.GetProperty("daily")[1];
        Assert.AreEqual("2026-06-02", juneSecond.GetProperty("period").GetString());
        Assert.AreEqual(80, juneSecond.GetProperty("cacheMissTokens").GetInt64());
        Assert.AreEqual(100, juneSecond.GetProperty("cacheHitTokens").GetInt64());
        Assert.AreEqual(40, juneSecond.GetProperty("completionTokens").GetInt64());
        Assert.AreEqual(2, juneSecond.GetProperty("requestCount").GetInt64());
        Assert.AreEqual(0.21m, juneSecond.GetProperty("inputCost").GetDecimal());
        Assert.AreEqual(0.42m, juneSecond.GetProperty("cacheHitCost").GetDecimal());
        Assert.AreEqual(0.63m, juneSecond.GetProperty("outputCost").GetDecimal());
        Assert.AreEqual(1.26m, juneSecond.GetProperty("totalCost").GetDecimal());
    }

    [TestMethod]
    public async Task GetContextLayerTokenStats_ReturnsRatiosMedianAndVolatility()
    {
        await using var scope = await CreateScopeAsync();
        await using (var db = await scope.Factory.CreateDbContextAsync())
        {
            db.ContextLayerMetricEvents.AddRange(
                CreateLayer("m1", "L0-STATIC", order: 0, tokens: 40, hash: "static-a", hit: 40, miss: 0),
                CreateLayer("m1", "L5-RECENT", order: 1, tokens: 60, hash: "recent-a", hit: 30, miss: 30),
                CreateLayer("m2", "L0-STATIC", order: 0, tokens: 50, hash: "static-a", hit: 50, miss: 0, minutes: 1),
                CreateLayer("m2", "L5-RECENT", order: 1, tokens: 50, hash: "recent-b", hit: 0, miss: 50, minutes: 1, changed: true, reason: "history_changed"));
            await db.SaveChangesAsync();
        }

        await using var controllerDb = await scope.Factory.CreateDbContextAsync();
        var controller = CreateController(scope, controllerDb);

        var response = await controller.GetContextLayerTokenStats(
            from: "2026-06-06T00:00:00Z",
            to: "2026-06-06T01:00:00Z",
            providerId: "deepseek",
            modelId: null,
            sessionId: null,
            CancellationToken.None);

        var ok = response as OkObjectResult;
        Assert.IsNotNull(ok);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(
            ok.Value,
            new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        var layers = json.RootElement.GetProperty("layers");
        Assert.AreEqual(2, layers.GetArrayLength());

        var staticLayer = layers[0];
        Assert.AreEqual("L0-STATIC", staticLayer.GetProperty("layerName").GetString());
        Assert.AreEqual(90, staticLayer.GetProperty("tokenCount").GetInt64());
        Assert.AreEqual(0.45, staticLayer.GetProperty("tokenShare").GetDouble());
        Assert.AreEqual(45, staticLayer.GetProperty("medianTokens").GetDouble());
        Assert.AreEqual(1.0, staticLayer.GetProperty("medianCacheHitRate").GetDouble());
        Assert.AreEqual(0.0, staticLayer.GetProperty("changeRate").GetDouble());

        var recentLayer = layers[1];
        Assert.AreEqual("L5-RECENT", recentLayer.GetProperty("layerName").GetString());
        Assert.AreEqual(110, recentLayer.GetProperty("tokenCount").GetInt64());
        Assert.AreEqual(0.55, recentLayer.GetProperty("tokenShare").GetDouble());
        Assert.AreEqual(55, recentLayer.GetProperty("medianTokens").GetDouble());
        Assert.AreEqual(0.25, recentLayer.GetProperty("medianCacheHitRate").GetDouble());
        Assert.AreEqual(0.5, recentLayer.GetProperty("changeRate").GetDouble());
        Assert.AreEqual(80, recentLayer.GetProperty("estimatedMissTokens").GetInt64());
    }

    [TestMethod]
    public async Task GetContextLayerTokenStats_ReadsRuntimeSnakeCaseTable()
    {
        await using var scope = await CreateScopeWithoutSchemaAsync();
        await ExecuteSqlAsync(scope, """
            CREATE TABLE context_layer_metric_events (
                id                              INTEGER PRIMARY KEY AUTOINCREMENT,
                source_type                     TEXT    NOT NULL,
                source_id                       TEXT    NOT NULL,
                workspace_id                    TEXT,
                session_id                      TEXT,
                provider_id                     TEXT,
                model_id                        TEXT,
                occurred_at_utc                 TEXT    NOT NULL,
                assembler_version               TEXT    NOT NULL,
                layout_version                  TEXT    NOT NULL,
                layer_name                      TEXT    NOT NULL,
                layer_order                     INTEGER NOT NULL,
                layer_role                      TEXT    NOT NULL,
                token_count                     INTEGER NOT NULL DEFAULT 0,
                char_count                      INTEGER NOT NULL DEFAULT 0,
                content_hash                    TEXT    NOT NULL,
                previous_hash                   TEXT,
                is_changed                      INTEGER NOT NULL DEFAULT 0,
                change_reason                   TEXT,
                starts_at_token                 INTEGER NOT NULL DEFAULT 0,
                ends_at_token                   INTEGER NOT NULL DEFAULT 0,
                is_cache_eligible               INTEGER NOT NULL DEFAULT 1,
                estimated_cache_hit_tokens      INTEGER NOT NULL DEFAULT 0,
                estimated_cache_miss_tokens     INTEGER NOT NULL DEFAULT 0,
                estimated_cache_hit_rate        REAL,
                confidence                      TEXT    NOT NULL,
                truncated_tokens                INTEGER NOT NULL DEFAULT 0,
                truncated_reason                TEXT,
                created_at_utc                  TEXT    NOT NULL,
                UNIQUE(source_type, source_id, layer_name)
            );

            INSERT INTO context_layer_metric_events (
                source_type, source_id, workspace_id, session_id, provider_id, model_id,
                occurred_at_utc, assembler_version, layout_version, layer_name, layer_order,
                layer_role, token_count, char_count, content_hash, previous_hash, is_changed,
                change_reason, starts_at_token, ends_at_token, is_cache_eligible,
                estimated_cache_hit_tokens, estimated_cache_miss_tokens, estimated_cache_hit_rate,
                confidence, truncated_tokens, truncated_reason, created_at_utc
            )
            VALUES (
                'chat_message', 'm-runtime', 'w1', 's1', 'deepseek', 'deepseek-chat',
                '2026-06-06T00:00:00+00:00', 'context-v1', 'layer-v1', 'L0-STATIC', 0,
                'stable_prefix', 42, 168, 'hash-a', NULL, 0, NULL, 0, 42, 1,
                42, 0, 1.0, 'estimated', 0, NULL, '2026-06-06T00:00:01+00:00'
            );
            """);

        await using var controllerDb = await scope.Factory.CreateDbContextAsync();
        var controller = CreateController(scope, controllerDb, CreateScopedPriceConfig());

        var response = await controller.GetContextLayerTokenStats(
            from: "2026-06-06T00:00:00Z",
            to: "2026-06-06T01:00:00Z",
            providerId: "deepseek",
            modelId: null,
            sessionId: null,
            CancellationToken.None);

        var ok = response as OkObjectResult;
        Assert.IsNotNull(ok);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(
            ok.Value,
            new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        var layer = json.RootElement.GetProperty("layers")[0];
        Assert.AreEqual("L0-STATIC", layer.GetProperty("layerName").GetString());
        Assert.AreEqual(42, layer.GetProperty("tokenCount").GetInt64());
        Assert.AreEqual(1.0, layer.GetProperty("medianCacheHitRate").GetDouble());
    }

    private static StatsApiController CreateController(
        TestScope scope,
        PlatformDbContext db,
        PuddingLlmProvidersConfig? llmConfig = null)
        => new(
            db,
            new TokenUsageRebuildService(
                scope.Factory,
                new TokenUsageNormalizer(),
                llmConfig is null ? null : new PuddingFileLlmConfigService(llmConfig),
                NullLogger<TokenUsageRebuildService>.Instance),
            new PuddingFileLlmConfigService(llmConfig ?? new PuddingLlmProvidersConfig()));

    private static PuddingLlmProvidersConfig CreateScopedPriceConfig() => new()
    {
        Providers =
        [
            new PuddingLlmProviderConfig
            {
                ProviderId = "deepseek",
                Name = "DeepSeek",
                BaseUrl = "https://api.deepseek.com/v1",
                Models =
                [
                    new PuddingLlmModelConfig
                    {
                        ModelId = "shared-model",
                        Name = "DeepSeek shared",
                        PricePer1MInputTokens = 1m,
                        PricePer1MOutputTokens = 2m,
                        PricePer1MCacheHitTokens = 0.1m,
                    },
                ],
            },
            new PuddingLlmProviderConfig
            {
                ProviderId = "openai",
                Name = "OpenAI",
                BaseUrl = "https://api.openai.com/v1",
                Models =
                [
                    new PuddingLlmModelConfig
                    {
                        ModelId = "shared-model",
                        Name = "OpenAI shared",
                        PricePer1MInputTokens = 100m,
                        PricePer1MOutputTokens = 200m,
                        PricePer1MCacheHitTokens = 10m,
                    },
                ],
            },
        ],
    };

    private static PuddingLlmProvidersConfig CreateDeepSeekModelsConfig() => new()
    {
        Providers =
        [
            new PuddingLlmProviderConfig
            {
                ProviderId = "deepseek",
                Name = "DeepSeek",
                BaseUrl = "https://api.deepseek.com",
                Models =
                [
                    new PuddingLlmModelConfig
                    {
                        ModelId = "deepseek-v4-pro",
                        Name = "DeepSeek V4 Pro",
                        PricePer1MInputTokens = 0m,
                        PricePer1MOutputTokens = 0m,
                        PricePer1MCacheHitTokens = 0m,
                    },
                    new PuddingLlmModelConfig
                    {
                        ModelId = "deepseek-v4-flash",
                        Name = "DeepSeek V4 Flash",
                        PricePer1MInputTokens = 0m,
                        PricePer1MOutputTokens = 0m,
                        PricePer1MCacheHitTokens = 0m,
                    },
                ],
            },
        ],
    };

    private static ContextLayerMetricEventEntity CreateLayer(
        string sourceId,
        string layerName,
        int order,
        long tokens,
        string hash,
        long hit,
        long miss,
        int minutes = 0,
        bool changed = false,
        string? reason = null) => new()
        {
            SourceType = "chat_message",
            SourceId = sourceId,
            WorkspaceId = "w1",
            SessionId = "s1",
            ProviderId = "deepseek",
            ModelId = "deepseek-chat",
            OccurredAtUtc = DateTimeOffset.Parse("2026-06-06T00:00:00Z").AddMinutes(minutes),
            AssemblerVersion = "context-v1",
            LayoutVersion = "layer-v1",
            LayerName = layerName,
            LayerOrder = order,
            LayerRole = layerName.Contains("STATIC", StringComparison.OrdinalIgnoreCase) ? "stable_prefix" : "dynamic_history",
            TokenCount = tokens,
            CharCount = tokens * 4,
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

    private static async Task<TestScope> CreateScopeWithoutSchemaAsync()
    {
        var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<PlatformDbContext>()
            .UseSqlite(connection)
            .Options;
        var factory = new TestDbContextFactory(options);

        return new TestScope(connection, factory);
    }

    private static async Task ExecuteSqlAsync(TestScope scope, string sql)
    {
        await using var command = scope.Connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private sealed class TestDbContextFactory(DbContextOptions<PlatformDbContext> options)
        : IDbContextFactory<PlatformDbContext>
    {
        public PlatformDbContext CreateDbContext() => new(options);

        public Task<PlatformDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(CreateDbContext());
    }

    private sealed class TestScope : IAsyncDisposable
    {
        public TestScope(SqliteConnection connection, TestDbContextFactory factory)
        {
            Connection = connection;
            Factory = factory;
        }

        public SqliteConnection Connection { get; }

        public TestDbContextFactory Factory { get; }

        public async ValueTask DisposeAsync()
        {
            await Connection.DisposeAsync();
        }
    }
}
