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

    /// <summary>
    /// 2026-11-20 回归：provider 报数出现跨数量级异常时，不得被学进校准系数。
    /// <para>
    /// 实测现场（session=…-sub-ce1d23b7）：同一份约 209 KB 的请求，provider 第二轮报
    /// prompt=582,469（其中 cache_read=534,194），相对本地原始估算约 6.7 倍，而两轮之间
    /// 文本只多了 2,792 字节。系数只升不降，一旦学到 6.7，该会话每次 usedTokens 都被放大 6.7 倍，
    /// growthTokens 随之变成约 50 万 ⇒ 预测占用越窗 ⇒ 保护态拒发 ⇒ 子代理当场失败。
    /// </para>
    /// </summary>
    [TestMethod]
    public void Ratio_IgnoresImplausiblyHighObservation()
    {
        var store = new ContextUsageSnapshotStore();
        var prepared = Capture(store, "s-implausible", "inv-x");

        Record(store, "s-implausible", (int)(prepared.RawEstimatedTokens * 6.7), "inv-x");

        Assert.AreEqual(1.0, store.GetPromptCalibrationRatio("s-implausible", "model-A"), 1e-6,
            "跨数量级的观测比必须被忽略，系数保持未校准状态");
    }

    /// <summary>
    /// 2026-11-21 回归：缓存读参与的观测不参与校准。
    /// <para>
    /// 实测（session=…-sub-0b13bcea，49 轮按轮拆解）：round0 prompt=89,987（cacheHit=0）；
    /// round1 prompt=651,027，其中 cacheHit=598,378 —— 缓存读大于上一轮全量请求，
    /// 不可能是本轮请求的前缀。observedRatio 的分子含 cache_read，学进去就会把一个
    /// 与请求体积无关的数当成「密度事实」（历史事故：学到 6.7 ⇒ 子代理必死）。
    /// 对照组：<see cref="Ratio_RisesImmediately_WhenProviderReportsMoreTokens"/> 保持缓存未命中的
    /// 观测仍能正常校准。
    /// </para>
    /// </summary>
    [TestMethod]
    public void Ratio_IgnoresObservationWhenCacheReadParticipated()
    {
        var store = new ContextUsageSnapshotStore();
        var prepared = Capture(store, "s-cache", "inv-c");

        store.RecordProviderUsage(
            "s-cache",
            new TokenUsageDto
            {
                PromptTokens = (int)(prepared.RawEstimatedTokens * 2),
                PromptCacheHitTokens = (int)(prepared.RawEstimatedTokens * 1.5),
            },
            LlmCallPurposes.Agent,
            "inv-c",
            attemptId: null);

        Assert.AreEqual(1.0, store.GetPromptCalibrationRatio("s-cache", "model-A"), 1e-6,
            "缓存读参与时不得参与校准，系数保持未校准状态");
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
