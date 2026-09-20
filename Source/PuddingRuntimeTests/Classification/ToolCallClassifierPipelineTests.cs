using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using PuddingCode.Classification;
using PuddingCode.Tools;
using PuddingRuntime.Classification;

namespace PuddingRuntimeTests.Classification;

/// <summary>
/// <see cref="ToolCallClassifierPipeline"/> 的离线单测（方案 v2 §14.13 / §11.3 / §14.9.1，切片 S3a）。
/// <para>
/// 覆盖映射（§14.13.8 可测性，全部零网络、假件注入）：
/// ① 规则 allow 命中 ⇒ 终局放行且仲裁零调用（01）；
/// ② 规则 deny + 仲裁 allow ⇒ 最终 allow 且必落覆盖审计（02）；
/// ③ 规则 deny + 仲裁 deny ⇒ 最终 deny，保留候选信息（03）；
/// ④ 规则未命中 ⇒ 采纳仲裁结论、无候选不落覆盖审计（04）；
/// ⑤ ② 场景仲裁异常 ⇒ Unknown + override_unavailable，不抛不拒（05）；
/// ⑥ ③ 场景仲裁异常 ⇒ Unknown + arbiter_unavailable（06）；
/// ⑦ 仲裁超时 ⇒ 同 ④ 分支且不挂起（07）；
/// ⑧ 永久类低置信度 ⇒ 降级为单次类，含阈值边界 0.899/0.900（08/08b/08c）；
/// ⑨ 单次求值仲裁至多一次（09）；
/// ⑩ 外层取消令牌 ⇒ OperationCanceledException 传播（10）；
/// ⑪ 防循环：分类器自产 deny 规则命中 ⇒ 不回调仲裁（11）；覆盖审计字段可解析（12）。
/// </para>
/// </summary>
[TestClass]
public sealed class ToolCallClassifierPipelineTests
{
    private const string TestWorkspace = "ws-test";
    private static readonly DateTimeOffset T0 = new(2026, 9, 21, 8, 0, 0, TimeSpan.Zero);

    // —— 01 规则 allow 命中 ⇒ 终局放行且仲裁调用次数为 0（§14.13.2① 核心不变量） ——

    [TestMethod]
    public async Task ClassifyAsync_RuleAllowHit_FinalAllow_ZeroArbiterCalls()
    {
        var arbiter = new StubClassifier("arbiter-stub", ClassificationOutcome.AllowOnce);
        var rules = new IToolCallClassifier[]
        {
            new StubClassifier("rules-a", ClassificationOutcome.Unknown),
            new StubClassifier("rules-b", ClassificationOutcome.AllowOnce)
            {
                AppliedRuleId = "rule-allow-1",
                Reason = "命中 allow 规则 rule-allow-1",
            },
        };
        var audit = new RecordingAuditStore();
        var pipeline = CreatePipeline(rules, arbiter, audit);

        var verdict = await pipeline.ClassifyAsync(ToolContext());

        Assert.AreEqual(ClassificationOutcome.AllowOnce, verdict.Outcome, "快路径必须终局放行。");
        Assert.AreEqual(ToolCallClassifierPipeline.ReasonAllowFastPath, verdict.ReasonCode);
        Assert.AreEqual(0, arbiter.CallCount, "快路径零仲裁调用（核心不变量）。");
        Assert.AreEqual("rule-allow-1", verdict.AppliedRuleId, "快路径保留命中规则 id。");
        Assert.IsTrue(verdict.LatencyMs.HasValue, "LatencyMs 必须经注入时钟计量产出。");
        Assert.AreEqual(0, audit.Events.Count, "快路径不是覆盖裁决，不得写覆盖审计。");
    }

    // —— 02 规则 deny + 仲裁 allow ⇒ 最终 allow 且必落覆盖审计（§14.13.2② / §14.13.4） ——

