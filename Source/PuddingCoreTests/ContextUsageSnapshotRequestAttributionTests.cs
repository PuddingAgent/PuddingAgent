using PuddingCode.Models;
using PuddingCode.Platform;

namespace PuddingCoreTests;

/// <summary>
/// F1 请求归因：<c>currentPreparedRequest</c>（本轮准备）与 <c>lastMeasuredRequest</c>（最近一次
/// 主请求实测）必须分离；辅助/摘要调用单独归因，迟到、交错、取消或缺 usage 都不得覆盖更晚的
/// 准备快照，也不得参与出站安全校准（ADR-095 D6、修复方案 §2.1/§2.2）。
/// </summary>
[TestClass]
public sealed class ContextUsageSnapshotRequestAttributionTests
{
    private static List<ChatMessage> Messages(int count)
    {
        var messages = new List<ChatMessage>();
        for (var i = 0; i < count; i++)
            messages.Add(new ChatMessage(i % 2 == 0 ? ChatRole.User : ChatRole.Assistant, new string('a', 2_000) + i));
        return messages;
    }

    /// <summary>计量不得发布：候选测量不能改写 session 的准备快照。</summary>
    [TestMethod]
    public void MeasureLlmRequest_DoesNotPublishPreparedRequest()
    {
        var store = new ContextUsageSnapshotStore();
        var prepared = store.CaptureLlmRequest("session-measure", Messages(4), tools: null, "test-model");

        var measured = store.MeasureLlmRequest("session-measure", Messages(40), tools: null, "test-model");

        Assert.IsGreaterThan(prepared.UsedTokens, measured.UsedTokens);
        Assert.IsTrue(store.TryGet("session-measure", out var stored));
        Assert.AreSame(prepared, stored, "只计量不得发布，准备快照必须原样保留");
    }

    /// <summary>
    /// 辅助/摘要调用不得改写准备快照，也不得抬高出站硬门禁用的保守校准系数。
    /// </summary>
    [TestMethod]
    public void RecordProviderUsage_AuxiliaryPurposeDoesNotTouchPreparedOrCalibration()
    {
        var store = new ContextUsageSnapshotStore();
        var prepared = store.CaptureLlmRequest("session-aux", Messages(4), tools: null, "test-model");

        var returned = store.RecordProviderUsage(
            "session-aux",
            new TokenUsageDto { PromptTokens = prepared.RawEstimatedTokens * 5, CompletionTokens = 10, TotalTokens = 1 },
            purpose: LlmCallPurposes.Compaction,
            invocationId: "session-aux:trace:1:compaction",
            attemptId: "session-aux:trace:1:compaction:a0");

        Assert.AreSame(prepared, returned, "辅助调用必须返回未改动的准备快照");
        Assert.IsTrue(store.TryGet("session-aux", out var stored));
        Assert.AreSame(prepared, stored);
        Assert.AreEqual(1.0, store.GetPromptCalibrationRatio("session-aux", "test-model"), 1e-9);

        Assert.IsTrue(store.TryGetLastAuxiliaryMeasurement("session-aux", out var auxiliary));
        Assert.AreEqual(LlmCallPurposes.Compaction, auxiliary!.Purpose);
        Assert.AreEqual("session-aux:trace:1:compaction", auxiliary.InvocationId);
        Assert.AreEqual(prepared.RawEstimatedTokens * 5, auxiliary.ProviderPromptTokens);

        Assert.IsFalse(
            store.TryGetLastMeasuredRequest("session-aux", out _),
            "辅助调用不得写入主请求实测槽位");
    }

