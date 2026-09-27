using PuddingDesktop.Foundation;

namespace PuddingDesktop.FoundationTests;

/// <summary>DS-10 vault and classifier slice: write-only secrets, placeholder syntax, honest health states.</summary>
public sealed class SecurityContractTests
{
    [Fact]
    public void PlaceholderMatchesCoresRegexSyntax()
    {
        // Core 的正则是 \{\{vault:(?<name>[a-zA-Z0-9._-]+)\}\}
        Assert.Equal("{{vault:openai-key}}", SecurityText.BuildPlaceholder("openai-key"));
        Assert.Equal("{{vault:a.b_c-1}}", SecurityText.BuildPlaceholder("a.b_c-1"));

        var secret = new VaultSecret(1, "kv-1", "openai-key", "", "api", ["prod"],
            DateTimeOffset.UtcNow, null);
        Assert.Equal("{{vault:openai-key}}", secret.Placeholder);
        Assert.Equal("api（接口密钥）", secret.CategoryText);
        Assert.Equal("prod", secret.TagsText);
        Assert.Equal("无标签", (secret with { Tags = [] }).TagsText);
    }

    [Fact]
    public void SecretNamesMustStayInsideThePlaceholderAlphabet()
    {
        Assert.True(SecurityText.IsValidSecretName("openai-key"));
        Assert.True(SecurityText.IsValidSecretName("a.b_c-1"));
        Assert.False(SecurityText.IsValidSecretName("has space"));
        Assert.False(SecurityText.IsValidSecretName("has:colon"));
        Assert.False(SecurityText.IsValidSecretName(""));
        Assert.False(SecurityText.IsValidSecretName(null));
    }

    [Fact]
    public void CreateNeedsAValueAndUpdateMayKeepIt()
    {
        var create = new VaultSecretEdit("", "openai-key", "desc", "api", "sk-value", ["prod"]);
        Assert.True(create.IsCreate);
        Assert.Empty(SecurityText.Validate(create));
        Assert.Contains("必须填写密钥值", SecurityText.Validate(create with { Value = "" }).Single(), StringComparison.Ordinal);

        // 更新留空 = 保持原值，Core 的 UpdateKeyVaultSecretCommand 也是这个语义。
        var update = new VaultSecretEdit("kv-1", "openai-key", "desc", "api", "", []);
        Assert.False(update.IsCreate);
        Assert.Empty(SecurityText.Validate(update));

        Assert.Contains("名称", SecurityText.Validate(create with { Name = " " }).Single(), StringComparison.Ordinal);
        Assert.Contains("只能包含", SecurityText.Validate(create with { Name = "bad name" }).Single(), StringComparison.Ordinal);
        Assert.Contains("分类", SecurityText.Validate(create with { Category = "secret" }).Single(), StringComparison.Ordinal);
        Assert.Equal(["general", "api", "token"], SecurityText.VaultCategories);
    }

    [Fact]
    public void TagParsingIsDeduplicatedAndBlankTolerant()
    {
        Assert.Equal(["prod", "eu"], SecurityText.ParseTags(" prod, eu "));
        Assert.Equal(["prod", "eu"], SecurityText.ParseTags("prod;eu"));
        Assert.Equal(["prod"], SecurityText.ParseTags("prod, PROD, prod"));
        Assert.Empty(SecurityText.ParseTags(null));
        Assert.Empty(SecurityText.ParseTags("  "));
        Assert.Equal("prod, eu", SecurityText.FormatTags(["prod", "eu"]));
        Assert.Equal("", SecurityText.FormatTags(null));
    }

