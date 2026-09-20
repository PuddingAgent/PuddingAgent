using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using PuddingCode.Classification;
using PuddingCode.Tools;
using PuddingRuntime.Classification;
using PuddingRuntime.Services.Tools;

namespace PuddingRuntimeTests.Tools;

/// <summary>
/// 审批门户（request_tool_approval → 门户，方案 v2 §14，切片 S4）的离线单元测试。
/// <para>
/// 覆盖映射（§14.11 验收逐条对应）：
/// 1 零厂商依赖 ⇒ 全文件只引用抽象（IToolCallClassifier / IAgentFullAccessGrantService）；
/// 2 完全访问时长 ⇒ <c>FullAccessRequest_600Seconds…</c> / <c>FullAccess_Expires…</c>；
/// 3 分类器不可用 ⇒ <c>Classify_ClassifierMissing…</c> / <c>Classify_UnknownOutcome…</c> / <c>FullAccessRequest_ClassifierMissing…</c>；
    /// 4 缓存/快路径 ⇒ <c>Classify_ClassifierDenyRuleReused_ArbiterNotConsultedAgain…</c>（分类器自身永久 deny 规则命中后计数不变）；
    ///   对照：<c>Classify_AfterManualDenyRule_ArbiterConsultedOnce_OverrideAllowed…</c>（人工规则是候选 ⇒ 仍给一次覆盖机会）；
/// 5 冲突策略 ⇒ <c>RulesUpdate_SameKeyAllowThenDeny_DenyWins…</c>；
/// 6 向后兼容 ⇒ <c>ExecuteAsync_WithoutAction_BehavesLikeSubmit…</c>（工具级）；
/// 7 范围隔离 ⇒ <c>FullAccess_ScopeIsolation…</c>。
/// 全部零网络：注入假时钟、内存 store 与计数假分类器。
/// </para>
/// </summary>
[TestClass]
public sealed class ToolApprovalPortalTests
{
    private const string WorkspaceA = "ws-a";
    private const string WorkspaceB = "ws-b";
    private const string AgentA = "agent-a";
    private const string AgentB = "agent-b";
    private static readonly DateTimeOffset T0 = new(2026, 9, 21, 10, 0, 0, TimeSpan.Zero);

    // ---------- classify（§14.5） ----------

    [TestMethod]
    public async Task Classify_AllowOnce_ReturnsWireOutcome_AuditsInvoked_CreatesNoRule()
    {
        var (portal, allowlist, audit, _, classifier) = CreatePortal(
            () => Verdict(ClassificationOutcome.AllowOnce, classifierId: "fake-arbiter"));

        var json = await portal.ClassifyAsync(Request(commandName: "dotnet test"));

        var payload = Json(json);
        Assert.AreEqual("classify", payload.RootElement.GetProperty("action").GetString());
        Assert.AreEqual("allow_once", payload.RootElement.GetProperty("outcome").GetString());
        Assert.IsFalse(payload.RootElement.GetProperty("deferred").GetBoolean());
        Assert.AreEqual("fake-arbiter", payload.RootElement.GetProperty("classifier_id").GetString());
        Assert.IsNull(payload.RootElement.GetProperty("rule_id").GetString(), "单次类不落规则。");
        Assert.AreEqual(1, classifier.CallCount);
        Assert.AreEqual(0, (await allowlist.ListAsync()).Count(r => r.Source == ToolApprovalAllowlistRuleSource.Classifier));
        var events = await ListAsync(audit);
        Assert.AreEqual(1, events.Count(e => e.EventType == ToolApprovalAuditEventType.ClassifierInvoked));
        Assert.IsFalse(events.Any(e => e.EventType == ToolApprovalAuditEventType.ClassifierUnavailable));
    }

    [TestMethod]
    public async Task Classify_DenyPermanent_CreatesDenyRuleViaCurator_ReturnsRuleId()
    {
        var (portal, allowlist, audit, _, _) = CreatePortal(
            () => Verdict(ClassificationOutcome.DenyPermanent, classifierId: "fake-arbiter"));

        var json = await portal.ClassifyAsync(Request(commandName: "dotnet format"));

        var payload = Json(json);
        Assert.AreEqual("deny_permanent", payload.RootElement.GetProperty("outcome").GetString());
        var ruleId = payload.RootElement.GetProperty("rule_id").GetString();
        Assert.IsFalse(string.IsNullOrEmpty(ruleId), "永久类必须落规则并回传 rule_id。");
        Assert.IsTrue(payload.RootElement.GetProperty("rule_created").GetBoolean());

        var rule = (await allowlist.ListAsync()).Single(r => r.RuleId == ruleId);
        Assert.AreEqual(ToolApprovalRuleEffect.Deny, rule.Effect);
        Assert.AreEqual(ToolApprovalAllowlistRuleSource.Classifier, rule.Source);
        Assert.AreEqual(ToolApprovalAllowlistRuleStatus.Enabled, rule.Status);
        Assert.AreEqual("fake-arbiter", rule.SourceClassifierId, "溯源必须写明产出分类器。");

        var events = await ListAsync(audit);
        Assert.IsTrue(events.Any(e => e.EventType == ToolApprovalAuditEventType.DenylistRuleCreated));
    }

