using PuddingCode.Classification;
using PuddingCode.Tools;

namespace PuddingRuntime.Classification;

/// <summary>
/// 单个 <c>(classifier, tool_id, args_hash)</c> 键的连续 deferred 计数快照
///（方案 v2 §14.9.2，切片 S6a；只读输出契约，供 <c>classifier_status</c> 工具序列化）。
/// </summary>
public sealed record DeferredKeyCounterSnapshot
{
    /// <summary>上报该计数的分类器稳定标识。</summary>
    public required string ClassifierId { get; init; }

    /// <summary>被调用的工具 id。</summary>
    public required string ToolId { get; init; }

    /// <summary>参数哈希（<see cref="ToolAuthorizationDefaults.ComputeArgumentsHash"/>，与票务/防火墙同算法；不承载参数原文）。</summary>
    public required string ArgumentsHash { get; init; }

    /// <summary>当前连续 deferred 次数（分类器成功返回后清零）。</summary>
    public required int ConsecutiveDeferred { get; init; }

    /// <summary>当前退避档：基数 × 2^(n-1)，封顶 60 000 ms（§14.9.2）。</summary>
    public required int RetryAfterMs { get; init; }

    /// <summary>最近一次 deferred 的稳定原因码。</summary>
    public string? LastReasonCode { get; init; }

    /// <summary>最近一次 deferred 时间（UTC）。</summary>
    public DateTimeOffset? LastDeferredAtUtc { get; init; }
}

/// <summary>一次 <see cref="ClassifierHealthReporter.RecordDeferred"/> 上报后的读数（便于调用方与测试断言）。</summary>
public sealed record DeferredReport
{
    /// <summary>上报后该键的连续 deferred 次数（含本次）。</summary>
    public required int ConsecutiveDeferred { get; init; }

    /// <summary>上报后的退避档（毫秒）。</summary>
    public required int RetryAfterMs { get; init; }

    /// <summary>上报后该分类器的健康状态。</summary>
    public required ClassifierHealth Health { get; init; }
}

/// <summary>
/// 分类器健康面（服务端权威，方案 v2 §8.2 + §14.9.2，切片 S6a）。
/// <para>
/// 进程内单例：计数发生在进程内、由分类器侧（审批评审器 ClassifierToolApprovalReporter）
/// 在裁决后上报——不由前端或调用方推断（§8.2 硬性要求）。同一
/// <c>(tool_id, args_hash)</c>（外加 classifier 维度）连续 deferred 达 3 次 ⇒ 该分类器
/// <see cref="ClassifierHealth.Degraded"/>，达 5 次 ⇒ <see cref="ClassifierHealth.Unavailable"/>；
/// 分类器成功返回后该分类器名下全部键计数清零、健康恢复（§14.9.2「仅在成功后被重置」，
/// 防止残留计数造成「偶发失败永久降级」）。
/// </para>
/// <para>
/// 健康推导规则（对 §14.9.2 未定义区间的最保守解释，见切片报告）：
/// 任一键计数 ≥5 ⇒ Unavailable；≥3 ⇒ Degraded；1–2（未达档）⇒ Unknown（既无成功证据也未达降级档，
/// 不假装健康也不夸大故障）；计数为 0 且成功过 ⇒ Healthy；从未上报 ⇒ Unknown。
/// </para>
/// <para>
/// 本类不发起任何网络调用；<see cref="Snapshot"/> 实现的是 PuddingCore 健康面只读端口
/// <see cref="IClassifierHealthReporter"/>，扩展读数（per-key 计数与退避档）由本类自有方法提供。
/// </para>
/// </summary>
public sealed class ClassifierHealthReporter : IClassifierHealthReporter
{
    /// <summary>连续 deferred 达 3 次 ⇒ Degraded（§14.9.2）。</summary>
    public const int DegradedAfterConsecutiveDeferred = 3;

    /// <summary>连续 deferred 达 5 次 ⇒ Unavailable（§14.9.2）。</summary>
    public const int UnavailableAfterConsecutiveDeferred = 5;

    /// <summary>退避封顶：60 000 ms（§14.9.2）。</summary>
    public const int MaxRetryAfterMs = 60_000;

    /// <summary>退避基数默认值：2000 ms（§14.9 <c>ToolApproval:Classifier:UnavailableBackoffBaseMs</c>）。</summary>
    public const int DefaultUnavailableBackoffBaseMs = 2000;

