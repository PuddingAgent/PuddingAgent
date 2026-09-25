using PuddingFullTextIndex.Contracts;

namespace PuddingFullTextIndex.Infrastructure.Supply;

/// <summary>供给 job 的可变记录（读写一律经 <see cref="SupplyJobStore"/> 加锁，外部不得直接改）。</summary>
internal sealed class SupplyJobEntry
{
    internal SupplyJobEntry(string jobId, SupplyScope scope, DateTimeOffset startedAt)
    {
        JobId = jobId;
        Scope = scope;
        StartedAt = startedAt;
    }

    internal string JobId { get; }

    internal SupplyScope Scope { get; }

    internal DateTimeOffset StartedAt { get; }

    internal SupplyJobState State { get; set; } = SupplyJobState.Queued;

    internal string Phase { get; set; } = SupplyJobPhases.Queued;

    internal int DiscoveredFileCount { get; set; }

    internal long DiscoveredBytes { get; set; }

    internal DateTimeOffset? FinishedAt { get; set; }

    /// <summary>成功终态时刻（用于 MinRebuildInterval 判定）；未成功为 null。</summary>
    internal DateTimeOffset? SucceededAt { get; set; }

    internal string? Message { get; set; }

    internal SupplyLeaseHolder? LeaseHolder { get; set; }

    internal CancellationTokenSource Cancellation { get; } = new();

    /// <summary>后台执行任务（测试与诊断用；不参与业务判定）。</summary>
    internal Task? Worker { get; set; }

    internal SupplyJobStatus Snapshot() => new(
        JobId,
        Scope.ScopeKey,
        Scope.RootPath,
        State,
        Phase,
        DiscoveredFileCount,
        DiscoveredBytes,
        StartedAt,
        FinishedAt,
        Message,
        LeaseHolder);
}

/// <summary>
/// 供给 job 的进程内状态存储：状态机流转（非法流转被拒绝并留痕）、进行中/最近成功检索、
/// 终态历史有界淘汰。
/// </summary>
internal sealed class SupplyJobStore
{
    private readonly object _gate = new();
    private readonly Dictionary<string, SupplyJobEntry> _jobs = new(StringComparer.Ordinal);

    /// <summary>终态 job 的进入顺序（最旧在前），用于有界淘汰。</summary>
    private readonly List<string> _terminalOrder = new();

    private readonly Queue<string> _transitionRejections = new();
    private readonly int _maxRetainedJobs;
    private readonly int _maxRetainedRejections;

    internal SupplyJobStore(int maxRetainedJobs, int maxRetainedTransitionRejections)
    {
        _maxRetainedJobs = Math.Max(1, maxRetainedJobs);
        _maxRetainedRejections = Math.Max(1, maxRetainedTransitionRejections);
    }

    internal SupplyJobEntry Create(SupplyScope scope, string jobId, DateTimeOffset startedAtUtc, SupplyLeaseHolder? leaseHolder)
    {
        lock (_gate)
        {
            var entry = new SupplyJobEntry(jobId, scope, startedAtUtc) { LeaseHolder = leaseHolder };
            _jobs[jobId] = entry;
            return entry;
        }
    }

    internal SupplyJobEntry? Find(string jobId)
    {
        lock (_gate)
        {
            return _jobs.TryGetValue(jobId, out var entry) ? entry : null;
        }
    }

    /// <summary>该 scope 上仍处于 <c>Queued|Running</c> 的 job（同任务幂等合并的判据）。</summary>
    internal SupplyJobEntry? FindActive(string scopeKey)
    {
        lock (_gate)
        {
            return _jobs.Values.FirstOrDefault(e =>
                string.Equals(e.Scope.ScopeKey, scopeKey, StringComparison.OrdinalIgnoreCase)
                && e.State is SupplyJobState.Queued or SupplyJobState.Running);
        }
    }

