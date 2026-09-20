using PuddingCode.Classification;
using PuddingCode.Tools;
using PuddingRuntime.Classification;
using PuddingRuntime.Services.Tools;

namespace PuddingRuntimeTests.Classification;

/// <summary>
/// <see cref="ClassifierHealthReporter"/> 的离线单元测试（零网络，切片 S6a，方案 v2 §8.2 / §14.9.2）。
/// <para>
/// 逐条锁定：健康默认值、3/5 次连续 deferred ⇒ Degraded/Unavailable、成功重置（§14.9.2 原文
/// 「仅在成功后被重置」的最保守解释：清空该分类器名下全部键）、不同 (tool_id, args_hash) 键互不干扰、
/// 退避档基数 × 2^(n-1) 封顶 60s、退避基数从配置读取生效、以及评审器接线后
/// deferred 决策与原因码绝不被健康上报改写（ADR-091 §4.4 不折叠）。
/// </para>
/// </summary>
[TestClass]
public sealed class ClassifierHealthReporterTests
{

    // ---------- ① 健康默认值：登记未上报 ⇒ Unknown / 0 计数 / 无原因码 ----------

    [TestMethod]
    public void T01_DefaultHealth_IsUnknown_WithZeroCounters()
    {
        var reporter = new ClassifierHealthReporter();
        reporter.EnsureRegistered("pipeline");
        reporter.EnsureRegistered("system-rules");

        var snapshot = reporter.Snapshot();

        Assert.AreEqual(2, snapshot.Count);
        Assert.IsTrue(snapshot.All(s => s.Health == ClassifierHealth.Unknown), "未上报 ⇒ Unknown。");
        Assert.IsTrue(snapshot.All(s => s.ConsecutiveFailures == 0), "未上报 ⇒ 计数为 0。");
        Assert.IsNull(snapshot.Single(s => s.ClassifierId == "system-rules").Detail);
        Assert.AreEqual(0, reporter.SnapshotDeferredCounters().Count);
        Assert.AreEqual(2000, reporter.UnavailableBackoffBaseMs, "默认退避基数 2000（§14.9）。");
    }

    // ---------- ② 连续 3 次 deferred（同一键）⇒ Degraded ----------

    [TestMethod]
    public void T02_ThreeConsecutiveDeferred_SameKey_SetsDegraded()
    {
        var reporter = new ClassifierHealthReporter();
        const string argsJson = """{"command":"dotnet test"}""";

        var first = reporter.RecordDeferred("stub-classifier", "shell", argsJson, "approval_review_classifier_unknown");
        var second = reporter.RecordDeferred("stub-classifier", "shell", argsJson, "approval_review_classifier_unknown");
        var third = reporter.RecordDeferred("stub-classifier", "shell", argsJson, "approval_review_classifier_unknown");

        Assert.AreEqual(ClassifierHealth.Unknown, first.Health, "1–2 次未达档 ⇒ Unknown（最保守解释）。");
        Assert.AreEqual(ClassifierHealth.Unknown, second.Health);
        Assert.AreEqual(ClassifierHealth.Degraded, third.Health, "第 3 次连续 deferred ⇒ Degraded（§14.9.2）。");

        var status = AssertSingleStatus(reporter, "stub-classifier");
        Assert.AreEqual(3, status.ConsecutiveFailures);
        var counter = reporter.SnapshotDeferredCounters().Single();
        Assert.AreEqual(3, counter.ConsecutiveDeferred);
        Assert.AreEqual(8000, counter.RetryAfterMs, "n=3 ⇒ 2000 × 2^2 = 8000。");
        Assert.AreEqual("approval_review_classifier_unknown", counter.LastReasonCode);
    }

    // ---------- ③ 连续 5 次 ⇒ Unavailable；6 次后仍 Unavailable 且封顶 60s ----------

    [TestMethod]
    public void T03_FiveConsecutiveDeferred_SetsUnavailable_AndCapsRetryAfter()
    {
        var reporter = new ClassifierHealthReporter();

        for (var n = 1; n <= 5; n++)
        {
            ClassifierHealth[] expectedByN =
                [ClassifierHealth.Unknown, ClassifierHealth.Unknown, ClassifierHealth.Degraded, ClassifierHealth.Degraded, ClassifierHealth.Unavailable];
            var report = reporter.RecordDeferred("stub-classifier", "shell", """{"command":"a"}""", "code");
            Assert.AreEqual(expectedByN[n - 1], report.Health, $"n={n} 的健康档位（§14.9.2：3 ⇒ Degraded，5 ⇒ Unavailable）。");
        }

        var status = AssertSingleStatus(reporter, "stub-classifier");
        Assert.AreEqual(ClassifierHealth.Unavailable, status.Health, "第 5 次 ⇒ Unavailable（§14.9.2）。");

        var sixth = reporter.RecordDeferred("stub-classifier", "shell", """{"command":"a"}""", "code");
        Assert.AreEqual(ClassifierHealth.Unavailable, sixth.Health, "超过 5 次保持 Unavailable。");
        Assert.AreEqual(60_000, sixth.RetryAfterMs, "n=6 ⇒ 64000 封顶 60000（§14.9.2）。");
    }

