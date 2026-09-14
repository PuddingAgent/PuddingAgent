using PuddingCode.Models;
using PuddingCode.Platform;
using PuddingCode.Runtime;
using PuddingRuntime.Services;

namespace PuddingRuntimeTests.Services;

[TestClass]
public sealed class ExecutionUsageBudgetTrackerTests
{
    [TestMethod]
    public void SixHundredRequests_KeepCumulativeLedgerWithoutExhaustingInputCapacity()
    {
        var tracker = new ExecutionUsageBudgetTracker(new ExecutionUsageBudget { MaxInputTokens = 1_000_000 });
        for (var round = 0; round < 600; round++)
        {
            Assert.IsFalse(tracker.EvaluateBeforeRound().ShouldStop);
            Assert.IsFalse(tracker.RecordInvocationUsage(Usage(80_000, 100, 70_000)).ShouldStop);
        }
        Assert.AreEqual(48_000_000L, tracker.InputTokens);
        Assert.AreEqual(80_000L, tracker.PeakRoundInputTokens);
        Assert.AreEqual(60_000L, tracker.OutputTokens);
        Assert.AreEqual(42_000_000L, tracker.CacheHitTokens);
        Assert.AreEqual(48_000_000, tracker.CreateUsageSnapshot()!.PromptTokens);
    }

    [TestMethod]
    [DataRow(1_000_000, false)]
    [DataRow(1_000_001, true)]
    public void InputCapacity_IncludesExactBoundary_AndAccountsRejectedRequest(int input, bool stops)
    {
        var tracker = new ExecutionUsageBudgetTracker(new ExecutionUsageBudget { MaxInputTokens = 1_000_000 });
        Assert.AreEqual(stops, tracker.RecordInvocationUsage(Usage(input, 100, 0)).ShouldStop);
        Assert.AreEqual(stops, tracker.EvaluateBeforeRound().ShouldStop);
        Assert.AreEqual((long)input, tracker.InputTokens);
        Assert.AreEqual((long)input, tracker.PeakRoundInputTokens);
    }

    [TestMethod]
    public void DelegatedAggregate_IsChargedOnce_WithoutBecomingOneOversizedRequest()
    {
        var tracker = new ExecutionUsageBudgetTracker(new ExecutionUsageBudget
        {
            MaxInputTokens = 100_000, MaxOutputTokens = 10_000, MaxCost = 10,
            PricingKnown = true, InputPricePer1MTokens = 2, OutputPricePer1MTokens = 4,
            CacheHitPricePer1MTokens = 0.2m,
        });
        tracker.RecordInvocationUsage(Usage(24_000, 100, 20_000));
        var result = tracker.RecordDelegatedUsage(Usage(1_790_000, 500, 1_700_000));
        Assert.IsFalse(result.ShouldStop);
        Assert.AreEqual(1_814_000L, tracker.InputTokens);
        Assert.AreEqual(24_000L, tracker.PeakRoundInputTokens);
        Assert.AreEqual(600L, tracker.OutputTokens);
        Assert.AreEqual(1_720_000L, tracker.CacheHitTokens);
        Assert.AreEqual(0.5344m, tracker.Cost);
        var remaining = tracker.CreateRemainingBudget()!;
        Assert.AreEqual(100_000L, remaining.MaxInputTokens);
        Assert.AreEqual(9_400L, remaining.MaxOutputTokens);
        Assert.AreEqual(9.4656m, remaining.MaxCost);
    }

    [TestMethod]
    public void UnsetCumulativeAxesRemainUnsetThroughDelegationAndSerialization()
    {
        var tracker = new ExecutionUsageBudgetTracker(new ExecutionUsageBudget { MaxInputTokens = 100_000 });
        tracker.RecordInvocationUsage(Usage(10_000, 50, 0));
        var remaining = tracker.CreateRemainingBudget();
        var divided = SubAgentInvocationService.DivideUsageBudget(remaining, 3)!;
        var copy = System.Text.Json.JsonSerializer.Deserialize<ExecutionUsageBudget>(
            System.Text.Json.JsonSerializer.Serialize(divided))!;
        Assert.IsFalse(copy.HasOutputLimit);
        Assert.IsFalse(copy.HasCostLimit);
        Assert.AreEqual(100_000L, copy.MaxInputTokens);
        Assert.IsNull(new SubAgentExecutionOptions().DescribeBudgetInfeasibility(copy));
        Assert.IsFalse(new ExecutionUsageBudgetTracker(copy).EvaluateBeforeRound().ShouldStop);
    }

