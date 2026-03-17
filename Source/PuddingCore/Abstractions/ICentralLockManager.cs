using PuddingCode.Models;

namespace PuddingCode.Abstractions;

public interface ICentralLockManager
{
    Task<LockAcquireResult> AcquireAsync(LockAcquireRequest request, CancellationToken ct = default);

    /// <summary>Leader 强制获取（无视当前持有者）。</summary>
    Task<LockAcquireResult> ForceAcquireAsync(LockAcquireRequest request, CancellationToken ct = default);

    Task<LockAccessResult> CheckAccessAsync(string agentId, string targetPath, CancellationToken ct = default);

    Task<bool> ReleaseAsync(string lockId, string byAgentId, bool force = false, CancellationToken ct = default);

    /// <summary>
    /// 向锁持有者发出解锁请求（协同模式）。
    /// 不直接释放锁，只在总线广播 <see cref="CoordinationEventKind.UnlockRequested"/> 事件。
    /// </summary>
    Task<bool> RequestUnlockAsync(string lockId, string requestingAgentId, string reason = "", CancellationToken ct = default);

    Task<IReadOnlyList<CoordinationLock>> ListActiveLocksAsync(CancellationToken ct = default);
    Task<int> CleanupExpiredAsync(CancellationToken ct = default);
}

