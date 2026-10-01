using System.Collections.Concurrent;
using Pudding.Contracts;
using Proto = Pudding.Rpc.Protocol.V1;

namespace Pudding.DesktopConnection;

internal enum DuplicateKind
{
    New,
    InProgress,
    CachedTerminal,
    ExpiredTerminal,
    ConflictingPayload,
}

/// <summary>单个操作 ID 的记录：进行中或终态。终态记录是幂等复用/拒绝重复的依据。</summary>
internal sealed class OperationRecord
{
    public required OperationId Id { get; init; }

    public required string Fingerprint { get; init; }

    public required string Capability { get; init; }

    public required ConnectionGeneration Generation { get; init; }

    public required DateTimeOffset CreatedAtUtc { get; init; }

    public string? TraceId { get; init; }

    public DesktopCorrelationId? CorrelationId { get; init; }

    public CancellationTokenSource? Cancellation { get; set; }

    public Task? Execution { get; set; }

    public bool Started { get; set; }

    public DateTimeOffset StartedAtUtc { get; set; }

    public bool Terminal { get; set; }

    public Proto.DesktopFrame? TerminalFrame { get; set; }

    public DesktopCapabilityError? TerminalError { get; set; }

    public DateTimeOffset TerminalAtUtc { get; set; }

    /// <summary>0/1：保证一个操作只产生一条审计记录。</summary>
    public int AuditFlag;
}

/// <summary>
/// 待完成表 + 终态缓存（容量与 TTL 有界）。
///
/// 规则（计划 §5）：
/// · 同 ID 且指纹相同的进行中命令 ⇒ 复用，不重复执行；
/// · 同 ID 相同 payload 的终态 ⇒ 幂等重放缓存结果；
/// · 同 ID 不同 payload ⇒ 拒绝；
/// · 终态缓存过期 ⇒ 只回 <c>OutcomeUnknown</c>，**绝不重新执行**（副作用安全优先）。
/// </summary>
internal sealed class OperationRegistry
{
    private readonly object _sync = new();
    private readonly Dictionary<OperationId, OperationRecord> _records = new();
    private readonly Queue<OperationId> _terminalOrder = new();
    private readonly int _capacity;
    private readonly TimeSpan _ttl;
    private int _running;

    public OperationRegistry(int capacity, TimeSpan ttl)
    {
        _capacity = capacity;
        _ttl = ttl;
    }

    public int Count
    {
        get
        {
            lock (_sync)
            {
                return _records.Count;
            }
        }
    }

    /// <summary>进行中（尚未终态）的操作数。</summary>
    public int RunningCount => Volatile.Read(ref _running);

    public DuplicateKind TryBegin(
        OperationId id,
        string fingerprint,
        string capability,
        ConnectionGeneration generation,
        DateTimeOffset now,
        string? traceId,
        DesktopCorrelationId? correlationId,
        out OperationRecord record)
    {
        lock (_sync)
        {
            if (_records.TryGetValue(id, out var existing))
            {
                record = existing;
                var samePayload = string.Equals(existing.Fingerprint, fingerprint, StringComparison.Ordinal);
                if (!existing.Terminal)
                {
                    // 进行中也要比对 payload：同 ID 不同 payload 必须拒绝，不能当成「重复投递」默默忽略。
                    return samePayload ? DuplicateKind.InProgress : DuplicateKind.ConflictingPayload;
                }

                if (!samePayload)
                {
                    return DuplicateKind.ConflictingPayload;
                }

                return existing.TerminalFrame is not null && now - existing.TerminalAtUtc <= _ttl
                    ? DuplicateKind.CachedTerminal
                    : DuplicateKind.ExpiredTerminal;
            }

            record = new OperationRecord
            {
                Id = id,
                Fingerprint = fingerprint,
                Capability = capability,
                Generation = generation,
                CreatedAtUtc = now,
                TraceId = traceId,
                CorrelationId = correlationId,
            };
            _records[id] = record;
            Interlocked.Increment(ref _running);
            return DuplicateKind.New;
        }
    }

    public OperationRecord? Find(OperationId id)
    {
        lock (_sync)
        {
            return _records.TryGetValue(id, out var record) ? record : null;
        }
    }

    public IReadOnlyList<OperationRecord> SnapshotRunning()
    {
        lock (_sync)
        {
            return _records.Values.Where(record => !record.Terminal).ToArray();
        }
    }

    public int TerminalCount
    {
        get
        {
            lock (_sync)
            {
                return _terminalOrder.Count;
            }
        }
    }

    public void Complete(OperationRecord record, Proto.DesktopFrame? terminalFrame, DesktopCapabilityError? error, DateTimeOffset now)
    {
        lock (_sync)
        {
            if (!record.Terminal)
            {
                record.Terminal = true;
                _terminalOrder.Enqueue(record.Id);
                Interlocked.Decrement(ref _running);
            }

            record.TerminalFrame = terminalFrame;
            record.TerminalError = error;
            record.TerminalAtUtc = now;

            while (_terminalOrder.Count > _capacity)
            {
                var evicted = _terminalOrder.Dequeue();
                if (_records.TryGetValue(evicted, out var evictedRecord) && evictedRecord.Terminal)
                {
                    _records.Remove(evicted);
                }
            }
        }

        // 终态后不再需要取消源；之后到来的取消命令按「记录不存在」处理。
        try
        {
            record.Cancellation?.Dispose();
        }
        catch (ObjectDisposedException)
        {
            // 并发取消与释放竞争：忽略。
        }

        record.Cancellation = null;
    }

    /// <summary>取消该操作（若仍可取消）。取消是尽力而为：不撤销已执行的脚本。</summary>
    public bool TryCancel(OperationRecord record)
    {
        var cancellation = record.Cancellation;
        if (cancellation is null)
        {
            return false;
        }

        try
        {
            cancellation.Cancel();
            return true;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
        catch (AggregateException)
        {
            return true;
        }
    }
}

/// <summary>
/// 按目标（ContextId/PageId）串行化变更类能力。只读能力不经过此门，保持可并发。
/// </summary>
internal sealed class TargetSerializationGate
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates = new(StringComparer.Ordinal);

    public SemaphoreSlim For(string key) => _gates.GetOrAdd(key, static _ => new SemaphoreSlim(1, 1));

    public int TrackedTargetCount => _gates.Count;
}