    /// <summary>单个退避档索引上限：超过后直接取封顶值（防 2^n 溢出）。</summary>
    private const int MaxBackoffShift = 5;

    private readonly object _gate = new();
    private readonly Dictionary<string, ClassifierEntry> _classifiers = new(StringComparer.Ordinal);
    private readonly Dictionary<DeferredKey, DeferredCounterState> _counters = new();
    private readonly int _unavailableBackoffBaseMs;
    private readonly TimeProvider _timeProvider;

    private sealed record DeferredKey(string ClassifierId, string ToolId, string ArgumentsHash);

    private sealed class ClassifierEntry
    {
        public bool EverSucceeded;
        public string? LastReasonCode;
        public DateTimeOffset? LastCheckedAtUtc;
        public double? LastLatencyMs;
    }

    private sealed class DeferredCounterState
    {
        public int Count;
        public string? LastReasonCode;
        public DateTimeOffset? LastDeferredAtUtc;
    }

    /// <summary>构造健康面。</summary>
    /// <param name="unavailableBackoffBaseMs">退避基数（§14.9 配置读取；生产 DI 从
    /// <c>ToolApproval:Classifier:UnavailableBackoffBaseMs</c> 注入）。</param>
    /// <param name="timeProvider">时间源；缺省系统时钟（测试注入假钟）。</param>
    public ClassifierHealthReporter(
        int unavailableBackoffBaseMs = DefaultUnavailableBackoffBaseMs,
        TimeProvider? timeProvider = null)
    {
        _unavailableBackoffBaseMs = Math.Max(0, unavailableBackoffBaseMs);
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>生效的退避基数（毫秒）。</summary>
    public int UnavailableBackoffBaseMs => _unavailableBackoffBaseMs;

    /// <summary>
    /// 幂等登记一个分类器实现（DI 组装管线时调用）：保证 Snapshot 覆盖全部已注册实现
    /// （含从未上报者，默认 <see cref="ClassifierHealth.Unknown"/>）。
    /// </summary>
    public void EnsureRegistered(string classifierId)
    {
        if (string.IsNullOrWhiteSpace(classifierId))
            return;

        lock (_gate)
        {
            _ = EnsureEntryLocked(classifierId);
        }
    }

    /// <summary>
    /// 分类器侧上报：一次 deferred（未产生有效裁决）。返回上报后的计数与退避档。
    /// 只记录健康数据，<b>不改变</b>调用方已定的 deferred 决策与原因码（ADR-091 §4.4 不折叠）。
    /// </summary>
    public DeferredReport RecordDeferred(
        string classifierId,
        string toolId,
        string? argumentsJson,
        string? reasonCode,
        double? latencyMs = null)
    {
        var now = _timeProvider.GetUtcNow();
        var argumentsHash = ToolAuthorizationDefaults.ComputeArgumentsHash(argumentsJson);

        lock (_gate)
        {
            var entry = EnsureEntryLocked(classifierId);
            entry.LastCheckedAtUtc = now;
            entry.LastLatencyMs = latencyMs;
            entry.LastReasonCode = string.IsNullOrWhiteSpace(reasonCode) ? null : reasonCode;

            var key = new DeferredKey(classifierId, toolId, argumentsHash);
            if (!_counters.TryGetValue(key, out var counter))
            {
                counter = new DeferredCounterState();
                _counters[key] = counter;
            }

            counter.Count++;
            counter.LastReasonCode = entry.LastReasonCode;
            counter.LastDeferredAtUtc = now;

            return new DeferredReport
            {
                ConsecutiveDeferred = counter.Count,
                RetryAfterMs = ComputeRetryAfterMs(_unavailableBackoffBaseMs, counter.Count),
                Health = ComputeHealth(MaxConsecutiveDeferredLocked(classifierId), entry.EverSucceeded),
            };
        }
    }

    /// <summary>
    /// 分类器侧上报：一次成功裁决（四选一有效结论）。§14.9.2：该计数仅在成功返回后被重置——
    /// 这里重置该分类器名下<b>全部</b>键的计数并恢复健康（最保守解释：残留的其它键计数会让
    /// 下一键失败 1 次即回到降级档，等于「偶发失败永久降级」，与 §14.9.2 的目的相反）。
    /// </summary>
    public void RecordSuccess(string classifierId, double? latencyMs = null)
    {
        var now = _timeProvider.GetUtcNow();

        lock (_gate)
        {
            var entry = EnsureEntryLocked(classifierId);
            entry.LastCheckedAtUtc = now;
            entry.LastLatencyMs = latencyMs;
            entry.LastReasonCode = null;
            entry.EverSucceeded = true;

            foreach (var key in _counters.Keys.Where(k => k.ClassifierId == classifierId).ToArray())
            {
                _counters.Remove(key);
            }
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<ClassifierStatus> Snapshot()
    {
        lock (_gate)
        {
            return _classifiers.Keys
                .OrderBy(id => id, StringComparer.Ordinal)
                .Select(id =>
                {
                    var entry = _classifiers[id];
                    var maxDeferred = MaxConsecutiveDeferredLocked(id);
                    return new ClassifierStatus
                    {
                        ClassifierId = id,
                        Health = ComputeHealth(maxDeferred, entry.EverSucceeded),
                        Detail = entry.LastReasonCode,
                        ConsecutiveFailures = maxDeferred,
                        LastCheckedAtUtc = entry.LastCheckedAtUtc,
                        LastLatencyMs = entry.LastLatencyMs,
                    };
                })
                .ToArray();
        }
    }

    /// <summary>当前全部 per-key 连续 deferred 计数（含退避档读数；只读）。</summary>
    public IReadOnlyList<DeferredKeyCounterSnapshot> SnapshotDeferredCounters()
    {
        lock (_gate)
        {
            return _counters
                .OrderBy(pair => pair.Key.ClassifierId, StringComparer.Ordinal)
                .ThenBy(pair => pair.Key.ToolId, StringComparer.Ordinal)
                .ThenBy(pair => pair.Key.ArgumentsHash, StringComparer.Ordinal)
                .Select(pair => new DeferredKeyCounterSnapshot
                {
                    ClassifierId = pair.Key.ClassifierId,
                    ToolId = pair.Key.ToolId,
                    ArgumentsHash = pair.Key.ArgumentsHash,
                    ConsecutiveDeferred = pair.Value.Count,
                    RetryAfterMs = ComputeRetryAfterMs(_unavailableBackoffBaseMs, pair.Value.Count),
                    LastReasonCode = pair.Value.LastReasonCode,
                    LastDeferredAtUtc = pair.Value.LastDeferredAtUtc,
                })
                .ToArray();
        }
    }

    /// <summary>退避档纯函数：基数 × 2^(n-1)，封顶 <see cref="MaxRetryAfterMs"/>（§14.9.2）。</summary>
    public static int ComputeRetryAfterMs(int baseMs, int consecutiveDeferred)
    {
        if (baseMs <= 0)
            return 0;
        if (consecutiveDeferred <= 1)
            return Cap(baseMs);

        var shift = Math.Min(consecutiveDeferred - 1, MaxBackoffShift);
        var retryAfter = (long)baseMs << shift;
        return Cap(retryAfter);

        static int Cap(long value) => (int)Math.Min(value, MaxRetryAfterMs);
    }

    private ClassifierEntry EnsureEntryLocked(string classifierId)
    {
        if (!_classifiers.TryGetValue(classifierId, out var entry))
        {
            entry = new ClassifierEntry();
            _classifiers[classifierId] = entry;
        }

        return entry;
    }

    private int MaxConsecutiveDeferredLocked(string classifierId)
    {
        var max = 0;
        foreach (var pair in _counters)
        {
            if (string.Equals(pair.Key.ClassifierId, classifierId, StringComparison.Ordinal)
                && pair.Value.Count > max)
            {
                max = pair.Value.Count;
            }
        }

        return max;
    }

    private static ClassifierHealth ComputeHealth(int maxConsecutiveDeferred, bool everSucceeded)
        => maxConsecutiveDeferred >= UnavailableAfterConsecutiveDeferred ? ClassifierHealth.Unavailable
           : maxConsecutiveDeferred >= DegradedAfterConsecutiveDeferred ? ClassifierHealth.Degraded
           : maxConsecutiveDeferred >= 1 ? ClassifierHealth.Unknown
           : everSucceeded ? ClassifierHealth.Healthy
           : ClassifierHealth.Unknown;
}
