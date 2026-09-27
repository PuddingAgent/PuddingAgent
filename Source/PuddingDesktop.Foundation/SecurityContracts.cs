namespace PuddingDesktop.Foundation;

/// <summary>
/// A vault secret's metadata. The plaintext value is never part of this model: the vault is write-only
/// from the settings panel, and references are expressed with the placeholder instead.
/// </summary>
public sealed record VaultSecret(
    long Id, string KeyVaultId, string Name, string Description, string Category,
    IReadOnlyList<string> Tags, DateTimeOffset CreatedAt, DateTimeOffset? UpdatedAt)
{
    /// <summary>Core's placeholder syntax: {{vault:name}}. This is what other configuration copies.</summary>
    public string Placeholder => SecurityText.BuildPlaceholder(Name);
    public string CategoryText => SecurityText.DescribeVaultCategory(Category);
    public string TagsText => Tags.Count == 0 ? "无标签" : string.Join("、", Tags);
}

/// <summary>Create or update. A blank value on update keeps the stored secret.</summary>
public sealed record VaultSecretEdit(
    string KeyVaultId, string Name, string Description, string Category, string Value,
    IReadOnlyList<string> Tags)
{
    public bool IsCreate => string.IsNullOrWhiteSpace(KeyVaultId);
}

public sealed record ClassifierStatusEntry(
    string ClassifierId, string Health, string Detail, int ConsecutiveFailures,
    DateTimeOffset? LastCheckedAtUtc, double? LastLatencyMs)
{
    public string HealthText => SecurityText.DescribeClassifierHealth(Health);
    public string LatencyText => LastLatencyMs is null ? "无延迟数据" : $"{LastLatencyMs:0.#} ms";
    public string CheckedText => LastCheckedAtUtc is null
        ? "尚未探测"
        : $"最近探测 {LastCheckedAtUtc.Value.ToLocalTime():MM-dd HH:mm:ss}";
}

/// <summary>
/// Whether the classifier health surface is wired at all. Core reports this explicitly so the page can say
/// "unknown" instead of pretending everything is healthy.
/// </summary>
public sealed record ClassifierHealthReport(bool Configured, IReadOnlyList<ClassifierStatusEntry> Classifiers)
{
    public static ClassifierHealthReport NotConfigured { get; } = new(false, []);
}

/// <summary>An exact approval allowlist rule. Core keeps disabled rules instead of deleting them.</summary>
public sealed record ApprovalRule(
    string RuleId, string WorkspaceId, string ToolId, string Command, string ArgumentsJson,
    string Source, string Status, string Effect,
    string ApprovedByAgentInstanceId, string ApprovedByUserId, string ApprovalTicketId, string Reason,
    long HitCount, DateTimeOffset? LastHitAtUtc,
    DateTimeOffset CreatedAtUtc, DateTimeOffset? UpdatedAtUtc, DateTimeOffset? DisabledAtUtc,
    int DefinitionVersion, DateTimeOffset? ExpiresAtUtc)
{
    public bool IsEnabled => string.Equals(Status, "enabled", StringComparison.OrdinalIgnoreCase);
    public bool IsDeny => string.Equals(Effect, "deny", StringComparison.OrdinalIgnoreCase);
    public string SourceText => SecurityText.DescribeRuleSource(Source);
    public string EffectText => SecurityText.DescribeRuleEffect(Effect);
    public string StatusText => IsEnabled ? "已启用" : "已停用";
    public string MatchText => (Command.Length == 0 ? "" : $"命令：{Command}") +
                               (ArgumentsJson.Length == 0 ? "" : $" 参数：{ArgumentsJson}");
    public string ApproverText
    {
        get
        {
            var parts = new List<string>();
            if (ApprovedByUserId.Length > 0) parts.Add($"用户 {ApprovedByUserId}");
            if (ApprovedByAgentInstanceId.Length > 0) parts.Add($"Agent {ApprovedByAgentInstanceId}");
            if (ApprovalTicketId.Length > 0) parts.Add($"工单 {ApprovalTicketId}");
            return parts.Count == 0 ? "无批准来源" : string.Join(" · ", parts);
        }
    }
}