    [TestMethod]
    public async Task Classify_UnknownOutcome_ReturnsDeferred_NoRule_NoGrant()
    {
        var (portal, allowlist, _, _, classifier) = CreatePortal(
            () => Verdict(
                ClassificationOutcome.Unknown,
                classifierId: "pipeline",
                reasonCode: "classifier.pipeline.arbiter_unavailable"));

        var json = await portal.ClassifyAsync(Request(commandName: "dotnet test"));

        var payload = Json(json);
        Assert.AreEqual("deferred", payload.RootElement.GetProperty("outcome").GetString(), "Unknown 不得渲染成 allow/deny。");
        Assert.IsTrue(payload.RootElement.GetProperty("deferred").GetBoolean());
        Assert.AreEqual(0, (await allowlist.ListAsync()).Count(r => r.Source == ToolApprovalAllowlistRuleSource.Classifier), "deferred ⇒ 规则库零新增。");
        Assert.AreEqual(1, classifier.CallCount);

        // 完全访问未被授予：同一 portal 的状态查询必须为空。
        var status = Json(await portal.FullAccessStatusAsync(Request()));
        Assert.IsFalse(status.RootElement.GetProperty("active").GetBoolean(), "deferred 的 classify 绝不授予完全访问。");
    }

    [TestMethod]
    public async Task Classify_ClassifierMissing_ReturnsDeferred_ZeroRules_AuditsUnavailable()
    {
        var allowlist = new InMemoryToolApprovalAllowlistStore();
        var audit = new InMemoryToolApprovalAuditStore();
        var clock = new FakeClock();
        var portal = new ToolApprovalPortalService(allowlist, audit, classifier: null, timeProvider: clock);
        var beforeCount = (await allowlist.ListAsync()).Count;

        var json = await portal.ClassifyAsync(Request(commandName: "dotnet test"));

        var payload = Json(json);
        Assert.AreEqual("deferred", payload.RootElement.GetProperty("outcome").GetString());
        Assert.AreEqual("classifier.not_configured", payload.RootElement.GetProperty("reason_code").GetString());
        Assert.AreEqual(beforeCount, (await allowlist.ListAsync()).Count, "分类器不可用 ⇒ 规则库零新增。");

        var events = await ListAsync(audit);
        Assert.AreEqual(1, events.Count(e => e.EventType == ToolApprovalAuditEventType.ClassifierUnavailable));
    }

    // ---------- 快路径 / 缓存（§14.11-4） ----------

    [TestMethod]
    public async Task Classify_AfterManualDenyRule_ArbiterConsultedOnce_OverrideAllowed()
    {
        // 门户人工写入的规则是**候选**权威（§14.12.2）：命中 deny 时分类器仍须有一次覆盖机会（§11.3）。
        var allowlist = new InMemoryToolApprovalAllowlistStore();
        var audit = new InMemoryToolApprovalAuditStore();
        var clock = new FakeClock();
        var arbiter = new CountingClassifier(Verdict(ClassificationOutcome.AllowOnce, classifierId: "fake-arbiter"));
        var pipeline = new ToolCallClassifierPipeline(
            [new SystemRuleClassifier(allowlist, clock)],
            arbiter,
            audit,
            clock);
        var portal = new ToolApprovalPortalService(allowlist, audit, pipeline, timeProvider: clock);

        // ① 无规则 ⇒ 规则分类器 Unknown ⇒ 仲裁一次（计数 = 1）。
        var first = Json(await portal.ClassifyAsync(Request(commandName: "dotnet test")));
        Assert.AreEqual("allow_once", first.RootElement.GetProperty("outcome").GetString());
        Assert.AreEqual(1, arbiter.CallCount);

        // ② 门户加 deny 规则 ⇒ 必须落为 Source=Human（人工/候选权威），且不得携带分类器身份。
        var added = Json(await portal.RulesUpdateAsync(Request(commandName: "dotnet test", ruleOp: "add", ruleEffect: "deny")));
        Assert.IsTrue(added.RootElement.GetProperty("applied").GetBoolean());
        var manualRule = (await allowlist.ListAsync())
            .Single(r => r.RuleId == added.RootElement.GetProperty("rule_id").GetString());
        Assert.AreEqual(
            ToolApprovalAllowlistRuleSource.Human,
            manualRule.Source,
            "门户人工规则必须是候选权威，不得标为分类器终局（否则人工黑名单会变成分类器也无权覆盖的封锁）。");
        Assert.IsNull(manualRule.SourceClassifierId, "人工规则不得冒充分类器产物。");

        // ③ 同一调用再 classify ⇒ deny 候选命中 ⇒ **必须**给分类器一次覆盖机会（计数 +1）⇒ 分类器放行 ⇒ 覆盖生效。
        var second = Json(await portal.ClassifyAsync(Request(commandName: "dotnet test")));
        Assert.AreEqual("allow_once", second.RootElement.GetProperty("outcome").GetString(), "分类器应能覆盖人工 deny（§11.3 覆盖权）。");
        Assert.AreEqual(2, arbiter.CallCount, "人工 deny 规则是候选 ⇒ 必须请求仲裁一次（绝不因人工规则而跳过）。");
    }

