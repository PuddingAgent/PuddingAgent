using System.Collections.Concurrent;
using PuddingCode.Platform;

namespace PuddingController.Services;

/// <summary>内存审批服务——管理审批请求的生命周期。</summary>
public sealed class InMemoryApprovalService : IApprovalService
{
    private readonly ConcurrentDictionary<string, ApprovalRecord> _approvals = new();
    private static readonly TimeSpan DefaultExpiry = TimeSpan.FromHours(24);

    public Task<ApprovalRecord> RequestApprovalAsync(string sessionId, string workspaceId, string actionDescription, CancellationToken ct = default)
    {
        var record = new ApprovalRecord
        {
            SessionId = sessionId,
            WorkspaceId = workspaceId,
            ActionDescription = actionDescription,
            ConfirmationCode = Guid.NewGuid().ToString("N")[..8],
            ExpiresAt = DateTimeOffset.UtcNow.Add(DefaultExpiry),
        };
        _approvals[record.ApprovalId] = record;
        return Task.FromResult(record);
    }

    public Task<ApprovalRecord?> GetAsync(string approvalId, CancellationToken ct = default)
        => Task.FromResult(_approvals.GetValueOrDefault(approvalId));

    public Task<IReadOnlyList<ApprovalRecord>> QueryPendingAsync(CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        var pending = _approvals.Values
            .Where(a => a.Status == ApprovalStatus.Pending && (a.ExpiresAt is null || a.ExpiresAt > now))
            .OrderByDescending(a => a.CreatedAt)
            .ToList();
        return Task.FromResult<IReadOnlyList<ApprovalRecord>>(pending);
    }

    public Task<bool> ConfirmAsync(string approvalId, string confirmationCode, string confirmedBy, CancellationToken ct = default)
    {
        if (!_approvals.TryGetValue(approvalId, out var record))
            return Task.FromResult(false);

        if (record.Status != ApprovalStatus.Pending)
            return Task.FromResult(false);

        if (record.ExpiresAt is not null && record.ExpiresAt < DateTimeOffset.UtcNow)
        {
            _approvals[approvalId] = record with { Status = ApprovalStatus.Expired };
            return Task.FromResult(false);
        }

        if (record.ConfirmationCode != confirmationCode)
            return Task.FromResult(false);

        _approvals[approvalId] = record with
        {
            Status = ApprovalStatus.Confirmed,
            ResolvedAt = DateTimeOffset.UtcNow,
            ResolvedBy = confirmedBy
        };
        return Task.FromResult(true);
    }

    public Task<bool> RejectAsync(string approvalId, string rejectedBy, CancellationToken ct = default)
    {
        if (!_approvals.TryGetValue(approvalId, out var record))
            return Task.FromResult(false);

        if (record.Status != ApprovalStatus.Pending)
            return Task.FromResult(false);

        _approvals[approvalId] = record with
        {
            Status = ApprovalStatus.Rejected,
            ResolvedAt = DateTimeOffset.UtcNow,
            ResolvedBy = rejectedBy
        };
        return Task.FromResult(true);
    }
}