/// <summary>Create when RuleId is blank, update otherwise. Disabling is a separate operation in Core.</summary>
public sealed record ApprovalRuleEdit(
    string RuleId, string WorkspaceId, string ToolId, string Command, string ArgumentsJson,
    string Source, string Status, string Effect,
    string ApprovedByAgentInstanceId, string ApprovedByUserId, string ApprovalTicketId, string Reason)
{
    public bool IsCreate => string.IsNullOrWhiteSpace(RuleId);
}

public sealed record ApprovalAuditEntry(
    string EventId, string EventType, string WorkspaceId, string SessionId, string AgentInstanceId,
    string UserId, string ToolId, string Command, string ArgumentsJson, string TicketId,
    string AllowlistRuleId, string Decision, string Source, string ReviewerModel, string Reason,
    DateTimeOffset CreatedAtUtc)
{
    public string EventTypeText => SecurityText.DescribeAuditEventType(EventType);
    public string DecisionText => SecurityText.DescribeDecision(Decision);
    public string TargetText => (ToolId.Length == 0 ? "（无工具）" : ToolId) +
                                (Command.Length == 0 ? "" : $" · {Command}");
}

public sealed record ApprovalStats(
    long TicketSubmitted, long TicketApproved, long TicketDenied, long TicketNeedHuman,
    long TicketMatched, long TicketConsumed, long TicketMismatch,
    long ImplicitApproved, long ImplicitDenied, long AllowlistHit,
    long AllowlistRules, long EnabledAllowlistRules, long BuiltInRules, long DynamicRules)
{
    public string SummaryText =>
        $"工单：提交 {TicketSubmitted} · 批准 {TicketApproved} · 拒绝 {TicketDenied} · 需人工 {TicketNeedHuman}\n" +
        $"工单后续：匹配 {TicketMatched} · 消费 {TicketConsumed} · 不匹配 {TicketMismatch}\n" +
        $"隐式裁决：批准 {ImplicitApproved} · 拒绝 {ImplicitDenied} · 白名单命中 {AllowlistHit}\n" +
        $"规则：共 {AllowlistRules} · 启用 {EnabledAllowlistRules} · 内置 {BuiltInRules} · 动态 {DynamicRules}";
}

public sealed record ApprovalAuditQuery(string WorkspaceId, string ToolId, string EventType, int Limit);

public interface ISecuritySettings
{
    Task<IReadOnlyList<ApprovalRule>> ListApprovalRulesAsync(
        string? workspaceId, string? toolId, string? status, CancellationToken cancellationToken = default);
    Task SaveApprovalRuleAsync(ApprovalRuleEdit edit, CancellationToken cancellationToken = default);
    /// <summary>Core disables the rule (status=disabled); it does not delete the record or its audit trail.</summary>
    Task DisableApprovalRuleAsync(string ruleId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ApprovalAuditEntry>> ListApprovalAuditAsync(
        ApprovalAuditQuery query, CancellationToken cancellationToken = default);
    Task<ApprovalStats> ReadApprovalStatsAsync(CancellationToken cancellationToken = default);    Task<IReadOnlyList<VaultSecret>> ListSecretsAsync(CancellationToken cancellationToken = default);
    Task SaveSecretAsync(VaultSecretEdit edit, CancellationToken cancellationToken = default);
    Task DeleteSecretAsync(string keyVaultId, CancellationToken cancellationToken = default);
    Task<ClassifierHealthReport> ReadClassifierHealthAsync(CancellationToken cancellationToken = default);
}

public static class SecurityText
{
    /// <summary>Mirrors KeyVaultService.VaultPlaceholderRegex: {{vault:name}}.</summary>
    public static string BuildPlaceholder(string name) => $"{{{{vault:{name}}}}}";

    public static IReadOnlyList<string> VaultCategories { get; } = ["general", "api", "token"];

    public const string WriteOnlyNotice =
        "密钥值是只写的：界面只显示元数据与引用占位符，不回显明文，也不提供「显示密钥」。";

    public const string PlaceholderNotice =
        "引用占位符形如 {{vault:名称}}，由 Core 在注入时替换；把它复制到需要该密钥的配置里即可。";

    public const string ClassifierUnknownNotice =
        "分类器健康面未接线时 Core 明确返回未知态；界面照实显示「未接线」而不是「健康」。";

