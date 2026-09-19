using System.Collections.Concurrent;

namespace PuddingRuntime.Services;

/// <summary>
/// 压缩并发协调器：为会话压缩提供 per-session 单飞锁与冷却限流，
/// 并通过历史失效回调（<see cref="Action{T}"/> 委托）解耦 <see cref="ContextWindowManager"/>，
/// 避免 <see cref="ContextCompactionService"/> 反向强依赖 <see cref="ContextWindowManager"/>
/// 而构成循环依赖。
///
/// 锁顺序约束（重要，禁止违反）：
/// 1. 压缩锁（本类的 per-session <see cref="SemaphoreSlim"/>）内，禁止获取执行锁
///    （ChatExecutionWorker._sessionLocks）。既有执行路径按「执行锁 → 压缩」方向加锁，
///    若压缩持锁期间反向获取执行锁，将与执行路径形成 AB-BA 死锁。
/// 2. 压缩锁内禁止等待消息 dispatch（SendMessageToSession / dispatch 回执）。
///    压缩只读写 DB 与内存历史，不参与消息投递；若在持锁期间等待投递，
///    而投递链路又反等待压缩锁，会死锁。
/// </summary>
public sealed class CompactionCoordinator
{
    /// <summary>默认冷却窗口：同一 session 两次压缩之间的最小间隔。</summary>
    public static readonly TimeSpan DefaultCooldown = TimeSpan.FromSeconds(30);

    private readonly ConcurrentDictionary<string, SemaphoreSlim> _sessionLocks = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastCompactionAt = new(StringComparer.Ordinal);
    private readonly TimeSpan _cooldown;
    private readonly Action<string>? _onHistoryInvalidated;

    /// <summary>A2 无收益抑制：标记条目上限。超过后整体清空再写入——抑制标记只用于短期跳过、可安全重建，防止长驻进程会话数无界膨胀。</summary>
    private const int MaxNoGainMarkEntries = 2048;

    /// <summary>A2 无收益抑制：session → 最近一次「候选窗口未变但无收益」标记。</summary>
    private readonly ConcurrentDictionary<string, NoGainMark> _noGainMarks = new(StringComparer.Ordinal);

    private readonly record struct NoGainMark(string Fingerprint, int Generation, DateTimeOffset ObservedAtUtc);

    public CompactionCoordinator(
        Action<string>? onHistoryInvalidated = null,
        TimeSpan? cooldown = null)
    {
        _onHistoryInvalidated = onHistoryInvalidated;
        _cooldown = cooldown ?? DefaultCooldown;
    }

    /// <summary>
    /// 获取指定 session 的压缩单飞锁。返回的 <see cref="CompactionLease"/> 必须在
    /// await using / finally 中释放；获取过程中若被取消，异常直接向上传播且不持有锁。
    /// </summary>
    public async Task<CompactionLease> AcquireAsync(string sessionId, CancellationToken ct = default)
    {
        var semaphore = _sessionLocks.GetOrAdd(sessionId, static _ => new SemaphoreSlim(1, 1));
        await semaphore.WaitAsync(ct);
        return new CompactionLease(semaphore);
    }

    /// <summary>
    /// 该会话当前是否有在途压缩。
    /// 依据是 per-session 单飞锁的**持有状态**——这是进程内的权威事实，且天然随重启
    /// 归零：上一个进程遗留的 started 事件不可能还在跑。因此“是否在跑”不能由事件
    /// 序列推断（终态事件丢失 / 进程重启都会留下孤儿 started）。
    /// </summary>
    public bool IsRunning(string sessionId)
        => !string.IsNullOrWhiteSpace(sessionId)
            && _sessionLocks.TryGetValue(sessionId, out var semaphore)
            && semaphore.CurrentCount == 0;