    [TestMethod]
    public async Task ClassifyAsync_RuleDeny_ArbiterAllows_FinalAllowWithOverrideAudit()
    {
        var rules = new IToolCallClassifier[]
        {
            new StubClassifier("system-rules", ClassificationOutcome.DenyOnce)
            {
                AppliedRuleId = "rule-deny-1",
                Reason = "命中 deny 规则 rule-deny-1（候选）",
            },
        };
        var arbiter = new StubClassifier("arbiter-stub", ClassificationOutcome.AllowOnce)
        {
            Reason = "该调用无风险",
            PerOutcomeConfidence = Confidences(allowPermanent: 0.95, allowOnce: 0.77),
        };
        var audit = new RecordingAuditStore();
        var pipeline = CreatePipeline(rules, arbiter, audit);

        var verdict = await pipeline.ClassifyAsync(ToolContext());

        Assert.AreEqual(ClassificationOutcome.AllowOnce, verdict.Outcome, "仲裁覆盖候选 deny ⇒ 最终放行。");
        Assert.AreEqual(ToolCallClassifierPipeline.ReasonArbiterFinal, verdict.ReasonCode);
        Assert.AreEqual(1, arbiter.CallCount, "候选 deny 必须给仲裁一次覆盖机会。");

        Assert.AreEqual(1, audit.Events.Count, "覆盖裁决必落一条审计（§14.13.4）。");
        var auditEvent = audit.Events[0];
        Assert.AreEqual(ToolApprovalAuditEventType.ClassifierInvoked, auditEvent.EventType);
        Assert.AreEqual(ToolApprovalRuleEffect.Allow, auditEvent.Effect, "覆盖后生效 effect 为 allow。");
        Assert.AreEqual("rule-deny-1", auditEvent.AllowlistRuleId, "审计保留候选规则 id。");
        Assert.AreEqual("terminal_execute", auditEvent.ToolId);
        Assert.AreEqual(TestWorkspace, auditEvent.WorkspaceId);
        Assert.AreEqual("sess-1", auditEvent.SessionId);
        Assert.AreEqual("agent-1", auditEvent.AgentInstanceId);
        Assert.AreEqual("user-1", auditEvent.UserId);
        Assert.AreEqual("arbiter-stub", auditEvent.ClassifierId, "新增 append-only 属性记录仲裁分类器 id。");
        AssertClose(0.77, auditEvent.ClassifierConfidence, "记录门槛校验所用可信度。");
        Assert.IsTrue(auditEvent.Reason!.Contains("candidate_reason=命中 deny 规则 rule-deny-1（候选）"), "审计保留原始候选理由。");
    }

    // —— 03 规则 deny + 仲裁 deny ⇒ 最终 deny（保留候选信息） ——

    [TestMethod]
    public async Task ClassifyAsync_RuleDeny_ArbiterDenies_FinalDenyKeepsCandidateInfo()
    {
        var rules = new IToolCallClassifier[]
        {
            new StubClassifier("system-rules", ClassificationOutcome.DenyOnce)
            {
                AppliedRuleId = "rule-deny-1",
                Reason = "命中 deny 规则 rule-deny-1（候选）",
            },
        };
        var arbiter = new StubClassifier("arbiter-stub", ClassificationOutcome.DenyOnce) { Reason = "仲裁确认拒绝" };
        var audit = new RecordingAuditStore();
        var pipeline = CreatePipeline(rules, arbiter, audit);

        var verdict = await pipeline.ClassifyAsync(ToolContext());

        Assert.AreEqual(ClassificationOutcome.DenyOnce, verdict.Outcome);
        Assert.AreEqual(ToolCallClassifierPipeline.ReasonArbiterFinal, verdict.ReasonCode);
        Assert.AreEqual(1, arbiter.CallCount);
        Assert.AreEqual("rule-deny-1", verdict.AppliedRuleId, "最终结论保留候选规则 id。");
        Assert.IsTrue(verdict.Reason!.Contains("候选 deny（规则 rule-deny-1）"), "结论保留候选信息。");

        var auditEvent = audit.Events[0];
        Assert.AreEqual(ToolApprovalRuleEffect.Deny, auditEvent.Effect, "覆盖后生效 effect 为 deny。");
        Assert.AreEqual(ToolApprovalAuditEventType.ClassifierInvoked, auditEvent.EventType);
    }