    public static string DescribeVaultCategory(string? category) => category switch
    {
        null or "" => "未分类",
        var value when string.Equals(value, "general", StringComparison.OrdinalIgnoreCase) => "general（通用）",
        var value when string.Equals(value, "api", StringComparison.OrdinalIgnoreCase) => "api（接口密钥）",
        var value when string.Equals(value, "token", StringComparison.OrdinalIgnoreCase) => "token（令牌）",
        var value => value
    };

    public static string DescribeClassifierHealth(string? health) => health switch
    {
        null or "" => "状态未知",
        var value when string.Equals(value, "unknown", StringComparison.OrdinalIgnoreCase) => "未知（尚未探测）",
        var value when string.Equals(value, "healthy", StringComparison.OrdinalIgnoreCase) => "健康",
        var value when string.Equals(value, "degraded", StringComparison.OrdinalIgnoreCase) => "降级",
        var value when string.Equals(value, "unavailable", StringComparison.OrdinalIgnoreCase) => "不可用",
        var value => value
    };

    public static bool IsKnownCategory(string? category) =>
        category is not null && VaultCategories.Contains(category, StringComparer.OrdinalIgnoreCase);

    /// <summary>Core matches the placeholder with [a-zA-Z0-9._-]+, so the name must stay inside it.</summary>
    public static bool IsValidSecretName(string? name) =>
        !string.IsNullOrWhiteSpace(name)
        && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-');

