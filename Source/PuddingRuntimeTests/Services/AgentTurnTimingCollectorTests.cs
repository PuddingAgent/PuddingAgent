using PuddingCode.Models;
using PuddingRuntime.Services;

namespace PuddingRuntimeTests.Services;

[TestClass]
public sealed class AgentTurnTimingCollectorTests
{
    [TestMethod]
    public void Payload_LeavesUnobservedMetricsNull()
    {
        var collector = new AgentTurnTimingCollector();

        var payload = collector.ToPayload();

        Assert.IsNull(payload["providerTtftMs"], "An unobserved TTFT must stay null, never 0.");
        Assert.IsNull(payload["providerFirstContentMs"]);
        Assert.IsNull(payload["firstContentFrameMs"]);
        Assert.IsNull(payload["contextReadyMs"]);
        Assert.IsNull(payload["completedMs"]);
        Assert.IsNull(payload["historyLoadMs"]);
        Assert.AreEqual(0L, payload["modelMs"]);
        Assert.AreEqual(0L, payload["toolMs"]);
    }

    [TestMethod]
    public void ObserveProviderDelta_ReportsTtftOnce_AndSplitsByDeltaKind()
    {
        var collector = new AgentTurnTimingCollector();
        collector.BeginModelCall();

        // A transport-only chunk (headers, no model payload) must not set TTFT.
        Assert.IsFalse(collector.ObserveProviderDelta(new StreamDelta
        {
            ProviderHeadersMs = 40,
            ProviderDispatchElapsedMs = 120,
        }));

        Assert.IsTrue(collector.ObserveProviderDelta(new StreamDelta
        {
            ProviderDispatchElapsedMs = 200,
            ReasoningDelta = "think",
        }));
        Assert.AreEqual(200L, collector.ProviderTtftMs);
        Assert.AreEqual("provider_dispatch", collector.ProviderTtftSource);
        Assert.AreEqual(200L, collector.ProviderFirstReasoningMs);
        Assert.AreEqual(40L, collector.ProviderHeadersMs);

        // Later deltas must not move TTFT.
        Assert.IsFalse(collector.ObserveProviderDelta(new StreamDelta
        {
            ProviderDispatchElapsedMs = 350,
            ContentDelta = "answer",
        }));
        Assert.AreEqual(200L, collector.ProviderTtftMs);
        Assert.AreEqual(350L, collector.ProviderFirstContentMs);
    }

    [TestMethod]
    public void ObserveProviderDelta_WithoutGatewayClock_FallsBackToLocalModelCall()
    {
        var collector = new AgentTurnTimingCollector();
        collector.BeginModelCall();
        Thread.Sleep(20);

        Assert.IsTrue(collector.ObserveProviderDelta(new StreamDelta { ContentDelta = "answer" }));

        Assert.IsNotNull(collector.ProviderTtftMs);
        Assert.IsTrue(collector.ProviderTtftMs!.Value >= 1L);
        Assert.AreEqual("local_model_call", collector.ProviderTtftSource);
    }

    [TestMethod]
    public void ToolAndCompletionFacts_FeedThePayload()
    {
        var collector = new AgentTurnTimingCollector();
        collector.RegisterToolCall(15);
        collector.RegisterToolCall(5);
        collector.RegisterToolCall(0);
        collector.MarkFirstContentFrame();
        collector.MarkContextReady();
        collector.MarkCompleted();

        var payload = collector.ToPayload();

        Assert.AreEqual(20L, payload["toolMs"]);
        Assert.AreEqual(3, payload["toolCalls"]);
        Assert.IsNotNull(payload["firstContentFrameMs"]);
        Assert.IsNotNull(payload["contextReadyMs"]);
        Assert.IsNotNull(payload["completedMs"]);
    }

    [TestMethod]
    public void ModelCalls_AccumulateWallDurations()
    {
        var collector = new AgentTurnTimingCollector();

        collector.BeginModelCall();
        Thread.Sleep(15);
        collector.EndModelCall();

        collector.BeginModelCall();
        Thread.Sleep(15);
        collector.EndModelCall();

        Assert.AreEqual(2, collector.ModelCalls);
        Assert.IsTrue(collector.ModelMs >= 1L);
    }

    /// <summary>
    /// ADR-095 §6.3：压缩是串行阶段，必须能解释「受理→首增量」，但它是独立归因，不是主模型耗时；
    /// 未观察到同步压缩时保持 null（不用 0 掩盖），软维护延期不得被记成一次压缩。
    /// </summary>
    [TestMethod]
    public void CompactionFacts_StayNullUntilObserved_ThenAccumulateSeparately()
    {
        var collector = new AgentTurnTimingCollector();

        var untouched = collector.ToPayload();
        Assert.IsNull(untouched["compactionMs"], "未观察到同步压缩时必须是 null，不是 0");
        Assert.AreEqual(0, untouched["compactionAttempts"]);
        Assert.AreEqual(0, untouched["deferredSoftCompactions"]);
        Assert.AreEqual(0, untouched["appliedCompactions"]);

        // 软维护延期：没有摘要调用，也没有耗时。
        collector.RegisterCompactionAttempt(durationMs: null, applied: false, deferredSoft: true);
        Assert.IsNull(collector.CompactionMs);
        Assert.AreEqual(1, collector.CompactionAttempts);
        Assert.AreEqual(1, collector.DeferredSoftCompactions);
        Assert.AreEqual(0, collector.AppliedCompactions);

        // 真正的同步压缩 + 一次无收益/失败尝试：耗时累加，但仍不计入 ModelMs。
        collector.RegisterCompactionAttempt(1_500, applied: true, deferredSoft: false);
        collector.RegisterCompactionAttempt(500, applied: false, deferredSoft: false);

        Assert.AreEqual(2_000L, collector.CompactionMs);
        Assert.AreEqual(3, collector.CompactionAttempts);
        Assert.AreEqual(1, collector.AppliedCompactions);
        Assert.AreEqual(0L, collector.ModelMs, "摘要耗时不得混入主模型耗时");

        var payload = collector.ToPayload();
        Assert.AreEqual(2_000L, payload["compactionMs"]);
        Assert.AreEqual(3, payload["compactionAttempts"]);
        Assert.AreEqual(1, payload["deferredSoftCompactions"]);
        Assert.AreEqual(1, payload["appliedCompactions"]);
    }
}