    // —— 04 规则均未命中 ⇒ 采纳仲裁结论；无候选 ⇒ 不落覆盖审计 ——

    [TestMethod]
    public async Task ClassifyAsync_NoRuleMatch_AdoptsArbiterOutcome_NoOverrideAudit()
    {
        var rules = new IToolCallClassifier[]
        {
            new StubClassifier("system-rules", ClassificationOutcome.Unknown),
        };
        var arbiter = new StubClassifier("arbiter-stub", ClassificationOutcome.DenyOnce) { Reason = "仲裁拒绝" };
        var audit = new RecordingAuditStore();
        var pipeline = CreatePipeline(rules, arbiter, audit);

        var verdict = await pipeline.ClassifyAsync(ToolContext());

        Assert.AreEqual(ClassificationOutcome.DenyOnce, verdict.Outcome, "直接采纳仲裁结论。");
        Assert.AreEqual(ToolCallClassifierPipeline.ReasonArbiterFinal, verdict.ReasonCode);
        Assert.AreEqual(1, arbiter.CallCount);
        Assert.AreEqual(0, audit.Events.Count, "无候选 deny 就没有「覆盖」，不得写覆盖审计（§14.13.4 仅约束覆盖）。");
        Assert.IsFalse(string.IsNullOrWhiteSpace(verdict.Reason));
        Assert.IsFalse(string.IsNullOrWhiteSpace(verdict.ReasonCode));
        Assert.AreEqual(ToolCallClassifierPipeline.WellKnownClassifierId, verdict.ClassifierId);
    }

    // —— 05 ② 场景仲裁抛异常 ⇒ Unknown + override_unavailable（不抛、不返回 Deny、不放行） ——

    [TestMethod]
    public async Task ClassifyAsync_RuleDeny_ArbiterThrows_ReturnsUnknownOverrideUnavailable()
    {
        var rules = new IToolCallClassifier[]
        {
            new StubClassifier("system-rules", ClassificationOutcome.DenyOnce) { AppliedRuleId = "rule-deny-1" },
        };
        var arbiter = new StubClassifier("arbiter-stub", ClassificationOutcome.AllowOnce)
        {
            ThrowOnCall = new InvalidOperationException("arbiter-boom"),
        };
        var pipeline = CreatePipeline(rules, arbiter);

        var verdict = await pipeline.ClassifyAsync(ToolContext());

        Assert.AreEqual(ClassificationOutcome.Unknown, verdict.Outcome, "④b：候选 deny 遇仲裁不可用 ⇒ Unknown。");
        Assert.AreEqual(ToolCallClassifierPipeline.ReasonOverrideUnavailable, verdict.ReasonCode);
        Assert.AreNotEqual(ClassificationOutcome.DenyOnce, verdict.Outcome, "绝不折叠为 Deny（ADR-091 §4.4）。");
        Assert.AreNotEqual(ClassificationOutcome.AllowOnce, verdict.Outcome, "绝不因故障放行。");
        Assert.AreEqual("rule-deny-1", verdict.AppliedRuleId, "降级结论保留候选规则 id 供上层诊断。");
    }

    // —— 06 ③ 场景仲裁抛异常 ⇒ Unknown + arbiter_unavailable ——

    [TestMethod]
    public async Task ClassifyAsync_NoRuleMatch_ArbiterThrows_ReturnsUnknownArbiterUnavailable()
    {
        var rules = new IToolCallClassifier[]
        {
            new StubClassifier("system-rules", ClassificationOutcome.Unknown),
        };
        var arbiter = new StubClassifier("arbiter-stub", ClassificationOutcome.AllowOnce)
        {
            ThrowOnCall = new InvalidOperationException("arbiter-boom"),
        };
        var pipeline = CreatePipeline(rules, arbiter);

        var verdict = await pipeline.ClassifyAsync(ToolContext());

        Assert.AreEqual(ClassificationOutcome.Unknown, verdict.Outcome, "④a：仲裁不可用 ⇒ Unknown。");
        Assert.AreEqual(ToolCallClassifierPipeline.ReasonArbiterUnavailable, verdict.ReasonCode);
    }

