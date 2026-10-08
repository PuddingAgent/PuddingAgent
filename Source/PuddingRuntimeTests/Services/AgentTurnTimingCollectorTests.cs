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

    /// <summary>
    /// 请求副本裁剪是**独立事实**（不是持久摘要压缩）：与压缩计数分开登记，
    /// 未裁剪时保持 0/0 且首裁时刻为 null（不用 0 冒充已测量）。
    /// </summary>
    [TestMethod]
    public void RequestTrimFacts_AreAggregatedSeparatelyFromCompaction()
    {
        var collector = new AgentTurnTimingCollector();

        var untouched = collector.ToPayload();
        Assert.AreEqual(0, untouched["requestTrimmedRounds"]);
        Assert.AreEqual(0, untouched["requestTrimmedMessages"]);
        Assert.IsNull(untouched["firstRequestTrimMs"], "从未裁剪时首裁时刻必须是 null");
        Assert.IsNull(untouched["compactionMs"], "请求副本裁剪不得让压缩耗时变成 0");

        // 一条非正数不构成裁剪事实。
        collector.RegisterRequestTrim(0);
        collector.RegisterRequestTrim(-3);
        Assert.AreEqual(0, collector.RequestTrimmedRounds);
        Assert.AreEqual(0, collector.RequestTrimmedMessages);

        collector.RegisterRequestTrim(1);
        collector.RegisterRequestTrim(76);
        Assert.AreEqual(2, collector.RequestTrimmedRounds);
        Assert.AreEqual(77, collector.RequestTrimmedMessages);
        Assert.IsNotNull(collector.FirstRequestTrimMs, "首次裁剪后必须记录时刻");
        Assert.IsNull(collector.CompactionMs, "请求副本裁剪不得写成压缩耗时");

        var payload = collector.ToPayload();
        Assert.AreEqual(2, payload["requestTrimmedRounds"]);
        Assert.AreEqual(77, payload["requestTrimmedMessages"]);
        Assert.AreEqual(0, payload["compactionAttempts"]);
        Assert.AreEqual(0, payload["appliedCompactions"]);
    }
}
