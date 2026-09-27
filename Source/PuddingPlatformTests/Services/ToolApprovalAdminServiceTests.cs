using PuddingCode.Skills;
using PuddingCode.Tools;
using PuddingPlatform.Services;

namespace PuddingPlatformTests.Services;

/// <summary>
/// DS-10 approval slice: rule mutations must write their audit events in the same operation. The original
/// implementation wrote them inside ToolApprovalAdminApiController, so any admin surface that did not go
/// through HTTP (the native client writing the store directly) changed rules without leaving a trace.
/// </summary>
[TestClass]
public sealed class ToolApprovalAdminServiceTests
{
    [TestMethod]
    public async Task CreateUpdateAndDisableEachWriteAnAuditEvent()
    {
        var harness = new Harness();

        var created = await harness.Service.CreateRuleAsync(new ApprovalRuleMutation
        {
            WorkspaceId = " ws-1 ",
            ToolId = "Shell_Exec",
            Command = "git status",
            Source = "human",
            Status = "enabled",
            Effect = "allow",
            ApprovedByUserId = "reviewer",
            Reason = "fixture",
        });
        Assert.AreEqual(SkillHubStatus.Ok, created.Status);
        // Core's normalization still applies, and the workspace id is trimmed.
        Assert.AreEqual("shell_exec", created.Value!.ToolId);
        Assert.AreEqual("ws-1", created.Value.WorkspaceId);
        Assert.AreEqual("human", ToolApprovalAdminService.FormatSource(created.Value.Source));
        Assert.AreEqual(ToolApprovalRuleEffect.Allow, created.Value.Effect);

        var updated = await harness.Service.UpdateRuleAsync(created.Value.RuleId, new ApprovalRuleMutation
        {
            ToolId = "shell_exec",
            ArgumentsJson = "{\"cmd\":\"git status\"}",
            Source = "classifier",
            Status = "enabled",
            Effect = "deny",
            Reason = "blocked",
        });
        Assert.AreEqual(SkillHubStatus.Ok, updated.Status);
        Assert.IsNull(updated.Value!.Command, "改成参数匹配后命令应为空");
        Assert.AreEqual(ToolApprovalRuleEffect.Deny, updated.Value.Effect);
        Assert.AreEqual(ToolApprovalAllowlistRuleSource.Classifier, updated.Value.Source);

        var disabled = await harness.Service.DisableRuleAsync(created.Value.RuleId);
        Assert.AreEqual(SkillHubStatus.Ok, disabled.Status);
        Assert.AreEqual(ToolApprovalAllowlistRuleStatus.Disabled, disabled.Value!.Status);
        Assert.IsNotNull(disabled.Value.DisabledAtUtc);
        // 停用不是删除：记录仍在。
        Assert.IsNotNull(await harness.Allowlist.GetAsync(created.Value.RuleId));

        var events = await harness.Audit.ListAsync();
        Assert.AreEqual(3, events.Count);
        CollectionAssert.AreEqual(
            new[]
            {
                ToolApprovalAuditEventType.AllowlistRuleCreated,
                ToolApprovalAuditEventType.AllowlistRuleUpdated,
                ToolApprovalAuditEventType.AllowlistRuleDisabled,
            },
            events.Select(evt => evt.EventType).ToArray());
        Assert.IsTrue(events.All(evt => evt.AllowlistRuleId == created.Value.RuleId));
        // 审计携带了变更后的规则内容，便于溯源。
        Assert.AreEqual("shell_exec", events[1].ToolId);
        Assert.AreEqual(ToolApprovalRuleEffect.Deny, events[1].Effect);
    }

