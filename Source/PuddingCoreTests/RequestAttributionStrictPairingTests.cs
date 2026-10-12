using PuddingCode.Models;
using PuddingCode.Platform;

namespace PuddingCoreTests;

/// <summary>
/// 复核新增 P1（请求归因第二轮）：**明确给了 attempt 就必须精确匹配**。
/// <para>
/// 旧实现 attempt 查不到会继续退到 invocation 键 ⇒ 探针实测：传入 <c>attempt-never-prepared</c>
/// 竟匹配到 <c>attempt-known</c>，让未知/已淘汰 attempt 的 usage 借用另一次请求的估算参与校准。
/// </para>
/// <para>
/// 本套用例钉住四条规则：① attempt 精确匹配；② invocation 一致性；③ 未给 attempt 时仅当
/// invocation 唯一才降级；④ purpose 不得跨类借用。
/// </para>
/// </summary>
[TestClass]
public sealed class RequestAttributionStrictPairingTests
{
    private static IReadOnlyList<ChatMessage> Messages(int count, int chars)
    {
        var messages = new List<ChatMessage>();
        for (var i = 0; i < count; i++)
            messages.Add(new ChatMessage(ChatRole.User, $"m{i} " + new string('x', chars)));
        return messages;
    }

    /// <summary>复核探针的等价场景：未知 attempt 不得回退到同 invocation 的已知 attempt。</summary>
    [TestMethod]
    public void UnknownAttempt_DoesNotFallBackToKnownAttempt()
    {
        var store = new ContextUsageSnapshotStore();
        var prepared = store.CaptureLlmRequest(
            "s-strict", Messages(2, 200), tools: null, "model-A",
            LlmCallPurposes.Agent, invocationId: "inv-1", attemptId: "attempt-known");

        var returned = store.RecordProviderUsage(
            "s-strict",
            new TokenUsageDto { PromptTokens = prepared.RawEstimatedTokens * 3 },
            LlmCallPurposes.Agent,
            invocationId: "inv-1",
            attemptId: "attempt-never-prepared");

        // 未知 attempt ⇒ 未配对：不校准、不投影、不改写准备槽位。
        Assert.AreEqual(1.0, store.GetPromptCalibrationRatio("s-strict", "model-A"), 1e-9,
            "未知 attempt 的 usage 不得借用已知 attempt 的 raw 做校准");
        Assert.IsFalse(store.TryGetLastMeasuredRequest("s-strict", out _), "未知 attempt 不得进入最近实测投影");
        Assert.IsTrue(store.TryGetUnpairedMeasurement("s-strict", out var unpaired), "必须留证");
        Assert.AreEqual("attempt-never-prepared", unpaired!.AttemptId);
        Assert.IsTrue(store.TryGet("s-strict", out var current));
        Assert.AreSame(prepared, current, "准备槽位不得被改写");
        Assert.AreSame(prepared, returned);
    }

    /// <summary>attempt 命中但 invocation 对不上 ⇒ 身份冲突，不得配对。</summary>
    [TestMethod]
    public void AttemptWithMismatchedInvocation_IsUnpaired()
    {
        var store = new ContextUsageSnapshotStore();
        var prepared = store.CaptureLlmRequest(
            "s-conflict", Messages(2, 200), tools: null, "model-A",
            LlmCallPurposes.Agent, invocationId: "inv-A", attemptId: "attempt-shared");

        store.RecordProviderUsage(
            "s-conflict",
            new TokenUsageDto { PromptTokens = prepared.RawEstimatedTokens * 4 },
            LlmCallPurposes.Agent,
            invocationId: "inv-B",
            attemptId: "attempt-shared");

        Assert.AreEqual(1.0, store.GetPromptCalibrationRatio("s-conflict", "model-A"), 1e-9,
            "identity 冲突不得参与校准");
        Assert.IsFalse(store.TryGetLastMeasuredRequest("s-conflict", out _));
        Assert.IsTrue(store.TryGetUnpairedMeasurement("s-conflict", out _));
    }

