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

        // Rule 1: Destructive operations without backup → deny.
        // Mirrors the service-layer BuildHardDenialReason exemption: operations
        // backed by TemporaryFileEvidence (workspace temporary/generated file
        // cleanup detected by IsWorkspaceTemporaryFileCleanupCommand) are not
        // denied on the missing-backup ground.
        if (request.MayDamageOrDeleteData
            && !request.BackupTaken
            && string.IsNullOrWhiteSpace(request.TemporaryFileEvidence))
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

        // Rule 3: Outside authorized area without explicit reason → deny.
        // The authorized-area concept applies to path-like targets (files and
        // directories). Command text / tool-name targets are not path resources:
        // prefix-matching them against a workspace id would structurally deny
        // every command-based request (implicit audit requests are built with
        // TargetResources=[actualCommand] and AuthorizedArea=[workspaceId]), so
        // only path-like targets are checked here. All other rules still apply.
        if (request.AuthorizedArea.Count > 0
            && request.TargetResources.Count > 0
            && string.IsNullOrWhiteSpace(request.OutsideAuthorizedAreaReason))
        {
            var outside = request.TargetResources
                .Where(t => IsPathLikeTarget(t)
                            && !request.AuthorizedArea.Any(a => t.StartsWith(a, StringComparison.OrdinalIgnoreCase)))
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

    /// <summary>
    /// True when the target text starts with a file-system path indicator
    /// (drive letter, UNC prefix, or a path separator). Command text and tool
    /// names do not look like paths and are not subject to the authorized-area
    /// prefix check.
    /// </summary>
    private static bool IsPathLikeTarget(string target)
    {
        if (string.IsNullOrWhiteSpace(target))
            return false;

        var text = target.TrimStart();
        if (text.Length == 0)
            return false;

        // Drive letter prefix, e.g. C:\... or C:/...
        if (text.Length >= 2 && IsAsciiLetter(text[0]) && text[1] == ':')
            return true;

        // UNC path.
        if (text.StartsWith(@"\\", StringComparison.Ordinal))
            return true;

        // Absolute path with forward/back slash.
        if (text[0] is '/' or '\\')
            return true;

        // Dot-relative path (./, .\, ../, ..\).
        if (text.StartsWith("./", StringComparison.Ordinal)
            || text.StartsWith(@".\", StringComparison.Ordinal)
            || text.StartsWith("../", StringComparison.Ordinal)
            || text.StartsWith(@"..\", StringComparison.Ordinal))
        {
            return true;
        }

        return false;
    }

    private static bool IsAsciiLetter(char c)
        => (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z');
}
