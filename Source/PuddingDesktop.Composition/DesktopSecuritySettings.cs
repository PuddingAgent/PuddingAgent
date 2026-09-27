using Microsoft.Extensions.DependencyInjection;
using PuddingCode.Abstractions;
using PuddingCode.Classification;
using PuddingCode.Tools;
using PuddingDesktop.Foundation;
using PuddingPlatform.Services;

namespace PuddingDesktop.Composition;

/// <summary>
/// DS-10 vault and classifier-health slice.
///
/// The vault is write-only from the settings panel: this adapter asks Core for summaries only and never
/// requests the plaintext. Classifier health is an optional Core surface, so a missing reporter is reported
/// as "not wired" rather than as healthy.
/// </summary>
internal sealed class DesktopSecuritySettings(IDesktopKernel kernel) : ISecuritySettings
{
    public Task<IReadOnlyList<VaultSecret>> ListSecretsAsync(CancellationToken cancellationToken = default)
        => kernel.RunSettingsAsync("security.vault.list", async (scope, token) =>
        {
            var secrets = await scope.Services.GetRequiredService<IKeyVaultService>().ListSecretsAsync(token);
            return (IReadOnlyList<VaultSecret>)secrets.Select(secret => new VaultSecret(
                secret.Id, secret.KeyVaultId, secret.Name, secret.Description ?? "", secret.Category,
                secret.Tags ?? [], secret.CreatedAt, secret.UpdatedAt)).ToArray();
        }, cancellationToken);

    public Task SaveSecretAsync(VaultSecretEdit edit, CancellationToken cancellationToken = default)
        => kernel.RunSettingsAsync("security.vault.save", async (scope, token) =>
        {
            var service = scope.Services.GetRequiredService<IKeyVaultService>();
            if (edit.IsCreate)
            {
                await service.CreateSecretAsync(new CreateKeyVaultSecretCommand
                {
                    Name = edit.Name.Trim(),
                    Value = edit.Value,
                    Description = Nullable(edit.Description),
                    Category = edit.Category,
                    Tags = [.. edit.Tags],
                }, token);
                return true;
            }

            // A blank value means "keep the stored secret"; Core treats it that way, so pass null.
            var updated = await service.UpdateSecretAsync(edit.KeyVaultId, new UpdateKeyVaultSecretCommand
            {
                Name = edit.Name.Trim(),
                Value = string.IsNullOrWhiteSpace(edit.Value) ? null : edit.Value,
                Description = Nullable(edit.Description),
                Category = edit.Category,
                Tags = [.. edit.Tags],
            }, token);
            if (updated is null) throw new InvalidOperationException($"密钥 '{edit.KeyVaultId}' 不存在。");
            return true;
        }, cancellationToken);

    public Task DeleteSecretAsync(string keyVaultId, CancellationToken cancellationToken = default)
        => kernel.RunSettingsAsync("security.vault.delete", async (scope, token) =>
        {
            if (!await scope.Services.GetRequiredService<IKeyVaultService>().DeleteSecretAsync(keyVaultId, token))
                throw new InvalidOperationException($"密钥 '{keyVaultId}' 未删除：Core 返回了否。");
            return true;
        }, cancellationToken);

    public Task<IReadOnlyList<ApprovalRule>> ListApprovalRulesAsync(
        string? workspaceId, string? toolId, string? status, CancellationToken cancellationToken = default)
        => kernel.RunSettingsAsync("security.approvals.rules.list", async (scope, token) =>
        {
            var rules = await scope.Services.GetRequiredService<IToolApprovalAllowlistStore>().ListAsync(token);
            return (IReadOnlyList<ApprovalRule>)rules
                .Where(rule => Filter(rule, workspaceId, toolId, status))
                .Select(Map).ToArray();
        }, cancellationToken);

