using PuddingCode.Models;
using PuddingCode.Platform;

namespace PuddingCoreTests;

/// <summary>
/// 校准系数必须能**向下**收敛。
/// 实测 provider 上报 284,325 而本地原始估算 393,839（≈0.72）：只要系数被夹在 1.0，
/// 这种系统性高估永远学不回来，usedTokens 长期虚高，闸门就会在真实占用远未到达
/// 阈值时提前触发压缩。
/// 安全网仍由 RecordProviderInputLimitFailure 承担——provider 报输入超限时系数会被立即抬高，
/// 不依赖这里的下限。
/// </summary>
[TestClass]
public sealed class PromptCalibrationConvergenceTests
{
    [TestMethod]
    public void Ratio_FallsBelowOne_WhenProviderReportsFewerTokens()
    {
        var store = new ContextUsageSnapshotStore();
        var prepared = Capture(store, "s-fall", "inv-1");

        Record(store, "s-fall", (int)(prepared.RawEstimatedTokens * 0.7), "inv-1");

        var ratio = store.GetPromptCalibrationRatio("s-fall", "model-A");
        Assert.IsTrue(ratio < 1.0, $"系数必须能低于 1.0，实际 {ratio}");
        Assert.IsTrue(ratio > 0.5, "单轮回落不应直接触底");
    }

    [TestMethod]
    public void Ratio_StopsAtFloor_WhenProviderStaysLow()
    {
        var store = new ContextUsageSnapshotStore();
        var ratio = 1.0;
        for (var round = 0; round < 8; round++)
        {
            var prepared = Capture(store, "s-floor", $"inv-{round}");
            Record(store, "s-floor", (int)(prepared.RawEstimatedTokens * 0.3), $"inv-{round}");
            ratio = store.GetPromptCalibrationRatio("s-floor", "model-A");
        }

        Assert.IsTrue(ratio >= 0.5 - 1e-9, $"系数不得跌破下限 0.5，实际 {ratio}");
        Assert.IsTrue(ratio < 0.6, $"持续偏差时应收敛到下限附近，实际 {ratio}");
    }

    [TestMethod]
    public void Ratio_RisesImmediately_WhenProviderReportsMoreTokens()
    {
        var store = new ContextUsageSnapshotStore();
        var low = Capture(store, "s-rise", "inv-low");
        Record(store, "s-rise", (int)(low.RawEstimatedTokens * 0.6), "inv-low");

        var high = Capture(store, "s-rise", "inv-high");
        Record(store, "s-rise", high.RawEstimatedTokens * 2, "inv-high");

        Assert.AreEqual(2.0, store.GetPromptCalibrationRatio("s-rise", "model-A"), 1e-6,
            "实测更高必须立即抬高，不能等回落");
    }

    private static ContextUsageSnapshot Capture(
        ContextUsageSnapshotStore store, string sessionId, string invocationId)
        => store.CaptureLlmRequest(
            sessionId, Messages(), tools: null, "model-A",
            LlmCallPurposes.Agent, invocationId, $"{invocationId}:a0");

    private static void Record(
        ContextUsageSnapshotStore store, string sessionId, int promptTokens, string invocationId)
        => store.RecordProviderUsage(
            sessionId,
            new TokenUsageDto { PromptTokens = promptTokens },
            LlmCallPurposes.Agent,
            invocationId,
            attemptId: null);

    private static IReadOnlyList<ChatMessage> Messages()
    {
        var messages = new List<ChatMessage>();
        for (var i = 0; i < 6; i++)
            messages.Add(new ChatMessage(ChatRole.User, $"m{i} " + new string('x', 400)));
        return messages;
    }
}
