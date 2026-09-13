using PuddingCode.Models;
using PuddingCode.Platform;
using PuddingCode.Runtime;
using PuddingRuntime.Services;

namespace PuddingRuntimeTests.Services;

[TestClass]
public sealed class ExecutionUsageBudgetTrackerTests
{
    [TestMethod]
    public void Record_AccumulatesPerRoundUsageAndStopsAtInputBoundary()
    {
        var tracker = new ExecutionUsageBudgetTracker(new ExecutionUsageBudget
        {
            MaxInputTokens = 1_000,
            MaxOutputTokens = 500,
            MaxCost = 10m,
            PricingKnown = true,
            InputPricePer1MTokens = 2m,
            OutputPricePer1MTokens = 4m,
            CacheHitPricePer1MTokens = 0.2m,
        });

        var first = tracker.Record(Usage(prompt: 600, output: 50, cacheHit: 500));
        var second = tracker.Record(Usage(prompt: 400, output: 25, cacheHit: 300));

        Assert.IsFalse(first.ShouldStop);
        Assert.IsTrue(second.ShouldStop);
        Assert.AreEqual(TerminalErrorCodes.WorkUnitBudgetExhausted, second.ErrorCode);
        Assert.AreEqual(1_000L, second.InputTokens);
        Assert.AreEqual(75L, second.OutputTokens);
        Assert.AreEqual(800L, second.CacheHitTokens);
    }

    [TestMethod]
    public void Record_UsesCacheHitPriceAndStopsAtCostBoundary()
    {
        var tracker = new ExecutionUsageBudgetTracker(new ExecutionUsageBudget
        {
            MaxCost = 0.001m,
            PricingKnown = true,
            InputPricePer1MTokens = 10m,
            OutputPricePer1MTokens = 20m,
            CacheHitPricePer1MTokens = 1m,
        });

        var decision = tracker.Record(Usage(prompt: 1_000, output: 100, cacheHit: 900));

        Assert.IsTrue(decision.ShouldStop);
        Assert.AreEqual(0.0039m, decision.Cost);
    }

    [TestMethod]
    public void EvaluateBeforeRound_CostBudgetWithoutPricingFailsClosed()
    {
        var tracker = new ExecutionUsageBudgetTracker(new ExecutionUsageBudget
        {
            MaxCost = 1m,
            PricingKnown = false,
        });

        var decision = tracker.EvaluateBeforeRound();

        Assert.IsTrue(decision.ShouldStop);
        Assert.AreEqual(TerminalErrorCodes.WorkUnitPricingUnavailable, decision.ErrorCode);
    }

    [TestMethod]
    public void Record_MissingProviderUsageFailsClosedWhenBudgetIsActive()
    {
        var tracker = new ExecutionUsageBudgetTracker(new ExecutionUsageBudget
        {
            MaxInputTokens = 100,
            PricingKnown = true,
        });

        var decision = tracker.Record(null);

        Assert.IsTrue(decision.ShouldStop);
        Assert.AreEqual(TerminalErrorCodes.WorkUnitUsageUnavailable, decision.ErrorCode);
    }

    [TestMethod]
    public void RemainingBudget_IsReducedBeforeDelegatedExecution()
    {
        var tracker = new ExecutionUsageBudgetTracker(new ExecutionUsageBudget
        {
            MaxInputTokens = 1_000,
            MaxOutputTokens = 200,
            MaxCost = 1m,
            PricingKnown = true,
            InputPricePer1MTokens = 10m,
            OutputPricePer1MTokens = 20m,
            CacheHitPricePer1MTokens = 1m,
        });

        tracker.Record(Usage(prompt: 400, output: 50, cacheHit: 300));

        var remaining = tracker.CreateRemainingBudget();
        Assert.IsNotNull(remaining);
        Assert.AreEqual(600L, remaining.MaxInputTokens);
        Assert.AreEqual(150L, remaining.MaxOutputTokens);
        Assert.IsLessThan(1m, remaining.MaxCost);
    }

    [TestMethod]
    public void UsageSnapshot_AggregatesAllDelegatedRounds()
    {
        var tracker = new ExecutionUsageBudgetTracker(new ExecutionUsageBudget
        {
            MaxInputTokens = 10_000,
            PricingKnown = true,
        });

        tracker.Record(Usage(prompt: 600, output: 50, cacheHit: 500));
        tracker.Record(Usage(prompt: 700, output: 25, cacheHit: 650));

        var usage = tracker.CreateUsageSnapshot();
        Assert.IsNotNull(usage);
        Assert.AreEqual(1_300, usage.PromptTokens);
        Assert.AreEqual(75, usage.CompletionTokens);
        Assert.AreEqual(1_150, usage.PromptCacheHitTokens);
        Assert.AreEqual(150, usage.PromptCacheMissTokens);
    }

    [TestMethod]
    public void CreateRemainingBudget_ExhaustedAxisReportsHonestZeroWithDerivedMarker()
    {
        // 原子片2：剩余值必须诚实归零——轴耗尽后剩余为 0 且带 IsDerivedRemainder，
        // 不再 Math.Max(1, …) 伪造非零（那会派发首轮即死的子代理）。
        var tracker = new ExecutionUsageBudgetTracker(new ExecutionUsageBudget
        {
            MaxInputTokens = 500,
            MaxOutputTokens = 200,
            MaxCost = 1m,
            PricingKnown = true,
            InputPricePer1MTokens = 10m,
            OutputPricePer1MTokens = 20m,
            CacheHitPricePer1MTokens = 1m,
        });

        var decision = tracker.Record(Usage(prompt: 600, output: 50, cacheHit: 300));
        Assert.IsTrue(decision.ShouldStop); // 600 >= 500，输入轴已耗尽

        var remaining = tracker.CreateRemainingBudget();
        Assert.IsNotNull(remaining);
        Assert.IsTrue(remaining.IsDerivedRemainder);
        Assert.AreEqual(0L, remaining.MaxInputTokens); // 诚实归零，不是 1
        Assert.AreEqual(150L, remaining.MaxOutputTokens);
    }

    [TestMethod]
    public void CreateRemainingBudget_TracksPeakRoundInputTokensAndDerivedMarker()
    {
        var tracker = new ExecutionUsageBudgetTracker(new ExecutionUsageBudget
        {
            MaxInputTokens = 100_000,
            PricingKnown = true,
        });

        tracker.Record(Usage(prompt: 12_000, output: 100, cacheHit: 2_000));
        tracker.Record(Usage(prompt: 24_473, output: 100, cacheHit: 3_000)); // 单轮峰值（sub-feca2176 实测）
        tracker.Record(Usage(prompt: 18_000, output: 100, cacheHit: 1_000));

        var remaining = tracker.CreateRemainingBudget();
        Assert.IsNotNull(remaining);
        Assert.IsTrue(remaining.IsDerivedRemainder);
        Assert.AreEqual(24_473L, remaining.PeakRoundInputTokens); // 取 max，不是末轮
        Assert.AreEqual(100_000L - 54_473L, remaining.MaxInputTokens);
    }

    private static TokenUsageDto Usage(int prompt, int output, int cacheHit) => new()
    {
        PromptTokens = prompt,
        CompletionTokens = output,
        TotalTokens = prompt + output,
        PromptCacheHitTokens = cacheHit,
        PromptCacheMissTokens = prompt - cacheHit,
    };
}
