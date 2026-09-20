using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PuddingPlatform.Data;
using PuddingPlatform.Data.Entities;
using PuddingPlatform.Services;

namespace PuddingPlatformTests.Services;

[TestClass]
public sealed class GoodputAttributionServiceTests
{
    private const string TraceId = "e01cde260def42d4bf2b62dddc7ac756";

    [TestMethod]
    public void Parse_ThreeSegmentSourceId_ExtractsTraceIdAndRound()
    {
        var key = UsageAttribution.Parse($"sess-1:{TraceId}:9");

        Assert.IsTrue(key.Parsed);
        Assert.AreEqual("sess-1", key.SessionId);
        Assert.AreEqual(TraceId, key.TraceId);
        Assert.AreEqual(9, key.Round);
        Assert.IsNull(key.ReasonCode);
    }

    [TestMethod]
    public void Parse_SourceIdWithPurposeSuffix_ExtractsTraceIdAndRound()
    {
        var key = UsageAttribution.Parse("sess-1:run-identity:4:warm-prefix");

        Assert.IsTrue(key.Parsed);
        Assert.AreEqual("run-identity", key.TraceId);
        Assert.AreEqual(4, key.Round);
    }

    [TestMethod]
    public void Parse_TooFewSegments_ReturnsUnparsedReasonCode()
    {
        var key = UsageAttribution.Parse("mem:0f8c9b1d4e5a4b2c8d3e6f7a8b9c0d1e");

        Assert.IsFalse(key.Parsed);
        Assert.AreEqual(UsageAttribution.UnparsedReasonCode, key.ReasonCode);
        Assert.AreEqual(string.Empty, key.TraceId);
    }

    [TestMethod]
    public void Parse_NonNumericRound_KeepsTraceIdButStaysUnparsed()
    {
        var key = UsageAttribution.Parse($"sess-1:{TraceId}:settled");

        Assert.IsFalse(key.Parsed, "无法确定轮次时不得视为已归因");
        Assert.AreEqual(UsageAttribution.UnparsedReasonCode, key.ReasonCode);
        Assert.AreEqual(TraceId, key.TraceId);
    }

    [TestMethod]
    public void Parse_BlankSourceId_FallsBackToSessionIdButStaysUnparsed()
    {
        var key = UsageAttribution.Parse("   ", "sess-fallback");

        Assert.IsFalse(key.Parsed);
        Assert.AreEqual("sess-fallback", key.SessionId);
        Assert.AreEqual(UsageAttribution.UnparsedReasonCode, key.ReasonCode);
    }

    [TestMethod]
    public void FromProfile_ZeroPriceProfile_IsFreeAndNotCountedAsSaving()
    {
        var classification = PricingClassifier.FromProfile("fastrouter", "gpt-6-astra", 0m, 0m);

        Assert.AreEqual(PricingStatusCodes.Free, classification.Status);
        Assert.IsFalse(classification.CountsAsSaving, "真实免费档不是节省");
    }

    [TestMethod]
    public void FromProfile_PositivePrice_IsPricedAndCountsAsSaving()
    {
        var classification = PricingClassifier.FromProfile("deepseek", "deepseek-flash", 2m, 8m);

        Assert.AreEqual(PricingStatusCodes.Priced, classification.Status);
        Assert.IsTrue(classification.CountsAsSaving);
    }

    [TestMethod]
    public void FromProfile_MissingModel_IsUnknownProviderAndNotSaving()
    {
        var classification = PricingClassifier.FromProfile("deepseek", null, 2m, 8m);

        Assert.AreEqual(PricingStatusCodes.UnknownProvider, classification.Status);
        Assert.IsFalse(classification.CountsAsSaving);
    }

    [TestMethod]
    public void Unpriced_MissingProfile_IsFlaggedAndNotCountedAsSaving()
    {
        var classification = PricingClassifier.Unpriced();

        Assert.AreEqual(PricingStatusCodes.Unpriced, classification.Status);
        Assert.IsFalse(classification.CountsAsSaving, "缺价格档案导致的 0 成本不得当节省");
    }