    [Fact]
    public void ClassifierHealthIsDescribedWithoutPretending()
    {
        Assert.Contains("未接线", SecurityText.DescribeHealthSummary(ClassifierHealthReport.NotConfigured), StringComparison.Ordinal);
        Assert.Contains("没有", SecurityText.DescribeHealthSummary(new ClassifierHealthReport(true, [])), StringComparison.Ordinal);

        var healthy = new ClassifierHealthReport(true,
            [new ClassifierStatusEntry("safety", "healthy", "", 0, DateTimeOffset.UtcNow, 12.5)]);
        Assert.Contains("全部健康", SecurityText.DescribeHealthSummary(healthy), StringComparison.Ordinal);

        var degraded = new ClassifierHealthReport(true,
        [
            new ClassifierStatusEntry("safety", "healthy", "", 0, DateTimeOffset.UtcNow, 12.5),
            new ClassifierStatusEntry("intent", "degraded", "timeout", 3, DateTimeOffset.UtcNow, 900)
        ]);
        Assert.Contains("1 个非健康", SecurityText.DescribeHealthSummary(degraded), StringComparison.Ordinal);

        Assert.Equal("降级", SecurityText.DescribeClassifierHealth("degraded"));
        Assert.Equal("不可用", SecurityText.DescribeClassifierHealth("Unavailable"));
        Assert.Equal("未知（尚未探测）", SecurityText.DescribeClassifierHealth("unknown"));
        Assert.Equal("状态未知", SecurityText.DescribeClassifierHealth(null));
        Assert.Equal("SomethingNew", SecurityText.DescribeClassifierHealth("SomethingNew"));

        Assert.Equal("12.5 ms", healthy.Classifiers[0].LatencyText);
        Assert.Equal("无延迟数据", (healthy.Classifiers[0] with { LastLatencyMs = null }).LatencyText);
        Assert.Equal("尚未探测", (healthy.Classifiers[0] with { LastCheckedAtUtc = null }).CheckedText);
    }

    [Fact]
    public void ApprovalRuleVocabularyAndDescriptionsMatchCore()
    {
        Assert.Equal(["built_in", "audit_agent", "human", "classifier"], SecurityText.RuleSources);
        Assert.Equal(["enabled", "disabled"], SecurityText.RuleStatuses);
        Assert.Equal(["allow", "deny"], SecurityText.RuleEffects);

        Assert.Contains("内置", SecurityText.DescribeRuleSource("built_in"), StringComparison.Ordinal);
        Assert.Contains("已下线", SecurityText.DescribeRuleSource("audit_agent"), StringComparison.Ordinal);
        Assert.Contains("分类器", SecurityText.DescribeRuleSource("classifier"), StringComparison.Ordinal);
        Assert.Equal("SomethingNew", SecurityText.DescribeRuleSource("SomethingNew"));

        // allow 是 Core 的缺省值：没有 effect 字段的旧记录就是放行规则。
        Assert.Contains("缺省", SecurityText.DescribeRuleEffect(""), StringComparison.Ordinal);
        Assert.Contains("优先级更高", SecurityText.DescribeRuleEffect("deny"), StringComparison.Ordinal);

        Assert.Equal("批准", SecurityText.DescribeDecision("approved"));
        Assert.Equal("依赖不可用（等待）", SecurityText.DescribeDecision("deferreddependency"));
        Assert.Equal("无裁决", SecurityText.DescribeDecision(null));
        Assert.Equal("白名单命中", SecurityText.DescribeAuditEventType("allowlist_hit"));
        Assert.Equal("工单拒绝", SecurityText.DescribeAuditEventType("ticket_denied"));
        Assert.Equal("SomeFutureEvent", SecurityText.DescribeAuditEventType("SomeFutureEvent"));
    }