    // ---------- ④ 成功一次 ⇒ 计数归零 / 健康恢复（§14.9.2「仅在成功后被重置」） ----------

    [TestMethod]
    public void T04_SuccessAfterDeferred_ResetsCounters_RestoresHealthy()
    {
        var reporter = new ClassifierHealthReporter();
        RecordThreeDeferred(reporter, "shell", """{"command":"a"}""");

        reporter.RecordSuccess("stub-classifier", latencyMs: 12.5);

        var status = AssertSingleStatus(reporter, "stub-classifier");
        Assert.AreEqual(ClassifierHealth.Healthy, status.Health, "成功返回 ⇒ 健康恢复。");
        Assert.AreEqual(0, status.ConsecutiveFailures, "成功一次即清零（S1 契约注释）。");
        Assert.IsNull(status.Detail, "成功后最近失败原因码清空。");
        Assert.AreEqual(0, reporter.SnapshotDeferredCounters().Count, "全部键计数已重置。");
    }

    // ---------- ⑤ 不同 (tool_id, args_hash) 计数互不干扰 ----------

    [TestMethod]
    public void T05_DifferentKeys_CountIndependently()
    {
        var reporter = new ClassifierHealthReporter();
        RecordThreeDeferred(reporter, "shell", """{"command":"a"}""");

        var firstB = reporter.RecordDeferred("stub-classifier", "shell", """{"command":"b"}""", "code");

        Assert.AreEqual(1, firstB.ConsecutiveDeferred, "新键从 1 开始计数。");
        Assert.AreEqual(2000, firstB.RetryAfterMs, "新键退避档从基数起步。");
        Assert.AreEqual(ClassifierHealth.Degraded, firstB.Health, "健康取键间最大计数（键 A 的 3 次）。");

        var counters = reporter.SnapshotDeferredCounters();
        Assert.AreEqual(2, counters.Count);
        Assert.AreEqual(3, counters.Single(c => c.ArgumentsHash == HashOf("""{"command":"a"}""")).ConsecutiveDeferred);
        Assert.AreEqual(1, counters.Single(c => c.ArgumentsHash == HashOf("""{"command":"b"}""")).ConsecutiveDeferred);
        Assert.AreNotEqual(
            counters.Single(c => c.ArgumentsHash == HashOf("""{"command":"a"}""")).ArgumentsHash,
            counters.Single(c => c.ArgumentsHash == HashOf("""{"command":"b"}""")).ArgumentsHash,
            "不同参数 ⇒ 不同 args_hash 键。");
    }

    // ---------- ⑥（硬性禁止折叠）评审器接线：deferred 决策与原因码绝不被上报改写 ----------

    [TestMethod]
    public async Task T09_ReviewerHealthReporting_DoesNotRewriteDeferredDecisionOrReasonCode()
    {
        var reporter = new ClassifierHealthReporter();
        var classifier = new StubClassifier(VerdictUnknown());
        var reviewer = new ClassifierToolApprovalReviewer(classifier, healthReporter: reporter);

        for (var attempt = 1; attempt <= 3; attempt++)
        {
            var result = await reviewer.ReviewAsync(TicketRequest(), Identity(), Descriptor());

            Assert.AreEqual(ToolApprovalDecision.DeferredDependency, result.Decision, $"第 {attempt} 次：决策必须保持 deferred。");
            Assert.AreNotEqual(ToolApprovalDecision.Denied, result.Decision, "ADR-091 §4.4：不得折叠为 Denied。");
            Assert.AreEqual(ToolApprovalWire.CodeClassifierUnknown, result.ReasonCode, "原因码不得被退避改写。");
            Assert.IsFalse(result.RequiresHumanAuthorization);
        }

        Assert.AreEqual(
            ClassifierHealth.Degraded,
            reporter.Snapshot().Single(s => s.ClassifierId == StubClassifierId).Health,
            "健康面已计入 3 次连续 deferred（旁路上报，不改变决策）。");
        Assert.AreEqual(3, reporter.SnapshotDeferredCounters().Single().ConsecutiveDeferred);
    }

    // ---------- 退避档：基数 × 2^(n-1)，封顶 60000（纯函数） ----------

    [TestMethod]
    public void T06_RetryAfter_ExponentialGrowth_CappedAtSixtySeconds()
    {
        int[] expected = [2000, 4000, 8000, 16000, 32000, 60000, 60000];
        for (var n = 1; n <= expected.Length; n++)
        {
            Assert.AreEqual(expected[n - 1], ClassifierHealthReporter.ComputeRetryAfterMs(2000, n), $"n={n}。");
        }

        Assert.AreEqual(60_000, ClassifierHealthReporter.MaxRetryAfterMs);
        Assert.AreEqual(2000, new ClassifierHealthReporter().UnavailableBackoffBaseMs, "默认基数哨兵：2000（§14.9）。");
    }

