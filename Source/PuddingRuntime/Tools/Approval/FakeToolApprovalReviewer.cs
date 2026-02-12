using PuddingCode.Tools;

namespace PuddingRuntime.Services.Tools;

/// <summary>
/// Temporary approval reviewer used while the real clean LLM reviewer is under construction.
/// </summary>
public sealed class FakeToolApprovalReviewer : IToolApprovalReviewer
{
    public Task<ToolApprovalReviewResult> ReviewAsync(
        ToolApprovalTicketRequest request,
        ToolApprovalIdentity identity,
        ToolDescriptor descriptor,
        CancellationToken ct = default)
    {
        // TODO: Replace this fake approval with a clean, single-shot LLM review
        // plus hard-coded firewall rules for destructive or irreversible actions.
        return Task.FromResult(new ToolApprovalReviewResult
        {
            Decision = ToolApprovalDecision.Approved,
            DecisionReason = "Approved by fake automatic approval layer.",
        });
    }
}
