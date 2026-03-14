using PuddingCode.Models;

namespace PuddingCode.Abstractions;

public interface ICentralLockManager
{
    Task<LockAcquireResult> AcquireAsync(LockAcquireRequest request, CancellationToken ct = default);
    Task<LockAccessResult> CheckAccessAsync(string agentId, string targetPath, CancellationToken ct = default);
    Task<bool> ReleaseAsync(string lockId, string byAgentId, bool force = false, CancellationToken ct = default);
    Task<IReadOnlyList<CoordinationLock>> ListActiveLocksAsync(CancellationToken ct = default);
    Task<int> CleanupExpiredAsync(CancellationToken ct = default);
}

