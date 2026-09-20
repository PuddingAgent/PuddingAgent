using System.Text.Json;
using PuddingCode.Classification;
using PuddingCode.Tools;

namespace PuddingCoreTests.Classification;

/// <summary>
/// S1 切片（安全分类器抽象层）契约测试：纯契约、零行为变更的向后兼容锁定。
/// </summary>
[TestClass]
public sealed class ToolCallClassificationContractTests
{
    [TestMethod]
    public void ClassificationOutcome_Unknown_IsZero_And_RuleEffect_Allow_IsZero()
    {
        Assert.AreEqual(0, (int)ClassificationOutcome.Unknown);
        Assert.AreEqual(1, (int)ClassificationOutcome.AllowOnce);
        Assert.AreEqual(2, (int)ClassificationOutcome.AllowPermanent);
        Assert.AreEqual(3, (int)ClassificationOutcome.DenyOnce);
        Assert.AreEqual(4, (int)ClassificationOutcome.DenyPermanent);
        Assert.AreEqual(ClassificationOutcome.Unknown, default(ClassificationOutcome));

        Assert.AreEqual(0, (int)ToolApprovalRuleEffect.Allow);
        Assert.AreEqual(1, (int)ToolApprovalRuleEffect.Deny);
        Assert.AreEqual(ToolApprovalRuleEffect.Allow, default(ToolApprovalRuleEffect));
    }

    [TestMethod]
    public void AuditEventType_ExistingMemberValues_AreUnchanged_And_NewMembers_AppendOnly()
    {
        // 既有成员：硬断数值未被改变（N01：禁止重排 / 插入，避免改变序列化数值）。
        Assert.AreEqual(0, (int)ToolApprovalAuditEventType.TicketSubmitted);
        Assert.AreEqual(1, (int)ToolApprovalAuditEventType.TicketApproved);
        Assert.AreEqual(2, (int)ToolApprovalAuditEventType.TicketDenied);
        Assert.AreEqual(3, (int)ToolApprovalAuditEventType.TicketNeedHuman);
        Assert.AreEqual(4, (int)ToolApprovalAuditEventType.TicketMatched);
        Assert.AreEqual(5, (int)ToolApprovalAuditEventType.TicketConsumed);
        Assert.AreEqual(6, (int)ToolApprovalAuditEventType.TicketMismatch);
        Assert.AreEqual(7, (int)ToolApprovalAuditEventType.ImplicitApproved);
        Assert.AreEqual(8, (int)ToolApprovalAuditEventType.ImplicitDenied);
        Assert.AreEqual(9, (int)ToolApprovalAuditEventType.AllowlistHit);
        Assert.AreEqual(10, (int)ToolApprovalAuditEventType.AllowlistRuleCreated);
        Assert.AreEqual(11, (int)ToolApprovalAuditEventType.AllowlistRuleUpdated);
        Assert.AreEqual(12, (int)ToolApprovalAuditEventType.AllowlistRuleDisabled);
        Assert.AreEqual(13, (int)ToolApprovalAuditEventType.DefinitionDriftDetected);
        Assert.AreEqual(14, (int)ToolApprovalAuditEventType.TicketDeferredDependency);

        // S1 追加成员：序号必须严格大于既有最大值（14），且按追加顺序连续。
        Assert.AreEqual(15, (int)ToolApprovalAuditEventType.ClassifierInvoked);
        Assert.AreEqual(16, (int)ToolApprovalAuditEventType.ClassifierUnavailable);
        Assert.AreEqual(17, (int)ToolApprovalAuditEventType.DenylistRuleCreated);
        Assert.AreEqual(18, (int)ToolApprovalAuditEventType.DenylistRuleDisabled);
        Assert.AreEqual(19, (int)ToolApprovalAuditEventType.FullAccessRequested);
        Assert.AreEqual(20, (int)ToolApprovalAuditEventType.FullAccessGranted);
        Assert.AreEqual(21, (int)ToolApprovalAuditEventType.FullAccessDenied);
        Assert.AreEqual(22, (int)ToolApprovalAuditEventType.FullAccessExpired);
        Assert.AreEqual(23, (int)ToolApprovalAuditEventType.FullAccessRevoked);
        Assert.AreEqual(24, (int)ToolApprovalAuditEventType.RuleConflictDetected);
    }

    [TestMethod]
    public void Deserialize_LegacyRuleJson_WithoutEffect_DefaultsToAllow()
    {
        // 不含 effect 字段的旧 JSON（P0-6 时代形状）：必须反序列化为 Allow，保证旧记录语义不变。
        const string legacyJson = """
            {"RuleId":"rule-legacy-1","ToolId":"terminal_execute","CreatedAtUtc":"2026-01-01T00:00:00Z"}
            """;

        var rule = JsonSerializer.Deserialize<ToolApprovalAllowlistRule>(legacyJson);

        Assert.IsNotNull(rule);
        Assert.AreEqual(ToolApprovalRuleEffect.Allow, rule.Effect);
        Assert.AreEqual("rule-legacy-1", rule.RuleId);
    }

    [TestMethod]
    public void Deserialize_LegacyAuditEventJson_WithoutEffect_DefaultsToAllow()
    {
        const string legacyJson = """
            {"EventId":"evt-1","EventType":0,"CreatedAtUtc":"2026-01-01T00:00:00Z"}
            """;

        var auditEvent = JsonSerializer.Deserialize<ToolApprovalAuditEvent>(legacyJson);

        Assert.IsNotNull(auditEvent);
        Assert.AreEqual(ToolApprovalAuditEventType.TicketSubmitted, auditEvent.EventType);
        Assert.AreEqual(ToolApprovalRuleEffect.Allow, auditEvent.Effect);
    }