    // —— ⑦ 仲裁超时（可控延迟假件 + 短 ArbiterTimeoutMs）⇒ 同 ④ 分支且不挂起 ——

    [TestMethod]
    public async Task ClassifyAsync_ArbiterTimeout_ReturnsUnknown_WithoutHanging()
    {
        var rules = new IToolCallClassifier[]
        {
            new StubClassifier("system-rules", ClassificationOutcome.Unknown),
        };
        var arbiter = new StubClassifier("arbiter-stub", ClassificationOutcome.AllowOnce)
        {
            Delay = TimeSpan.FromSeconds(30),
        };
        var pipeline = CreatePipeline(
            rules, arbiter,
            options: new ToolCallClassifierPipelineOptions { ArbiterTimeoutMs = 60 });

        var stopwatch = Stopwatch.StartNew();
        var verdict = await pipeline.ClassifyAsync(ToolContext());
        stopwatch.Stop();

        Assert.AreEqual(ClassificationOutcome.Unknown, verdict.Outcome, "超时计入仲裁不可用（§14.13.6）。");
        Assert.AreEqual(ToolCallClassifierPipeline.ReasonArbiterUnavailable, verdict.ReasonCode);
        Assert.AreEqual(1, arbiter.CallCount);
        Assert.IsTrue(
            stopwatch.ElapsedMilliseconds < 5_000,
            $"超时必须立刻返回而非等满仲裁延迟（实际 {stopwatch.ElapsedMilliseconds}ms）。");
        Assert.IsTrue(verdict.Reason!.Contains("timeout_after_60ms"), "降级理由注明超时事实。");
    }

    // —— ⑧ 永久类 + 低置信度 ⇒ 降级为单次类（阈值边界 0.899 / 0.900） ——

    [DataTestMethod]
    [DataRow(0.899, ClassificationOutcome.AllowOnce, ToolCallClassifierPipeline.ReasonPermanentDowngraded)]
    [DataRow(0.900, ClassificationOutcome.AllowPermanent, ToolCallClassifierPipeline.ReasonArbiterFinal)]
    public async Task ClassifyAsync_AllowPermanentConfidenceGate_Boundary(
        double confidence,
        ClassificationOutcome expectedOutcome,
        string expectedReasonCode)
    {
        var rules = new IToolCallClassifier[]
        {
            new StubClassifier("system-rules", ClassificationOutcome.Unknown),
        };
        var arbiter = new StubClassifier("arbiter-stub", ClassificationOutcome.AllowPermanent)
        {
            Reason = "建议永久放行",
            PerOutcomeConfidence = Confidences(allowPermanent: confidence),
        };
        var audit = new RecordingAuditStore();
        var pipeline = CreatePipeline(rules, arbiter, audit);

        var verdict = await pipeline.ClassifyAsync(ToolContext());

        Assert.AreEqual(expectedOutcome, verdict.Outcome, $"allow_permanent={confidence} 的门槛判定不符。");
        Assert.AreEqual(expectedReasonCode, verdict.ReasonCode);
        Assert.AreEqual(0, audit.Events.Count, "③ 场景无候选 deny，不落覆盖审计（§14.13.4）。");

        if (confidence < 0.900)
        {
            Assert.IsTrue(verdict.Reason!.Contains("降级为单次类"), "降级事实保留在 Reason 中。");
        }
    }

    // —— ⑧b DenyPermanent 低置信度 ⇒ 降级为 DenyOnce ——

