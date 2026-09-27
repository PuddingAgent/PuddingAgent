using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using PuddingCode.Configuration;
using PuddingPlatform.Data;
using PuddingPlatform.Data.Dtos;
using PuddingPlatform.Data.Entities;
using PuddingPlatform.Services;

namespace PuddingPlatformTests.Services;

/// <summary>
/// DS-02 配额：限额来自 llm.providers.json，用量来自 token 账本；reset-daily 只推进窗口起点，
/// 不改写账本，也不返回“保存成功但未生效”的假结果。
/// </summary>
[TestClass]
public sealed class LlmProviderQuotaServiceTests
{
    [TestMethod]
    public async Task QuotaLimitsPersistInProviderFile_AndUsageComesFromTheLedger()
    {
        await using var harness = await Harness.CreateAsync();
        await harness.AddUsageAsync("pool", prompt: 100, completion: 50, occurredAt: DateTimeOffset.UtcNow);
        await harness.AddUsageAsync("other-provider", prompt: 900, completion: 900, occurredAt: DateTimeOffset.UtcNow);

        var quota = await harness.Service.TryGetAsync("pool");
        Assert.IsNotNull(quota);
        Assert.IsNull(quota.DailyTokenLimit);
        Assert.IsNull(quota.MonthlyTokenLimit);
        Assert.AreEqual(150L, quota.DailyTokensUsed, "只统计该 provider 的账本行");
        Assert.AreEqual(150L, quota.MonthlyTokensUsed);
        Assert.IsFalse(quota.IsSuspended);

        var limited = await harness.Service.UpsertAsync("pool", new UpdateQuotaRequest(100, null));
        Assert.AreEqual(100L, limited.DailyTokenLimit);
        Assert.IsTrue(limited.IsSuspended, "已用 150 ≥ 日限额 100");
        Assert.IsNull(limited.MonthlyTokenLimit, "未提交的月限额保持为空而不是 0");

        var persisted = (await harness.Providers.LoadAsync()).Providers.Single().Quota;
        Assert.IsNotNull(persisted);
        Assert.AreEqual(100L, persisted.DailyTokenLimit);
        Assert.IsNull(persisted.MonthlyTokenLimit);

        var generous = await harness.Service.UpsertAsync("pool", new UpdateQuotaRequest(1000, 2000));
        Assert.AreEqual(1000L, generous.DailyTokenLimit);
        Assert.AreEqual(2000L, generous.MonthlyTokenLimit);
        Assert.IsFalse(generous.IsSuspended);

        Assert.IsNull(await harness.Service.TryGetAsync("missing-provider"));
        await Assert.ThrowsExactlyAsync<KeyNotFoundException>(
            () => harness.Service.UpsertAsync("missing-provider", new UpdateQuotaRequest(null, null)));
    }

    [TestMethod]
    public async Task QuotaLimitsAreRejectedInsteadOfWritten()
    {
        await using var harness = await Harness.CreateAsync();
        Assert.ThrowsExactly<ArgumentException>(() => LlmProviderQuotaService.Validate(new UpdateQuotaRequest(0, null)));
        Assert.ThrowsExactly<ArgumentException>(() => LlmProviderQuotaService.Validate(new UpdateQuotaRequest(-1, null)));
        Assert.ThrowsExactly<ArgumentException>(() => LlmProviderQuotaService.Validate(new UpdateQuotaRequest(null, 0)));
        Assert.ThrowsExactly<ArgumentException>(() => LlmProviderQuotaService.Validate(new UpdateQuotaRequest(5000, 1000)));
        LlmProviderQuotaService.Validate(new UpdateQuotaRequest(null, null));
        LlmProviderQuotaService.Validate(new UpdateQuotaRequest(1000, 1000));

        await Assert.ThrowsExactlyAsync<ArgumentException>(
            () => harness.Service.UpsertAsync("pool", new UpdateQuotaRequest(0, null)));
        Assert.IsNull((await harness.Providers.LoadAsync()).Providers.Single().Quota, "校验失败不得写入配置");
    }