    public static IReadOnlyList<string> Validate(VaultSecretEdit edit)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(edit.Name)) errors.Add("名称不能为空。");
        else if (!IsValidSecretName(edit.Name))
            errors.Add("名称只能包含字母、数字、'.'、'_' 与 '-'（Core 的占位符语法只认这些字符）。");
        if (!IsKnownCategory(edit.Category)) errors.Add($"分类必须是 {string.Join(" / ", VaultCategories)} 之一。");
        // 新建必须有值；更新留空表示保持原值（Core 语义）。
        if (edit.IsCreate && string.IsNullOrWhiteSpace(edit.Value)) errors.Add("新建密钥必须填写密钥值。");
        return errors;
    }

    public static IReadOnlyList<string> ParseTags(string? text) =>
        string.IsNullOrWhiteSpace(text)
            ? []
            : text.Split([',', ';', ' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                .Select(value => value.Trim())
                .Where(value => value.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

    public static string FormatTags(IEnumerable<string>? tags) => string.Join(", ", tags ?? []);

    /// <summary>Mirrors ToolApprovalAdminApiController's accepted sources.</summary>
    public static IReadOnlyList<string> RuleSources { get; } = ["built_in", "audit_agent", "human", "classifier"];
    public static IReadOnlyList<string> RuleStatuses { get; } = ["enabled", "disabled"];
    /// <summary>Core keeps allow as the default value, so a record without an effect field is an allow rule.</summary>
    public static IReadOnlyList<string> RuleEffects { get; } = ["allow", "deny"];

    public const string DisableIsNotDelete =
        "停用不是删除：Core 把规则标记为 disabled 并保留记录与审计痕迹，界面也不提供硬删除。";

    public const string DenyBeatsAllow =
        "同键冲突时 deny 优先于 allow（Core 的安全侧优先）；界面不把 deny 规则显示成授权放行。";

    public const string AuditAppendOnly =
        "审计事件是追加写的：界面只读，按事件类型/工具/工作区筛选，不提供编辑或删除。";

    public static string DescribeRuleSource(string? source) => source switch
    {
        null or "" => "来源未知",
        var value when string.Equals(value, "built_in", StringComparison.OrdinalIgnoreCase) => "built_in（内置）",
        var value when string.Equals(value, "audit_agent", StringComparison.OrdinalIgnoreCase) => "audit_agent（旧审计 Agent，已下线）",
        var value when string.Equals(value, "human", StringComparison.OrdinalIgnoreCase) => "human（人工）",
        var value when string.Equals(value, "classifier", StringComparison.OrdinalIgnoreCase) => "classifier（安全分类器）",
        var value => value
    };

    public static string DescribeRuleEffect(string? effect) => effect switch
    {
        null or "" => "allow（缺省，放行）",
        var value when string.Equals(value, "allow", StringComparison.OrdinalIgnoreCase) => "allow（放行）",
        var value when string.Equals(value, "deny", StringComparison.OrdinalIgnoreCase) => "deny（拒绝，优先级更高）",
        var value => value
    };

    public static string DescribeDecision(string? decision) => decision switch
    {
        null or "" => "无裁决",
        var value when string.Equals(value, "approved", StringComparison.OrdinalIgnoreCase) => "批准",
        var value when string.Equals(value, "denied", StringComparison.OrdinalIgnoreCase) => "拒绝",
        var value when string.Equals(value, "needhuman", StringComparison.OrdinalIgnoreCase) => "需要人工",
        var value when string.Equals(value, "deferreddependency", StringComparison.OrdinalIgnoreCase) => "依赖不可用（等待）",
        var value => value
    };

    /// <summary>Wire names come from ToolApprovalWire; unknown types pass through instead of being renamed.</summary>
    public static string DescribeAuditEventType(string? eventType) => eventType switch
    {
        null or "" => "事件类型未知",
        var value when string.Equals(value, "ticket_submitted", StringComparison.OrdinalIgnoreCase) => "工单提交",
        var value when string.Equals(value, "ticket_approved", StringComparison.OrdinalIgnoreCase) => "工单批准",
        var value when string.Equals(value, "ticket_denied", StringComparison.OrdinalIgnoreCase) => "工单拒绝",
        var value when string.Equals(value, "ticket_need_human", StringComparison.OrdinalIgnoreCase) => "工单转人工",
        var value when string.Equals(value, "ticket_matched", StringComparison.OrdinalIgnoreCase) => "工单匹配",
        var value when string.Equals(value, "ticket_consumed", StringComparison.OrdinalIgnoreCase) => "工单消费",
        var value when string.Equals(value, "ticket_mismatch", StringComparison.OrdinalIgnoreCase) => "工单不匹配",
        var value when string.Equals(value, "implicit_approved", StringComparison.OrdinalIgnoreCase) => "隐式批准",
        var value when string.Equals(value, "implicit_denied", StringComparison.OrdinalIgnoreCase) => "隐式拒绝",
        var value when string.Equals(value, "allowlist_hit", StringComparison.OrdinalIgnoreCase) => "白名单命中",
        var value => value
    };

    public static IReadOnlyList<int> AuditLimits { get; } = [50, 100, 200, 500];

    public static IReadOnlyList<string> Validate(ApprovalRuleEdit edit)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(edit.ToolId)) errors.Add("toolId 不能为空。");
        // Core requires one of the two exact-match keys.
        if (string.IsNullOrWhiteSpace(edit.Command) && string.IsNullOrWhiteSpace(edit.ArgumentsJson))
            errors.Add("命令与参数 JSON 至少要填一个（Core 的精确匹配键）。");
        if (!string.IsNullOrWhiteSpace(edit.ArgumentsJson) && !IsValidRuleJson(edit.ArgumentsJson))
            errors.Add("参数 JSON 不是合法 JSON。");
        if (!RuleSources.Contains(edit.Source, StringComparer.OrdinalIgnoreCase))
            errors.Add($"来源必须是 {string.Join(" / ", RuleSources)} 之一。");
        if (!RuleStatuses.Contains(edit.Status, StringComparer.OrdinalIgnoreCase))
            errors.Add($"状态必须是 {string.Join(" / ", RuleStatuses)} 之一。");
        if (!RuleEffects.Contains(edit.Effect, StringComparer.OrdinalIgnoreCase))
            errors.Add($"效果必须是 {string.Join(" / ", RuleEffects)} 之一。");
        return errors;
    }

    private static bool IsValidRuleJson(string value)
    {
        try { using var _ = System.Text.Json.JsonDocument.Parse(value); return true; }
        catch (System.Text.Json.JsonException) { return false; }
    }

    public static string DescribeHealthSummary(ClassifierHealthReport report)
    {
        if (!report.Configured) return "分类器健康面未接线（未知态）。";
        if (report.Classifiers.Count == 0) return "已接线，但没有任何分类器快照。";
        var unhealthy = report.Classifiers.Count(item =>
            !string.Equals(item.Health, "healthy", StringComparison.OrdinalIgnoreCase));
        return unhealthy == 0
            ? $"已接线：{report.Classifiers.Count} 个分类器全部健康。"
            : $"已接线：{report.Classifiers.Count} 个分类器，其中 {unhealthy} 个非健康。";
    }
}