    [TestMethod]
    public async Task ClassifyAsync_DenyPermanentLowConfidence_DowngradesToDenyOnce()
    {
        var rules = new IToolCallClassifier[]
        {
            new StubClassifier("system-rules", ClassificationOutcome.Unknown),
        };
        var arbiter = new StubClassifier("arbiter-stub", ClassificationOutcome.DenyPermanent)
        {
            Reason = "建议永久拒绝",
            PerOutcomeConfidence = Confidences(allowPermanent: 0.95, denyPermanent: 0.10),
        };
        var audit = new RecordingAuditStore();
        var pipeline = CreatePipeline(rules, arbiter, audit);

        var verdict = await pipeline.ClassifyAsync(ToolContext());

        Assert.AreEqual(ClassificationOutcome.DenyOnce, verdict.Outcome, "永久 deny 置信度不足 ⇒ 降级单次 deny。");
        Assert.AreEqual(ToolCallClassifierPipeline.ReasonPermanentDowngraded, verdict.ReasonCode);
        Assert.IsTrue(verdict.Reason!.Contains("deny_permanent=0.1"), "降级理由注明判定键与可信度。");
        Assert.AreEqual(0, audit.Events.Count, "③ 场景无候选 deny，不落覆盖审计（§14.13.4）。");
    }

    // —— ⑧c 永久类缺失逐分类可信度 ⇒ 视为低于门槛，降级（对齐规则溯源契约「缺失视为低于阈值」） ——

    [TestMethod]
    public async Task ClassifyAsync_AllowPermanentMissingConfidence_DowngradesToAllowOnce()
    {
        var rules = new IToolCallClassifier[]
        {
            new StubClassifier("system-rules", ClassificationOutcome.Unknown),
        };
        var arbiter = new StubClassifier("arbiter-stub", ClassificationOutcome.AllowPermanent)
        {
            Reason = "建议永久放行但未给逐分类可信度",
            PerOutcomeConfidence = null,
        };
        var pipeline = CreatePipeline(rules, arbiter);

        var verdict = await pipeline.ClassifyAsync(ToolContext());

        Assert.AreEqual(ClassificationOutcome.AllowOnce, verdict.Outcome, "可信度缺失 ⇒ 视为低于门槛 ⇒ 降级。");
        Assert.AreEqual(ToolCallClassifierPipeline.ReasonPermanentDowngraded, verdict.ReasonCode);
        Assert.IsTrue(verdict.Reason!.Contains("allow_permanent=missing"));
    }

    // —— ⑨ 单次求值对仲裁至多调用一次（多规则环 + 候选场景） ——

    [TestMethod]
    public async Task ClassifyAsync_ArbiterInvokedAtMostOnce()
    {
        var rules = new IToolCallClassifier[]
        {
            new StubClassifier("rules-a", ClassificationOutcome.Unknown),
            new StubClassifier("rules-b", ClassificationOutcome.DenyOnce) { AppliedRuleId = "rule-deny-1" },
            new StubClassifier("rules-c", ClassificationOutcome.Unknown),
        };
        var arbiter = new StubClassifier("arbiter-stub", ClassificationOutcome.AllowOnce);
        var pipeline = CreatePipeline(rules, arbiter);

        var verdict = await pipeline.ClassifyAsync(ToolContext());

        Assert.AreEqual(ClassificationOutcome.AllowOnce, verdict.Outcome);
        Assert.AreEqual(1, arbiter.CallCount, "§14.13.5：同一次求值只调用仲裁一次。");
    }

    // —— ⑩ 外层取消令牌触发 ⇒ OperationCanceledException 照常传播 ——

    [TestMethod]
    public async Task ClassifyAsync_OuterCancellation_PropagatesOperationCanceledException()
    {
        // ① 令牌已取消：入口取消检查即抛。
        var pipeline = CreatePipeline(
            [new StubClassifier("system-rules", ClassificationOutcome.Unknown)],
            new StubClassifier("arbiter-stub", ClassificationOutcome.AllowOnce));
        await Assert.ThrowsAsync<OperationCanceledException>(
            () => pipeline.ClassifyAsync(ToolContext(), new CancellationToken(canceled: true)),
            "已取消令牌必须让 OCE 照常传播。");

        // ② 仲裁执行中外层令牌触发：OCE 不得被折叠成 Unknown（区别于独立超时）。
        var slowArbiter = new StubClassifier("arbiter-stub", ClassificationOutcome.AllowOnce)
        {
            Delay = Timeout.InfiniteTimeSpan,
        };
        var gatedPipeline = CreatePipeline(
            [new StubClassifier("system-rules", ClassificationOutcome.Unknown)],
            slowArbiter);
        using var cts = new CancellationTokenSource(50);
        await Assert.ThrowsAsync<OperationCanceledException>(
            () => gatedPipeline.ClassifyAsync(ToolContext(), cts.Token),
            "仲裁执行中的外层取消同样必须传播。");
    }

