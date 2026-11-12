using System.Collections.Immutable;
using PuddingAuthorization.Contracts;

namespace PuddingAuthorization;

/// <summary>
/// PA-05A pure authorization decision: system Deny always wins, plugin rules can only tighten, a pending or
/// mismatched approval never grants execution, expired rules/approvals are ignored, and an approval is bound to
/// the exact action/resource/argument hash/policy version. No UI, storage or host service is touched here.
/// </summary>
public sealed class AuthorizationEvaluator
{
    private readonly IAuthorizationPolicySource _source;
    private readonly IApprovalStore _approvals;
    private readonly TimeProvider _clock;

    public AuthorizationEvaluator(IAuthorizationPolicySource source, IApprovalStore approvals, TimeProvider? clock = null)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _approvals = approvals ?? throw new ArgumentNullException(nameof(approvals));
        _clock = clock ?? TimeProvider.System;
    }

    public async ValueTask<AuthorizationDecision> EvaluateAsync(AuthorizationRequest request,
        CancellationToken ct = default)
    {
        if (request is null || !request.IsValid)
            throw new AuthorizationException(AuthorizationErrorCodes.RequestInvalid, "A bounded authorization request is required.");
        var now = _clock.GetUtcNow();
        ImmutableArray<AuthorizationRule> rules;
        try
        {
            rules = await _source.GetRulesAsync(request.Action, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (AuthorizationException) { throw; }
        catch (Exception)
        {
            return Decision(AuthorizationOutcome.Unavailable, request, AuthorizationErrorCodes.Unavailable, null, now);
        }

        var expected = rules.Where(r => string.Equals(r.Action, request.Action, StringComparison.Ordinal)).ToArray();
        var active = expected.Where(r => r.IsActiveAt(now)).ToArray();
        if (expected.Length > 0 && active.Length == 0)
            return Decision(AuthorizationOutcome.Deny, request, AuthorizationErrorCodes.Denied, null, now);
        if (active.Any(r => r.PolicyVersion != request.PolicyVersion))
            return Decision(AuthorizationOutcome.Unavailable, request, AuthorizationErrorCodes.Unavailable, null, now);

        // 1) system Deny is irreversible; an approval or plugin Allow cannot override it.
        if (active.Any(r => r.Outcome == AuthorizationOutcome.Deny && r.Source == PolicySourceKind.System))
            return Decision(AuthorizationOutcome.Deny, request, AuthorizationErrorCodes.Denied, null, now);
        // 2) plugin/workspace Deny may only tighten.
        if (active.Any(r => r.Outcome == AuthorizationOutcome.Deny))
            return Decision(AuthorizationOutcome.Deny, request, AuthorizationErrorCodes.Denied, null, now);
        if (active.Length == 0)
            return Decision(AuthorizationOutcome.Deny, request, AuthorizationErrorCodes.Denied, null, now);

        var needsApproval = active.Any(r => r.Outcome == AuthorizationOutcome.NeedsApproval);
        if (needsApproval || !string.IsNullOrWhiteSpace(request.ApprovalId))
        {
            if (string.IsNullOrWhiteSpace(request.ApprovalId))
                return Decision(AuthorizationOutcome.NeedsApproval, request, AuthorizationErrorCodes.ApprovalRequired, null, now);
            var approval = await _approvals.GetAsync(request.ApprovalId, ct).ConfigureAwait(false);
            if (approval is null)
                return Decision(AuthorizationOutcome.NeedsApproval, request, AuthorizationErrorCodes.ApprovalMissing, request.ApprovalId, now);
            if (approval.State == ApprovalState.Expired || now >= approval.ExpiresAt)
                return Decision(AuthorizationOutcome.NeedsApproval, request, AuthorizationErrorCodes.ApprovalExpired, request.ApprovalId, now);
            if (approval.State != ApprovalState.Approved)
                return Decision(AuthorizationOutcome.NeedsApproval, request, AuthorizationErrorCodes.ApprovalRequired, request.ApprovalId, now);
            if (!Matches(approval, request))
                return Decision(AuthorizationOutcome.Deny, request, AuthorizationErrorCodes.ApprovalMismatch, request.ApprovalId, now);
            return Decision(AuthorizationOutcome.Allow, request, "authorization.approved", request.ApprovalId, now);
        }

        return active.Any(r => r.Outcome == AuthorizationOutcome.Allow)
            ? Decision(AuthorizationOutcome.Allow, request, "authorization.allowed", null, now)
            : Decision(AuthorizationOutcome.Deny, request, AuthorizationErrorCodes.Denied, null, now);
    }

    public static bool Matches(ApprovalRecord approval, AuthorizationRequest request) =>
        string.Equals(approval.WorkspaceId, request.WorkspaceId, StringComparison.Ordinal)
        && string.Equals(approval.AgentId, request.AgentId, StringComparison.Ordinal)
        && string.Equals(approval.Action, request.Action, StringComparison.Ordinal)
        && string.Equals(approval.Resource, request.Resource, StringComparison.Ordinal)
        && string.Equals(approval.ArgumentHash, request.ArgumentHash, StringComparison.Ordinal)
        && approval.PolicyVersion == request.PolicyVersion;

    private static AuthorizationDecision Decision(AuthorizationOutcome outcome, AuthorizationRequest request,
        string reason, string? approvalId, DateTimeOffset now) => new(outcome, request.PolicyVersion, reason, approvalId, now);
}
