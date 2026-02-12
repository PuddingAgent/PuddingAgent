using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PuddingCode.Tools;

namespace PuddingPlatform.Controllers.Api;

/// <summary>Admin APIs for automatic approval allowlist rules and audit records.</summary>
[Authorize]
[ApiController]
[Route("api/tool-approval")]
public sealed class ToolApprovalAdminApiController(
    IToolApprovalAllowlistStore allowlistStore,
    IToolApprovalAuditStore auditStore) : ControllerBase
{
    [HttpGet("allowlist")]
    public async Task<IActionResult> ListAllowlist(
        [FromQuery] string? workspaceId = null,
        [FromQuery] string? toolId = null,
        [FromQuery] string? status = null,
        CancellationToken ct = default)
    {
        var rules = await allowlistStore.ListAsync(ct);
        var filtered = rules.Where(rule =>
            (string.IsNullOrWhiteSpace(workspaceId)
             || string.Equals(rule.WorkspaceId ?? "", workspaceId, StringComparison.Ordinal))
            && (string.IsNullOrWhiteSpace(toolId)
                || string.Equals(rule.ToolId, ToolAuthorizationDefaults.NormalizeToolId(toolId), StringComparison.Ordinal))
            && (string.IsNullOrWhiteSpace(status)
                || string.Equals(rule.Status.ToString(), status, StringComparison.OrdinalIgnoreCase)));

        return Ok(new
        {
            items = filtered.Select(MapRule).ToArray(),
        });
    }

    [HttpPost("allowlist")]
    public async Task<IActionResult> CreateAllowlistRule(
        [FromBody] AllowlistRuleMutationDto request,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.ToolId))
            return BadRequest(new { message = "toolId is required." });
        if (string.IsNullOrWhiteSpace(request.Command) && string.IsNullOrWhiteSpace(request.ArgumentsJson))
            return BadRequest(new { message = "command or argumentsJson is required." });
        if (!TryParseSource(request.Source, out var source))
            return BadRequest(new { message = "source must be one of: built_in, audit_agent, human." });
        if (!TryParseStatus(request.Status, out var status))
            return BadRequest(new { message = "status must be one of: enabled, disabled." });

        var now = DateTimeOffset.UtcNow;
        var rule = new ToolApprovalAllowlistRule
        {
            RuleId = "tal_" + Guid.NewGuid().ToString("N"),
            WorkspaceId = string.IsNullOrWhiteSpace(request.WorkspaceId) ? null : request.WorkspaceId.Trim(),
            ToolId = ToolAuthorizationDefaults.NormalizeToolId(request.ToolId),
            Command = string.IsNullOrWhiteSpace(request.Command) ? null : request.Command.Trim(),
            ArgumentsJson = string.IsNullOrWhiteSpace(request.ArgumentsJson) ? null : request.ArgumentsJson.Trim(),
            Source = source,
            Status = status,
            ApprovedByAgentInstanceId = request.ApprovedByAgentInstanceId,
            ApprovedByUserId = request.ApprovedByUserId,
            ApprovalTicketId = request.ApprovalTicketId,
            Reason = request.Reason,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
        await allowlistStore.SaveAsync(rule, ct);
        await SaveAuditAsync(ToolApprovalAuditEventType.AllowlistRuleCreated, rule, "Allowlist rule created from admin API.", now, ct);

        return CreatedAtAction(nameof(GetAllowlistRule), new { ruleId = rule.RuleId }, MapRule(rule));
    }

    [HttpGet("allowlist/{ruleId}")]
    public async Task<IActionResult> GetAllowlistRule(string ruleId, CancellationToken ct)
    {
        var rule = await allowlistStore.GetAsync(ruleId, ct);
        return rule is null ? NotFound() : Ok(MapRule(rule));
    }

    [HttpPut("allowlist/{ruleId}")]
    public async Task<IActionResult> UpdateAllowlistRule(
        string ruleId,
        [FromBody] AllowlistRuleMutationDto request,
        CancellationToken ct)
    {
        var existing = await allowlistStore.GetAsync(ruleId, ct);
        if (existing is null)
            return NotFound();
        if (string.IsNullOrWhiteSpace(request.ToolId))
            return BadRequest(new { message = "toolId is required." });
        if (string.IsNullOrWhiteSpace(request.Command) && string.IsNullOrWhiteSpace(request.ArgumentsJson))
            return BadRequest(new { message = "command or argumentsJson is required." });
        if (!TryParseSource(request.Source, out var source))
            return BadRequest(new { message = "source must be one of: built_in, audit_agent, human." });
        if (!TryParseStatus(request.Status, out var status))
            return BadRequest(new { message = "status must be one of: enabled, disabled." });

        var now = DateTimeOffset.UtcNow;
        var rule = existing with
        {
            WorkspaceId = string.IsNullOrWhiteSpace(request.WorkspaceId) ? null : request.WorkspaceId.Trim(),
            ToolId = ToolAuthorizationDefaults.NormalizeToolId(request.ToolId),
            Command = string.IsNullOrWhiteSpace(request.Command) ? null : request.Command.Trim(),
            ArgumentsJson = string.IsNullOrWhiteSpace(request.ArgumentsJson) ? null : request.ArgumentsJson.Trim(),
            Source = source,
            Status = status,
            ApprovedByAgentInstanceId = request.ApprovedByAgentInstanceId,
            ApprovedByUserId = request.ApprovedByUserId,
            ApprovalTicketId = request.ApprovalTicketId,
            Reason = request.Reason,
            UpdatedAtUtc = now,
            DisabledAtUtc = status == ToolApprovalAllowlistRuleStatus.Disabled
                ? existing.DisabledAtUtc ?? now
                : null,
        };
        await allowlistStore.SaveAsync(rule, ct);
        await SaveAuditAsync(ToolApprovalAuditEventType.AllowlistRuleUpdated, rule, "Allowlist rule updated from admin API.", now, ct);

        return Ok(MapRule(rule));
    }

    [HttpDelete("allowlist/{ruleId}")]
    public async Task<IActionResult> DisableAllowlistRule(string ruleId, CancellationToken ct)
    {
        var existing = await allowlistStore.GetAsync(ruleId, ct);
        if (existing is null)
            return NotFound();

        var now = DateTimeOffset.UtcNow;
        var rule = existing with
        {
            Status = ToolApprovalAllowlistRuleStatus.Disabled,
            UpdatedAtUtc = now,
            DisabledAtUtc = now,
        };
        await allowlistStore.SaveAsync(rule, ct);
        await SaveAuditAsync(ToolApprovalAuditEventType.AllowlistRuleDisabled, rule, "Allowlist rule disabled from admin API.", now, ct);

        return NoContent();
    }

    [HttpGet("audit-events")]
    public async Task<IActionResult> ListAuditEvents(
        [FromQuery] string? workspaceId = null,
        [FromQuery] string? toolId = null,
        [FromQuery] string? eventType = null,
        [FromQuery] int limit = 200,
        CancellationToken ct = default)
    {
        limit = Math.Clamp(limit, 1, 500);
        var events = await auditStore.ListAsync(ct);
        var filtered = events.Where(evt =>
            (string.IsNullOrWhiteSpace(workspaceId)
             || string.Equals(evt.WorkspaceId ?? "", workspaceId, StringComparison.Ordinal))
            && (string.IsNullOrWhiteSpace(toolId)
                || string.Equals(evt.ToolId ?? "", ToolAuthorizationDefaults.NormalizeToolId(toolId), StringComparison.Ordinal))
            && (string.IsNullOrWhiteSpace(eventType)
                || string.Equals(FormatEventType(evt.EventType), eventType, StringComparison.OrdinalIgnoreCase)));

        return Ok(new
        {
            items = filtered.Take(limit).Select(MapAuditEvent).ToArray(),
        });
    }

    [HttpGet("stats")]
    public async Task<IActionResult> GetStats(CancellationToken ct)
    {
        var events = await auditStore.ListAsync(ct);
        var rules = await allowlistStore.ListAsync(ct);

        return Ok(new
        {
            ticketSubmittedCount = events.LongCount(e => e.EventType == ToolApprovalAuditEventType.TicketSubmitted),
            ticketApprovedCount = events.LongCount(e => e.EventType == ToolApprovalAuditEventType.TicketApproved),
            ticketDeniedCount = events.LongCount(e => e.EventType == ToolApprovalAuditEventType.TicketDenied),
            ticketNeedHumanCount = events.LongCount(e => e.EventType == ToolApprovalAuditEventType.TicketNeedHuman),
            ticketMatchedCount = events.LongCount(e => e.EventType == ToolApprovalAuditEventType.TicketMatched),
            ticketConsumedCount = events.LongCount(e => e.EventType == ToolApprovalAuditEventType.TicketConsumed),
            ticketMismatchCount = events.LongCount(e => e.EventType == ToolApprovalAuditEventType.TicketMismatch),
            implicitApprovedCount = events.LongCount(e => e.EventType == ToolApprovalAuditEventType.ImplicitApproved),
            implicitDeniedCount = events.LongCount(e => e.EventType == ToolApprovalAuditEventType.ImplicitDenied),
            allowlistHitCount = events.LongCount(e => e.EventType == ToolApprovalAuditEventType.AllowlistHit),
            allowlistRuleCount = rules.LongCount(),
            enabledAllowlistRuleCount = rules.LongCount(r => r.Status == ToolApprovalAllowlistRuleStatus.Enabled),
            builtInAllowlistRuleCount = rules.LongCount(r => r.Source == ToolApprovalAllowlistRuleSource.BuiltIn),
            dynamicAllowlistRuleCount = rules.LongCount(r => r.Source != ToolApprovalAllowlistRuleSource.BuiltIn),
        });
    }

    private async Task SaveAuditAsync(
        ToolApprovalAuditEventType eventType,
        ToolApprovalAllowlistRule rule,
        string reason,
        DateTimeOffset now,
        CancellationToken ct)
    {
        await auditStore.SaveAsync(new ToolApprovalAuditEvent
        {
            EventId = "taa_" + Guid.NewGuid().ToString("N"),
            EventType = eventType,
            WorkspaceId = rule.WorkspaceId,
            ToolId = rule.ToolId,
            Command = rule.Command,
            ArgumentsJson = rule.ArgumentsJson,
            AllowlistRuleId = rule.RuleId,
            Source = rule.Source,
            Reason = reason,
            CreatedAtUtc = now,
        }, ct);
    }

    private static object MapRule(ToolApprovalAllowlistRule rule)
        => new
        {
            rule.RuleId,
            rule.WorkspaceId,
            rule.ToolId,
            rule.Command,
            rule.ArgumentsJson,
            source = FormatSource(rule.Source),
            status = FormatStatus(rule.Status),
            rule.ApprovedByAgentInstanceId,
            rule.ApprovedByUserId,
            rule.ApprovalTicketId,
            rule.Reason,
            rule.CreatedAtUtc,
            rule.UpdatedAtUtc,
            rule.DisabledAtUtc,
            rule.HitCount,
            rule.LastHitAtUtc,
        };

    private static object MapAuditEvent(ToolApprovalAuditEvent evt)
        => new
        {
            evt.EventId,
            eventType = FormatEventType(evt.EventType),
            evt.WorkspaceId,
            evt.SessionId,
            evt.AgentInstanceId,
            evt.UserId,
            evt.ToolId,
            evt.Command,
            evt.ArgumentsJson,
            evt.OriginalCommand,
            evt.OriginalArgumentsJson,
            evt.TicketId,
            evt.AllowlistRuleId,
            evt.AllowlistRuleCommand,
            evt.AllowlistRuleArgumentsJson,
            evt.AllowlistRuleHitCount,
            decision = evt.Decision?.ToString().ToLowerInvariant(),
            source = evt.Source.HasValue ? FormatSource(evt.Source.Value) : null,
            evt.ReviewerModel,
            evt.Reason,
            evt.CreatedAtUtc,
        };

    private static bool TryParseSource(string? value, out ToolApprovalAllowlistRuleSource source)
    {
        source = ToolApprovalAllowlistRuleSource.Human;
        return (value ?? "human").Trim().Replace("-", "_", StringComparison.Ordinal).ToLowerInvariant() switch
        {
            "built_in" or "builtin" => Set(ToolApprovalAllowlistRuleSource.BuiltIn, out source),
            "audit_agent" or "auditagent" => Set(ToolApprovalAllowlistRuleSource.AuditAgent, out source),
            "human" => Set(ToolApprovalAllowlistRuleSource.Human, out source),
            _ => false,
        };
    }

    private static bool TryParseStatus(string? value, out ToolApprovalAllowlistRuleStatus status)
    {
        status = ToolApprovalAllowlistRuleStatus.Enabled;
        return (value ?? "enabled").Trim().Replace("-", "_", StringComparison.Ordinal).ToLowerInvariant() switch
        {
            "enabled" => Set(ToolApprovalAllowlistRuleStatus.Enabled, out status),
            "disabled" => Set(ToolApprovalAllowlistRuleStatus.Disabled, out status),
            _ => false,
        };
    }

    private static bool Set<T>(T value, out T target)
    {
        target = value;
        return true;
    }

    private static string FormatSource(ToolApprovalAllowlistRuleSource source)
        => source switch
        {
            ToolApprovalAllowlistRuleSource.BuiltIn => "built_in",
            ToolApprovalAllowlistRuleSource.AuditAgent => "audit_agent",
            _ => "human",
        };

    private static string FormatStatus(ToolApprovalAllowlistRuleStatus status)
        => status == ToolApprovalAllowlistRuleStatus.Disabled ? "disabled" : "enabled";

    private static string FormatEventType(ToolApprovalAuditEventType eventType)
        => eventType switch
        {
            ToolApprovalAuditEventType.TicketSubmitted => "ticket_submitted",
            ToolApprovalAuditEventType.TicketApproved => "ticket_approved",
            ToolApprovalAuditEventType.TicketDenied => "ticket_denied",
            ToolApprovalAuditEventType.TicketNeedHuman => "ticket_need_human",
            ToolApprovalAuditEventType.TicketMatched => "ticket_matched",
            ToolApprovalAuditEventType.TicketConsumed => "ticket_consumed",
            ToolApprovalAuditEventType.TicketMismatch => "ticket_mismatch",
            ToolApprovalAuditEventType.ImplicitApproved => "implicit_approved",
            ToolApprovalAuditEventType.ImplicitDenied => "implicit_denied",
            ToolApprovalAuditEventType.AllowlistHit => "allowlist_hit",
            ToolApprovalAuditEventType.AllowlistRuleCreated => "allowlist_rule_created",
            ToolApprovalAuditEventType.AllowlistRuleUpdated => "allowlist_rule_updated",
            ToolApprovalAuditEventType.AllowlistRuleDisabled => "allowlist_rule_disabled",
            _ => eventType.ToString().ToLowerInvariant(),
        };

    public sealed record AllowlistRuleMutationDto
    {
        public string? WorkspaceId { get; init; }
        public required string ToolId { get; init; }
        public string? Command { get; init; }
        public string? ArgumentsJson { get; init; }
        public string? Source { get; init; }
        public string? Status { get; init; }
        public string? ApprovedByAgentInstanceId { get; init; }
        public string? ApprovedByUserId { get; init; }
        public string? ApprovalTicketId { get; init; }
        public string? Reason { get; init; }
    }
}