    [TestMethod]
    public async Task Classify_ClassifierDenyRuleReused_ArbiterNotConsultedAgain_CallCountUnchanged()
    {
        // 只有分类器**自身产出**的永久 deny 规则才是终局：命中后复用裁决、不再回调仲裁（§14.13.5 防循环 / §14.11-4 缓存）。
        var allowlist = new InMemoryToolApprovalAllowlistStore();
        var audit = new InMemoryToolApprovalAuditStore();
        var clock = new FakeClock();
        // 经管线 ⇒ 必须给足逐分类可信度（≥0.90），否则永久类会被门槛降级为 deny_once、根本不会沉淀规则。
        var arbiter = new CountingClassifier(Verdict(
            ClassificationOutcome.DenyPermanent,
            classifierId: "fake-arbiter",
            permanentConfidenceKey: "deny_permanent"));
        var pipeline = new ToolCallClassifierPipeline(
            [new SystemRuleClassifier(allowlist, clock)],
            arbiter,
            audit,
            clock);
        var portal = new ToolApprovalPortalService(allowlist, audit, pipeline, timeProvider: clock);

        // ① 首次 classify ⇒ DenyPermanent ⇒ 沉淀规则（Source=Classifier，带分类器身份）。
        var first = Json(await portal.ClassifyAsync(Request(commandName: "dotnet test")));
        Assert.AreEqual("deny_permanent", first.RootElement.GetProperty("outcome").GetString());
        Assert.AreEqual(1, arbiter.CallCount);
        var classifierRule = (await allowlist.ListAsync()).Single(r => r.Effect == ToolApprovalRuleEffect.Deny);
        Assert.AreEqual(ToolApprovalAllowlistRuleSource.Classifier, classifierRule.Source);
        Assert.IsNotNull(classifierRule.SourceClassifierId, "分类器产物必须带分类器身份。");

        // ② 同一调用再 classify ⇒ 命中分类器自身永久规则 ⇒ 防循环复用，仲裁调用计数不变。
        var second = Json(await portal.ClassifyAsync(Request(commandName: "dotnet test")));
        Assert.AreEqual("deny_once", second.RootElement.GetProperty("outcome").GetString());
        Assert.AreEqual(1, arbiter.CallCount, "分类器自身永久规则命中后绝不重问仲裁（调用计数不变）。");
    }

    // ---------- rules_list / rules_update（§14.1/§14.5） ----------

    [TestMethod]
    public async Task RulesUpdate_AddDeny_ThenRulesList_ShowsBothEffectsWithProvenance()
    {
        var (portal, allowlist, _, _, _) = CreatePortal();

        var added = Json(await portal.RulesUpdateAsync(Request(
            commandName: "dotnet test", ruleOp: "add", ruleEffect: "deny", reason: "unit test deny")));
        Assert.IsTrue(added.RootElement.GetProperty("applied").GetBoolean());
        var ruleId = added.RootElement.GetProperty("rule_id").GetString()!;

        var list = Json(await portal.RulesListAsync(Request(commandName: "dotnet test")));
        var rules = list.RootElement.GetProperty("rules");
        var deny = rules.EnumerateArray().Single(r => r.GetProperty("rule_id").GetString() == ruleId);
        Assert.AreEqual("deny", deny.GetProperty("effect").GetString());
        Assert.AreEqual("human", deny.GetProperty("source").GetString(), "人工规则必须是候选权威（§14.12.2），不得冒充分类器终局裁决。");
        Assert.AreEqual("enabled", deny.GetProperty("status").GetString());
        Assert.IsTrue(
            !deny.TryGetProperty("source_classifier_id", out var scid) || scid.ValueKind == JsonValueKind.Null,
            "人工规则不得携带分类器身份（SourceClassifierId 必须为空）。");
        Assert.IsTrue(deny.GetProperty("hit_count").GetInt64() >= 1);

        // 黑名单与白名单都要可见：内置 allow 规则（全局）也在列表中。
        Assert.IsTrue(rules.EnumerateArray().Any(r => r.GetProperty("effect").GetString() == "allow"));
        Assert.AreEqual(1, (await allowlist.ListAsync()).Count(r => r.Effect == ToolApprovalRuleEffect.Deny && r.RuleId == ruleId));
    }

    [TestMethod]
    public async Task RulesUpdate_Add_NarrownessHit_RejectsPermanentRule_WithViolationDetail()
    {
        var (portal, allowlist, audit, _, _) = CreatePortal();

        var json = await portal.RulesUpdateAsync(Request(
            commandName: "dotnet build & dir bin", ruleOp: "add", ruleEffect: "deny"));

        var payload = Json(json);
        Assert.IsFalse(payload.RootElement.GetProperty("applied").GetBoolean(), "尽窄命中 ⇒ 拒绝落永久规则。");
        Assert.IsTrue(payload.RootElement.GetProperty("degraded").GetBoolean());
        var degradeReason = payload.RootElement.GetProperty("degrade_reason").GetString();
        Assert.IsFalse(string.IsNullOrWhiteSpace(degradeReason));
        Assert.IsTrue(degradeReason!.Contains("14.12.5-1", StringComparison.Ordinal), $"返回必须说明命中了哪一条（§14.12.5-1），实际：{degradeReason}");
        Assert.AreEqual(0, (await allowlist.ListAsync()).Count(r => r.Effect == ToolApprovalRuleEffect.Deny), "尽窄命中 ⇒ 规则库零新增。");
        Assert.IsFalse((await ListAsync(audit)).Any(e => e.EventType == ToolApprovalAuditEventType.DenylistRuleCreated));
    }

