using Pudding.Contracts.Computer;

namespace PuddingComputer.Automation;

/// <summary>准入结论。</summary>
public enum OperationAdmitStatus
{
    /// <summary>首次见到该 operation，可以执行。</summary>
    Accepted,

    /// <summary>同一个 operation + 同一个指纹：在途则等同一完成源，已完成则直接返回原回执。</summary>
    AlreadyKnown,

    /// <summary>同一个 operation 但指纹不同：这是调用方的语义冲突，必须拒绝而不是覆盖。</summary>
    Conflict,

    /// <summary>账本已满。**不驱逐旧记录**——待定与未知结果一旦丢掉就只能猜。</summary>
    Full,
}

public readonly record struct OperationAdmitResult(
    OperationAdmitStatus Status,
    ComputerActionReceipt? Existing)
{
    public bool CanExecute => Status == OperationAdmitStatus.Accepted;
}

/// <summary>
/// 操作账本：同一个 operation 只允许一种语义，且**永不因为拥挤而丢掉未定结果**。
/// <para>
/// 设计 §7.2 的原话是「待定/unknown 记录不按普通 LRU 驱逐，账本满时拒绝新写入」。
/// 这条反直觉但正确：LRU 的前提是"丢掉的可以重建"，而一个 unknown 的操作丢了之后，
/// 调用方既不知道它成没成，也无法再查 —— 那才是真正的重复副作用来源。
/// </para>
/// </summary>
public sealed class ComputerOperationLedger
{
    public const int Capacity = 1024;

    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    public int Count => _entries.Count;

    private sealed class Entry(string fingerprint)
    {
        public string Fingerprint { get; } = fingerprint;
        public ComputerActionReceipt? Receipt { get; set; }
    }

    /// <summary>
    /// 尝试准入。同一 operation 携带不同指纹时返回 <see cref="OperationAdmitStatus.Conflict"/>，
    /// **不覆盖**已有记录 —— 覆盖会让"重试"变成"另一个操作"，去重彻底失效。
    /// </summary>
    public OperationAdmitResult TryAdmit(string operationId, string fingerprint)
    {
        if (string.IsNullOrWhiteSpace(operationId))
            throw new ArgumentException("operationId is required.", nameof(operationId));
        if (string.IsNullOrWhiteSpace(fingerprint))
            throw new ArgumentException("fingerprint is required.", nameof(fingerprint));

        if (_entries.TryGetValue(operationId, out var existing))
        {
            return string.Equals(existing.Fingerprint, fingerprint, StringComparison.Ordinal)
                ? new OperationAdmitResult(OperationAdmitStatus.AlreadyKnown, existing.Receipt)
                : new OperationAdmitResult(OperationAdmitStatus.Conflict, existing.Receipt);
        }

        if (_entries.Count >= Capacity)
            return new OperationAdmitResult(OperationAdmitStatus.Full, null);

        _entries[operationId] = new Entry(fingerprint);
        return new OperationAdmitResult(OperationAdmitStatus.Accepted, null);
    }

    /// <summary>记录终态回执。未知的 operation 不允许凭空写入终态。</summary>
    public bool Complete(string operationId, ComputerActionReceipt receipt)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        if (!_entries.TryGetValue(operationId, out var entry))
            return false;

        entry.Receipt = receipt;
        return true;
    }

    public ComputerActionReceipt? GetReceipt(string operationId) =>
        _entries.TryGetValue(operationId, out var e) ? e.Receipt : null;

    /// <summary>
    /// 丢弃一条记录。仅供**已确认不会再生效**的场景（例如所属会话已经结束）使用；
    /// 不该为了腾空间而调用它。
    /// </summary>
    public bool Forget(string operationId) => _entries.Remove(operationId);

    public void Clear() => _entries.Clear();
}