    /// <summary>主请求实测写入自己的槽位；Total 与 Prompt 分开保存，不得当成「实测输入」。</summary>
    [TestMethod]
    public void RecordProviderUsage_MainPurposeWritesSeparateMeasurement()
    {
        var store = new ContextUsageSnapshotStore();
        var prepared = store.CaptureLlmRequest("session-main", Messages(4), tools: null, "test-model");
        var prompt = prepared.RawEstimatedTokens * 2;

        store.RecordProviderUsage(
            "session-main",
            new TokenUsageDto { PromptTokens = prompt, CompletionTokens = 128, TotalTokens = prompt + 128 },
            purpose: LlmCallPurposes.Agent,
            invocationId: "session-main:trace:1:agent",
            attemptId: "session-main:trace:1:agent:a0");

        Assert.IsTrue(store.TryGetLastMeasuredRequest("session-main", out var measured));
        Assert.AreEqual(prompt, measured!.ProviderPromptTokens, "实测输入 = PromptTokens");
        Assert.AreEqual(128, measured.ProviderCompletionTokens);
        Assert.AreEqual(prompt + 128, measured.ProviderTotalTokens, "Total 与 Prompt 分开保存");
        Assert.AreEqual(prepared.RawEstimatedTokens, measured.RawEstimatedTokens, "raw 必须来自被配对的那次准备");
        Assert.AreEqual("session-main:trace:1:agent", measured.InvocationId);
        Assert.AreEqual("session-main:trace:1:agent:a0", measured.AttemptId);
        Assert.IsTrue(measured.HasProviderUsage);
    }

    /// <summary>
    /// 迟到/交错的辅助 usage 不得覆盖更晚的准备快照：先准备的 A、后到的摘要 usage、再准备的 B，
    /// 最终准备快照必须是 B，且校准仍是 1.0。
    /// </summary>
    [TestMethod]
    public void RecordProviderUsage_LateAuxiliaryUsageDoesNotClobberNewerPreparedRequest()
    {
        var store = new ContextUsageSnapshotStore();
        var preparedA = store.CaptureLlmRequest("session-late", Messages(4), tools: null, "test-model");

        // 摘要调用（辅助用途）迟到落账，带着远大于估算的报数。
        store.RecordProviderUsage(
            "session-late",
            new TokenUsageDto { PromptTokens = preparedA.RawEstimatedTokens * 5, CompletionTokens = 20 },
            purpose: LlmCallPurposes.Compaction);

        var preparedB = store.CaptureLlmRequest("session-late", Messages(6), tools: null, "test-model");

        Assert.IsTrue(store.TryGet("session-late", out var stored));
        Assert.AreSame(preparedB, stored, "更晚的准备快照不得被迟到的辅助 usage 改写");
        Assert.AreEqual(1.0, store.GetPromptCalibrationRatio("session-late", "test-model"), 1e-9);
    }

    /// <summary>缺 usage：既不发布、也不写实测槽位（不得用 0 覆盖估算）。</summary>
    [TestMethod]
    public void RecordProviderUsage_MissingUsageWritesNoMeasurement()
    {
        var store = new ContextUsageSnapshotStore();
        var prepared = store.CaptureLlmRequest("session-missing", Messages(4), tools: null, "test-model");

        var returned = store.RecordProviderUsage("session-missing", new TokenUsageDto());

        Assert.AreSame(prepared, returned);
        Assert.IsFalse(store.TryGetLastMeasuredRequest("session-missing", out _));
        Assert.IsFalse(store.TryGetLastAuxiliaryMeasurement("session-missing", out _));
    }

    /// <summary>主/辅助交错：两个槽位各自保留自己的那一条，互不覆盖。</summary>
    [TestMethod]
    public void InterleavedMainAndAuxiliaryUsage_StaySeparated()
    {
        var store = new ContextUsageSnapshotStore();
        var prepared = store.CaptureLlmRequest("session-interleaved", Messages(4), tools: null, "test-model");
        var mainPrompt = prepared.RawEstimatedTokens * 2;

        store.RecordProviderUsage(
            "session-interleaved",
            new TokenUsageDto { PromptTokens = mainPrompt, CompletionTokens = 50 },
            purpose: LlmCallPurposes.Agent,
            invocationId: "inv-main");
        store.RecordProviderUsage(
            "session-interleaved",
            new TokenUsageDto { PromptTokens = 193_218, CompletionTokens = 4_343 },
            purpose: LlmCallPurposes.Compaction,
            invocationId: "inv-summary");

        Assert.IsTrue(store.TryGetLastMeasuredRequest("session-interleaved", out var main));
        Assert.IsTrue(store.TryGetLastAuxiliaryMeasurement("session-interleaved", out var auxiliary));
        Assert.AreEqual("inv-main", main!.InvocationId);
        Assert.AreEqual(mainPrompt, main.ProviderPromptTokens);
        Assert.AreEqual("inv-summary", auxiliary!.InvocationId);
        Assert.AreEqual(193_218, auxiliary.ProviderPromptTokens);
    }
}