    [TestMethod]
    public async Task RulesUpdate_Disable_SoftDisables_KeepsAuditChain_Idempotent()
    {
        var (portal, allowlist, audit, _, _) = CreatePortal();

        var added = Json(await portal.RulesUpdateAsync(Request(
            commandName: "dotnet test", ruleOp: "add", ruleEffect: "deny")));
        var ruleId = added.RootElement.GetProperty("rule_id").GetString()!;

        var disabled = Json(await portal.RulesUpdateAsync(Request(commandName: "dotnet test", ruleOp: "disable", ruleId: ruleId)));
        Assert.IsTrue(disabled.RootElement.GetProperty("applied").GetBoolean());
        Assert.AreEqual("disabled", disabled.RootElement.GetProperty("status").GetString());

        var rule = (await allowlist.ListAsync()).Single(r => r.RuleId == ruleId);
        Assert.AreEqual(ToolApprovalAllowlistRuleStatus.Disabled, rule.Status, "不硬删除，置 Disabled 保留审计链。");
        Assert.AreEqual(1, (await ListAsync(audit)).Count(e => e.EventType == ToolApprovalAuditEventType.DenylistRuleDisabled));

        // 重复 disable 幂等：成功且不重复落审计。
        var again = Json(await portal.RulesUpdateAsync(Request(commandName: "dotnet test", ruleOp: "disable", ruleId: ruleId)));
        Assert.IsTrue(again.RootElement.GetProperty("applied").GetBoolean());
        Assert.AreEqual(1, (await ListAsync(audit)).Count(e => e.EventType == ToolApprovalAuditEventType.DenylistRuleDisabled), "重复禁用不得重复落审计。");
    }

    [TestMethod]
    public async Task RulesUpdate_DisableUnknownRuleId_ExplicitFailure_NoFakeSuccess()
    {
        var (portal, allowlist, audit, _, _) = CreatePortal();

        var json = await portal.RulesUpdateAsync(Request(commandName: "dotnet test", ruleOp: "disable", ruleId: "no-such-rule"));

        var payload = Json(json);
        Assert.IsFalse(payload.RootElement.GetProperty("applied").GetBoolean(), "rule_id 不存在 ⇒ 明确失败，不伪造成功。");
        Assert.AreEqual("rule_not_found", payload.RootElement.GetProperty("error").GetString());
        Assert.AreEqual(0, (await ListAsync(audit)).Count(e => e.EventType == ToolApprovalAuditEventType.DenylistRuleDisabled));
        Assert.AreEqual(0, (await allowlist.ListAsync()).Count(r => r.RuleId == "no-such-rule"));
    }

    [TestMethod]
    public async Task RulesUpdate_DisableRuleOfOtherWorkspace_Rejected()
    {
        var (portal, allowlist, _, _, _) = CreatePortal();

        var added = Json(await portal.RulesUpdateAsync(Request(
            commandName: "dotnet test", ruleOp: "add", ruleEffect: "deny")));
        var ruleId = added.RootElement.GetProperty("rule_id").GetString()!;

        var cross = Json(await portal.RulesUpdateAsync(Request(
            commandName: "dotnet test", ruleOp: "disable", ruleId: ruleId, workspaceId: WorkspaceB)));

        Assert.IsFalse(cross.RootElement.GetProperty("applied").GetBoolean());
        Assert.AreEqual("rule_not_in_this_workspace", cross.RootElement.GetProperty("error").GetString());
        Assert.AreEqual(
            ToolApprovalAllowlistRuleStatus.Enabled,
            (await allowlist.ListAsync()).Single(r => r.RuleId == ruleId).Status,
            "跨工作区禁用必须被拒绝。");
    }

    [TestMethod]
    public async Task RulesUpdate_SameKeyAllowThenDeny_DenyWins_AuditsConflict()
    {
        var (portal, allowlist, audit, _, _) = CreatePortal();

        var allowAdded = Json(await portal.RulesUpdateAsync(Request(
            commandName: "dotnet test", ruleOp: "add", ruleEffect: "allow")));
        var allowRuleId = allowAdded.RootElement.GetProperty("rule_id").GetString()!;

        var denyAdded = Json(await portal.RulesUpdateAsync(Request(
            commandName: "dotnet test", ruleOp: "add", ruleEffect: "deny")));
        Assert.IsTrue(denyAdded.RootElement.GetProperty("applied").GetBoolean());
        Assert.IsTrue(denyAdded.RootElement.GetProperty("conflict_detected").GetBoolean(), "同键相反 Effect 必须上报冲突。");
        Assert.AreEqual(allowRuleId, denyAdded.RootElement.GetProperty("conflicting_rule_id").GetString());

        var events = await ListAsync(audit);
        Assert.IsTrue(events.Any(e => e.EventType == ToolApprovalAuditEventType.RuleConflictDetected), "冲突必须落 RuleConflictDetected 审计。");

        // 生效 deny：SystemRuleClassifier 同键 allow+deny ⇒ deny 候选（deny 胜）。
        var verdict = await new SystemRuleClassifier(allowlist).ClassifyAsync(Context("dotnet test"));
        Assert.AreEqual(ClassificationOutcome.DenyOnce, verdict.Outcome, "同键 allow+deny ⇒ 生效 deny。");
        Assert.AreEqual(2, (await allowlist.ListAsync()).Count(r => r.Command == "dotnet test" && r.Status == ToolApprovalAllowlistRuleStatus.Enabled));
    }

    [TestMethod]
    public async Task Rules_WorkWithoutClassifier_ZeroNetwork()
    {
        // §14.7：分类器不可用 ⇒ rules_* 照常工作（本地确定性数据，零网络依赖）。
        var allowlist = new InMemoryToolApprovalAllowlistStore();
        var audit = new InMemoryToolApprovalAuditStore();
        var portal = new ToolApprovalPortalService(allowlist, audit, classifier: null, timeProvider: new FakeClock());

        var added = Json(await portal.RulesUpdateAsync(Request(commandName: "dotnet test", ruleOp: "add", ruleEffect: "allow")));
        Assert.IsTrue(added.RootElement.GetProperty("applied").GetBoolean());

        var list = Json(await portal.RulesListAsync(Request()));
        Assert.IsTrue(list.RootElement.GetProperty("count").GetInt32() >= 1);

        var disabled = Json(await portal.RulesUpdateAsync(Request(
            commandName: "dotnet test", ruleOp: "disable", ruleId: added.RootElement.GetProperty("rule_id").GetString())));
        Assert.IsTrue(disabled.RootElement.GetProperty("applied").GetBoolean());
    }

