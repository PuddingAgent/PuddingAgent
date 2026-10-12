using PuddingCode.Models;
using PuddingCode.Platform;

namespace PuddingCoreTests;

/// <summary>
/// R1 门禁：请求归因必须**按 invocation/attempt 配对**，而不是按 session 取「当前最新」。
/// <para>
/// 复核实测的缺陷：A 准备（model-A、raw=2504）→ B 准备（model-B、raw=6）→ A 的主 usage 迟到，
/// 旧实现用 B 的 raw 去除 A 的实测输入，得到 417.33 倍并写到 model-B 的校准键，
/// 同时把 B 的准备快照改成 Provider Total。
/// </para>
/// <para>
/// 本套用例把「B 的对象与数值不被改写」「B 的校准不被污染」「A 的 raw 配到 A」
/// 「最近实测不倒退」逐条钉住。
/// </para>
/// </summary>
[TestClass]
public sealed class RequestAttributionIdentityTests
{
    private static IReadOnlyList<ChatMessage> Messages(int count, int chars)
    {
        var messages = new List<ChatMessage>();
        for (var i = 0; i < count; i++)
            messages.Add(new ChatMessage(ChatRole.User, $"m{i} " + new string('x', chars)));
        return messages;
    }

    /// <summary>核心回归：迟到的 A usage 不得改写 B 的准备快照，也不得用 B 的 raw 校准。</summary>
    [TestMethod]
    public void LateUsageForEarlierRequest_DoesNotRewriteNewerPreparedSnapshot_NorCalibrateWithItsRaw()
    {
        var store = new ContextUsageSnapshotStore();

        // A：model-A，raw 大得多（多且长的消息）
        var preparedA = store.CaptureLlmRequest(
            "s1", Messages(8, 400), tools: null, "model-A",
            LlmCallPurposes.Agent, invocationId: "inv-A", attemptId: "inv-A:a0");
        var rawA = preparedA.RawEstimatedTokens;

        // B：model-B，raw 极小（故意与 A 差两个数量级，放大错误校准）
        var preparedB = store.CaptureLlmRequest(
            "s1", Messages(1, 1), tools: null, "model-B",
            LlmCallPurposes.Agent, invocationId: "inv-B", attemptId: "inv-B:a0");
        var rawB = preparedB.RawEstimatedTokens;
        Assert.IsTrue(rawB > 0 && rawA > rawB * 20, $"前提：rawA({rawA}) 必须远大于 rawB({rawB})");

        var calibrationBBefore = store.GetPromptCalibrationRatio("s1", "model-B");

        // A 的主 usage 迟到：Provider 报的就是 A 的规模
        store.RecordProviderUsage(
            "s1",
            new TokenUsageDto { PromptTokens = rawA * 2, CompletionTokens = 5, TotalTokens = rawA * 2 + 5 },
            LlmCallPurposes.Agent,
            invocationId: "inv-A",
            attemptId: "inv-A:a0");

        // ① 准备槽位仍是 B，且对象/数值未被改写
        Assert.IsTrue(store.TryGet("s1", out var current));
        Assert.AreSame(preparedB, current, "迟到的 A usage 不得改写 B 的准备快照");
        Assert.AreEqual(rawB, current!.RawEstimatedTokens);
        Assert.AreEqual("model-B", current.ModelId);

        // ② B 的校准不得被 A 的实测污染（旧实现会得到 rawA*2/rawB ≈ 417 倍）
        Assert.AreEqual(
            calibrationBBefore,
            store.GetPromptCalibrationRatio("s1", "model-B"),
            1e-9,
            "A 的实测不得写进 model-B 的校准键");

        // ③ A 的校准用的是 A 自己的 raw（2 倍）
        Assert.AreEqual(
            2.0,
            store.GetPromptCalibrationRatio("s1", "model-A"),
            1e-6,
            "A 的实测必须配到 A 自己的 raw");

        // ④ 最近实测 = A，且 raw 配的是 A（不是 B）
        Assert.IsTrue(store.TryGetLastMeasuredRequest("s1", out var measured));
        Assert.AreEqual("inv-A", measured!.InvocationId);
        Assert.AreEqual(rawA, measured.RawEstimatedTokens, "实测记录的 raw 必须是它自己那次请求的");
    }