    // —— ⑪ 防循环：候选 deny 规则由分类器自身产出 ⇒ 不回调仲裁（§11.3 / §14.13.5） ——

    [TestMethod]
    public async Task ClassifyAsync_ClassifierAuthoredDenyRule_NoArbiterCall_FinalDeny()
    {
        var classifierAuthoredAudit = new RecordingAuditStore();
        classifierAuthoredAudit.Seed(CuratorEvent("rule-clf-deny-1", ToolApprovalAllowlistRuleSource.Classifier));
        var rules = new IToolCallClassifier[]
        {
            new StubClassifier("system-rules", ClassificationOutcome.DenyOnce)
            {
                AppliedRuleId = "rule-clf-deny-1",
                Reason = "命中 deny 规则 rule-clf-deny-1（候选）",
            },
        };

        // ① 规则来源=Classifier ⇒ 复用分类器裁决，仲裁零调用。
        var arbiter = new StubClassifier("arbiter-stub", ClassificationOutcome.AllowOnce);
        var pipeline = CreatePipeline(rules, arbiter, classifierAuthoredAudit);
        var verdict = await pipeline.ClassifyAsync(ToolContext());
        Assert.AreEqual(ClassificationOutcome.DenyOnce, verdict.Outcome, "分类器自产 deny 规则即分类器裁决，生效 deny。");
        Assert.AreEqual(0, arbiter.CallCount, "防循环：不得再请求仲裁（§11.3）。");
        Assert.AreEqual(ToolCallClassifierPipeline.ReasonClassifierRuleDenyFinal, verdict.ReasonCode);
        Assert.AreEqual(1, classifierAuthoredAudit.Events.Count, "防循环路径不是覆盖，不新增审计。");

        // ② 对照：同形状规则来源=Human ⇒ 仍须给仲裁覆盖机会。
        var humanAudit = new RecordingAuditStore();
        humanAudit.Seed(CuratorEvent("rule-clf-deny-1", ToolApprovalAllowlistRuleSource.Human));
        var humanArbiter = new StubClassifier("arbiter-stub", ClassificationOutcome.AllowOnce);
        var humanPipeline = CreatePipeline(rules, humanArbiter, humanAudit);
        var humanVerdict = await humanPipeline.ClassifyAsync(ToolContext());
        Assert.AreEqual(1, humanArbiter.CallCount, "人工规则候选 deny 必须给仲裁覆盖机会。");
        Assert.AreEqual(ClassificationOutcome.AllowOnce, humanVerdict.Outcome);

        // ③ 对照：审计不可读（无法判定来源）⇒ 按普通候选处理，仍请求仲裁。
        var unreadableAudit = new RecordingAuditStore { ThrowOnList = true };
        var unreadableArbiter = new StubClassifier("arbiter-stub", ClassificationOutcome.AllowOnce);
        var unreadablePipeline = CreatePipeline(rules, unreadableArbiter, unreadableAudit);
        var unreadableVerdict = await unreadablePipeline.ClassifyAsync(ToolContext());
        Assert.AreEqual(1, unreadableArbiter.CallCount, "无法判定来源时按普通候选处理。");
        Assert.AreEqual(ClassificationOutcome.AllowOnce, unreadableVerdict.Outcome);
    }

    // —— ⑫（加分）覆盖审计字段完备：candidate/final/classifier/confidence 均可从 Reason 解析 ——