    // ---------- full_access_*（§14.5/§14.6/§14.7） ----------

    [TestMethod]
    public async Task FullAccessRequest_AllowOnce_GrantsDefault300_ServerSideClock()
    {
        var (portal, _, audit, clock, classifier) = CreatePortal(
            () => Verdict(ClassificationOutcome.AllowOnce, classifierId: "fake-arbiter"));

        var json = await portal.FullAccessRequestAsync(Request());

        var payload = Json(json);
        Assert.IsTrue(payload.RootElement.GetProperty("granted").GetBoolean());
        var grantId = payload.RootElement.GetProperty("grant_id").GetString();
        Assert.IsFalse(string.IsNullOrEmpty(grantId));
        Assert.AreEqual(300d, payload.RootElement.GetProperty("ttl_seconds").GetDouble(), "缺省 TTL = 300。");
        Assert.AreEqual("allow_once", payload.RootElement.GetProperty("outcome").GetString());
        Assert.AreEqual(1, classifier.CallCount, "full_access_request 必须经分类器裁决。");

        var events = await ListAsync(audit);
        Assert.AreEqual(1, events.Count(e => e.EventType == ToolApprovalAuditEventType.FullAccessRequested));
        Assert.AreEqual(1, events.Count(e => e.EventType == ToolApprovalAuditEventType.FullAccessGranted));

        var status = Json(await portal.FullAccessStatusAsync(Request()));
        Assert.IsTrue(status.RootElement.GetProperty("active").GetBoolean());
        Assert.AreEqual(grantId, status.RootElement.GetProperty("grant_id").GetString());
        Assert.AreEqual(300d, status.RootElement.GetProperty("ttl_remaining_seconds").GetDouble(), "到期时刻由服务端计时。");
    }

    [TestMethod]
    public async Task FullAccessRequest_600Seconds_Rejected_NotTruncated()
    {
        var (portal, _, audit, _, _) = CreatePortal(
            () => Verdict(ClassificationOutcome.AllowOnce, classifierId: "fake-arbiter"));

        var json = await portal.FullAccessRequestAsync(Request(fullAccessSeconds: 600));

        var payload = Json(json);
        Assert.IsFalse(payload.RootElement.GetProperty("granted").GetBoolean(), "600 秒 ⇒ 被拒（不是截断到 300）。");
        Assert.AreEqual("full_access.duration_exceeded", payload.RootElement.GetProperty("reason_code").GetString());
        Assert.IsTrue(payload.RootElement.GetProperty("duration_exceeded").GetBoolean(), "错误信息必须能区分「超上限被拒」与其它失败。");

        var status = Json(await portal.FullAccessStatusAsync(Request()));
        Assert.IsFalse(status.RootElement.GetProperty("active").GetBoolean(), "绝不发生截断到 300 的授予。");
        var events = await ListAsync(audit);
        Assert.IsFalse(events.Any(e => e.EventType == ToolApprovalAuditEventType.FullAccessGranted), "拒绝 ⇒ 不得有 Granted 审计。");
        Assert.IsTrue(events.Any(e => e.EventType == ToolApprovalAuditEventType.FullAccessRequested), "被拒请求同样先落 Requested 审计。");
    }

    [TestMethod]
    public async Task FullAccess_ExpiresAutomaticallyAfter300s_ByServerClock()
    {
        var (portal, _, audit, clock, _) = CreatePortal(
            () => Verdict(ClassificationOutcome.AllowOnce, classifierId: "fake-arbiter"));

        await portal.FullAccessRequestAsync(Request());
        clock.Advance(TimeSpan.FromSeconds(299));
        var before = Json(await portal.FullAccessStatusAsync(Request()));
        Assert.IsTrue(before.RootElement.GetProperty("active").GetBoolean(), "299 秒时仍生效（含边界语义）。");

        clock.Advance(TimeSpan.FromSeconds(1));
        var after = Json(await portal.FullAccessStatusAsync(Request()));
        Assert.IsFalse(after.RootElement.GetProperty("active").GetBoolean(), "300 秒到期 ⇒ 自动失效（服务端计时）。");

        var events = await ListAsync(audit);
        Assert.AreEqual(1, events.Count(e => e.EventType == ToolApprovalAuditEventType.FullAccessExpired), "被观测到失效时落 Expired 审计。");
    }

    [TestMethod]
    public async Task FullAccessRequest_ClassifierMissing_FailClosed_NotGranted()
    {
        var allowlist = new InMemoryToolApprovalAllowlistStore();
        var audit = new InMemoryToolApprovalAuditStore();
        var portal = new ToolApprovalPortalService(allowlist, audit, classifier: null, timeProvider: new FakeClock());

        var json = await portal.FullAccessRequestAsync(Request());

        var payload = Json(json);
        Assert.IsFalse(payload.RootElement.GetProperty("granted").GetBoolean(), "分类器不可用 ⇒ 一律拒绝（fail-closed，语义是放宽闸门）。");
        Assert.AreEqual("full_access.outcome_not_allowed", payload.RootElement.GetProperty("reason_code").GetString());

        var status = Json(await portal.FullAccessStatusAsync(Request()));
        Assert.IsFalse(status.RootElement.GetProperty("active").GetBoolean());

        var events = await ListAsync(audit);
        Assert.IsTrue(events.Any(e => e.EventType == ToolApprovalAuditEventType.ClassifierUnavailable));
        Assert.IsFalse(events.Any(e => e.EventType == ToolApprovalAuditEventType.FullAccessGranted));
        Assert.IsTrue(events.Any(e => e.EventType == ToolApprovalAuditEventType.FullAccessDenied));
    }