    // ---------- ⑩ 退避基数从配置读取生效（3000 覆盖 ⇒ 读数变化） ----------

    [TestMethod]
    public void T07_BackoffBaseFromConfiguration_Override3000_ChangesReadings()
    {
        var overridden = new ClassifierHealthReporter(unavailableBackoffBaseMs: 3000);
        var baseline = new ClassifierHealthReporter();

        Assert.AreEqual(3000, overridden.UnavailableBackoffBaseMs);
        Assert.AreEqual(2000, baseline.UnavailableBackoffBaseMs);

        var firstOverride = overridden.RecordDeferred("stub-classifier", "shell", """{"command":"x"}""", "code");
        var secondOverride = overridden.RecordDeferred("stub-classifier", "shell", """{"command":"x"}""", "code");
        Assert.AreEqual(3000, firstOverride.RetryAfterMs, "覆盖 3000 ⇒ n=1 档 = 3000。");
        Assert.AreEqual(6000, secondOverride.RetryAfterMs, "覆盖 3000 ⇒ n=2 档 = 6000。");

        var secondBaselineNext = baseline.RecordDeferred("other-classifier", "shell", """{"command":"x"}""", "code");
        secondBaselineNext = baseline.RecordDeferred("other-classifier", "shell", """{"command":"x"}""", "code");
        Assert.AreEqual(4000, secondBaselineNext.RetryAfterMs, "默认 2000 ⇒ n=2 档 = 4000（对照）。");
        Assert.AreNotEqual(secondOverride.RetryAfterMs, secondBaselineNext.RetryAfterMs, "配置覆盖确实改变读数。");
    }

    // ---------- ④ 的最保守解释：成功重置该分类器名下全部键（防残留计数永久降级） ----------

    [TestMethod]
    public void T08_Success_ClearsAllKeys_ForClassifier_NotOnlyCurrentKey()
    {
        var reporter = new ClassifierHealthReporter();
        RecordThreeDeferred(reporter, "shell", """{"command":"a"}""");
        reporter.RecordDeferred("stub-classifier", "shell", """{"command":"b"}""", "code");
        reporter.RecordDeferred("stub-classifier", "shell", """{"command":"b"}""", "code");
        Assert.AreEqual(ClassifierHealth.Degraded, AssertSingleStatus(reporter, "stub-classifier").Health);

        reporter.RecordSuccess("stub-classifier");

        Assert.AreEqual(ClassifierHealth.Healthy, AssertSingleStatus(reporter, "stub-classifier").Health);
        Assert.AreEqual(0, reporter.SnapshotDeferredCounters().Count, "两个键全部清零：残留计数会让下一键失败 1 次即回降级档，等于偶发失败永久降级（§14.9.2 目的反面）。");
    }

    // ---------- helpers ----------

    private const string StubClassifierId = "stub-classifier";

    private static void RecordThreeDeferred(ClassifierHealthReporter reporter, string toolId, string argsJson)
    {
        reporter.RecordDeferred(StubClassifierId, toolId, argsJson, "code");
        reporter.RecordDeferred(StubClassifierId, toolId, argsJson, "code");
        reporter.RecordDeferred(StubClassifierId, toolId, argsJson, "code");
    }

    private static string HashOf(string argumentsJson)
        => ToolAuthorizationDefaults.ComputeArgumentsHash(argumentsJson);

    private static ClassifierStatus AssertSingleStatus(ClassifierHealthReporter reporter, string classifierId)
        => reporter.Snapshot().Single(s => s.ClassifierId == classifierId);

    private static ClassificationVerdict VerdictUnknown() => new()
    {
        Outcome = ClassificationOutcome.Unknown,
        Reason = "arbiter unavailable",
        ReasonCode = "classifier.pipeline.arbiter_unavailable",
        ClassifierId = StubClassifierId,
    };

    private static ToolApprovalTicketRequest TicketRequest() => new()
    {
        ToolId = "shell",
        CommandName = "dotnet",
        Purpose = "unit test purpose",
        RequestedArgumentsJson = """{"command":"dotnet test"}""",
    };

    private static ToolApprovalIdentity Identity() => new()
    {
        WorkspaceId = "ws-test",
        SessionId = "s-1",
        AgentInstanceId = "agent-1",
        UserId = "user-1",
    };

    private static ToolDescriptor Descriptor() => new()
    {
        ToolId = "shell",
        Name = "shell",
        Description = "test descriptor",
    };

    private sealed class StubClassifier(ClassificationVerdict verdict) : IToolCallClassifier
    {
        public string ClassifierId => StubClassifierId;

        public Task<ClassificationVerdict> ClassifyAsync(
            ToolCallClassificationContext context,
            CancellationToken ct = default)
            => Task.FromResult(verdict);
    }
}