    public Task SaveApprovalRuleAsync(ApprovalRuleEdit edit, CancellationToken cancellationToken = default)
        => kernel.RunSettingsAsync("security.approvals.rules.save", async (scope, token) =>
        {
            // The application service owns the audit side effect, so a native change is audited too.
            var service = scope.Services.GetRequiredService<ToolApprovalAdminService>();
            var mutation = Mutation(edit);
            var result = edit.IsCreate
                ? await service.CreateRuleAsync(mutation, token)
                : await service.UpdateRuleAsync(edit.RuleId, mutation, token);
            if (!result.IsOk) throw new InvalidOperationException(result.Error ?? "Core 拒绝了该操作。");
            return true;
        }, cancellationToken);

    public Task DisableApprovalRuleAsync(string ruleId, CancellationToken cancellationToken = default)
        => kernel.RunSettingsAsync("security.approvals.rules.disable", async (scope, token) =>
        {
            // Same as the HTTP surface: disable, never delete.
            var result = await scope.Services.GetRequiredService<ToolApprovalAdminService>()
                .DisableRuleAsync(ruleId, token);
            if (!result.IsOk) throw new InvalidOperationException(result.Error ?? "Core 拒绝了该操作。");
            return true;
        }, cancellationToken);

    public Task<IReadOnlyList<ApprovalAuditEntry>> ListApprovalAuditAsync(
        ApprovalAuditQuery query, CancellationToken cancellationToken = default)
        => kernel.RunSettingsAsync("security.approvals.audit.list", async (scope, token) =>
        {
            var events = await scope.Services.GetRequiredService<IToolApprovalAuditStore>().ListAsync(token);
            var limit = Math.Clamp(query.Limit, 1, 500);
            return (IReadOnlyList<ApprovalAuditEntry>)events
                .Where(evt => (string.IsNullOrWhiteSpace(query.WorkspaceId)
                               || string.Equals(evt.WorkspaceId ?? "", query.WorkspaceId, StringComparison.Ordinal))
                              && (string.IsNullOrWhiteSpace(query.ToolId)
                                  || string.Equals(evt.ToolId ?? "",
                                      ToolAuthorizationDefaults.NormalizeToolId(query.ToolId), StringComparison.Ordinal))
                              && (string.IsNullOrWhiteSpace(query.EventType)
                                  || string.Equals(ToolApprovalWire.ToWire(evt.EventType), query.EventType,
                                      StringComparison.OrdinalIgnoreCase)))
                .Take(limit)
                .Select(evt => new ApprovalAuditEntry(evt.EventId, ToolApprovalWire.ToWire(evt.EventType),
                    evt.WorkspaceId ?? "", evt.SessionId ?? "", evt.AgentInstanceId ?? "", evt.UserId ?? "",
                    evt.ToolId ?? "", evt.Command ?? "", evt.ArgumentsJson ?? "", evt.TicketId ?? "",
                    evt.AllowlistRuleId ?? "",
                    evt.Decision?.ToString().ToLowerInvariant() ?? "",
                    evt.Source is { } source ? FormatSource(source) : "",
                    evt.ReviewerModel ?? "", evt.Reason ?? "", evt.CreatedAtUtc)).ToArray();
        }, cancellationToken);

    public Task<ApprovalStats> ReadApprovalStatsAsync(CancellationToken cancellationToken = default)
        => kernel.RunSettingsAsync("security.approvals.stats", async (scope, token) =>
        {
            var events = await scope.Services.GetRequiredService<IToolApprovalAuditStore>().ListAsync(token);
            var rules = await scope.Services.GetRequiredService<IToolApprovalAllowlistStore>().ListAsync(token);
            long Count(ToolApprovalAuditEventType type) => events.LongCount(evt => evt.EventType == type);
            return new ApprovalStats(
                Count(ToolApprovalAuditEventType.TicketSubmitted), Count(ToolApprovalAuditEventType.TicketApproved),
                Count(ToolApprovalAuditEventType.TicketDenied), Count(ToolApprovalAuditEventType.TicketNeedHuman),
                Count(ToolApprovalAuditEventType.TicketMatched), Count(ToolApprovalAuditEventType.TicketConsumed),
                Count(ToolApprovalAuditEventType.TicketMismatch), Count(ToolApprovalAuditEventType.ImplicitApproved),
                Count(ToolApprovalAuditEventType.ImplicitDenied), Count(ToolApprovalAuditEventType.AllowlistHit),
                rules.Count, rules.LongCount(rule => rule.Status == ToolApprovalAllowlistRuleStatus.Enabled),
                rules.LongCount(rule => rule.Source == ToolApprovalAllowlistRuleSource.BuiltIn),
                rules.LongCount(rule => rule.Source != ToolApprovalAllowlistRuleSource.BuiltIn));
        }, cancellationToken);

