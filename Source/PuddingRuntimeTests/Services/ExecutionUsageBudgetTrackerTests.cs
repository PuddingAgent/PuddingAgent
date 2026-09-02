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

    private static TokenUsageDto Usage(int prompt, int output, int cacheHit) => new()
    {
        PromptTokens = prompt,
        CompletionTokens = output,
        TotalTokens = prompt + output,
        PromptCacheHitTokens = cacheHit,
        PromptCacheMissTokens = prompt - cacheHit,
    };
}