    [TestMethod]
    public async Task InvalidMutationsAreRejectedBeforeAnythingIsWritten()
    {
        var harness = new Harness();
        Assert.AreEqual(SkillHubStatus.BadRequest,
            (await harness.Service.CreateRuleAsync(new ApprovalRuleMutation { ToolId = " " })).Status);
        Assert.AreEqual(SkillHubStatus.BadRequest,
            (await harness.Service.CreateRuleAsync(new ApprovalRuleMutation { ToolId = "shell" })).Status);
        Assert.AreEqual(SkillHubStatus.BadRequest,
            (await harness.Service.CreateRuleAsync(new ApprovalRuleMutation
            {
                ToolId = "shell", Command = "ls", Source = "robot",
            })).Status);
        Assert.AreEqual(SkillHubStatus.BadRequest,
            (await harness.Service.CreateRuleAsync(new ApprovalRuleMutation
            {
                ToolId = "shell", Command = "ls", Status = "zombie",
            })).Status);
        Assert.AreEqual(SkillHubStatus.BadRequest,
            (await harness.Service.CreateRuleAsync(new ApprovalRuleMutation
            {
                ToolId = "shell", Command = "ls", Effect = "maybe",
            })).Status);
        Assert.AreEqual(SkillHubStatus.NotFound,
            (await harness.Service.UpdateRuleAsync("tal_missing", new ApprovalRuleMutation
            {
                ToolId = "shell", Command = "ls",
            })).Status);
        Assert.AreEqual(SkillHubStatus.NotFound, (await harness.Service.DisableRuleAsync("tal_missing")).Status);

        Assert.IsEmpty(await harness.Allowlist.ListAsync());
        Assert.IsEmpty(await harness.Audit.ListAsync(), "被拒绝的变更不得留下审计事件");
    }

    [TestMethod]
    public async Task ClassifierRulesCanBeEditedAndEffectDefaultsToAllow()
    {
        var harness = new Harness();
        // 分类器落的规则必须能在管理端创建/修改，否则编辑会直接 400。
        var rule = await harness.Service.CreateRuleAsync(new ApprovalRuleMutation
        {
            ToolId = "http_fetch", Command = "curl https://example.invalid", Source = "Classifier",
            // 缺省 effect 必须落成 allow（记录默认值），而不是被拒或被当成 deny。
        });
        Assert.AreEqual(SkillHubStatus.Ok, rule.Status);
        Assert.AreEqual(ToolApprovalRuleEffect.Allow, rule.Value!.Effect);
        Assert.AreEqual(ToolApprovalAllowlistRuleSource.Classifier, rule.Value.Source);

        Assert.IsTrue(ToolApprovalAdminService.TryParseEffect(null, out var allow));
        Assert.AreEqual(ToolApprovalRuleEffect.Allow, allow);
        Assert.IsTrue(ToolApprovalAdminService.TryParseEffect("DENY", out var deny));
        Assert.AreEqual(ToolApprovalRuleEffect.Deny, deny);
        Assert.IsFalse(ToolApprovalAdminService.TryParseEffect("maybe", out _));
    }

    private sealed class Harness
    {
        public Harness()
        {
            Allowlist = new RecordingAllowlistStore();
            Audit = new RecordingAuditStore();
            Service = new ToolApprovalAdminService(Allowlist, Audit);
        }

        public RecordingAllowlistStore Allowlist { get; }
        public RecordingAuditStore Audit { get; }
        public ToolApprovalAdminService Service { get; }
    }

    private sealed class RecordingAllowlistStore : IToolApprovalAllowlistStore
    {
        private readonly Dictionary<string, ToolApprovalAllowlistRule> _rules = new(StringComparer.Ordinal);

        public Task SaveAsync(ToolApprovalAllowlistRule rule, CancellationToken ct = default)
        {
            _rules[rule.RuleId] = rule;
            return Task.CompletedTask;
        }

        public Task<ToolApprovalAllowlistRule?> GetAsync(string ruleId, CancellationToken ct = default) =>
            Task.FromResult(_rules.TryGetValue(ruleId, out var rule) ? rule : null);

        public Task<IReadOnlyList<ToolApprovalAllowlistRule>> ListAsync(CancellationToken ct = default) =>
            Task.FromResult((IReadOnlyList<ToolApprovalAllowlistRule>)_rules.Values.ToArray());
    }

    private sealed class RecordingAuditStore : IToolApprovalAuditStore
    {
        private readonly List<ToolApprovalAuditEvent> _events = [];

        public Task SaveAsync(ToolApprovalAuditEvent auditEvent, CancellationToken ct = default)
        {
            _events.Add(auditEvent);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<ToolApprovalAuditEvent>> ListAsync(CancellationToken ct = default) =>
            Task.FromResult((IReadOnlyList<ToolApprovalAuditEvent>)_events.ToArray());
    }
}
