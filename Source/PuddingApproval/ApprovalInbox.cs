namespace PuddingApproval;

public sealed record ApprovalInboxScope(string WorkspaceId, string AgentId, string SessionId);
public sealed record ApprovalInboxPage(IReadOnlyList<ApprovalRecord> Items, string? NextAfterId);

/// <summary>Read-only Pending-state projection. Expiry is reported in each record, never silently decided by a read.</summary>
public interface IApprovalInbox
{
    // Cursor is exclusive, ordinal ID order. Refresh from null after committed changes;
    // pages are not a multi-request transaction snapshot. Core must authenticate the scope.
    Task<ApprovalInboxPage> ReadPendingAsync(ApprovalInboxScope scope, string? afterId = null,
        int limit = 25, CancellationToken ct = default);
}