    [TestMethod]
    public void SerializeDeserialize_DenyRule_JsonRoundTrip_KeepsEffect()
    {
        var rule = new ToolApprovalAllowlistRule
        {
            RuleId = "rule-deny-1",
            ToolId = "shell",
            Effect = ToolApprovalRuleEffect.Deny,
            CreatedAtUtc = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
        };

        var json = JsonSerializer.Serialize(rule);
        var back = JsonSerializer.Deserialize<ToolApprovalAllowlistRule>(json);

        Assert.IsNotNull(back);
        Assert.AreEqual(ToolApprovalRuleEffect.Deny, back.Effect);
        Assert.AreEqual("rule-deny-1", back.RuleId);
        Assert.AreEqual("shell", back.ToolId);
    }

    [TestMethod]
    public void ClassificationContracts_AllCoreTypes_AreConstructible()
    {
        // 分类器契约四件套 + 健康面 + 完全访问授予：纯数据类型可构造、字段可回读。
        var context = new ToolCallClassificationContext
        {
            ToolId = "terminal_execute",
            CommandName = "dotnet build",
            ArgumentsJson = """{"command":"dotnet build"}""",
            WorkingDirectory = @"E:\repo",
            Shell = "pwsh",
            IsIrreversibleOperation = false,
            MayDamageOrDeleteData = false,
            WorkspaceId = "default",
            SessionId = "s-1",
            AgentInstanceId = "a-1",
            UserId = "u-1",
            RecentTrajectory = "…(有界轨迹摘录)…",
            MatchedRuleSummaries = ["allow: terminal_execute / dotnet *"],
        };

        var verdict = new ClassificationVerdict
        {
            Outcome = ClassificationOutcome.AllowPermanent,
            Reason = "构建命令属常规开发操作",
            PerOutcomeConfidence = new Dictionary<string, double>
            {
                ["allow_once"] = 0.98,
                ["allow_permanent"] = 0.95,
                ["deny_once"] = 0.01,
                ["deny_permanent"] = 0.01,
            },
            ClassifierId = "system-rules",
            ClassifierModel = null,
            LatencyMs = 0.4,
            ReasonCode = null,
            AppliedRuleId = "rule-1",
        };

        var status = new ClassifierStatus
        {
            ClassifierId = "system-rules",
            Health = ClassifierHealth.Healthy,
            ConsecutiveFailures = 0,
            LastCheckedAtUtc = DateTimeOffset.UtcNow,
            LastLatencyMs = 0.4,
        };

        var grant = new AgentFullAccessGrant
        {
            GrantId = "g-1",
            WorkspaceId = "default",
            AgentInstanceId = "a-1",
            SessionId = null,
            GrantedAtUtc = DateTimeOffset.UtcNow,
            ExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(5),
            GrantedByClassifierId = "model-classifier",
            Outcome = ClassificationOutcome.AllowOnce,
            Reason = "演练环境临时完全访问",
            RevokedAtUtc = null,
        };

        Assert.AreEqual("terminal_execute", context.ToolId);
        Assert.AreEqual(ClassificationOutcome.AllowPermanent, verdict.Outcome);
        Assert.AreEqual(4, verdict.PerOutcomeConfidence!.Count);
        Assert.AreEqual(ClassifierHealth.Healthy, status.Health);
        Assert.IsNull(grant.RevokedAtUtc);
    }

    [TestMethod]
    public async Task ClassifierInterfaces_AreImplementable_ByContractOnly()
    {
        // 接口面可被无厂商依赖的假实现满足（可更换性：新增实现即可替换，调用方零改动）。
        IToolCallClassifier classifier = new StubClassifier();
        IClassifierHealthReporter reporter = new StubHealthReporter();
        IAgentFullAccessGrantService grants = new StubGrantService();

        Assert.AreEqual("stub", classifier.ClassifierId);
        Assert.AreEqual(1, reporter.Snapshot().Count);
        Assert.AreEqual("stub", reporter.Snapshot()[0].ClassifierId);

        var active = await grants.GetActiveAsync("default", "a-1");
        Assert.IsNull(active);
    }

    private sealed class StubClassifier : IToolCallClassifier
    {
        public string ClassifierId => "stub";

        public Task<ClassificationVerdict> ClassifyAsync(
            ToolCallClassificationContext context, CancellationToken ct = default)
            => Task.FromResult(new ClassificationVerdict
            {
                Outcome = ClassificationOutcome.Unknown,
                Reason = "stub",
                ClassifierId = ClassifierId,
            });
    }

    private sealed class StubHealthReporter : IClassifierHealthReporter
    {
        public IReadOnlyList<ClassifierStatus> Snapshot()
            => [new ClassifierStatus { ClassifierId = "stub", Health = ClassifierHealth.Unknown }];
    }

    private sealed class StubGrantService : IAgentFullAccessGrantService
    {
        public Task<AgentFullAccessGrant?> GetActiveAsync(
            string workspaceId, string agentInstanceId, CancellationToken ct = default)
            => Task.FromResult<AgentFullAccessGrant?>(null);

        public Task<AgentFullAccessGrant> GrantAsync(AgentFullAccessGrant grant, CancellationToken ct = default)
            => Task.FromResult(grant);

        public Task<bool> RevokeAsync(string grantId, CancellationToken ct = default)
            => Task.FromResult(false);
    }
}