    [TestMethod]
    public async Task FullAccessRequest_DenyVerdict_FailClosed()
    {
        var (portal, _, _, _, _) = CreatePortal(
            () => Verdict(ClassificationOutcome.DenyOnce, classifierId: "fake-arbiter"));

        var json = await portal.FullAccessRequestAsync(Request());

        var payload = Json(json);
        Assert.IsFalse(payload.RootElement.GetProperty("granted").GetBoolean(), "仅 AllowOnce/AllowPermanent 才授予。");
        Assert.AreEqual("full_access.outcome_not_allowed", payload.RootElement.GetProperty("reason_code").GetString());
    }

    [TestMethod]
    public async Task FullAccess_ScopeIsolation_AgentAGrantDoesNotAffectAgentB()
    {
        var (portal, _, _, _, _) = CreatePortal(
            () => Verdict(ClassificationOutcome.AllowPermanent, classifierId: "fake-arbiter"));

        var granted = Json(await portal.FullAccessRequestAsync(Request()));
        Assert.IsTrue(granted.RootElement.GetProperty("granted").GetBoolean());

        var otherAgent = Json(await portal.FullAccessStatusAsync(Request(agentInstanceId: AgentB)));
        Assert.IsFalse(otherAgent.RootElement.GetProperty("active").GetBoolean(), "作用域 = workspace + agent_instance，不跨 Agent。");

        var otherWorkspace = Json(await portal.FullAccessStatusAsync(Request(workspaceId: WorkspaceB)));
        Assert.IsFalse(otherWorkspace.RootElement.GetProperty("active").GetBoolean(), "不跨 workspace。");

        var self = Json(await portal.FullAccessStatusAsync(Request()));
        Assert.IsTrue(self.RootElement.GetProperty("active").GetBoolean());
    }

    [TestMethod]
    public async Task FullAccessRevoke_RevokesImmediately_AndIdempotent()
    {
        var (portal, _, audit, _, _) = CreatePortal(
            () => Verdict(ClassificationOutcome.AllowOnce, classifierId: "fake-arbiter"));

        var granted = Json(await portal.FullAccessRequestAsync(Request()));
        var grantId = granted.RootElement.GetProperty("grant_id").GetString()!;

        var revoked = Json(await portal.FullAccessRevokeAsync(Request()));
        Assert.IsTrue(revoked.RootElement.GetProperty("revoked").GetBoolean());
        Assert.AreEqual(grantId, revoked.RootElement.GetProperty("grant_id").GetString());

        var status = Json(await portal.FullAccessStatusAsync(Request()));
        Assert.IsFalse(status.RootElement.GetProperty("active").GetBoolean(), "撤销 ⇒ 立即失效。");

        // 重复撤销幂等：成功语义、不报错、不重复落审计。
        var again = Json(await portal.FullAccessRevokeAsync(Request()));
        Assert.IsFalse(again.RootElement.GetProperty("revoked").GetBoolean());
        Assert.IsTrue((again.RootElement.GetProperty("reason").GetString() ?? "").Contains("no_active_grant", StringComparison.Ordinal));
        Assert.AreEqual(1, (await ListAsync(audit)).Count(e => e.EventType == ToolApprovalAuditEventType.FullAccessRevoked));
    }

    [TestMethod]
    public async Task FullAccessRequest_ZeroSeconds_FallsBackToDefaultTtl()
    {
        // 占位缺省语义对齐 S5a 授予服务：≤0 视为「未指定」⇒ 默认 300（而非拒绝）。
        var (portal, _, _, _, _) = CreatePortal(
            () => Verdict(ClassificationOutcome.AllowOnce, classifierId: "fake-arbiter"));

        var json = await portal.FullAccessRequestAsync(Request(fullAccessSeconds: 0));

        var payload = Json(json);
        Assert.IsTrue(payload.RootElement.GetProperty("granted").GetBoolean());
        Assert.AreEqual(300d, payload.RootElement.GetProperty("ttl_seconds").GetDouble());
    }

    // ---------- 工具级：向后兼容 + 参数校验 + 门户接线（§14.11-6） ----------

    [TestMethod]
    public async Task ExecuteAsync_WithoutAction_BehavesLikeSubmit()
    {
        using var harness = CreateToolHarness();
        const string argsWithoutAction = """{"tool_id":"shell","purpose":"run tests","necessity":"verify"}""";
        const string argsWithSubmit = """{"tool_id":"shell","purpose":"run tests","necessity":"verify","action":"submit"}""";

        var without = await harness.Tool.ExecuteAsync(ToolRequest(argsWithoutAction));
        var with = await harness.Tool.ExecuteAsync(ToolRequest(argsWithSubmit));

        Assert.IsTrue(without.Success);
        Assert.IsTrue(with.Success);
        Assert.AreEqual(2, harness.Approval.CallCount, "不传 action 与 action=submit 都必须走既有提交流程。");
        var first = harness.Approval.Requests[0];
        var second = harness.Approval.Requests[1];
        Assert.AreEqual(first.ToolId, second.ToolId);
        Assert.AreEqual(first.Purpose, second.Purpose);
        Assert.AreEqual(first.Necessity, second.Necessity);
        Assert.AreEqual(first.UserConsentStatus, second.UserConsentStatus);
        Assert.AreEqual(first.RequestedScope, second.RequestedScope);
        Assert.AreEqual(first.TicketKind, second.TicketKind);
        Assert.AreEqual(without.Output, with.Output, "两种入参的工具输出必须逐字节一致。");

        var payload = Json(with.Output);
        Assert.AreEqual("ticket-1", payload.RootElement.GetProperty("ticketId").GetString());
    }

