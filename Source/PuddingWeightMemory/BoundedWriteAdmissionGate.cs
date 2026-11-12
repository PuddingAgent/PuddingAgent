namespace PuddingWeightMemory;

/// <summary>
/// 基于计数信号量的写入准入，进程内有效。
/// <para>
/// 局限（必须让调用方知道）：它只看本进程。宿主若有多个进程写同一个库，SQLite 只会串行化
/// 事务，管不住"总量"；那种部署必须换成跨进程配额，或按设计只允许一个写者进程。
/// </para>
/// </summary>
public sealed class BoundedWriteAdmissionGate : IWriteAdmissionGate
{
    private readonly SemaphoreSlim _slots;

    /// <summary>按给定容量创建准入闸门。</summary>
    /// <param name="capacity">允许并发占用的写入槽数，至少为 1。</param>
    public BoundedWriteAdmissionGate(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        Capacity = capacity;
        _slots = new SemaphoreSlim(capacity, capacity);
    }

    /// <inheritdoc />
    public int Capacity { get; }

    /// <inheritdoc />
    public int PendingWrites => Capacity - _slots.CurrentCount;

    /// <inheritdoc />
    public bool IsSaturated => _slots.CurrentCount == 0;

    /// <inheritdoc />
    public bool TryEnter() => _slots.Wait(0);

    /// <inheritdoc />
    public void Exit() => _slots.Release();
}