    /// <summary>身份未知的 usage 只进未配对账本：不校准、不改写准备槽位、不污染最近实测。</summary>
    [TestMethod]
    public void UnknownIdentityUsage_GoesToUnpairedLedger_WithoutCalibrationOrOverwrite()
    {
        var store = new ContextUsageSnapshotStore();
        var prepared = store.CaptureLlmRequest(
            "s2", Messages(2, 200), tools: null, "model-A",
            LlmCallPurposes.Agent, invocationId: "inv-known", attemptId: "inv-known:a0");

        store.RecordProviderUsage(
            "s2",
            new TokenUsageDto { PromptTokens = 100_000, CompletionTokens = 1, TotalTokens = 100_001 },
            LlmCallPurposes.Agent,
            invocationId: "inv-unknown",
            attemptId: "inv-unknown:a0");

        Assert.IsTrue(store.TryGet("s2", out var current));
        Assert.AreSame(prepared, current, "未配对身份不得改写准备槽位");
        Assert.AreEqual(1.0, store.GetPromptCalibrationRatio("s2", "model-A"), 1e-9, "未配对 usage 不得校准");
        Assert.IsFalse(store.TryGetLastMeasuredRequest("s2", out _), "未配对 usage 不得进入最近实测投影");
        Assert.IsTrue(store.TryGetUnpairedMeasurement("s2", out var unpaired), "未配对 usage 必须留证");
        Assert.AreEqual("inv-unknown", unpaired!.InvocationId);
    }

    /// <summary>最近实测按**请求序号**单调推进：先到的旧请求不能覆盖后到的较新请求。</summary>
    [TestMethod]
    public void LastMeasuredProjection_AdvancesByRequestSequence_NotArrivalOrder()
    {
        var store = new ContextUsageSnapshotStore();

        var older = store.CaptureLlmRequest(
            "s3", Messages(2, 100), tools: null, "model-A",
            LlmCallPurposes.Agent, invocationId: "inv-old", attemptId: "inv-old:a0");
        var newer = store.CaptureLlmRequest(
            "s3", Messages(4, 100), tools: null, "model-A",
            LlmCallPurposes.Agent, invocationId: "inv-new", attemptId: "inv-new:a0");

        // 新的先回来
        store.RecordProviderUsage(
            "s3", new TokenUsageDto { PromptTokens = newer.RawEstimatedTokens }, LlmCallPurposes.Agent,
            invocationId: "inv-new", attemptId: "inv-new:a0");
        Assert.IsTrue(store.TryGetLastMeasuredRequest("s3", out var afterNew));
        Assert.AreEqual("inv-new", afterNew!.InvocationId);

        // 旧的随后迟到：不得把投影拉回去
        store.RecordProviderUsage(
            "s3", new TokenUsageDto { PromptTokens = older.RawEstimatedTokens }, LlmCallPurposes.Agent,
            invocationId: "inv-old", attemptId: "inv-old:a0");
        Assert.IsTrue(store.TryGetLastMeasuredRequest("s3", out var afterLate));
        Assert.AreEqual("inv-new", afterLate!.InvocationId, "最近实测不得因迟到而倒退");
        Assert.IsTrue(afterLate.RequestSequence >= afterNew.RequestSequence);
    }

    /// <summary>同一 attempt 重复上报幂等：不重复推进投影、不重复抬高校准。</summary>
    [TestMethod]
    public void RepeatedUsageForSameAttempt_IsIdempotent()
    {
        var store = new ContextUsageSnapshotStore();
        var prepared = store.CaptureLlmRequest(
            "s4", Messages(2, 100), tools: null, "model-A",
            LlmCallPurposes.Agent, invocationId: "inv-1", attemptId: "inv-1:a0");

        var usage = new TokenUsageDto { PromptTokens = prepared.RawEstimatedTokens * 3 };
        store.RecordProviderUsage("s4", usage, LlmCallPurposes.Agent, "inv-1", "inv-1:a0");
        var ratioAfterFirst = store.GetPromptCalibrationRatio("s4", "model-A");
        store.TryGetLastMeasuredRequest("s4", out var first);

        store.RecordProviderUsage("s4", usage, LlmCallPurposes.Agent, "inv-1", "inv-1:a0");
        var ratioAfterSecond = store.GetPromptCalibrationRatio("s4", "model-A");
        store.TryGetLastMeasuredRequest("s4", out var second);

        Assert.AreEqual(ratioAfterFirst, ratioAfterSecond, 1e-9, "重复 usage 不得继续抬高校准");
        Assert.AreEqual(first!.RequestSequence, second!.RequestSequence, "重复 usage 不得改变请求序号");
    }