    [TestMethod]
    public async Task ClassifyAsync_OverrideAudit_ReasonFieldsAreParseable()
    {
        var rules = new IToolCallClassifier[]
        {
            new StubClassifier("system-rules", ClassificationOutcome.DenyOnce)
            {
                AppliedRuleId = "rule-deny-1",
                Reason = "命中 deny 规则 rule-deny-1（候选）",
            },
        };
        var arbiter = new StubClassifier("claude-arbiter", ClassificationOutcome.DenyOnce)
        {
            Reason = "仲裁确认拒绝",
            ClassifierModel = "claude-x",
            PerOutcomeConfidence = Confidences(allowPermanent: 0.95, denyOnce: 0.66),
        };
        var audit = new RecordingAuditStore();
        var pipeline = CreatePipeline(rules, arbiter, audit);

        await pipeline.ClassifyAsync(ToolContext(argumentsJson: "{\"k\":1}"));

        var auditEvent = audit.Events.Single();
        var reason = auditEvent.Reason!;
        Assert.AreEqual("DenyOnce", Extract(reason, "candidate"));
        Assert.AreEqual("DenyOnce", Extract(reason, "final"));
        Assert.AreEqual("claude-arbiter", Extract(reason, "classifier"));
        Assert.AreEqual("claude-x", Extract(reason, "model"));
        Assert.AreEqual(0.66, ParseDouble(Extract(reason, "confidence")), 1e-9);
        Assert.AreEqual(0.90, ParseDouble(Extract(reason, "threshold")), 1e-9);
        Assert.AreEqual("rule-deny-1", Extract(reason, "candidate_rule"));
        Assert.IsTrue(reason.Contains("arbiter_reason=仲裁确认拒绝"), "覆盖理由保留仲裁原始理由。");
        Assert.IsTrue(reason.Contains("candidate_reason=命中 deny 规则 rule-deny-1（候选）"), "保留原始候选理由。");
        Assert.AreEqual("claude-arbiter", auditEvent.ClassifierId);
        AssertClose(0.66, auditEvent.ClassifierConfidence, "审计记录可信度。");
        Assert.AreEqual(T0, auditEvent.CreatedAtUtc, "CreatedAtUtc 用注入时钟。");
    }

    // —— 测试辅助 ——

    private static ToolCallClassifierPipeline CreatePipeline(
        IToolCallClassifier[] rules,
        IToolCallClassifier arbiter,
        RecordingAuditStore? auditStore = null,
        ToolCallClassifierPipelineOptions? options = null)
        => new(rules, arbiter, auditStore ?? new RecordingAuditStore(), new FakeClock(), options);

    private static ToolApprovalAuditEvent CuratorEvent(string ruleId, ToolApprovalAllowlistRuleSource source) => new()
    {
        EventId = Guid.NewGuid().ToString("N"),
        EventType = source == ToolApprovalAllowlistRuleSource.Classifier
            ? ToolApprovalAuditEventType.DenylistRuleCreated
            : ToolApprovalAuditEventType.AllowlistRuleCreated,
        AllowlistRuleId = ruleId,
        Source = source,
        Effect = ToolApprovalRuleEffect.Deny,
        Reason = "curator deposit",
        CreatedAtUtc = T0,
    };

    private static ToolCallClassificationContext ToolContext(
        string? argumentsJson = null,
        string toolId = "terminal_execute",
        string workspaceId = TestWorkspace)
        => new()
        {
            ToolId = toolId,
            CommandName = "git status",
            ArgumentsJson = argumentsJson,
            WorkingDirectory = "E:/repo",
            Shell = "pwsh",
            WorkspaceId = workspaceId,
            SessionId = "sess-1",
            AgentInstanceId = "agent-1",
            UserId = "user-1",
        };

    private static IReadOnlyDictionary<string, double> Confidences(
        double allowPermanent,
        double allowOnce = 0.5,
        double denyOnce = 0.4,
        double denyPermanent = 0.3)
        => new Dictionary<string, double>
        {
            ["allow_permanent"] = allowPermanent,
            ["allow_once"] = allowOnce,
            ["deny_once"] = denyOnce,
            ["deny_permanent"] = denyPermanent,
        };

