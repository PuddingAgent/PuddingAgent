using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PuddingCode.Abstractions;
using PuddingCode.Models;
using PuddingCode.Platform;
using PuddingPlatform.Data;
using PuddingPlatform.Data.Entities;
using PuddingPlatform.Services;

namespace PuddingPlatformTests.Services;


[TestClass]
public sealed class AuditUsageConflictTests
{
    private static readonly TokenUsageDto BaseUsage = new() { PromptTokens=1000, CompletionTokens=500,
        TotalTokens=1500, PromptCacheHitTokens=400, PromptCacheMissTokens=600 };
    private static readonly DateTimeOffset At = new(2026,9,11,0,0,0,TimeSpan.Zero);
    [TestMethod]
    public async Task Gateway_ConflictingPayload_MustReject()
    {
        await using var harness = await CreateHarnessAsync();
        var p = harness.Provider;
        var recorder = new LlmGatewayUsageRecorder(p.GetRequiredService<IDbContextFactory<PlatformDbContext>>(),
            p.GetRequiredService<TokenUsageNormalizer>(), p.GetRequiredService<ILlmConfigService>(),
            NullLogger<LlmGatewayUsageRecorder>.Instance);
        await recorder.RecordRequiredAsync(BaseUsage,"gateway-conflict","chat","w","s","agent","prov-x","model-x",At);
        await Assert.ThrowsAsync<InvalidOperationException>(() => recorder.RecordRequiredAsync(
            BaseUsage with { CompletionTokens=9000, TotalTokens=10000 },"gateway-conflict","chat","w","s","agent","prov-x","model-x",At));
    }
    [TestMethod]
    public async Task Token_IdenticalUsageButDifferentWorkspace_MustReject()
    {
        await using var harness = await CreateHarnessAsync();
        var recorder = harness.Provider.GetRequiredService<TokenUsageRecorder>();
        await recorder.RecordRequiredAsync(BaseUsage,"audit","identity-conflict","w-a","s","prov-x","model-x",occurredAtUtc:At);
        await Assert.ThrowsAsync<InvalidOperationException>(() => recorder.RecordRequiredAsync(
            BaseUsage,"audit","identity-conflict","w-b","s","prov-x","model-x",occurredAtUtc:At));
    }
    [TestMethod]
    public async Task Token_UnrelatedUniqueConstraint_MustNotBeAcceptedAsDuplicateSource()
    {
        await using var harness = await CreateHarnessAsync();
        await using (var db = await harness.OpenDbContextAsync())
        {
            var table = db.Model.FindEntityType(typeof(TokenUsageEventEntity))!.GetTableName()!;
            await db.Database.ExecuteSqlRawAsync("CREATE TABLE audit_guard (value TEXT UNIQUE); INSERT INTO audit_guard VALUES ('sentinel');");
            var sql = "CREATE TRIGGER audit_unrelated_unique BEFORE INSERT ON [" + table + "] BEGIN INSERT INTO audit_guard VALUES ('sentinel'); END;";
            await db.Database.ExecuteSqlRawAsync(sql);
        }
        var recorder = harness.Provider.GetRequiredService<TokenUsageRecorder>();
        await Assert.ThrowsAsync<DbUpdateException>(() => recorder.RecordRequiredAsync(
            BaseUsage,"audit","unique-conflict","w","s","prov-x","model-x",occurredAtUtc:At));
    }
    private static async Task<TestHarness> CreateHarnessAsync()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "pudding-usage-concurrency-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var dbPath = Path.Combine(root, "platform.db");

        var services = new ServiceCollection();
        // Factory 注册：options/Factory 单例、context 每作用域新建；
        // 验证查询用 factory 自建实例，避免 root 作用域缓存实例被提前释放。
        services.AddDbContextFactory<PlatformDbContext>(options =>
            options.UseSqlite($"Data Source={dbPath}"));
        services.AddSingleton<TokenUsageNormalizer>();
        services.AddSingleton<ILlmConfigService>(new FixedLlmConfigService());
        services.AddSingleton<ILogger<TokenUsageRecorder>>(NullLogger<TokenUsageRecorder>.Instance);
        services.AddScoped<TokenUsageRecorder>();
        var provider = services.BuildServiceProvider();

        await using (var db = await provider
            .GetRequiredService<IDbContextFactory<PlatformDbContext>>()
            .CreateDbContextAsync())
        {
            await db.Database.EnsureCreatedAsync();
        }

        return new TestHarness(root, provider);
    }

    private sealed class TestHarness(string root, ServiceProvider provider) : IAsyncDisposable
    {
        public ServiceProvider Provider { get; } = provider;

        public Task<PlatformDbContext> OpenDbContextAsync()
            => Provider.GetRequiredService<IDbContextFactory<PlatformDbContext>>()
                .CreateDbContextAsync();

        public async ValueTask DisposeAsync()
        {
            await Provider.DisposeAsync();
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    /// <summary>固定价格的 ILlmConfigService 打桩：只提供价格匹配所需的最小面。</summary>
    private sealed class FixedLlmConfigService : ILlmConfigService
    {
        private static readonly LlmModelInfo Model = new()
        {
            ProviderId = "prov-x",
            ModelId = "model-x",
            InputPricePer1MTokens = 1m,
            OutputPricePer1MTokens = 2m,
            CacheHitPricePer1MTokens = 0.1m,
        };

        public IReadOnlyList<LlmProviderInfo> GetEnabledProviders() => [];
        public IReadOnlyList<LlmModelInfo> GetAllModels() => [Model];
        public LlmConfig? Resolve(string providerId, string modelId) => null;
        public LlmProfileInfo? ResolveProfile(string profileId) => null;
        public LlmConfig? GetMemoryConfig() => null;
        public LlmConfig? GetEmbeddingConfig() => null;
        public LlmProviderStrategy? GetProviderStrategy(string providerId) => null;
        public LlmProviderStrategy? GetModelStrategy(string providerId, string modelId) => null;
        public void Reload(object config) { }
    }
}