    /// <summary>同一 invocation 的不同 attempt（失败重试）各自配对，互不串槽。</summary>
    [TestMethod]
    public void SameInvocationDifferentAttempt_PairsPerAttempt()
    {
        var store = new ContextUsageSnapshotStore();
        var attempt0 = store.CaptureLlmRequest(
            "s5", Messages(2, 100), tools: null, "model-A",
            LlmCallPurposes.Agent, invocationId: "inv-r", attemptId: "inv-r:a0");
        var attempt1 = store.CaptureLlmRequest(
            "s5", Messages(3, 100), tools: null, "model-A",
            LlmCallPurposes.Agent, invocationId: "inv-r", attemptId: "inv-r:a1");

        // 只回报 attempt0（例如 a1 被取消）
        store.RecordProviderUsage(
            "s5", new TokenUsageDto { PromptTokens = attempt0.RawEstimatedTokens }, LlmCallPurposes.Agent,
            "inv-r", "inv-r:a0");

        Assert.IsTrue(store.TryGetLastMeasuredRequest("s5", out var measured));
        Assert.AreEqual("inv-r:a0", measured!.AttemptId);
        Assert.AreEqual(
            attempt0.RawEstimatedTokens,
            measured.RawEstimatedTokens,
            "必须用 attempt0 自己的 raw，而不是更晚的 attempt1");
        Assert.AreNotEqual(attempt1.RawEstimatedTokens, measured.RawEstimatedTokens);
    }

    /// <summary>辅助调用（压缩/审批）不参与主校准、不改写准备槽位；其 raw 来自它自己的冻结请求。</summary>
    [TestMethod]
    public void AuxiliaryUsage_NeitherCalibratesNorBorrowsMainSlot()
    {
        var store = new ContextUsageSnapshotStore();
        var main = store.CaptureLlmRequest(
            "s6", Messages(2, 100), tools: null, "model-A",
            LlmCallPurposes.Agent, invocationId: "inv-main", attemptId: "inv-main:a0");

        // 压缩请求自己的一次准备（purpose = Compaction）与它的 usage
        var compactionSnapshot = store.MeasureLlmRequest("s6", Messages(6, 100), tools: null, "model-A");
        store.PublishPreparedRequest(
            compactionSnapshot, LlmCallPurposes.Compaction, "inv-cmp", "inv-cmp:a0");

        store.RecordProviderUsage(
            "s6", new TokenUsageDto { PromptTokens = compactionSnapshot.RawEstimatedTokens * 4 },
            LlmCallPurposes.Compaction, "inv-cmp", "inv-cmp:a0");

        Assert.AreEqual(1.0, store.GetPromptCalibrationRatio("s6", "model-A"), 1e-9, "辅助调用不得参与主校准");
        Assert.IsFalse(store.TryGetLastMeasuredRequest("s6", out _), "辅助调用不得进入主实测投影");
        Assert.IsTrue(store.TryGetLastAuxiliaryMeasurement("s6", out var auxiliary));
        Assert.AreEqual(
            compactionSnapshot.RawEstimatedTokens,
            auxiliary!.RawEstimatedTokens,
            "辅助 raw 必须来自它自己的冻结请求，而不是主槽位");
        Assert.AreNotEqual(main.RawEstimatedTokens, auxiliary.RawEstimatedTokens);
    }

    /// <summary>缺 usage（Provider 未报数）时不得产生实测、不得推进校准。</summary>
    [TestMethod]
    public void MissingProviderUsage_RecordsNothing()
    {
        var store = new ContextUsageSnapshotStore();
        store.CaptureLlmRequest(
            "s7", Messages(2, 100), tools: null, "model-A",
            LlmCallPurposes.Agent, invocationId: "inv-1", attemptId: "inv-1:a0");

        store.RecordProviderUsage(
            "s7", new TokenUsageDto(), LlmCallPurposes.Agent, "inv-1", "inv-1:a0");

        Assert.AreEqual(1.0, store.GetPromptCalibrationRatio("s7", "model-A"), 1e-9);
        Assert.IsFalse(store.TryGetLastMeasuredRequest("s7", out _), "无 Provider 报数时不得伪造实测");
    }

    /// <summary>路由隔离：不同 model 的实测各自只影响自己的校准键。</summary>
    [TestMethod]
    public void CalibrationIsIsolatedPerModelRoute()
    {
        var store = new ContextUsageSnapshotStore();
        var preparedA = store.CaptureLlmRequest(
            "s8", Messages(2, 100), tools: null, "model-A",
            LlmCallPurposes.Agent, invocationId: "inv-A", attemptId: "inv-A:a0");
        var preparedB = store.CaptureLlmRequest(
            "s8", Messages(2, 100), tools: null, "model-B",
            LlmCallPurposes.Agent, invocationId: "inv-B", attemptId: "inv-B:a0");

        // 只回报 A 的实测：A 学 5 倍，B 保持 1.0
        store.RecordProviderUsage(
            "s8", new TokenUsageDto { PromptTokens = preparedA.RawEstimatedTokens * 5 },
            LlmCallPurposes.Agent, "inv-A", "inv-A:a0");

        Assert.AreEqual(5.0, store.GetPromptCalibrationRatio("s8", "model-A"), 1e-6);
        Assert.AreEqual(1.0, store.GetPromptCalibrationRatio("s8", "model-B"), 1e-9, "B 的校准必须隔离");
        Assert.IsTrue(preparedB.RawEstimatedTokens > 0);
    }
}
