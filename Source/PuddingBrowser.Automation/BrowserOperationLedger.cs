using Pudding.Contracts;
using Pudding.Contracts.Desktop;

namespace PuddingBrowser.Automation;

/// <summary>
/// operationId 账本的容量与保留策略（设计方案 §4.3 拟定的 5 分钟 / 1,024 项）。
/// </summary>
public sealed record BrowserOperationLedgerOptions
{
    public static readonly BrowserOperationLedgerOptions Default = new();

    /// <summary>终态回执可被重放的时间窗；超过后重传返回 <c>receipt_expired</c>。</summary>
    public TimeSpan TerminalRetention { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>账本容量上限。<b>在途项不因容量被淘汰</b>；容量耗尽时拒绝新的写请求。</summary>
    public int MaxEntries { get; init; } = 1024;

    internal void Validate()
    {
        if (TerminalRetention <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(TerminalRetention), TerminalRetention, "Retention must be positive.");
        }

        if (MaxEntries < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxEntries), MaxEntries, "Capacity must be at least 1.");
        }
    }
}

/// <summary>账本中的一条操作记录：同一 operationId 的 running/terminal 状态与请求摘要。</summary>
public sealed record BrowserOperationLedgerEntry(
    OperationId OperationId,
    string CallerKey,
    ConnectionGeneration ConnectionGeneration,
    string RequestFingerprint,
    bool IsRunning,
    DateTimeOffset RecordedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    BrowserActionReceipt? Receipt)
{
    /// <summary>账本键：调用身份 + 连接世代 + operationId（三者共同定位一条记录）。</summary>
    public static string BuildKey(string callerKey, ConnectionGeneration generation, OperationId operationId) =>
        string.Concat(callerKey, "|", generation.Value.ToString(System.Globalization.CultureInfo.InvariantCulture), "|", operationId.Value);

    public string Key => BuildKey(CallerKey, ConnectionGeneration, OperationId);

    /// <summary>终态记录是否已超出可重放窗口。</summary>
    public bool IsExpiredAt(DateTimeOffset utcNow, TimeSpan retention) =>
        CompletedAtUtc is { } completedAt && utcNow - completedAt >= retention;

    public override string ToString() =>
        $"{Key} {(IsRunning ? "running" : "terminal")}"
        + (Receipt is { } receipt ? $" {BrowserActionCompletionWire.NameOf(receipt.Completion)}" : string.Empty);
}

/// <summary>账本准入结论。</summary>
public enum BrowserLedgerAdmissionKind
{
    /// <summary>新操作：调用方必须执行（且只执行一次）。</summary>
    Accepted,

    /// <summary>同一 operationId 仍在途：<b>不要</b>重复执行，等原操作结束。</summary>
    AlreadyRunning,

    /// <summary>同一 operationId 已有终态回执：直接重放，<b>不要</b>重新执行。</summary>
    Replay,

    /// <summary>终态回执已过期：返回 <c>receipt_expired</c>，不得当成新动作。</summary>
    ReceiptExpired,

    /// <summary>同 ID 不同请求摘要：拒绝。</summary>
    Conflict,

    /// <summary>容量耗尽：拒绝新的写请求（在途项不因容量被淘汰）。</summary>
    CapacityExhausted,
}

/// <summary>账本准入结果：结论 + 记录 + 拒绝时的重试裁定。</summary>
public sealed record BrowserLedgerAdmission(
    BrowserLedgerAdmissionKind Kind,
    BrowserOperationLedgerEntry? Entry = null,
    BrowserRetryDecision? Decision = null)
{
    /// <summary>只有 <see cref="BrowserLedgerAdmissionKind.Accepted"/> 允许真正执行驱动调用。</summary>
    public bool MustExecute => Kind == BrowserLedgerAdmissionKind.Accepted;

    /// <summary>除 Accepted 外的所有结论都禁止再次执行（重放/在途/过期/冲突/容量）。</summary>
    public bool MustNotExecute => !MustExecute;

    public bool IsAccepted => Kind == BrowserLedgerAdmissionKind.Accepted;

    public override string ToString() =>
        $"ledger {Kind}{(Entry is { } entry ? $" ({entry.Key})" : string.Empty)}";
}

