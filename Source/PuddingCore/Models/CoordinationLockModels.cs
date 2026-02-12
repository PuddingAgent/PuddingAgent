namespace PuddingCode.Models;

public enum CoordinationLockScope
{
    File,
    Directory
}

public enum CoordinationLockType
{
    Edit,
    Commit,
    TimeWindow
}

public enum CoordinationLockStatus
{
    Active,
    Released,
    Expired,
    ForceReleased
}

public sealed record LockTarget(string Path, CoordinationLockScope Scope);

public sealed record CoordinationLock(
    string Id,
    string OwnerAgentId,
    string OwnerAgentName,
    string OwnerRole,
    CoordinationLockType Type,
    IReadOnlyList<LockTarget> Targets,
    string Description,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpireAt,
    CoordinationLockStatus Status = CoordinationLockStatus.Active,
    DateTimeOffset? ReleasedAt = null,
    string? ReleasedBy = null);

public sealed record LockAcquireRequest(
    string OwnerAgentId,
    string OwnerAgentName,
    string OwnerRole,
    IReadOnlyList<LockTarget> Targets,
    CoordinationLockType Type = CoordinationLockType.Edit,
    TimeSpan? Ttl = null,
    string Description = "");

public sealed record LockAcquireResult(
    bool Acquired,
    string? LockId,
    string Message,
    CoordinationLock? ConflictingLock = null);

public sealed record LockAccessResult(
    bool Allowed,
    string Message,
    CoordinationLock? ConflictingLock = null);

