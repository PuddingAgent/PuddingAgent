using System.Text.Json;
using PuddingCode.Abstractions;
using PuddingCode.Models;

namespace PuddingCode.Swarm;

public sealed class CentralLockManager : ICentralLockManager
{
    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private readonly object _gate = new();
    private readonly string _statePath;
    private readonly StringComparison _pathComparison;
    private readonly ICoordinationEventBus? _bus;

    /// <param name="projectRoot">项目根目录，锁状态持久化到 .pudding/locks/lock-state.json。</param>
    /// <param name="bus">协同总线（可选），传入 null 时仅持久化锁状态，不发布事件。</param>
    public CentralLockManager(string projectRoot, ICoordinationEventBus? bus = null)
    {
        var root = Path.GetFullPath(projectRoot);
        _statePath = Path.Combine(root, ".pudding", "locks", "lock-state.json");
        _pathComparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        _bus = bus;
    }

    public async Task<LockAcquireResult> AcquireAsync(LockAcquireRequest request, CancellationToken ct = default)
    {
        if (request.Targets.Count == 0)
            return new LockAcquireResult(false, null, "No lock targets specified.");

        LockAcquireResult result;
        lock (_gate)
        {
            var state = LoadStateUnsafe();
            CleanupExpiredUnsafe(state, DateTimeOffset.Now);
            var normalizedTargets = request.Targets
                .Select(t => t with { Path = NormalizePath(t.Path) })
                .ToList();

            var conflict = state.Locks
                .Where(IsActive)
                .Where(l => !l.OwnerAgentId.Equals(request.OwnerAgentId, StringComparison.OrdinalIgnoreCase))
                .FirstOrDefault(l => Overlaps(l.Targets, normalizedTargets));

            if (conflict is not null)
            {
                result = new LockAcquireResult(
                    false,
                    null,
                    $"Locked by {conflict.OwnerAgentName} ({conflict.OwnerAgentId}).",
                    conflict);
            }
            else
            {
                var now = DateTimeOffset.Now;
                var ttl = request.Ttl ?? TimeSpan.FromMinutes(15);
                var lockEntry = new CoordinationLock(
                    Id: $"lock-{Guid.NewGuid():N}"[..13],
                    OwnerAgentId: request.OwnerAgentId,
                    OwnerAgentName: request.OwnerAgentName,
                    OwnerRole: request.OwnerRole,
                    Type: request.Type,
                    Targets: normalizedTargets,
                    Description: request.Description,
                    CreatedAt: now,
                    ExpireAt: now.Add(ttl),
                    Status: CoordinationLockStatus.Active);
                state.Locks.Add(lockEntry);
                SaveStateUnsafe(state);
                result = new LockAcquireResult(true, lockEntry.Id, "Lock acquired.");
            }
        }

        await Task.CompletedTask;

        // 在锁外发布总线事件（避免死锁）
        _bus?.Publish(new CoordinationEvent
        {
            Kind = result.Acquired ? CoordinationEventKind.LockAcquired : CoordinationEventKind.LockDenied,
            LockId = result.Acquired ? result.LockId : result.ConflictingLock?.Id,
            AgentId = request.OwnerAgentId,
            Detail = result.Message
        });

        return result;
    }

    public async Task<LockAcquireResult> ForceAcquireAsync(LockAcquireRequest request, CancellationToken ct = default)
    {
        if (request.Targets.Count == 0)
            return new LockAcquireResult(false, null, "No lock targets specified.");

        LockAcquireResult result;
        lock (_gate)
        {
            var state = LoadStateUnsafe();
            CleanupExpiredUnsafe(state, DateTimeOffset.Now);
            var normalizedTargets = request.Targets
                .Select(t => t with { Path = NormalizePath(t.Path) })
                .ToList();

            // 强制释放指定文件集合上的所有冲突锁
            var now = DateTimeOffset.Now;
            for (var i = 0; i < state.Locks.Count; i++)
            {
                var existing = state.Locks[i];
                if (!IsActive(existing)) continue;
                if (!Overlaps(existing.Targets, normalizedTargets)) continue;

                state.Locks[i] = existing with
                {
                    Status = CoordinationLockStatus.ForceReleased,
                    ReleasedAt = now,
                    ReleasedBy = request.OwnerAgentId
                };
            }

            var ttl = request.Ttl ?? TimeSpan.FromMinutes(15);
            var lockEntry = new CoordinationLock(
                Id: $"lock-{Guid.NewGuid():N}"[..13],
                OwnerAgentId: request.OwnerAgentId,
                OwnerAgentName: request.OwnerAgentName,
                OwnerRole: request.OwnerRole,
                Type: request.Type,
                Targets: normalizedTargets,
                Description: request.Description,
                CreatedAt: now,
                ExpireAt: now.Add(ttl),
                Status: CoordinationLockStatus.Active);
            state.Locks.Add(lockEntry);
            SaveStateUnsafe(state);
            result = new LockAcquireResult(true, lockEntry.Id, "Force lock acquired.");
        }

        await Task.CompletedTask;

        _bus?.Publish(new CoordinationEvent
        {
            Kind = CoordinationEventKind.LockAcquired,
            LockId = result.LockId,
            AgentId = request.OwnerAgentId,
            Detail = result.Message
        });

        return result;
    }

