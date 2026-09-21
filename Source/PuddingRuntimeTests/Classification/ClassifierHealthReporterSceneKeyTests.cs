using PuddingCode.Classification;
using PuddingCode.Tools;
using PuddingRuntime.Classification;

namespace PuddingRuntimeTests.Classification;

/// <summary>
/// S2b（旁挂按场景键泛化）健康面用例：<b>场景隔离</b>（T1）、<b>单场景行为逐位不变</b>（T2）、
/// <b>场景键归一</b>（T5）。
/// <para>
/// 为什么要锁定这三条：原先 per-key 键是 <c>(ClassifierId, ToolId, ArgumentsHash)</c>，多场景共用同一分类器时
/// 不同场景的失败计数会<b>混进同一桶</b>——一个场景的累积失败会把另一个场景推到 Degraded/Unavailable。
/// 加场景维度是修这个正确性缺陷，但同时必须证明「单场景下读数一个字节都没变」，否则就是拿回归换修复。
/// </para>
/// </summary>
[TestClass]
public sealed class ClassifierHealthReporterSceneKeyTests
{
    private const string StubClassifierId = "stub-classifier";
    private const string ToolId = "shell";
    private const string ArgsJsonA = """{"command":"a"}""";

    // ---------- T1：场景隔离（核心） ----------

    [TestMethod]
    public void T1_SameKeyInTwoScenes_DoesNotShareCounters_AndDoesNotDegradeTheOtherScene()
    {
        var reporter = new ClassifierHealthReporter();

        // 场景 A：同一 (ClassifierId, ToolId, ArgumentsHash) 连续 5 次 deferred ⇒ 场景 A 到 Unavailable。
        for (var n = 0; n < 5; n++)
        {
            reporter.RecordDeferred(StubClassifierId, ToolId, ArgsJsonA, "code", sceneKey: "scene-a");
        }

        var counters = reporter.SnapshotDeferredCounters();
        Assert.HasCount(1, counters, "场景 A 的 5 次只应产生 1 个桶。");
        Assert.AreEqual("scene-a", counters[0].SceneKey, "桶必须携带场景键。");
        Assert.AreEqual(5, counters[0].ConsecutiveDeferred);

        // 场景内视图：A 已 Unavailable；B 未受影响（不是 Degraded / Unavailable）。
        Assert.AreEqual(ClassifierHealth.Unavailable, reporter.HealthForScene(StubClassifierId, "scene-a"));

        var otherScene = reporter.HealthForScene(StubClassifierId, "scene-b");
        Assert.AreNotEqual(ClassifierHealth.Degraded, otherScene, "一个场景的累积失败不得让另一场景看起来降级。");
        Assert.AreNotEqual(ClassifierHealth.Unavailable, otherScene, "一个场景的累积失败不得让另一场景看起来不可用。");
        Assert.AreEqual(ClassifierHealth.Unknown, otherScene);

        // 场景 B 的首次 deferred 从 1 开始（不共享 A 的 5 次）。
        var second = reporter.RecordDeferred(StubClassifierId, ToolId, ArgsJsonA, "code", sceneKey: "scene-b");

        Assert.AreEqual(1, second.ConsecutiveDeferred, "不同场景的同一键不共享计数。");
        Assert.AreEqual("scene-b", second.SceneKey);
        Assert.AreEqual(2000, second.RetryAfterMs, "场景 B 的退避档从基数起步，而不是接续场景 A。");

        var afterBoth = reporter.SnapshotDeferredCounters();
        Assert.HasCount(2, afterBoth, "两个场景各占一个桶。");
        Assert.AreEqual(5, afterBoth.Single(c => c.SceneKey == "scene-a").ConsecutiveDeferred);
        Assert.AreEqual(1, afterBoth.Single(c => c.SceneKey == "scene-b").ConsecutiveDeferred);

        // 既有跨场景聚合语义（Snapshot）保持不变：计数取该分类器名下全部键的最大值。
        var status = reporter.Snapshot().Single(s => s.ClassifierId == StubClassifierId);
        Assert.AreEqual(5, status.ConsecutiveFailures, "Snapshot 仍是跨场景聚合（既有语义未动）。");
        Assert.AreEqual(ClassifierHealth.Unavailable, status.Health);
    }

    // ---------- T2：单场景回归（不传场景键 ⇒ 与改动前逐位相同） ----------