    /// <summary>
    /// 冷却检查：若 session 处于上次压缩后的冷却窗口内，返回 true 并给出可读的跳过原因。
    /// 调用方应跳过本次压缩（日志记录 skipReason 并返回空结果），避免同 session 高频重复压缩。
    /// </summary>
    public bool TryGetCooldownSkipReason(string sessionId, out string? skipReason)
    {
        if (_lastCompactionAt.TryGetValue(sessionId, out var last))
        {
            var remaining = _cooldown - (DateTimeOffset.UtcNow - last);
            if (remaining > TimeSpan.Zero)
            {
                skipReason =
                    $"Compaction skipped for session '{sessionId}': compacted {remaining.TotalSeconds:F1}s ago within {_cooldown.TotalSeconds:F0}s cooldown.";
                return true;
            }
        }

        skipReason = null;
        return false;
    }

    /// <summary>
    /// A2 无收益抑制：命中同一候选窗口的无收益标记时返回 true，并给出可日志化的跳过原因。
    /// 命中判定 = session 存在标记 且 <paramref name="fingerprint"/> 与标记相等；
    /// Generation 已参与指纹计算（ContextCompactionService.ComputeCandidateFingerprint），不单独比较。
    /// </summary>
    public bool TryGetNoGainSkipReason(string sessionId, string fingerprint, out string? skipReason)
    {
        if (_noGainMarks.TryGetValue(sessionId, out var mark)
            && string.Equals(mark.Fingerprint, fingerprint, StringComparison.Ordinal))
        {
            var ageSeconds = (long)(DateTimeOffset.UtcNow - mark.ObservedAtUtc).TotalSeconds;
            skipReason =
                $"Compaction skipped for session '{sessionId}': same candidate window (generation={mark.Generation}, observed {ageSeconds}s ago) produced no gain (no-op or non-shrinking summary); summarizer call suppressed.";
            return true;
        }

        skipReason = null;
        return false;
    }

    /// <summary>
    /// A2 无收益抑制：记录一次「候选窗口未变但无收益」的事实（no-op 或摘要不缩小）。
    /// 仅由 ContextCompactionService 在 no-gain 终态调用；异常失败路径不得调用（失败可恢复，不永久屏蔽）。
    /// </summary>
    public void RecordNoGain(string sessionId, string fingerprint, int generation)
    {
        // 有界内存：标记总数超上限时整体清空后再写入。
        if (_noGainMarks.Count > MaxNoGainMarkEntries)
        {
            _noGainMarks.Clear();
        }

        _noGainMarks[sessionId] = new NoGainMark(fingerprint, generation, DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// A2 无收益抑制：清除抑制。实际写入成功（<see cref="RecordCompactionCompleted"/> 内部调用）、
    /// 或显式手动重试（Manual）进入流程前调用；异常失败不调用。
    /// </summary>
    public void ClearNoGain(string sessionId)
    {
        _noGainMarks.TryRemove(sessionId, out _);
    }

    /// <summary>记录一次成功完成的压缩时间，用于冷却限流；同时清除该 session 的无收益抑制（实际写入成功后重新评估）。</summary>
    public void RecordCompactionCompleted(string sessionId)
    {
        _lastCompactionAt[sessionId] = DateTimeOffset.UtcNow;
        ClearNoGain(sessionId);
    }

    /// <summary>
    /// 触发历史失效回调（DI 绑定到 <see cref="ContextWindowManager.InvalidateHistory"/>），
    /// 使压缩写库成功后立即失效内存历史，下次访问时从 DB 重新水合。
    /// </summary>
    public void InvalidateHistory(string sessionId)
    {
        _onHistoryInvalidated?.Invoke(sessionId);
    }

    /// <summary>
    /// 压缩单飞锁句柄。Dispose 时释放对应 session 的 semaphore。
    /// semaphore 实例不从字典移除：避免移除后新请求 GetOrAdd 拿到新实例而绕过仍在等待的旧锁。
    /// </summary>
    public sealed class CompactionLease : IAsyncDisposable
    {
        private SemaphoreSlim? _semaphore;

        internal CompactionLease(SemaphoreSlim semaphore)
        {
            _semaphore = semaphore;
        }

        public ValueTask DisposeAsync()
        {
            var semaphore = Interlocked.Exchange(ref _semaphore, null);
            semaphore?.Release();
            return ValueTask.CompletedTask;
        }
    }
}
