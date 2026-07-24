using PuddingCode.Tools;

namespace PuddingRuntime.Services.Tools;

/// <summary>
/// Automatic tool approval reviewer with hard-coded firewall rules.
/// Future: add single-shot LLM review for nuanced cases.
/// </summary>
public sealed class FakeToolApprovalReviewer : IToolApprovalReviewer
{
    public Task<ToolApprovalReviewResult> ReviewAsync(
        ToolApprovalTicketRequest request,
        ToolApprovalIdentity identity,
        ToolDescriptor descriptor,
        CancellationToken ct = default)
    {
        // ── Firewall Rules ──

        // Rule 1: Destructive operations without backup → deny
        if (request.MayDamageOrDeleteData && !request.BackupTaken)
        {
            return Task.FromResult(new ToolApprovalReviewResult
            {
                Decision = ToolApprovalDecision.Denied,
                DecisionReason = "Operation may damage or delete data and no backup has been taken.",
            });
        }

        // Rule 2: Irreversible operations without rollback plan → deny
        if (request.IsIrreversibleOperation && string.IsNullOrWhiteSpace(request.RollbackPlan))
        {
            return Task.FromResult(new ToolApprovalReviewResult
            {
                Decision = ToolApprovalDecision.Denied,
                DecisionReason = "Operation is irreversible and no rollback plan has been provided.",
            });
        }

        // Rule 3: Outside authorized area without explicit reason → deny
        if (request.AuthorizedArea.Count > 0
            && request.TargetResources.Count > 0
            && string.IsNullOrWhiteSpace(request.OutsideAuthorizedAreaReason))
        {
            var outside = request.TargetResources
                .Where(t => !request.AuthorizedArea.Any(a => t.StartsWith(a, StringComparison.OrdinalIgnoreCase)))
                .ToList();
            if (outside.Count > 0)
            {
                return Task.FromResult(new ToolApprovalReviewResult
                {
                    Decision = ToolApprovalDecision.Denied,
                    DecisionReason = $"Target resource(s) outside authorized area: {string.Join(", ", outside)}.",
                });
            }
        }

        // Rule 4: May expose secrets → always deny for safety
        if (request.MayExposeSecrets)
        {
            return Task.FromResult(new ToolApprovalReviewResult
            {
                Decision = ToolApprovalDecision.Denied,
                DecisionReason = "Operation may expose secrets, tokens, or sensitive data.",
            });
        }

        // ── Default: approve with rationale ──
        return Task.FromResult(new ToolApprovalReviewResult
        {
            Decision = ToolApprovalDecision.Approved,
            DecisionReason = "Passed firewall rules. Approved by automatic approval layer.",
        });
    }
}