    /// <summary>
    /// 未给 attempt：invocation 唯一时按明确约定降级匹配；出现第二次 attempt（重试）后即歧义 ⇒ 未配对。
    /// </summary>
    [TestMethod]
    public void InvocationOnlyFallback_RequiresUniqueness()
    {
        var store = new ContextUsageSnapshotStore();
        var first = store.CaptureLlmRequest(
            "s-unique", Messages(2, 200), tools: null, "model-A",
            LlmCallPurposes.Agent, invocationId: "inv-1", attemptId: "inv-1:a0");

        // 唯一一次冻结请求 ⇒ 允许（且仅此情形允许）按 invocation 降级匹配。
        store.RecordProviderUsage(
            "s-unique",
            new TokenUsageDto { PromptTokens = first.RawEstimatedTokens * 2 },
            LlmCallPurposes.Agent,
            invocationId: "inv-1",
            attemptId: null);

        Assert.IsTrue(store.TryGetLastMeasuredRequest("s-unique", out var measured));
        Assert.AreEqual("inv-1:a0", measured!.AttemptId, "唯一时降级匹配到那一次请求");
        Assert.AreEqual(2.0, store.GetPromptCalibrationRatio("s-unique", "model-A"), 1e-6);

        // 第二次 attempt（同 invocation）⇒ 歧义：再按 invocation 降级必须失败。
        store.CaptureLlmRequest(
            "s-unique", Messages(3, 200), tools: null, "model-A",
            LlmCallPurposes.Agent, invocationId: "inv-1", attemptId: "inv-1:a1");

        store.RecordProviderUsage(
            "s-unique",
            new TokenUsageDto { PromptTokens = 999_999 },
            LlmCallPurposes.Agent,
            invocationId: "inv-1",
            attemptId: null);

        Assert.IsTrue(store.TryGetLastMeasuredRequest("s-unique", out var afterAmbiguous));
        Assert.AreEqual("inv-1:a0", afterAmbiguous!.AttemptId, "歧义时不得配对，投影保持原值");
        Assert.AreEqual(2.0, store.GetPromptCalibrationRatio("s-unique", "model-A"), 1e-6,
            "歧义时不得继续抬高校准");
        Assert.IsTrue(store.TryGetUnpairedMeasurement("s-unique", out _));
    }

    /// <summary>purpose 不得跨类借用：辅助 usage 不得用主请求的冻结记录做 raw。</summary>
    [TestMethod]
    public void AuxiliaryUsage_DoesNotBorrowMainPreparedRecord()
    {
        var store = new ContextUsageSnapshotStore();
        var main = store.CaptureLlmRequest(
            "s-purpose", Messages(2, 200), tools: null, "model-A",
            LlmCallPurposes.Agent, invocationId: "inv-main", attemptId: "inv-main:a0");

        store.RecordProviderUsage(
            "s-purpose",
            new TokenUsageDto { PromptTokens = 12_345, CompletionTokens = 10 },
            LlmCallPurposes.Compaction,
            invocationId: "inv-main",
            attemptId: "inv-main:a0");

        Assert.IsTrue(store.TryGetLastAuxiliaryMeasurement("s-purpose", out var auxiliary));
        Assert.AreEqual(0, auxiliary!.RawEstimatedTokens,
            "辅助 usage 配到的是主请求记录 ⇒ 必须视为未配对（raw 不得借用主请求）");
        Assert.AreEqual(1.0, store.GetPromptCalibrationRatio("s-purpose", "model-A"), 1e-9,
            "辅助 usage 不得参与主校准");
        Assert.IsFalse(store.TryGetLastMeasuredRequest("s-purpose", out _), "辅助 usage 不得进入主实测投影");
        Assert.AreNotEqual(main.RawEstimatedTokens, auxiliary.RawEstimatedTokens);
    }

    /// <summary>多次重试各自配对：每次 attempt 的 usage 只影响自己的那次（不串槽）。</summary>
    [TestMethod]
    public void RetriedAttempts_PairPerAttempt()
    {
        var store = new ContextUsageSnapshotStore();
        var a0 = store.CaptureLlmRequest(
            "s-retry", Messages(2, 100), tools: null, "model-A",
            LlmCallPurposes.Agent, invocationId: "inv-r", attemptId: "inv-r:a0");
        var a1 = store.CaptureLlmRequest(
            "s-retry", Messages(3, 100), tools: null, "model-A",
            LlmCallPurposes.Agent, invocationId: "inv-r", attemptId: "inv-r:a1");

        store.RecordProviderUsage(
            "s-retry", new TokenUsageDto { PromptTokens = a0.RawEstimatedTokens }, LlmCallPurposes.Agent,
            "inv-r", "inv-r:a0");

        Assert.IsTrue(store.TryGetLastMeasuredRequest("s-retry", out var measured));
        Assert.AreEqual("inv-r:a0", measured!.AttemptId);
        Assert.AreEqual(a0.RawEstimatedTokens, measured.RawEstimatedTokens);
        Assert.AreNotEqual(a1.RawEstimatedTokens, measured.RawEstimatedTokens);

        // a1 的 usage 随后到达：按序号推进（a1 更晚）并配 a1 自己的 raw。
        store.RecordProviderUsage(
            "s-retry", new TokenUsageDto { PromptTokens = a1.RawEstimatedTokens }, LlmCallPurposes.Agent,
            "inv-r", "inv-r:a1");

        Assert.IsTrue(store.TryGetLastMeasuredRequest("s-retry", out var latest));
        Assert.AreEqual("inv-r:a1", latest!.AttemptId);
        Assert.AreEqual(a1.RawEstimatedTokens, latest.RawEstimatedTokens);
    }
}