    [TestMethod]
    public void CostExhaustedAtZeroRemainsLimitedThroughDelegationAndSerialization()
    {
        var tracker = new ExecutionUsageBudgetTracker(new ExecutionUsageBudget
        {
            MaxInputTokens = 100_000, MaxCost = 0.001m, PricingKnown = true, InputPricePer1MTokens = 1,
        });
        Assert.IsTrue(tracker.RecordInvocationUsage(Usage(1_000, 50, 0)).ShouldStop);
        var divided = SubAgentInvocationService.DivideUsageBudget(tracker.CreateRemainingBudget(), 2)!;
        var copy = System.Text.Json.JsonSerializer.Deserialize<ExecutionUsageBudget>(
            System.Text.Json.JsonSerializer.Serialize(divided))!;
        Assert.AreEqual(0m, copy.MaxCost);
        Assert.IsTrue(copy.HasCostLimit);
        Assert.IsNotNull(new SubAgentExecutionOptions().DescribeBudgetInfeasibility(copy));
        Assert.IsTrue(new ExecutionUsageBudgetTracker(copy).EvaluateBeforeRound().ShouldStop);
    }

    [TestMethod]
    public void MissingPromptUsageCannotVerifyInputCapacity()
    {
        var tracker = new ExecutionUsageBudgetTracker(new ExecutionUsageBudget { MaxInputTokens = 100_000 });
        var decision = tracker.RecordInvocationUsage(new TokenUsageDto { CompletionTokens = 5 });
        Assert.IsTrue(decision.ShouldStop);
        Assert.AreEqual(TerminalErrorCodes.WorkUnitUsageUnavailable, decision.ErrorCode);
    }

    [TestMethod]
    public void Record_AccumulatesInputWithoutConsumingPerRequestCapacity()
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

        var first = tracker.RecordInvocationUsage(Usage(prompt: 600, output: 50, cacheHit: 500));
        var second = tracker.RecordInvocationUsage(Usage(prompt: 400, output: 25, cacheHit: 300));

        Assert.IsFalse(first.ShouldStop);
        Assert.IsFalse(second.ShouldStop);
        Assert.AreEqual(1_000L, tracker.InputTokens);
        Assert.AreEqual(75L, tracker.OutputTokens);
        Assert.AreEqual(800L, tracker.CacheHitTokens);
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

        var decision = tracker.RecordInvocationUsage(Usage(prompt: 1_000, output: 100, cacheHit: 900));

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

        var decision = tracker.RecordInvocationUsage(null);

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

        tracker.RecordInvocationUsage(Usage(prompt: 400, output: 50, cacheHit: 300));

        var remaining = tracker.CreateRemainingBudget();
        Assert.IsNotNull(remaining);
        Assert.AreEqual(1_000L, remaining.MaxInputTokens);
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

        tracker.RecordInvocationUsage(Usage(prompt: 600, output: 50, cacheHit: 500));
        tracker.RecordInvocationUsage(Usage(prompt: 700, output: 25, cacheHit: 650));

        var usage = tracker.CreateUsageSnapshot();
        Assert.IsNotNull(usage);
        Assert.AreEqual(1_300, usage.PromptTokens);
        Assert.AreEqual(75, usage.CompletionTokens);
        Assert.AreEqual(1_150, usage.PromptCacheHitTokens);
        Assert.AreEqual(150, usage.PromptCacheMissTokens);
    }

    [TestMethod]
    public void CreateRemainingBudget_ExhaustedOutputRemainsLimitedAtZero()
    {
        // 输出耗尽仍受限；输入容量不随累计量扣减。
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

        var decision = tracker.RecordInvocationUsage(Usage(prompt: 600, output: 250, cacheHit: 300));
        Assert.IsTrue(decision.ShouldStop);

        var remaining = tracker.CreateRemainingBudget();
        Assert.IsNotNull(remaining);
        Assert.IsTrue(remaining.IsDerivedRemainder);
        Assert.AreEqual(1_000L, remaining.MaxInputTokens);
        Assert.AreEqual(0L, remaining.MaxOutputTokens);
        Assert.IsTrue(remaining.HasOutputLimit);
        Assert.IsTrue(new ExecutionUsageBudgetTracker(remaining).EvaluateBeforeRound().ShouldStop);
    }

    [TestMethod]
    public void CreateRemainingBudget_TracksPeakRoundInputTokensAndDerivedMarker()
    {
        var tracker = new ExecutionUsageBudgetTracker(new ExecutionUsageBudget
        {
            MaxInputTokens = 100_000,
            PricingKnown = true,
        });

        tracker.RecordInvocationUsage(Usage(prompt: 12_000, output: 100, cacheHit: 2_000));
        tracker.RecordInvocationUsage(Usage(prompt: 24_473, output: 100, cacheHit: 3_000)); // 单轮峰值（sub-feca2176 实测）
        tracker.RecordInvocationUsage(Usage(prompt: 18_000, output: 100, cacheHit: 1_000));

        var remaining = tracker.CreateRemainingBudget();
        Assert.IsNotNull(remaining);
        Assert.IsTrue(remaining.IsDerivedRemainder);
        Assert.AreEqual(24_473L, remaining.PeakRoundInputTokens); // 取 max，不是末轮
        Assert.AreEqual(100_000L, remaining.MaxInputTokens);
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