    public async Task<LockAccessResult> CheckAccessAsync(string agentId, string targetPath, CancellationToken ct = default)
    {
        var normalized = NormalizePath(targetPath);
        LockAccessResult result;

        lock (_gate)
        {
            var state = LoadStateUnsafe();
            CleanupExpiredUnsafe(state, DateTimeOffset.Now);

            var conflict = state.Locks
                .Where(IsActive)
                .Where(l => !l.OwnerAgentId.Equals(agentId, StringComparison.OrdinalIgnoreCase))
                .FirstOrDefault(l => Overlaps(l.Targets, [new LockTarget(normalized, CoordinationLockScope.File)]));

            result = conflict is null
                ? new LockAccessResult(true, "Allowed.")
                : new LockAccessResult(
                    false,
                    $"File is locked by {conflict.OwnerAgentName} ({conflict.OwnerAgentId}).",
                    conflict);
        }

        await Task.CompletedTask;
        return result;
    }

    public async Task<bool> ReleaseAsync(string lockId, string byAgentId, bool force = false, CancellationToken ct = default)
    {
        var changed = false;
        CoordinationLock? releasedLock = null;
        lock (_gate)
        {
            var state = LoadStateUnsafe();
            var current = state.Locks.FirstOrDefault(l =>
                l.Id.Equals(lockId, StringComparison.OrdinalIgnoreCase));
            if (current is null || !IsActive(current))
            {
                changed = false;
            }
            else
            {
                var isOwner = current.OwnerAgentId.Equals(byAgentId, StringComparison.OrdinalIgnoreCase);
                if (isOwner || force)
                {
                    var status = force ? CoordinationLockStatus.ForceReleased : CoordinationLockStatus.Released;
                    var released = current with
                    {
                        Status = status,
                        ReleasedAt = DateTimeOffset.Now,
                        ReleasedBy = byAgentId
                    };
                    var index = state.Locks.FindIndex(l => l.Id.Equals(current.Id, StringComparison.OrdinalIgnoreCase));
                    state.Locks[index] = released;
                    SaveStateUnsafe(state);
                    changed = true;
                    releasedLock = released;
                }
            }
        }

        await Task.CompletedTask;

        if (changed && releasedLock is not null)
        {
            _bus?.Publish(new CoordinationEvent
            {
                Kind = force ? CoordinationEventKind.LockForceReleased : CoordinationEventKind.LockReleased,
                LockId = lockId,
                AgentId = byAgentId,
                Detail = force ? "Force released." : "Released."
            });
        }

        return changed;
    }

    public async Task<bool> RequestUnlockAsync(string lockId, string requestingAgentId, string reason = "", CancellationToken ct = default)
    {
        // 检查锁是否存在
        bool lockExists;
        lock (_gate)
        {
            var state = LoadStateUnsafe();
            lockExists = state.Locks.Any(l =>
                l.Id.Equals(lockId, StringComparison.OrdinalIgnoreCase) && IsActive(l));
        }

        if (!lockExists)
            return false;

        await Task.CompletedTask;

        // 广播解锁请求事件（不直接释放锁，让持有者自行决定）
        _bus?.Publish(new CoordinationEvent
        {
            Kind = CoordinationEventKind.UnlockRequested,
            LockId = lockId,
            AgentId = requestingAgentId,
            Detail = string.IsNullOrWhiteSpace(reason) ? "Agent requested unlock." : reason
        });

        return true;
    }

