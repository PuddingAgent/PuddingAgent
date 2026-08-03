using PuddingCode.Runtime;
using PuddingRuntime.Services;

namespace PuddingRuntimeTests.Services;

[TestClass]
public sealed class ContextHealthEvaluatorTests
{
    [TestMethod]
    public void Evaluate_DefaultThreshold_Is0_65()
    {
        var evaluator = new ContextHealthEvaluator();

        // ratio = 120000 / 180000 = 0.667, which is >= 0.65 → Critical
        var health = evaluator.Evaluate(
            sessionId: "session-1",
            usedTokens: 120_000,
            contextWindowTokens: 200_000,
            maxOutputTokens: 20_000);

        Assert.AreEqual(ContextHealthState.Critical, health.State);
        Assert.IsTrue(health.ShouldAutoCompact);
        Assert.IsFalse(health.ShouldBlockSend);
    }

    [TestMethod]
    public void Evaluate_BelowDefaultThreshold_IsWarning()
    {
        var evaluator = new ContextHealthEvaluator();

        // ratio = 115000 / 180000 = 0.639, which is < 0.65 but >= 0.60 → Warning
        var health = evaluator.Evaluate(
            sessionId: "session-1",
            usedTokens: 115_000,
            contextWindowTokens: 200_000,
            maxOutputTokens: 20_000);

        Assert.AreEqual(ContextHealthState.Warning, health.State);
        Assert.IsTrue(health.ShouldSuggestCompact);
        Assert.IsFalse(health.ShouldAutoCompact);
    }

    [TestMethod]
    public void Evaluate_CustomThreshold_IsUsed()
    {
        var evaluator = new ContextHealthEvaluator();

        // ratio = 136800 / 180000 = 0.76
        // With threshold 0.80: 0.76 < 0.80, but >= 0.75 → Unhealthy
        var health = evaluator.Evaluate(
            sessionId: "session-1",
            usedTokens: 136_800,
            contextWindowTokens: 200_000,
            maxOutputTokens: 20_000,
            compactionThreshold: 0.80);

        Assert.AreEqual(ContextHealthState.Unhealthy, health.State);
        Assert.IsFalse(health.ShouldAutoCompact);
    }

    [TestMethod]
    public void Evaluate_CustomThreshold_HigherTriggersAtHigherRatio()
    {
        var evaluator = new ContextHealthEvaluator();

        // ratio = 160000 / 180000 = 0.889
        // With threshold 0.85: 0.889 >= 0.85 → Critical
        var health = evaluator.Evaluate(
            sessionId: "session-1",
            usedTokens: 160_000,
            contextWindowTokens: 200_000,
            maxOutputTokens: 20_000,
            compactionThreshold: 0.85);

        Assert.AreEqual(ContextHealthState.Critical, health.State);
        Assert.IsTrue(health.ShouldAutoCompact);
    }

    [TestMethod]
    public void Evaluate_InvalidThreshold_Zero_FallsBackToDefault()
    {
        var evaluator = new ContextHealthEvaluator();

        // ratio = 120000 / 180000 = 0.667, threshold=0→fallback 0.65 → Critical
        var health = evaluator.Evaluate(
            sessionId: "session-1",
            usedTokens: 120_000,
            contextWindowTokens: 200_000,
            maxOutputTokens: 20_000,
            compactionThreshold: 0);

        Assert.AreEqual(ContextHealthState.Critical, health.State);
    }

    [TestMethod]
    public void Evaluate_InvalidThreshold_Negative_FallsBackToDefault()
    {
        var evaluator = new ContextHealthEvaluator();

        // ratio = 120000 / 180000 = 0.667, threshold=-0.5→fallback 0.65 → Critical
        var health = evaluator.Evaluate(
            sessionId: "session-1",
            usedTokens: 120_000,
            contextWindowTokens: 200_000,
            maxOutputTokens: 20_000,
            compactionThreshold: -0.5);

        Assert.AreEqual(ContextHealthState.Critical, health.State);
    }

    [TestMethod]
    public void Evaluate_InvalidThreshold_AboveOne_FallsBackToDefault()
    {
        var evaluator = new ContextHealthEvaluator();

        // ratio = 120000 / 180000 = 0.667, threshold=1.5→fallback 0.65 → Critical
        var health = evaluator.Evaluate(
            sessionId: "session-1",
            usedTokens: 120_000,
            contextWindowTokens: 200_000,
            maxOutputTokens: 20_000,
            compactionThreshold: 1.5);

        Assert.AreEqual(ContextHealthState.Critical, health.State);
    }

    [TestMethod]
    public void Evaluate_ThresholdExactlyOne_Works()
    {
        var evaluator = new ContextHealthEvaluator();

        // ratio = 163800 / 180000 = 0.91, threshold=0.90 → Critical (0.91 >= 0.90, < 0.92 so not Blocking)
        var health = evaluator.Evaluate(
            sessionId: "session-1",
            usedTokens: 163_800,
            contextWindowTokens: 200_000,
            maxOutputTokens: 20_000,
            compactionThreshold: 0.90);

        Assert.AreEqual(ContextHealthState.Critical, health.State);
    }
}