    [TestMethod]
    public void T2_SingleScene_WithoutSceneKey_KeepsExistingCountsAndBands()
    {
        var reporter = new ClassifierHealthReporter();

        // 既有单场景路径：不传 sceneKey（既有调用点的形态）。
        ClassifierHealth[] expectedByN =
            [ClassifierHealth.Unknown, ClassifierHealth.Unknown, ClassifierHealth.Degraded, ClassifierHealth.Degraded, ClassifierHealth.Unavailable];

        for (var n = 1; n <= 5; n++)
        {
            var report = reporter.RecordDeferred(StubClassifierId, ToolId, ArgsJsonA, "code");
            Assert.AreEqual(expectedByN[n - 1], report.Health, $"n={n} 的档位必须与改动前一致（3 ⇒ Degraded，5 ⇒ Unavailable）。");
        }

        // 3 ⇒ Degraded 的原始边界读数（退避档 2000 × 2^2 = 8000）。
        var fresh = new ClassifierHealthReporter();
        for (var n = 0; n < 3; n++)
        {
            fresh.RecordDeferred(StubClassifierId, ToolId, ArgsJsonA, "code");
        }

        var counter = fresh.SnapshotDeferredCounters().Single();
        Assert.AreEqual(3, counter.ConsecutiveDeferred);
        Assert.AreEqual(8000, counter.RetryAfterMs, "n=3 ⇒ 2000 × 2^2 = 8000（既有退避档未被改动）。");
        Assert.AreEqual("code", counter.LastReasonCode);
        Assert.AreEqual(ClassifierHealth.Degraded, fresh.Snapshot().Single().Health);

        // 6 次后仍 Unavailable 且封顶 60s。
        var sixth = reporter.RecordDeferred(StubClassifierId, ToolId, ArgsJsonA, "code");
        Assert.AreEqual(ClassifierHealth.Unavailable, sixth.Health);
        Assert.AreEqual(60_000, sixth.RetryAfterMs, "n=6 ⇒ 64000 封顶 60000（既有封顶未被改动）。");

        // 条目基数：单场景下每键仍恰好一个桶；快照字段一个不少。
        var single = reporter.SnapshotDeferredCounters();
        Assert.HasCount(1, single, "单场景下桶数与改动前一致。");
        Assert.AreEqual(StubClassifierId, single[0].ClassifierId);
        Assert.AreEqual(ToolId, single[0].ToolId);
        Assert.AreEqual(ToolAuthorizationDefaults.ComputeArgumentsHash(ArgsJsonA), single[0].ArgumentsHash);
        Assert.AreEqual(6, single[0].ConsecutiveDeferred);

        // 成功重置语义（分类器维度）逐位不变。
        reporter.RecordSuccess(StubClassifierId, latencyMs: 12.5);
        var status = reporter.Snapshot().Single(s => s.ClassifierId == StubClassifierId);
        Assert.AreEqual(ClassifierHealth.Healthy, status.Health);
        Assert.AreEqual(0, status.ConsecutiveFailures);
        Assert.IsEmpty(reporter.SnapshotDeferredCounters());
    }

    // ---------- T5：场景键归一（null / 空 / 空白 ⇒ 具名默认键，不是空串键） ----------

    [TestMethod]
    public void T5_MissingSceneKey_NormalizesToNamedDefault_NotEmptyKey()
    {
        var reporter = new ClassifierHealthReporter();

        var fromNull = reporter.RecordDeferred(StubClassifierId, ToolId, ArgsJsonA, "code", sceneKey: null);
        var fromEmpty = reporter.RecordDeferred(StubClassifierId, ToolId, ArgsJsonA, "code", sceneKey: string.Empty);
        var fromWhitespace = reporter.RecordDeferred(StubClassifierId, ToolId, ArgsJsonA, "code", sceneKey: "   ");

        Assert.AreEqual(OperatorSceneKeys.Default, fromNull.SceneKey);
        Assert.AreEqual(OperatorSceneKeys.Default, fromEmpty.SceneKey);
        Assert.AreEqual(OperatorSceneKeys.Default, fromWhitespace.SceneKey);

        Assert.IsFalse(string.IsNullOrWhiteSpace(OperatorSceneKeys.Default), "默认场景键必须是显式具名常量，不得为空。");
        Assert.AreEqual("operator.default", OperatorSceneKeys.Default, "默认场景键的取值被钉死：改它等于改变既有无场景读数的归属。");

        var counters = reporter.SnapshotDeferredCounters();
        Assert.HasCount(1, counters, "三种缺失形态归一后仍是同一个桶（1 个），且不为空串键。");
        Assert.AreEqual(OperatorSceneKeys.Default, counters[0].SceneKey);
        Assert.AreEqual(3, counters[0].ConsecutiveDeferred);

        // 默认键与真实场景键互不干扰：默认键的 3 次不会让真实场景看起来降级。
        Assert.AreEqual(ClassifierHealth.Degraded, reporter.HealthForScene(StubClassifierId, OperatorSceneKeys.Default));
        Assert.AreEqual(ClassifierHealth.Unknown, reporter.HealthForScene(StubClassifierId, "tool_approval"));
        Assert.AreEqual(
            reporter.HealthForScene(StubClassifierId, OperatorSceneKeys.Default),
            reporter.HealthForScene(StubClassifierId, null),
            "场景内视图同样归一：null 与具名默认键必须读到同一个桶。");

        // 归一函数是纯函数：非缺失的键原样返回（不改写调用方身份）。
        Assert.AreEqual("scene-x", OperatorSceneKeys.Normalize("scene-x"));
        Assert.AreEqual(" scene-x ", OperatorSceneKeys.Normalize(" scene-x "), "不做裁剪：不把本来不同的键折叠成一个。");
    }
}
