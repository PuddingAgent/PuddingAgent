namespace Pudding.DesktopConnection;

/// <summary>
/// 出站字节预算：按字节预留/释放，支持在握手后按 Core 声明的更严格上限<b>收缩</b>容量。
///
/// 为什么不用 <see cref="SemaphoreSlim"/>：它一次只能取 1 个许可，无法按帧字节数预留。
/// 语义：等待者按 FIFO 唤醒；取消是幂等的；容量收缩不会取消等待者，只会让它们等更久。
/// </summary>
internal sealed class ByteBudget
{
    private readonly object _sync = new();
    private readonly LinkedList<Waiter> _waiters = new();
    private long _used;
    private long _capacity;

    public ByteBudget(long capacity)
    {
        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity), capacity, "Budget capacity must be positive.");
        }

        _capacity = capacity;
    }

    public long Capacity
    {
        get
        {
            lock (_sync)
            {
                return _capacity;
            }
        }
    }

    public long Used
    {
        get
        {
            lock (_sync)
            {
                return _used;
            }
        }
    }

    /// <summary>收缩容量（只允许变小）。已超出部分不回收，等待自然回落。</summary>
    public void ShrinkTo(long capacity)
    {
        lock (_sync)
        {
            if (capacity > 0 && capacity < _capacity)
            {
                _capacity = capacity;
            }
        }
    }

    /// <summary>非阻塞预留：空间不足立即返回 false（尽力而为的帧据此丢弃并计数）。</summary>
    public bool TryReserve(long size)
    {
        lock (_sync)
        {
            if (_used + size <= _capacity)
            {
                _used += size;
                return true;
            }

            return false;
        }
    }

    /// <summary>阻塞式预留：容量不足时排队等待；取消时返回 false。终态结果用它（不可丢）。</summary>
    public async Task<bool> ReserveAsync(long size, CancellationToken cancellationToken)
    {
        lock (_sync)
        {
            if (_used + size <= _capacity)
            {
                _used += size;
                return true;
            }
        }

        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var node = new LinkedListNode<Waiter>(new Waiter(size, completion));
        lock (_sync)
        {
            _waiters.AddLast(node);
            PumpLocked();
        }

        await using var registration = cancellationToken.Register(
            static state =>
            {
                var (budget, waiter) = ((ByteBudget, LinkedListNode<Waiter>))state!;
                budget.CancelWaiter(waiter);
            },
            (this, node)).ConfigureAwait(false);

        return await completion.Task.ConfigureAwait(false);
    }

    public void Release(long size)
    {
        lock (_sync)
        {
            _used = Math.Max(0, _used - size);
            PumpLocked();
        }
    }

    private void PumpLocked()
    {
        while (_waiters.First is { } first && _used + first.Value.Size <= _capacity)
        {
            _waiters.RemoveFirst();
            _used += first.Value.Size;
            first.Value.Completion.TrySetResult(true);
        }
    }

    private void CancelWaiter(LinkedListNode<Waiter> node)
    {
        lock (_sync)
        {
            if (node.List is null)
            {
                return;
            }

            _waiters.Remove(node);
            node.Value.Completion.TrySetResult(false);
        }
    }

    private readonly record struct Waiter(long Size, TaskCompletionSource<bool> Completion);
}