    [TestMethod]
    public async Task ExecuteAsync_UnknownAction_FailsListingAllowedValues()
    {
        using var harness = CreateToolHarness();

        var result = await harness.Tool.ExecuteAsync(
            ToolRequest("""{"tool_id":"shell","action":"bogus","purpose":"x"}"""));

        Assert.IsFalse(result.Success);
        Assert.IsTrue(
            (result.Error ?? "").Contains("action must be one of", StringComparison.Ordinal),
            $"未知 action 必须列出合法值，实际：{result.Error}");
        Assert.AreEqual(0, harness.Approval.CallCount, "未知 action 绝不允许落入 submit 流程。");
    }

    [TestMethod]
    public async Task ExecuteAsync_RulesUpdate_ParameterValidation()
    {
        using var harness = CreateToolHarness();

        var missingEffect = await harness.Tool.ExecuteAsync(ToolRequest(
            """{"tool_id":"shell","action":"rules_update","rule_op":"add","command_name":"dotnet test"}"""));
        Assert.IsFalse(missingEffect.Success);
        Assert.IsTrue((missingEffect.Error ?? "").Contains("rule_effect", StringComparison.Ordinal));

        var missingSubject = await harness.Tool.ExecuteAsync(ToolRequest(
            """{"tool_id":"shell","action":"rules_update","rule_op":"add","rule_effect":"deny"}"""));
        Assert.IsFalse(missingSubject.Success);
        Assert.IsTrue((missingSubject.Error ?? "").Contains("command_name", StringComparison.Ordinal));

        var missingRuleId = await harness.Tool.ExecuteAsync(ToolRequest(
            """{"tool_id":"shell","action":"rules_update","rule_op":"disable"}"""));
        Assert.IsFalse(missingRuleId.Success);
        Assert.IsTrue((missingRuleId.Error ?? "").Contains("rule_id", StringComparison.Ordinal));

        var badOp = await harness.Tool.ExecuteAsync(ToolRequest(
            """{"tool_id":"shell","action":"rules_update","rule_op":"delete"}"""));
        Assert.IsFalse(badOp.Success);
        Assert.IsTrue((badOp.Error ?? "").Contains("rule_op", StringComparison.Ordinal));
        Assert.AreEqual(0, harness.Approval.CallCount, "门户动作不得触发提交流程。");
    }

    [TestMethod]
    public async Task ExecuteAsync_PortalActions_FlowThroughProviderServices()
    {
        using var harness = CreateToolHarness();

        var list = await harness.Tool.ExecuteAsync(ToolRequest("""{"tool_id":"shell","action":"rules_list"}"""));
        Assert.IsTrue(list.Success);
        Assert.IsFalse(string.IsNullOrWhiteSpace(list.Output));

        var status = await harness.Tool.ExecuteAsync(ToolRequest("""{"tool_id":"shell","action":"full_access_status"}"""));
        Assert.IsTrue(status.Success);
        Assert.IsFalse(Json(status.Output).RootElement.GetProperty("active").GetBoolean());

        var classify = await harness.Tool.ExecuteAsync(ToolRequest(
            """{"tool_id":"shell","action":"classify","purpose":"run unit tests","command_name":"dotnet test"}"""));
        Assert.IsTrue(classify.Success);
        Assert.AreEqual(
            "allow_once",
            Json(classify.Output).RootElement.GetProperty("outcome").GetString(),
            "工具必须只委派门户，不绕过 IToolCallClassifier 抽象。");
        Assert.AreEqual(1, harness.Classifier.CallCount);
    }

    // ---------- 测试辅助 ----------

    private static ToolApprovalPortalRequest Request(
        string? commandName = null,
        string? ruleOp = null,
        string? ruleEffect = null,
        string? ruleId = null,
        string? reason = null,
        int? fullAccessSeconds = null,
        string workspaceId = WorkspaceA,
        string agentInstanceId = AgentA)
        => new()
        {
            Identity = new ToolApprovalIdentity
            {
                WorkspaceId = workspaceId,
                SessionId = "sess-1",
                AgentInstanceId = agentInstanceId,
                UserId = "user-1",
            },
            ToolId = "shell",
            CommandName = commandName,
            Purpose = reason ?? "portal unit test",
            RequestedArgumentsJson = null,
            RuleOp = ruleOp,
            RuleEffect = ruleEffect,
            RuleId = ruleId,
            AllowlistReason = reason,
            FullAccessDurationSeconds = fullAccessSeconds,
        };

    private static ToolCallClassificationContext Context(string commandName) => new()
    {
        ToolId = "shell",
        CommandName = commandName,
        WorkspaceId = WorkspaceA,
        SessionId = "sess-1",
        AgentInstanceId = AgentA,
        UserId = "user-1",
    };

    private static ClassificationVerdict Verdict(
        ClassificationOutcome outcome,
        string classifierId,
        string? reasonCode = null,
        string? permanentConfidenceKey = null,
        double permanentConfidence = 0.95)
        => new()
        {
            Outcome = outcome,
            Reason = $"fake verdict: {outcome}",
            ClassifierId = classifierId,
            ClassifierModel = null,
            ReasonCode = reasonCode,
            // 经管线时必须提供逐分类可信度，否则永久类会被 §14.13.3 门槛降级为单次类；
            // 键名与 ToolCallClassifierPipeline 内部常量一致（allow_permanent / deny_permanent）。
            PerOutcomeConfidence = permanentConfidenceKey is null
                ? null
                : new Dictionary<string, double> { [permanentConfidenceKey] = permanentConfidence },
        };