    [TestMethod]
    public async Task GetGoalReportAsync_AttributesByTraceId_AndRefusesSavingsClaimWhenZeroCostWithTokens()
    {
        await using var scope = await CreateScopeAsync();
        scope.Db.GoalIterations.Add(new GoalIterationEntity
        {
            GoalIterationId = "gi-1",
            GoalRunId = "tg-1",
            ActivationEpoch = 1,
            IterationNo = 1,
            Status = "settled",
            TraceId = TraceId,
            CreatedAtUtc = OccurredAt,
        });
        scope.Db.TokenUsageEvents.Add(CreateEvent($"sess-1:{TraceId}:1", cost: 1.5m, prompt: 100, completion: 50));
        scope.Db.TokenUsageEvents.Add(CreateEvent($"sess-1:{TraceId}:2", cost: 0m, prompt: 10, completion: 5));
        scope.Db.TokenUsageEvents.Add(CreateEvent("mem:0f8c9b1d4e5a4b2c8d3e6f7a8b9c0d1e", cost: 9m, prompt: 1, completion: 1));
        await scope.Db.SaveChangesAsync();

        var report = await new GoodputAttributionService(scope.Db).GetGoalReportAsync("tg-1");

        Assert.AreEqual(1, report.IterationCount);
        Assert.AreEqual(1, report.TraceCount);
        Assert.AreEqual(3, report.ScannedUsageRows);
        Assert.AreEqual(1, report.UnattributedScannedRows, "未解析行不得被计入任何 Goal 归因");
        Assert.AreEqual(1, report.Traces.Count);
        Assert.AreEqual(2, report.Totals.UsageRows);
        Assert.AreEqual(110, report.Totals.PromptTokens);
        Assert.AreEqual(55, report.Totals.CompletionTokens);
        Assert.AreEqual(1.5m, report.Totals.PricedCost);
        Assert.AreEqual(1, report.Totals.ZeroCostWithTokensRows);
        Assert.IsFalse(report.SavingsClaimable, "存在零成本非零 token 行时不得宣称节省");
        Assert.AreEqual(2, report.Traces[0].Rounds);
        CollectionAssert.Contains(report.Traces[0].ProviderModelIds.ToList(), "deepseek/deepseek-chat");
    }

    [TestMethod]
    public async Task GetGoalReportAsync_AllRowsPriced_AllowsSavingsClaim()
    {
        await using var scope = await CreateScopeAsync();
        scope.Db.GoalIterations.Add(new GoalIterationEntity
        {
            GoalIterationId = "gi-2",
            GoalRunId = "tg-2",
            ActivationEpoch = 1,
            IterationNo = 1,
            Status = "settled",
            TraceId = TraceId,
            CreatedAtUtc = OccurredAt,
        });
        scope.Db.TokenUsageEvents.Add(CreateEvent($"sess-2:{TraceId}:1", cost: 2m, prompt: 200, completion: 100));
        await scope.Db.SaveChangesAsync();

        var report = await new GoodputAttributionService(scope.Db).GetGoalReportAsync("tg-2");

        Assert.AreEqual(0, report.Totals.ZeroCostWithTokensRows);
        Assert.IsTrue(report.SavingsClaimable);
        Assert.AreEqual(2m, report.Totals.PricedCost);
        Assert.AreEqual(0, report.UnattributedScannedRows);
    }

    private static DateTimeOffset OccurredAt => DateTimeOffset.Parse("2026-09-20T00:00:00Z");

    private static TokenUsageEventEntity CreateEvent(string sourceId, decimal cost, long prompt, long completion)
    {
        var total = prompt + completion;
        return new TokenUsageEventEntity
        {
            SourceType = "agent_llm",
            SourceId = sourceId,
            WorkspaceId = "w1",
            SessionId = sourceId.Split(':')[0],
            ProviderId = "deepseek",
            ModelId = "deepseek-chat",
            OccurredAtUtc = OccurredAt,
            YearMonth = "2026-09",
            PromptTokens = prompt,
            CompletionTokens = completion,
            TotalTokens = total,
            CacheHitTokens = prompt / 2,
            CacheMissTokens = prompt - (prompt / 2),
            CacheEligibleTokens = prompt,
            CacheHitRate = prompt > 0 ? 0.5 : null,
            TotalCost = cost,
            CreatedAtUtc = OccurredAt,
        };
    }

    private static async Task<TestScope> CreateScopeAsync()
    {
        var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<PlatformDbContext>()
            .UseSqlite(connection)
            .Options;
        var db = new PlatformDbContext(options);
        await db.Database.EnsureCreatedAsync();
        return new TestScope(connection, db);
    }

    private sealed class TestScope(SqliteConnection connection, PlatformDbContext db) : IAsyncDisposable
    {
        public PlatformDbContext Db { get; } = db;

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await connection.DisposeAsync();
        }
    }
}