    /// <summary>从结构化 Reason 前缀解析 key=value（结构化键先于原始理由出现，取首个匹配）。</summary>
    private static string Extract(string reason, string key)
    {
        var match = Regex.Match(reason, $@"(?:^|; ){Regex.Escape(key)}=([^;]+)");
        Assert.IsTrue(match.Success, $"Reason 应包含可解析的 {key}= 字段：{reason}");
        return match.Groups[1].Value;
    }

    private static double ParseDouble(string text)
        => double.Parse(text, CultureInfo.InvariantCulture);

    /// <summary>双精度容差断言（MSTest 4 无 (double, double, delta) 重载）。</summary>
    private static void AssertClose(double expected, double? actual, string message)
    {
        Assert.IsNotNull(actual, message);
        Assert.IsTrue(
            Math.Abs(expected - actual.Value) < 1e-9,
            $"{message}（期望 {expected}，实际 {actual.Value}）");
    }

    /// <summary>固定时钟：时间与计时刻度只经注入的 TimeProvider 获取。</summary>
    private sealed class FakeClock : TimeProvider
    {
        private long _timestamp;

        public DateTimeOffset Now { get; private set; } = T0;

        public void Advance(TimeSpan delta)
        {
            Now += delta;
            _timestamp += (long)(delta.TotalSeconds * TimestampFrequency);
        }

        public override DateTimeOffset GetUtcNow() => Now;

        public override long GetTimestamp() => _timestamp;
    }

    /// <summary>可配置分类器假件：可模拟命中/未命中/抛异常/可控延迟，并统计调用次数。</summary>
    private sealed class StubClassifier : IToolCallClassifier
    {
        private readonly ClassificationOutcome _outcome;

        public StubClassifier(string classifierId, ClassificationOutcome outcome)
        {
            ClassifierId = classifierId;
            _outcome = outcome;
        }

        public string ClassifierId { get; }

        public string Reason { get; init; } = "stub-verdict";

        public string? ReasonCode { get; init; }

        public string? AppliedRuleId { get; init; }

        public IReadOnlyDictionary<string, double>? PerOutcomeConfidence { get; init; }

        public string? ClassifierModel { get; init; }

        public Exception? ThrowOnCall { get; init; }

        /// <summary>每次调用的延迟；观察传入令牌（管线传链接 CTS 令牌 ⇒ 超时/外层取消都能打断）。</summary>
        public TimeSpan? Delay { get; init; }

        public int CallCount { get; private set; }

        public async Task<ClassificationVerdict> ClassifyAsync(
            ToolCallClassificationContext context,
            CancellationToken ct = default)
        {
            CallCount++;
            if (ThrowOnCall is not null)
            {
                throw ThrowOnCall;
            }

            if (Delay is not null)
            {
                await Task.Delay(Delay.Value, ct).ConfigureAwait(false);
            }

            return new ClassificationVerdict
            {
                Outcome = _outcome,
                Reason = Reason,
                ReasonCode = ReasonCode,
                ClassifierId = ClassifierId,
                ClassifierModel = ClassifierModel,
                PerOutcomeConfidence = PerOutcomeConfidence,
                AppliedRuleId = AppliedRuleId,
            };
        }
    }

    /// <summary>记录型审计存储假件：可预置策展事件、可模拟读取故障。</summary>
    private sealed class RecordingAuditStore : IToolApprovalAuditStore
    {
        public List<ToolApprovalAuditEvent> Events { get; } = [];

        public bool ThrowOnList { get; init; }

        public void Seed(ToolApprovalAuditEvent auditEvent) => Events.Add(auditEvent);

        public Task SaveAsync(ToolApprovalAuditEvent auditEvent, CancellationToken ct = default)
        {
            Events.Add(auditEvent);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<ToolApprovalAuditEvent>> ListAsync(CancellationToken ct = default)
        {
            if (ThrowOnList)
            {
                throw new InvalidOperationException("audit-list-boom");
            }

            IReadOnlyList<ToolApprovalAuditEvent> snapshot = Events.ToList();
            return Task.FromResult(snapshot);
        }
    }
}