    [TestMethod]
    public async Task ResetDailyMovesTheWindowWithoutTouchingTheLedger()
    {
        await using var harness = await Harness.CreateAsync();
        await harness.AddUsageAsync("pool", 100, 50, DateTimeOffset.UtcNow.AddMinutes(-5));
        Assert.AreEqual(150L, (await harness.Service.TryGetAsync("pool"))!.DailyTokensUsed);

        var reset = await harness.Service.ResetDailyAsync("pool");
        Assert.IsNotNull(reset.DailyResetAt, "重置必须记录新的窗口起点");
        Assert.AreEqual(0L, reset.DailyTokensUsed, "窗口起点之前的账本行不再计入");

        // 重置之后发生的调用照常计入：重置没有丢弃任何账本数据。
        await harness.AddUsageAsync("pool", 40, 10, DateTimeOffset.UtcNow.AddSeconds(2));
        var after = await harness.Service.TryGetAsync("pool");
        Assert.AreEqual(50L, after!.DailyTokensUsed);
        Assert.AreEqual(200L, after.MonthlyTokensUsed, "月窗口不受日重置影响");

        await using var verify = await harness.Factory.CreateDbContextAsync();
        Assert.AreEqual(2, await verify.TokenUsageEvents.CountAsync(), "重置不删除账本");
    }

    [TestMethod]
    public void WindowStartPrefersTheLaterOfNaturalPeriodAndResetMarker()
    {
        var natural = new DateTime(2026, 9, 27, 0, 0, 0, DateTimeKind.Utc);
        var now = new DateTimeOffset(2026, 9, 27, 12, 0, 0, TimeSpan.Zero);
        Assert.AreEqual(natural, LlmProviderQuotaService.WindowStart(natural, null, now));
        Assert.AreEqual(natural, LlmProviderQuotaService.WindowStart(natural, new DateTimeOffset(2026, 9, 26, 8, 0, 0, TimeSpan.Zero), now));
        Assert.AreEqual(natural, LlmProviderQuotaService.WindowStart(natural, new DateTimeOffset(natural, TimeSpan.Zero), now));
        var later = new DateTimeOffset(2026, 9, 27, 9, 30, 0, TimeSpan.Zero);
        Assert.AreEqual(later.UtcDateTime, LlmProviderQuotaService.WindowStart(natural, later, now));
        var future = new DateTimeOffset(2026, 9, 27, 18, 0, 0, TimeSpan.Zero);
        Assert.AreEqual(now.UtcDateTime, LlmProviderQuotaService.WindowStart(natural, future, now), "未来时间戳不得让用量变成负数");
    }

    private sealed class Harness(
        string root,
        SqliteConnection connection,
        TestDbContextFactory factory,
        LlmProviderFileService providers,
        LlmProviderQuotaService service) : IAsyncDisposable
    {
        public string Root => root;
        public TestDbContextFactory Factory => factory;
        public LlmProviderFileService Providers => providers;
        public LlmProviderQuotaService Service => service;

        public static async Task<Harness> CreateAsync()
        {
            var root = Path.Combine(Path.GetTempPath(), "pudding-quota-" + Guid.NewGuid().ToString("N"));
            var paths = PuddingDataPaths.FromRoot(root);
            await AtomicFileWriter.WriteJsonAsync(paths.SystemConfigFile("llm.providers.json"), new PuddingLlmProvidersConfig
            {
                Providers = [new() { ProviderId = "pool", Name = "Pool", BaseUrl = "https://example.invalid/v1" }]
            });

            var connection = new SqliteConnection("DataSource=:memory:");
            await connection.OpenAsync();
            var factory = new TestDbContextFactory(new DbContextOptionsBuilder<PlatformDbContext>()
                .UseSqlite(connection).Options);
            await using (var db = await factory.CreateDbContextAsync())
                await db.Database.EnsureCreatedAsync();

            var providers = new LlmProviderFileService(paths, NullLogger<LlmProviderFileService>.Instance);
            var usage = new TokenUsageDailyAggregateService(factory, NullLogger<TokenUsageDailyAggregateService>.Instance);
            return new Harness(root, connection, factory, providers, new LlmProviderQuotaService(providers, usage));
        }

        public async Task AddUsageAsync(string providerId, long prompt, long completion, DateTimeOffset occurredAt)
        {
            await using var db = await factory.CreateDbContextAsync();
            db.TokenUsageEvents.Add(new TokenUsageEventEntity
            {
                SourceType = "test",
                SourceId = Guid.NewGuid().ToString("N"),
                ProviderId = providerId,
                ModelId = "model",
                OccurredAtUtc = occurredAt,
                YearMonth = occurredAt.UtcDateTime.ToString("yyyy-MM"),
                PromptTokens = prompt,
                CompletionTokens = completion,
                TotalTokens = prompt + completion,
            });
            await db.SaveChangesAsync();
        }

        public async ValueTask DisposeAsync()
        {
            await connection.DisposeAsync();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    private sealed class TestDbContextFactory(DbContextOptions<PlatformDbContext> options)
        : IDbContextFactory<PlatformDbContext>
    {
        public PlatformDbContext CreateDbContext() => new(options);
        public Task<PlatformDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(CreateDbContext());
    }
}