    public async Task<IReadOnlyList<CoordinationLock>> ListActiveLocksAsync(CancellationToken ct = default)
    {
        List<CoordinationLock> output;
        lock (_gate)
        {
            var state = LoadStateUnsafe();
            CleanupExpiredUnsafe(state, DateTimeOffset.Now);
            output = state.Locks.Where(IsActive).ToList();
        }

        await Task.CompletedTask;
        return output;
    }

    public async Task<int> CleanupExpiredAsync(CancellationToken ct = default)
    {
        List<string>? expiredIds = null;
        int count;
        lock (_gate)
        {
            var state = LoadStateUnsafe();
            count = CleanupExpiredUnsafe(state, DateTimeOffset.Now, out expiredIds);
            if (count > 0)
                SaveStateUnsafe(state);
        }

        await Task.CompletedTask;

        if (_bus is not null && expiredIds is not null)
        {
            foreach (var id in expiredIds)
            {
                _bus.Publish(new CoordinationEvent
                {
                    Kind = CoordinationEventKind.LockExpired,
                    LockId = id,
                    AgentId = "system-expirer",
                    Detail = "Lock TTL exceeded."
                });
            }
        }

        return count;
    }

    private LockState LoadStateUnsafe()
    {
        if (!File.Exists(_statePath))
            return new LockState();

        try
        {
            var json = File.ReadAllText(_statePath);
            return JsonSerializer.Deserialize<LockState>(json, s_jsonOptions) ?? new LockState();
        }
        catch
        {
            return new LockState();
        }
    }

    private void SaveStateUnsafe(LockState state)
    {
        var dir = Path.GetDirectoryName(_statePath)!;
        Directory.CreateDirectory(dir);
        var json = JsonSerializer.Serialize(state, s_jsonOptions);
        File.WriteAllText(_statePath, json);
    }

    private int CleanupExpiredUnsafe(LockState state, DateTimeOffset now)
        => CleanupExpiredUnsafe(state, now, out _);

    private int CleanupExpiredUnsafe(LockState state, DateTimeOffset now, out List<string>? expiredIds)
    {
        expiredIds = null;
        var changed = 0;
        for (var i = 0; i < state.Locks.Count; i++)
        {
            var current = state.Locks[i];
            if (current.Status == CoordinationLockStatus.Active && current.ExpireAt <= now)
            {
                state.Locks[i] = current with
                {
                    Status = CoordinationLockStatus.Expired,
                    ReleasedAt = now,
                    ReleasedBy = "system-expirer"
                };
                expiredIds ??= [];
                expiredIds.Add(current.Id);
                changed++;
            }
        }

        return changed;
    }

    private bool Overlaps(IReadOnlyList<LockTarget> a, IReadOnlyList<LockTarget> b)
        => a.Any(left => b.Any(right => Overlaps(left, right)));

    private bool Overlaps(LockTarget a, LockTarget b)
    {
        var left = NormalizePath(a.Path);
        var right = NormalizePath(b.Path);
        if (a.Scope == CoordinationLockScope.File && b.Scope == CoordinationLockScope.File)
            return left.Equals(right, _pathComparison);
        if (a.Scope == CoordinationLockScope.Directory && b.Scope == CoordinationLockScope.Directory)
            return IsSameOrChild(left, right) || IsSameOrChild(right, left);
        if (a.Scope == CoordinationLockScope.Directory && b.Scope == CoordinationLockScope.File)
            return IsSameOrChild(right, left);
        return IsSameOrChild(left, right);
    }

    private bool IsSameOrChild(string path, string root)
    {
        if (path.Equals(root, _pathComparison))
            return true;

        var prefix = root.EndsWith(Path.DirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;
        return path.StartsWith(prefix, _pathComparison);
    }

    private string NormalizePath(string path)
    {
        var expanded = Environment.ExpandEnvironmentVariables(path);
        var normalized = expanded.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);
        return Path.GetFullPath(normalized);
    }

    private static bool IsActive(CoordinationLock value) => value.Status == CoordinationLockStatus.Active;

    private sealed class LockState
    {
        public List<CoordinationLock> Locks { get; set; } = [];
    }
}

