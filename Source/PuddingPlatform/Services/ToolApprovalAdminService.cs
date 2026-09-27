using PuddingCode.Skills;
using PuddingCode.Tools;

namespace PuddingPlatform.Services;

/// <summary>Rule mutation payload shared by every admin surface (HTTP and native).</summary>
public sealed record ApprovalRuleMutation
{
    public string? WorkspaceId { get; init; }
    public required string ToolId { get; init; }
    public string? Command { get; init; }
    public string? ArgumentsJson { get; init; }
    public string? Source { get; init; }
    public string? Status { get; init; }
    public string? Effect { get; init; }
    public string? ApprovedByAgentInstanceId { get; init; }
    public string? ApprovedByUserId { get; init; }
    public string? ApprovalTicketId { get; init; }
    public string? Reason { get; init; }
}

/// <summary>
/// 工具授权规则应用操作——从 ToolApprovalAdminApiController 原位下沉。
///
/// 关键点：**审计事件是规则变更的一部分**。原实现把写审计放在控制器里，于是任何不经过 HTTP 的
/// 管理面（例如原生客户端直接写 store）改规则都不留痕。现在创建/更新/停用与其审计写入在同一个
/// 应用操作里完成，两个管理面得到同一条审计轨迹。
/// </summary>
public sealed class ToolApprovalAdminService(
    IToolApprovalAllowlistStore allowlistStore,
    IToolApprovalAuditStore auditStore)
{
    public async Task<SkillHubResult<ToolApprovalAllowlistRule>> CreateRuleAsync(
        ApprovalRuleMutation request, CancellationToken ct = default)
    {
        if (!Validate(request, out var source, out var status, out var effect, out var error))
            return SkillHubResult<ToolApprovalAllowlistRule>.BadRequest(error!);

        var now = DateTimeOffset.UtcNow;
        var rule = new ToolApprovalAllowlistRule
        {
            RuleId = "tal_" + Guid.NewGuid().ToString("N"),
            WorkspaceId = Blank(request.WorkspaceId),
            ToolId = ToolAuthorizationDefaults.NormalizeToolId(request.ToolId),
            Command = Blank(request.Command),
            ArgumentsJson = Blank(request.ArgumentsJson),
            Source = source,
            Status = status,
            Effect = effect,
            ApprovedByAgentInstanceId = request.ApprovedByAgentInstanceId,
            ApprovedByUserId = request.ApprovedByUserId,
            ApprovalTicketId = request.ApprovalTicketId,
            Reason = request.Reason,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
        await allowlistStore.SaveAsync(rule, ct);
        await SaveAuditAsync(ToolApprovalAuditEventType.AllowlistRuleCreated, rule,
            "Allowlist rule created from admin API.", now, ct);
        return SkillHubResult<ToolApprovalAllowlistRule>.Ok(rule);
    }

    public async Task<SkillHubResult<ToolApprovalAllowlistRule>> UpdateRuleAsync(
        string ruleId, ApprovalRuleMutation request, CancellationToken ct = default)
    {
        var existing = await allowlistStore.GetAsync(ruleId, ct);
        if (existing is null) return SkillHubResult<ToolApprovalAllowlistRule>.NotFound($"规则 '{ruleId}' 不存在");
        if (!Validate(request, out var source, out var status, out var effect, out var error))
            return SkillHubResult<ToolApprovalAllowlistRule>.BadRequest(error!);

        var now = DateTimeOffset.UtcNow;
        var rule = existing with
        {
            WorkspaceId = Blank(request.WorkspaceId),
            ToolId = ToolAuthorizationDefaults.NormalizeToolId(request.ToolId),
            Command = Blank(request.Command),
            ArgumentsJson = Blank(request.ArgumentsJson),
            Source = source,
            Status = status,
            Effect = effect,
            ApprovedByAgentInstanceId = request.ApprovedByAgentInstanceId,
            ApprovedByUserId = request.ApprovedByUserId,
            ApprovalTicketId = request.ApprovalTicketId,
            Reason = request.Reason,
            UpdatedAtUtc = now,
            DisabledAtUtc = status == ToolApprovalAllowlistRuleStatus.Disabled ? existing.DisabledAtUtc ?? now : null,
        };
        await allowlistStore.SaveAsync(rule, ct);
        await SaveAuditAsync(ToolApprovalAuditEventType.AllowlistRuleUpdated, rule,
            "Allowlist rule updated from admin API.", now, ct);
        return SkillHubResult<ToolApprovalAllowlistRule>.Ok(rule);
    }

    /// <summary>Disables the rule. The record and its audit trail are kept — this is not a delete.</summary>
    public async Task<SkillHubResult<ToolApprovalAllowlistRule>> DisableRuleAsync(string ruleId, CancellationToken ct = default)
    {
        var existing = await allowlistStore.GetAsync(ruleId, ct);
        if (existing is null) return SkillHubResult<ToolApprovalAllowlistRule>.NotFound($"规则 '{ruleId}' 不存在");

        var now = DateTimeOffset.UtcNow;
        var rule = existing with
        {
            Status = ToolApprovalAllowlistRuleStatus.Disabled,
            UpdatedAtUtc = now,
            DisabledAtUtc = now,
        };
        await allowlistStore.SaveAsync(rule, ct);
        await SaveAuditAsync(ToolApprovalAuditEventType.AllowlistRuleDisabled, rule,
            "Allowlist rule disabled from admin API.", now, ct);
        return SkillHubResult<ToolApprovalAllowlistRule>.Ok(rule);
    }

    private async Task SaveAuditAsync(
        ToolApprovalAuditEventType eventType, ToolApprovalAllowlistRule rule,
        string reason, DateTimeOffset now, CancellationToken ct)
        => await auditStore.SaveAsync(new ToolApprovalAuditEvent
        {
            EventId = "taa_" + Guid.NewGuid().ToString("N"),
            EventType = eventType,
            WorkspaceId = rule.WorkspaceId,
            ToolId = rule.ToolId,
            Command = rule.Command,
            ArgumentsJson = rule.ArgumentsJson,
            AllowlistRuleId = rule.RuleId,
            Source = rule.Source,
            Effect = rule.Effect,
            Reason = reason,
            CreatedAtUtc = now,
        }, ct);

    private static bool Validate(ApprovalRuleMutation request,
        out ToolApprovalAllowlistRuleSource source, out ToolApprovalAllowlistRuleStatus status,
        out ToolApprovalRuleEffect effect, out string? error)
    {
        source = ToolApprovalAllowlistRuleSource.Human;
        status = ToolApprovalAllowlistRuleStatus.Enabled;
        effect = ToolApprovalRuleEffect.Allow;
        error = null;
        if (string.IsNullOrWhiteSpace(request.ToolId)) { error = "toolId is required."; return false; }
        if (string.IsNullOrWhiteSpace(request.Command) && string.IsNullOrWhiteSpace(request.ArgumentsJson))
        {
            error = "command or argumentsJson is required.";
            return false;
        }
        if (!TryParseSource(request.Source, out source))
        {
            error = "source must be one of: built_in, audit_agent, human, classifier.";
            return false;
        }
        if (!TryParseStatus(request.Status, out status))
        {
            error = "status must be one of: enabled, disabled.";
            return false;
        }
        if (!TryParseEffect(request.Effect, out effect))
        {
            error = "effect must be one of: allow, deny.";
            return false;
        }
        return true;
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    public static bool TryParseSource(string? value, out ToolApprovalAllowlistRuleSource source)
    {
        source = ToolApprovalAllowlistRuleSource.Human;
        switch ((value ?? "human").Trim().Replace("-", "_", StringComparison.Ordinal).ToLowerInvariant())
        {
            case "built_in" or "builtin": source = ToolApprovalAllowlistRuleSource.BuiltIn; return true;
            case "audit_agent" or "auditagent": source = ToolApprovalAllowlistRuleSource.AuditAgent; return true;
            case "human": source = ToolApprovalAllowlistRuleSource.Human; return true;
            // 分类器落的规则必须能在管理端创建/修改；漏掉这一支会让编辑分类器规则直接 400。
            case "classifier": source = ToolApprovalAllowlistRuleSource.Classifier; return true;
            default: return false;
        }
    }

    public static bool TryParseStatus(string? value, out ToolApprovalAllowlistRuleStatus status)
    {
        switch ((value ?? "enabled").Trim().Replace("-", "_", StringComparison.Ordinal).ToLowerInvariant())
        {
            case "enabled": status = ToolApprovalAllowlistRuleStatus.Enabled; return true;
            case "disabled": status = ToolApprovalAllowlistRuleStatus.Disabled; return true;
            default: status = ToolApprovalAllowlistRuleStatus.Enabled; return false;
        }
    }

    /// <summary>An omitted effect is an allow rule, matching the record's own default.</summary>
    public static bool TryParseEffect(string? value, out ToolApprovalRuleEffect effect)
    {
        switch ((value ?? "allow").Trim().ToLowerInvariant())
        {
            case "" or "allow": effect = ToolApprovalRuleEffect.Allow; return true;
            case "deny": effect = ToolApprovalRuleEffect.Deny; return true;
            default: effect = ToolApprovalRuleEffect.Allow; return false;
        }
    }

    public static string FormatSource(ToolApprovalAllowlistRuleSource source) => source switch
    {
        ToolApprovalAllowlistRuleSource.BuiltIn => "built_in",
        ToolApprovalAllowlistRuleSource.AuditAgent => "audit_agent",
        ToolApprovalAllowlistRuleSource.Human => "human",
        ToolApprovalAllowlistRuleSource.Classifier => "classifier",
        // 兜底不再默认 human：宁可如实回显枚举名，也不要把来源说错。
        _ => source.ToString().ToLowerInvariant()
    };
}