    private static bool Filter(ToolApprovalAllowlistRule rule, string? workspaceId, string? toolId, string? status) =>
        (string.IsNullOrWhiteSpace(workspaceId) || string.Equals(rule.WorkspaceId ?? "", workspaceId, StringComparison.Ordinal))
        && (string.IsNullOrWhiteSpace(toolId)
            || string.Equals(rule.ToolId, ToolAuthorizationDefaults.NormalizeToolId(toolId), StringComparison.Ordinal))
        && (string.IsNullOrWhiteSpace(status)
            || string.Equals(rule.Status.ToString(), status, StringComparison.OrdinalIgnoreCase));

    private static ApprovalRule Map(ToolApprovalAllowlistRule rule) => new(
        rule.RuleId, rule.WorkspaceId ?? "", rule.ToolId, rule.Command ?? "", rule.ArgumentsJson ?? "",
        FormatSource(rule.Source),
        rule.Status == ToolApprovalAllowlistRuleStatus.Disabled ? "disabled" : "enabled",
        rule.Effect == ToolApprovalRuleEffect.Deny ? "deny" : "allow",
        rule.ApprovedByAgentInstanceId ?? "", rule.ApprovedByUserId ?? "", rule.ApprovalTicketId ?? "",
        rule.Reason ?? "", rule.HitCount, rule.LastHitAtUtc, rule.CreatedAtUtc, rule.UpdatedAtUtc,
        rule.DisabledAtUtc, rule.DefinitionVersion, rule.ExpiresAtUtc);

    // Single source for the wire name: the same helper the admin service and HTTP surface use.
    private static string FormatSource(ToolApprovalAllowlistRuleSource source) =>
        ToolApprovalAdminService.FormatSource(source);

    private static ApprovalRuleMutation Mutation(ApprovalRuleEdit edit) => new()
    {
        WorkspaceId = Nullable(edit.WorkspaceId),
        ToolId = edit.ToolId,
        Command = Nullable(edit.Command),
        ArgumentsJson = Nullable(edit.ArgumentsJson),
        Source = edit.Source,
        Status = edit.Status,
        Effect = edit.Effect,
        ApprovedByAgentInstanceId = Nullable(edit.ApprovedByAgentInstanceId),
        ApprovedByUserId = Nullable(edit.ApprovedByUserId),
        ApprovalTicketId = Nullable(edit.ApprovalTicketId),
        Reason = Nullable(edit.Reason),
    };

    public Task<ClassifierHealthReport> ReadClassifierHealthAsync(CancellationToken cancellationToken = default)
        => kernel.RunSettingsAsync("security.classifierHealth", (scope, _) =>
        {
            // Optional surface: absent means unknown, never a fabricated healthy state.
            var reporter = scope.Services.GetService<IClassifierHealthReporter>();
            if (reporter is null) return Task.FromResult(ClassifierHealthReport.NotConfigured);
            var snapshot = reporter.Snapshot();
            return Task.FromResult(new ClassifierHealthReport(true,
                snapshot.Select(status => new ClassifierStatusEntry(status.ClassifierId,
                    status.Health.ToString().ToLowerInvariant(), status.Detail ?? "",
                    status.ConsecutiveFailures, status.LastCheckedAtUtc, status.LastLatencyMs)).ToArray()));
        }, cancellationToken);

    private static string? Nullable(string value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