    private static (ToolApprovalPortalService Portal, InMemoryToolApprovalAllowlistStore Allowlist, InMemoryToolApprovalAuditStore Audit, FakeClock Clock, CountingClassifier Classifier)
        CreatePortal(Func<ClassificationVerdict>? verdictFactory = null)
    {
        var allowlist = new InMemoryToolApprovalAllowlistStore();
        var audit = new InMemoryToolApprovalAuditStore();
        var clock = new FakeClock();
        var classifier = new CountingClassifier(
            verdictFactory is null
                ? Verdict(ClassificationOutcome.AllowOnce, classifierId: "fake-arbiter")
                : verdictFactory());
        var portal = new ToolApprovalPortalService(allowlist, audit, classifier, timeProvider: clock);
        return (portal, allowlist, audit, clock, classifier);
    }

    private static ToolHarness CreateToolHarness(Func<ClassificationVerdict>? verdictFactory = null)
    {
        var allowlist = new InMemoryToolApprovalAllowlistStore();
        var audit = new InMemoryToolApprovalAuditStore();
        var approval = new StubApprovalService();
        var classifier = new CountingClassifier(
            verdictFactory is null
                ? Verdict(ClassificationOutcome.AllowOnce, classifierId: "fake-arbiter")
                : verdictFactory());

        var services = new ServiceCollection();
        services.AddSingleton<IToolApprovalAllowlistStore>(allowlist);
        services.AddSingleton<IToolApprovalAuditStore>(audit);
        services.AddSingleton<IToolApprovalService>(approval);
        services.AddSingleton<IPuddingToolCatalogService>(new StubCatalog());
        services.AddSingleton<IToolCallClassifier>(classifier);
        var provider = services.BuildServiceProvider();
        var tool = new RequestToolApprovalTool(approval, provider);
        return new ToolHarness(tool, approval, classifier, provider);
    }

    private sealed record ToolHarness(
        RequestToolApprovalTool Tool,
        StubApprovalService Approval,
        CountingClassifier Classifier,
        ServiceProvider Provider)
        : IDisposable
    {
        public void Dispose() => Provider.Dispose();
    }

    private static ToolExecutionRequest ToolRequest(string argumentsJson) => new()
    {
        ToolCallId = "call-1",
        ArgumentsJson = argumentsJson,
        Context = new ToolExecutionContext
        {
            WorkspaceId = WorkspaceA,
            SessionId = "sess-1",
            AgentInstanceId = AgentA,
        },
    };

    private static JsonDocument Json(string json) => JsonDocument.Parse(json);

    private static async Task<List<ToolApprovalAuditEvent>> ListAsync(InMemoryToolApprovalAuditStore store)
        => [.. await store.ListAsync()];

    /// <summary>假时钟：时间只经注入的 TimeProvider 获取（服务端计时语义的可测性）。</summary>
    private sealed class FakeClock : TimeProvider
    {
        private DateTimeOffset _now = T0;

        public void Advance(TimeSpan delta) => _now += delta;

        public override DateTimeOffset GetUtcNow() => _now;
    }

    /// <summary>计数假分类器：零网络；CallCount 用于快路径/缓存断言。</summary>
    private sealed class CountingClassifier(ClassificationVerdict verdict) : IToolCallClassifier
    {
        private int _callCount;

        public int CallCount => _callCount;

        public string ClassifierId => verdict.ClassifierId;

        public Task<ClassificationVerdict> ClassifyAsync(ToolCallClassificationContext context, CancellationToken ct = default)
        {
            Interlocked.Increment(ref _callCount);
            return Task.FromResult(verdict);
        }
    }

    /// <summary>既有提交流程的捕获桩：只记录，不做任何裁决。</summary>
    private sealed class StubApprovalService : IToolApprovalService
    {
        public List<ToolApprovalTicketRequest> Requests { get; } = [];

        public List<ToolApprovalIdentity> Identities { get; } = [];

        public int CallCount { get; private set; }

        public Task<ToolApprovalTicketResult> SubmitAsync(
            ToolApprovalTicketRequest request,
            ToolApprovalIdentity identity,
            ToolDescriptor descriptor,
            CancellationToken ct = default)
        {
            Requests.Add(request);
            Identities.Add(identity);
            CallCount++;
            return Task.FromResult(new ToolApprovalTicketResult
            {
                TicketId = "ticket-1",
                Decision = ToolApprovalDecision.NeedHuman,
                Status = ToolApprovalTicketStatus.Pending,
                DecisionReason = "stub reviewer",
            });
        }

        public Task<ToolApprovalCheckResult> CheckAsync(
            ToolApprovalExecutionRequest request,
            ToolDescriptor descriptor,
            CancellationToken ct = default)
            => throw new NotSupportedException("Submit-path tests never check executions.");
    }

    private sealed class StubCatalog : IPuddingToolCatalogService
    {
        public static readonly ToolDescriptor ShellDescriptor = new()
        {
            ToolId = "shell",
            Name = "Shell",
            Description = "stub descriptor for submit-path tests",
        };

        public IReadOnlyList<ToolDescriptor> ListTools(bool enabledByDefaultOnly = false)
            => [ShellDescriptor];
    }
}
