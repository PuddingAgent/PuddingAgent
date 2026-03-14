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

    public CentralLockManager(string projectRoot)
    {
        var root = Path.GetFullPath(projectRoot);
        _statePath = Path.Combine(root, ".pudding", "locks", "lock-state.json");
        _pathComparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
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
                }
            }
        }

        await Task.CompletedTask;
        return changed;
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
        int count;
        lock (_gate)
        {
            var state = LoadStateUnsafe();
            count = CleanupExpiredUnsafe(state, DateTimeOffset.Now);
            if (count > 0)
                SaveStateUnsafe(state);
        }

        await Task.CompletedTask;
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
    {
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