    [Fact]
    public void ApprovalRuleFormsRequireAnExactMatchKey()
    {
        var valid = new ApprovalRuleEdit("", "ws", "shell_exec", "git status", "", "human", "enabled", "allow",
            "", "", "", "reviewed");
        Assert.Empty(SecurityText.Validate(valid));
        // 命令与参数 JSON 至少要有一个，否则规则没有精确匹配键。
        var noKey = valid with { Command = "", ArgumentsJson = "" };
        Assert.Contains("至少", SecurityText.Validate(noKey).Single(), StringComparison.Ordinal);
        Assert.Contains("toolId", SecurityText.Validate(valid with { ToolId = "" }).Single(), StringComparison.Ordinal);
        Assert.Contains("不是合法 JSON", SecurityText.Validate(valid with { ArgumentsJson = "{oops" }).Single(), StringComparison.Ordinal);
        Assert.Contains("来源", SecurityText.Validate(valid with { Source = "robot" }).Single(), StringComparison.Ordinal);
        Assert.Contains("状态", SecurityText.Validate(valid with { Status = "zombie" }).Single(), StringComparison.Ordinal);
        Assert.Contains("效果", SecurityText.Validate(valid with { Effect = "maybe" }).Single(), StringComparison.Ordinal);
        Assert.Empty(SecurityText.Validate(valid with { ArgumentsJson = "{\"a\":1}" }));
    }

    [Fact]
    public void ApprovalRuleAndAuditDisplaysUseCoreFieldsOnly()
    {
        var rule = new ApprovalRule("tal_1", "", "shell", "git status", "", "classifier", "disabled", "deny",
            "agent-1", "user-1", "ticket-1", "why", 7, DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, 3, null);
        Assert.False(rule.IsEnabled);
        Assert.True(rule.IsDeny);
        Assert.Equal("已停用", rule.StatusText);
        Assert.Contains("拒绝", rule.EffectText, StringComparison.Ordinal);
        Assert.Contains("用户 user-1", rule.ApproverText, StringComparison.Ordinal);
        Assert.Contains("Agent agent-1", rule.ApproverText, StringComparison.Ordinal);
        Assert.Contains("工单 ticket-1", rule.ApproverText, StringComparison.Ordinal);
        Assert.Contains("命令：git status", rule.MatchText, StringComparison.Ordinal);
        Assert.Equal("无批准来源", (rule with { ApprovedByUserId = "", ApprovedByAgentInstanceId = "", ApprovalTicketId = "" }).ApproverText);

        var stats = new ApprovalStats(5, 2, 1, 1, 2, 1, 0, 3, 0, 4, 6, 5, 2, 4);
        Assert.Contains("提交 5", stats.SummaryText, StringComparison.Ordinal);
        Assert.Contains("白名单命中 4", stats.SummaryText, StringComparison.Ordinal);
        Assert.Contains("动态 4", stats.SummaryText, StringComparison.Ordinal);

        var entry = new ApprovalAuditEntry("taa_1", "allowlist_hit", "ws", "", "", "", "shell", "ls", "", "",
            "tal_1", "approved", "classifier", "gpt", "matched", DateTimeOffset.UtcNow);
        Assert.Equal("白名单命中", entry.EventTypeText);
        Assert.Equal("批准", entry.DecisionText);
        Assert.Contains("shell · ls", entry.TargetText, StringComparison.Ordinal);
    }

    [Fact]
    public void ApprovalNoticesStateDisableAndDenyPrecedence()
    {
        Assert.Contains("不是删除", SecurityText.DisableIsNotDelete, StringComparison.Ordinal);
        Assert.Contains("deny 优先于 allow", SecurityText.DenyBeatsAllow, StringComparison.Ordinal);
        Assert.Contains("追加写", SecurityText.AuditAppendOnly, StringComparison.Ordinal);
        Assert.Equal([50, 100, 200, 500], SecurityText.AuditLimits);
    }
    [Fact]
    public void NoticesStateTheWriteOnlyBoundary()
    {
        Assert.Contains("不回显明文", SecurityText.WriteOnlyNotice, StringComparison.Ordinal);
        Assert.Contains("{{vault:名称}}", SecurityText.PlaceholderNotice, StringComparison.Ordinal);
        Assert.Contains("未知态", SecurityText.ClassifierUnknownNotice, StringComparison.Ordinal);
    }
}