    /// <summary>该 scope 上最近一次成功的 job（MinRebuildInterval 合并判据）。</summary>
    internal SupplyJobEntry? FindLastSucceeded(string scopeKey)
    {
        lock (_gate)
        {
            SupplyJobEntry? best = null;
            foreach (var entry in _jobs.Values)
            {
                if (entry.State != SupplyJobState.Succeeded
                    || entry.SucceededAt is not { } succeededAt
                    || !string.Equals(entry.Scope.ScopeKey, scopeKey, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (best?.SucceededAt is not { } bestAt || succeededAt > bestAt)
                    best = entry;
            }

            return best;
        }
    }

    /// <summary>
    /// 状态机流转。非法流转返回 false，并把可读消息记入拒绝台账（<see cref="TransitionRejections"/>）——
    /// 不抛异常、不静默。同一终态的重复写入按幂等处理（返回 true，不留痕）。
    /// </summary>
    internal bool TryTransition(SupplyJobEntry entry, SupplyJobState target, string? message, DateTimeOffset nowUtc)
    {
        lock (_gate)
        {
            if (entry.State == target)
            {
                if (!string.IsNullOrEmpty(message))
                    entry.Message = message;
                return true;
            }

            if (!SupplyJobStateMachine.CanTransition(entry.State, target))
            {
                RecordRejectionLocked(SupplyJobStateMachine.DescribeIllegalTransition(entry.State, target));
                return false;
            }

            entry.State = target;
            if (!string.IsNullOrEmpty(message))
                entry.Message = message;

            if (SupplyJobStateMachine.IsTerminal(target))
            {
                entry.FinishedAt = nowUtc;
                if (target == SupplyJobState.Succeeded)
                {
                    entry.SucceededAt = nowUtc;
                    entry.Phase = SupplyJobPhases.Completed;
                }

                _terminalOrder.Add(entry.JobId);
                TrimLocked();
            }

            return true;
        }
    }

    internal void SetPhase(SupplyJobEntry entry, string phase)
    {
        lock (_gate)
        {
            entry.Phase = phase;
        }
    }

    internal void SetInventory(SupplyJobEntry entry, int fileCount, long totalBytes)
    {
        lock (_gate)
        {
            entry.DiscoveredFileCount = fileCount;
            entry.DiscoveredBytes = totalBytes;
        }
    }

    internal void SetLeaseHolder(SupplyJobEntry entry, SupplyLeaseHolder holder)
    {
        lock (_gate)
        {
            entry.LeaseHolder = holder;
        }
    }

    internal void SetMessage(SupplyJobEntry entry, string message)
    {
        lock (_gate)
        {
            entry.Message = message;
        }
    }

    internal void RecordRejection(string message)
    {
        lock (_gate)
        {
            RecordRejectionLocked(message);
        }
    }

    internal SupplyJobStatus? Snapshot(string jobId)
    {
        lock (_gate)
        {
            return _jobs.TryGetValue(jobId, out var entry) ? entry.Snapshot() : null;
        }
    }

    internal IReadOnlyList<SupplyJobStatus> SnapshotAll()
    {
        lock (_gate)
        {
            return _jobs.Values
                .OrderBy(e => e.StartedAt)
                .ThenBy(e => e.JobId, StringComparer.Ordinal)
                .Select(e => e.Snapshot())
                .ToList();
        }
    }

    /// <summary>非法状态转换的拒绝台账（有界，供诊断与断言"不得静默"）。</summary>
    internal IReadOnlyList<string> TransitionRejections
    {
        get
        {
            lock (_gate)
            {
                return _transitionRejections.ToArray();
            }
        }
    }

    private void RecordRejectionLocked(string message)
    {
        _transitionRejections.Enqueue(message);
        while (_transitionRejections.Count > _maxRetainedRejections)
            _transitionRejections.Dequeue();
    }

    /// <summary>终态历史有界：超出 <c>MaxRetainedJobs</c> 时按时间淘汰最旧的终态 job（绝不淘汰进行中的）。</summary>
    private void TrimLocked()
    {
        while (_jobs.Count > _maxRetainedJobs && _terminalOrder.Count > 0)
        {
            var oldest = _terminalOrder[0];
            _terminalOrder.RemoveAt(0);
            _jobs.Remove(oldest);
        }
    }
}
