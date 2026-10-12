namespace Pudding.DesktopService;

/// <summary>账本对一次 <c>TryBegin</c> 的裁决（方案 §4.2）。</summary>
public enum BrowserOperationLedgerState
{
    /// <summary>不存在 ⇒ 本次是唯一获胜者，**执行**。</summary>
    Execute,

    /// <summary>同指纹且仍在途 ⇒ **等待同一完成源**，不得再次进入执行。</summary>
    AwaitPending,

    /// <summary>同指纹且已完成 ⇒ 在身份/会话有效性检查后**复用同一终态**。</summary>
    ReplayCompleted,

    /// <summary>同键但**指纹不同** ⇒ <c>operation_id_conflict</c>，且**不得透露原结果**。</summary>
    Conflict,
}

/// <summary>
/// 账本键（方案 §4.2）：**桌面进程实例 + 连接世代 + operationId**。
/// 世代进键 ⇒ 重连后的同 ID 落在新的命名空间，不会被旧世代的记录误判为重复。
/// </summary>
public readonly record struct BrowserOperationLedgerKey(
    string DesktopProcessInstance,
    long ConnectionGeneration,
    string OperationId);

/// <summary>一次裁决：状态 + 在途完成源（仅 <see cref="BrowserOperationLedgerState.AwaitPending"/>）+ 已复用终态。</summary>
public readonly record struct BrowserOperationLedgerDecision(
    BrowserOperationLedgerState State,
    Task<string>? Pending = null,
    string? Completed = null);

/// <summary>
/// **原子执行账本**（方案 §4.2）：把"同一连接世代的同一 operationId 只产生一次执行"落成类型。
///
/// 语义：
/// <list type="bullet">
/// <item>不存在 ⇒ 写入指纹 + 在途完成源，唯一获胜者执行；</item>
/// <item>存在且指纹相同、在途 ⇒ 等待**同一**完成源，不能再次进入 handler；</item>
/// <item>存在且指纹相同、已完成 ⇒ 复用同一终态；</item>
/// <item>存在但**指纹不同** ⇒ 冲突，**不得透露原结果**。</item>
/// </list>
///
/// 边界：
/// <list type="bullet">
/// <item>**在途记录永不被容量或保留期驱逐**（否则重传会再执行一次）；</item>
/// <item>已完成记录按容量驱逐（LRU 语义用插入顺序近似）；</item>
/// <item>**结果未知**（副作用已开始但终态未知）的记录：保留期内**不复用**（复用未知结果是谎报），
/// 过保留期**也不允许当作安全重试**（返回冲突）；只有**已知终态**才在保留期内复用。</item>
/// </list>
/// </summary>
public sealed class BrowserOperationLedger(
    int capacity = 1024,
    TimeSpan? retention = null,
    TimeProvider? clock = null)
{
    private readonly int _capacity = capacity > 0
        ? capacity
        : throw new ArgumentOutOfRangeException(nameof(capacity));

    private readonly TimeSpan _retention = retention ?? TimeSpan.FromMinutes(10);
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly Lock _gate = new();
    private readonly Dictionary<BrowserOperationLedgerKey, Entry> _entries = [];
    private readonly LinkedList<BrowserOperationLedgerKey> _completedOrder = new();

    private sealed class Entry
    {
        public required string Fingerprint { get; init; }

        public required TaskCompletionSource<string> Completion { get; init; }

        public string? Terminal { get; set; }

        public bool OutcomeKnown { get; set; }

        public DateTimeOffset RecordedAtUtc { get; init; }
    }

    /// <summary>当前记录数（测试与诊断用；含在途）。</summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _entries.Count;
            }
        }
    }

    public BrowserOperationLedgerDecision TryBegin(BrowserOperationLedgerKey key, string fingerprint)
    {
        ArgumentNullException.ThrowIfNull(fingerprint);

        lock (_gate)
        {
            PruneExpired();

            if (!_entries.TryGetValue(key, out var entry))
            {
                var completion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
                _entries[key] = new Entry
                {
                    Fingerprint = fingerprint,
                    Completion = completion,
                    RecordedAtUtc = _clock.GetUtcNow(),
                };

                return new BrowserOperationLedgerDecision(BrowserOperationLedgerState.Execute);
            }

            if (!string.Equals(entry.Fingerprint, fingerprint, StringComparison.Ordinal))
            {
                // 指纹不同：冲突，且**不透露**原结果。
                return new BrowserOperationLedgerDecision(BrowserOperationLedgerState.Conflict);
            }

            if (entry.Terminal is null)
            {
                return new BrowserOperationLedgerDecision(
                    BrowserOperationLedgerState.AwaitPending, Pending: entry.Completion.Task);
            }

            if (!entry.OutcomeKnown)
            {
                // 结果未知：**不复用**（复用未知结果是谎报），也不当安全重试 ⇒ 冲突。
                return new BrowserOperationLedgerDecision(BrowserOperationLedgerState.Conflict);
            }

            return new BrowserOperationLedgerDecision(
                BrowserOperationLedgerState.ReplayCompleted, Completed: entry.Terminal);
        }
    }

    /// <summary>记录**已知**终态（副作用结果确定）。</summary>
    public void Complete(BrowserOperationLedgerKey key, string terminal)
    {
        ArgumentNullException.ThrowIfNull(terminal);
        Record(key, terminal, outcomeKnown: true);
    }

    /// <summary>
    /// 记录**结果未知**（副作用可能已发生）：既不复用、也不允许按安全重试重放。
    /// 记录**保留**（不删除）——删除会让客户端重传再执行一次。
    /// </summary>
    public void Abandon(BrowserOperationLedgerKey key, string reason) => Record(key, reason, outcomeKnown: false);

    private void Record(BrowserOperationLedgerKey key, string terminal, bool outcomeKnown)
    {
        lock (_gate)
        {
            if (!_entries.TryGetValue(key, out var entry))
            {
                return;
            }

            entry.Terminal = terminal;
            entry.OutcomeKnown = outcomeKnown;

            // 只有**已知终态**参与"过期清理 / 容量驱逐"：未知结果的记录一旦被清掉，
            // 重传就会变成"安全重试"⇒ 重复副作用。未知记录因此永久保留（数量稀有且必须记住）。
            if (outcomeKnown)
            {
                _completedOrder.AddLast(key);
            }

            entry.Completion.TrySetResult(terminal);
            TrimCapacity();
        }
    }

    /// <summary>过期清理：**只清理已完成且超出保留期**的记录；在途记录永不清理。</summary>
    private void PruneExpired()
    {
        if (_completedOrder.Count == 0)
        {
            return;
        }

        var now = _clock.GetUtcNow();
        var node = _completedOrder.First;
        while (node is not null)
        {
            var next = node.Next;
            if (_entries.TryGetValue(node.Value, out var entry) && now - entry.RecordedAtUtc >= _retention)
            {
                _entries.Remove(node.Value);
                _completedOrder.Remove(node);
            }

            node = next;
        }
    }

    /// <summary>容量收敛：只驱逐**已完成**记录（在途记录被保留，否则重传会再执行）。</summary>
    private void TrimCapacity()
    {
        while (_entries.Count > _capacity && _completedOrder.First is { } oldest)
        {
            _entries.Remove(oldest.Value);
            _completedOrder.RemoveFirst();
        }
    }
}