/// <summary>
/// operationId 账本（设计方案 §4.3）：按「调用身份 + 实例/连接世代 + operationId + 请求摘要」
/// 去重，保证同一次内部请求/重传的驱动执行次数 ≤ 1。
///
/// 语义边界（诚实登记）：
/// • <b>在途项永不因容量被淘汰</b>，所以容量耗尽时拒新写请求，而不是悄悄丢掉在途事实；
/// • 终态 tombstone 会一直保留到容量压力出现，因此「过期重传」能返回 <c>receipt_expired</c>
///   而不是被误当成新动作；只有容量压力下被回收的过期项才无法再识别（跨进程崩溃不保证 exactly-once）。
/// </summary>
public sealed class BrowserOperationLedger
{
    private readonly Lock _sync = new();
    private readonly Dictionary<string, BrowserOperationLedgerEntry> _entries = new(StringComparer.Ordinal);
    private readonly BrowserOperationLedgerOptions _options;
    private readonly TimeProvider _timeProvider;

    public BrowserOperationLedger(
        BrowserOperationLedgerOptions? options = null,
        TimeProvider? timeProvider = null)
    {
        _options = options ?? BrowserOperationLedgerOptions.Default;
        _options.Validate();
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public BrowserOperationLedgerOptions Options => _options;

    public int Count
    {
        get
        {
            lock (_sync)
            {
                return _entries.Count;
            }
        }
    }

    public int RunningCount
    {
        get
        {
            lock (_sync)
            {
                return _entries.Values.Count(entry => entry.IsRunning);
            }
        }
    }

    public int TerminalCount
    {
        get
        {
            lock (_sync)
            {
                return _entries.Values.Count(entry => !entry.IsRunning);
            }
        }
    }

    /// <summary>
    /// 准入一次写操作。只有返回 <see cref="BrowserLedgerAdmission.MustExecute"/> 时才允许调用驱动。
    /// </summary>
    public BrowserLedgerAdmission Admit(
        string callerKey,
        ConnectionGeneration connectionGeneration,
        OperationId operationId,
        string requestFingerprint)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(callerKey);
        ArgumentNullException.ThrowIfNull(operationId);
        ArgumentNullException.ThrowIfNull(requestFingerprint);

        lock (_sync)
        {
            var now = _timeProvider.GetUtcNow();
            var key = BrowserOperationLedgerEntry.BuildKey(callerKey, connectionGeneration, operationId);

            if (_entries.TryGetValue(key, out var existing))
            {
                return AdmitExisting(existing, operationId, requestFingerprint, now);
            }

            if (_entries.Count >= _options.MaxEntries)
            {
                ReclaimExpiredUnsafe(now);

                if (_entries.Count >= _options.MaxEntries)
                {
                    return new BrowserLedgerAdmission(
                        BrowserLedgerAdmissionKind.CapacityExhausted,
                        null,
                        new BrowserRetryDecision(
                            BrowserRetryDisposition.CapacityExhausted,
                            MayHaveSideEffects: false,
                            Receipt: null,
                            Error: DesktopCapabilityError.ResourceExhausted(
                                "the browser operation ledger is at capacity; new write requests are refused"),
                            Detail: "in-flight operations are never evicted to make room"));
                }
            }

            var entry = new BrowserOperationLedgerEntry(
                operationId,
                callerKey,
                connectionGeneration,
                requestFingerprint,
                IsRunning: true,
                RecordedAtUtc: now,
                CompletedAtUtc: null,
                Receipt: null);

            _entries[key] = entry;
            return new BrowserLedgerAdmission(BrowserLedgerAdmissionKind.Accepted, entry);
        }
    }

    private BrowserLedgerAdmission AdmitExisting(
        BrowserOperationLedgerEntry existing,
        OperationId operationId,
        string requestFingerprint,
        DateTimeOffset now)
    {
        // 同 ID 不同请求摘要：拒绝（不是重传，是复用 ID 干了另一件事）。
        if (!string.Equals(existing.RequestFingerprint, requestFingerprint, StringComparison.Ordinal))
        {
            return new BrowserLedgerAdmission(
                BrowserLedgerAdmissionKind.Conflict,
                existing,
                new BrowserRetryDecision(
                    BrowserRetryDisposition.OperationConflict,
                    MayHaveSideEffects: true,
                    Receipt: null,
                    Error: DesktopCapabilityError.InvalidRequest(
                        $"operation '{operationId.Value}' was already used with a different request payload"),
                    Detail: "refusing to execute a different request under a reused operation id"));
        }

        if (existing.IsRunning)
        {
            return new BrowserLedgerAdmission(BrowserLedgerAdmissionKind.AlreadyRunning, existing);
        }

        if (!existing.IsExpiredAt(now, _options.TerminalRetention))
        {
            return new BrowserLedgerAdmission(
                BrowserLedgerAdmissionKind.Replay,
                existing,
                existing.Receipt is { } receipt ? BrowserRetryPolicy.Decide(receipt) : null);
        }

        // 过期 tombstone 仍然保留 ⇒ 明确回报 receipt_expired，而不是当成一次新动作。
        return new BrowserLedgerAdmission(
            BrowserLedgerAdmissionKind.ReceiptExpired,
            existing,
            new BrowserRetryDecision(
                BrowserRetryDisposition.ReceiptExpired,
                MayHaveSideEffects: false,
                Receipt: null,
                Error: DesktopCapabilityError.InvalidRequest(
                    $"receipt_expired: the cached terminal receipt for operation '{operationId.Value}' has expired;"
                    + " it is not treated as a new action"),
                Detail: "re-observe the page state instead of replaying the expired receipt"));
    }

    /// <summary>把在途记录标为终态并附回执；返回是否命中一条在途记录。</summary>
    public bool TryComplete(BrowserOperationLedgerEntry entry, BrowserActionReceipt receipt)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(receipt);

        if (!string.Equals(entry.OperationId.Value, receipt.OperationId.Value, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "the receipt does not belong to the ledger entry being completed.", nameof(receipt));
        }

        lock (_sync)
        {
            if (!_entries.TryGetValue(entry.Key, out var current) || !current.IsRunning)
            {
                return false;
            }

            _entries[entry.Key] = current with
            {
                IsRunning = false,
                CompletedAtUtc = _timeProvider.GetUtcNow(),
                Receipt = receipt,
            };

            return true;
        }
    }

    /// <summary>
    /// 放弃一条在途记录（确定未开始且不再重试）。移除记录 ⇒ 同 ID 之后会被当作新操作。
    /// </summary>
    public bool TryAbandon(BrowserOperationLedgerEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        lock (_sync)
        {
            return _entries.TryGetValue(entry.Key, out var current)
                && current.IsRunning
                && _entries.Remove(entry.Key);
        }
    }

    /// <summary>只读状态/回执查询入口（方案 §4.3「提供只读状态/回执查询入口」）。</summary>
    public BrowserOperationLedgerEntry? TryGet(
        string callerKey,
        ConnectionGeneration connectionGeneration,
        OperationId operationId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(callerKey);
        ArgumentNullException.ThrowIfNull(operationId);

        lock (_sync)
        {
            return _entries.TryGetValue(
                BrowserOperationLedgerEntry.BuildKey(callerKey, connectionGeneration, operationId),
                out var entry)
                ? entry
                : null;
        }
    }

    /// <summary>清理已过期的终态 tombstone；返回清理条数。在途项永不清理。</summary>
    public int Prune()
    {
        lock (_sync)
        {
            return ReclaimExpiredUnsafe(_timeProvider.GetUtcNow());
        }
    }

    private int ReclaimExpiredUnsafe(DateTimeOffset now)
    {
        var expired = _entries.Values
            .Where(entry => !entry.IsRunning && entry.IsExpiredAt(now, _options.TerminalRetention))
            .Select(entry => entry.Key)
            .ToArray();

        foreach (var key in expired)
        {
            _entries.Remove(key);
        }

        return expired.Length;
    }
}
