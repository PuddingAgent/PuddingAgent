using PuddingCodeIndex.Contracts;

namespace PuddingCodeIndex.Services.CodeIndex;

/// <summary>
/// D2「持久维护账本」的**纯逻辑**形态（2026-10-02 高磁盘读取修复）。
/// <para>
/// 账本回答四个问题，且只用单调版本而不是「有没有跑过」：
/// <list type="number">
///   <item><description><b>期望 vs 已提交</b>：每次记录变化批次递增 <c>DesiredVersion</c>；
///     提交只确认被<b>捕获</b>的那个版本，较新的变化把账本置为「又有变化」（D4）。</description></item>
///   <item><description><b>消费者分别推进</b>：全文与各语言 provider 各自有自己的水位，
///     一个消费者成功不代表另一个也完成。</description></item>
///   <item><description><b>扫描水位不掩盖失败</b>：只有「完整 + 根可用 + 没有未解决路径 +
///     捕获版本即当前期望」的轮次才推进；否则保持旧值（宁可重放，不可漏掉）。</description></item>
///   <item><description><b>待重试路径</b>：失败路径按阶梯退避重试；已成功的路径不受影响
///     （凭指纹跳过重复工作），失败不会把整个范围退回全量重建。</description></item>
/// </list>
/// </para>
/// <para>
/// 纯内存、无 I/O：持久化（表结构）是后续阶段的事，本类只把语义固定下来并可独立验证。
/// 调用方约定单线程使用（一个 scope 的维护由单一所有者驱动）。
/// </para>
/// </summary>
public sealed class CodeSourceMaintenanceLedger
{
    /// <summary>退避阶梯的底数（第一次失败后等这么久）。</summary>
    public static readonly TimeSpan DefaultRetryBase = TimeSpan.FromSeconds(60);

    /// <summary>退避阶梯的上限。</summary>
    public static readonly TimeSpan DefaultRetryMax = TimeSpan.FromMinutes(30);

    private readonly object _gate = new();
    private readonly Dictionary<string, long> _consumerAppliedVersions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CodeSourceRetry> _pendingRetries =
        new(CodePathIdentity.PathComparer);
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _retryBase;
    private readonly TimeSpan _retryMax;

    private long _epoch;
    private long _desiredVersion;
    private long _committedVersion;
    private DateTimeOffset? _scanWatermarkUtc;
    private bool _dirtyAgain;

    /// <summary>创建一个 scope 的账本。</summary>
    /// <param name="workspaceId">工作空间。</param>
    /// <param name="scopeId">范围（store 的 project id）。</param>
    /// <param name="epoch">初始世代（已有持久账本可传入其世代）。</param>
    /// <param name="timeProvider">可选时钟（确定性测试）。</param>
    /// <param name="retryBase">退避底数覆盖。</param>
    /// <param name="retryMax">退避上限覆盖。</param>
    public CodeSourceMaintenanceLedger(
        string workspaceId,
        string scopeId,
        long epoch = 0,
        TimeProvider? timeProvider = null,
        TimeSpan? retryBase = null,
        TimeSpan? retryMax = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(scopeId);

        WorkspaceId = workspaceId;
        ScopeId = scopeId;
        _epoch = epoch;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _retryBase = retryBase ?? DefaultRetryBase;
        _retryMax = retryMax ?? DefaultRetryMax;

        if (_retryBase <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(retryBase), _retryBase, "Retry base must be positive.");
        if (_retryMax < _retryBase)
            throw new ArgumentOutOfRangeException(nameof(retryMax), _retryMax, "Retry max must not be below the base.");
    }

    /// <summary>工作空间。</summary>
    public string WorkspaceId { get; }

    /// <summary>范围。</summary>
    public string ScopeId { get; }

    /// <summary>当前世代。</summary>
    public long Epoch
    {
        get { lock (_gate) return _epoch; }
    }

    /// <summary>已观察到的期望版本。</summary>
    public long DesiredVersion
    {
        get { lock (_gate) return _desiredVersion; }
    }

    /// <summary>已提交版本。</summary>
    public long CommittedVersion
    {
        get { lock (_gate) return _committedVersion; }
    }

    /// <summary>只读快照（可序列化持久化）。</summary>
    public CodeSourceMaintenanceLedgerState Snapshot()
    {
        lock (_gate)
        {
            return new CodeSourceMaintenanceLedgerState(
                WorkspaceId,
                ScopeId,
                _epoch,
                _desiredVersion,
                _committedVersion,
                new Dictionary<string, long>(_consumerAppliedVersions, StringComparer.Ordinal),
                new Dictionary<string, CodeSourceRetry>(_pendingRetries, CodePathIdentity.PathComparer),
                _scanWatermarkUtc,
                _dirtyAgain);
        }
    }

    /// <summary>
    /// 从持久快照恢复账本（含世代、版本、扫描水位、脏标记、消费者水位与待重试）。
    /// <para>
    /// 快照里的 <see cref="CodeSourceMaintenanceLedgerState.DirtyAgain"/> 会被保留：重启后仍然「还有工作」，
    /// 不能因为进程重来就当作已完成。
    /// </para>
    /// </summary>
    /// <param name="state">持久快照。</param>
    /// <param name="timeProvider">可选时钟（确定性测试）。</param>
    /// <param name="retryBase">退避底数覆盖。</param>
    /// <param name="retryMax">退避上限覆盖。</param>
    public static CodeSourceMaintenanceLedger FromState(
        CodeSourceMaintenanceLedgerState state,
        TimeProvider? timeProvider = null,
        TimeSpan? retryBase = null,
        TimeSpan? retryMax = null)
    {
        ArgumentNullException.ThrowIfNull(state);

        var ledger = new CodeSourceMaintenanceLedger(
            state.WorkspaceId,
            state.ScopeId,
            state.Epoch,
            timeProvider,
            retryBase,
            retryMax)
        {
            _desiredVersion = state.DesiredVersion,
            _committedVersion = state.CommittedVersion,
            _scanWatermarkUtc = state.ScanWatermarkUtc,
            _dirtyAgain = state.DirtyAgain,
        };

        foreach (var (providerId, appliedVersion) in state.ConsumerAppliedVersions
                     ?? new Dictionary<string, long>())
        {
            if (!string.IsNullOrWhiteSpace(providerId))
                ledger._consumerAppliedVersions[providerId] = appliedVersion;
        }

        foreach (var (filePath, retry) in state.PendingRetries ?? new Dictionary<string, CodeSourceRetry>())
        {
            if (!string.IsNullOrWhiteSpace(filePath) && retry is not null)
                ledger._pendingRetries[filePath] = retry;
        }

        return ledger;
    }

    /// <summary>
    /// 记录一批被观察到的变化：期望版本递增一次（一个批次一个版本），返回**被捕获的版本**。
    /// <para>
    /// 该版本要在执行结束时原样回报给 <see cref="CompleteCommit"/>：只确认被捕获的版本，
    /// 期间到达的新变化不会被这次提交「顺手」确认掉。
    /// </para>
    /// </summary>
    /// <param name="changes">本批需要动作的路径（只用于计数与诊断，可不含未变化路径）。</param>
    /// <returns>本批次的捕获版本。</returns>
    public long RecordObservedChanges(IReadOnlyCollection<CodeSourceChange>? changes = null)
    {
        _ = changes;

        lock (_gate)
        {
            _desiredVersion++;

            // 期望版本已经领先已提交版本 ⇒ 账本必须标记「还有工作」（进程中途退出也不会把它当成已完成）。
            _dirtyAgain = true;

            return _desiredVersion;
        }
    }

    /// <summary>捕获版本是否仍是当前期望（执行前的快速检查）。</summary>
    /// <param name="capturedVersion">批次开始时捕获的版本。</param>
    public bool IsCurrent(long capturedVersion)
    {
        lock (_gate)
            return capturedVersion == _desiredVersion;
    }

    /// <summary>
    /// 开启新世代（scope 重建 / 索引重置 / 不兼容 schema）：在途的旧世代批次一律作废。
    /// 世代切换本身就意味着「一切都要重新核对」。
    /// </summary>
    /// <returns>新世代号。</returns>
    public long BeginEpoch()
    {
        lock (_gate)
        {
            _epoch++;
            _dirtyAgain = true;
            return _epoch;
        }
    }

    /// <summary>该捕获版本所属的世代是否仍是当前世代。</summary>
    /// <param name="epoch">批次捕获时的世代。</param>
    public bool IsCurrentEpoch(long epoch)
    {
        lock (_gate)
            return epoch == _epoch;
    }

    /// <summary>待重试路径数（含尚未到期的）。</summary>
    public int PendingRetryCount
    {
        get { lock (_gate) return _pendingRetries.Count; }
    }

    /// <summary>
    /// 记录一条失败路径：连续失败次数递增，下一次允许尝试的时刻按阶梯退避
    /// （<c>base · 2^(attempts-1)</c>，封顶 <see cref="DefaultRetryMax"/>）。
    /// </summary>
    /// <param name="filePath">失败路径。</param>
    /// <param name="reason">失败原因（执行层给出）。</param>
    /// <param name="nowUtc">可选时刻（确定性测试）。</param>
    /// <returns>更新后的重试记录。</returns>
    public CodeSourceRetry RecordFailure(string filePath, string reason, DateTimeOffset? nowUtc = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        lock (_gate)
        {
            var now = nowUtc ?? _timeProvider.GetUtcNow();
            var attempts = _pendingRetries.TryGetValue(filePath, out var existing)
                ? existing.Attempts + 1
                : 1;
            var firstFailedAt = existing?.FirstFailedAtUtc ?? now;

            var retry = new CodeSourceRetry(
                filePath,
                reason,
                attempts,
                firstFailedAt,
                now + Backoff(attempts));

            _pendingRetries[filePath] = retry;
            _dirtyAgain = true;
            return retry;
        }
    }

    /// <summary>成功提交一条路径：移除它的待重试记录。</summary>
    /// <param name="filePath">成功路径。</param>
    /// <returns><c>true</c> 表示确实有记录被清除。</returns>
    public bool ClearRetry(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            return false;

        lock (_gate)
            return _pendingRetries.Remove(filePath);
    }

    /// <summary>到期的待重试路径（按路径排序，便于确定性驱动）。</summary>
    /// <param name="nowUtc">可选时刻（确定性测试）。</param>
    public IReadOnlyList<CodeSourceRetry> DueRetries(DateTimeOffset? nowUtc = null)
    {
        lock (_gate)
        {
            var now = nowUtc ?? _timeProvider.GetUtcNow();

            return _pendingRetries.Values
                .Where(retry => retry.NextAttemptAtUtc <= now)
                .OrderBy(retry => retry.FilePath, CodePathIdentity.PathComparer)
                .ToArray();
        }
    }

    /// <summary>
    /// 回报一轮提交，并据此推进（或不推进）水位。
    /// <para>
    /// 规则：
    /// <list type="bullet">
    ///   <item><description>世代不符 ⇒ <see cref="CodeSourceCommitOutcome.StaleEpoch"/>，什么都不改。</description></item>
    ///   <item><description>消费者水位只前进（<c>max</c>），且只对<b>本轮真正提交成功</b>的消费者推进。</description></item>
    ///   <item><description>捕获版本落后于当前期望，或仍有未解决路径，或本轮扫描不完整 ⇒ 标记「又有变化」，
    ///     扫描水位保持旧值。</description></item>
    ///   <item><description>全部满足（捕获版本即当前期望、扫描完整、无未解决路径、无待重试）⇒ 扫描水位推进到
    ///     <see cref="CodeSourceCommitCompletion.ScanStartedUtc"/>。</description></item>
    /// </list>
    /// </para>
    /// </summary>
    /// <param name="completion">本轮提交事实。</param>
    /// <returns>提交结果。</returns>
    public CodeSourceCommitOutcome CompleteCommit(CodeSourceCommitCompletion completion)
    {
        ArgumentNullException.ThrowIfNull(completion);

        lock (_gate)
        {
            if (completion.Epoch != _epoch)
                return CodeSourceCommitOutcome.StaleEpoch;

            foreach (var advance in completion.ProviderAdvances ?? [])
            {
                if (advance is null || string.IsNullOrWhiteSpace(advance.ProviderId))
                    continue;

                if (_consumerAppliedVersions.TryGetValue(advance.ProviderId, out var current))
                {
                    if (advance.AppliedVersion > current)
                        _consumerAppliedVersions[advance.ProviderId] = advance.AppliedVersion;
                }
                else
                {
                    _consumerAppliedVersions[advance.ProviderId] = advance.AppliedVersion;
                }
            }

            if (completion.CapturedVersion > _committedVersion)
                _committedVersion = completion.CapturedVersion;

            var superseded = completion.CapturedVersion < _desiredVersion
                || completion.UnresolvedPathCount > 0
                || _pendingRetries.Count > 0;

            if (!superseded && completion.ScanComplete && completion.ScanStartedUtc is { } scanStarted)
            {
                // 只有这一条路径会推进扫描水位：完整、无未解决、无待重试、捕获版本即当前期望。
                if (_scanWatermarkUtc is null || scanStarted > _scanWatermarkUtc)
                    _scanWatermarkUtc = scanStarted;

                _dirtyAgain = false;
                return CodeSourceCommitOutcome.Committed;
            }

            _dirtyAgain = true;
            return CodeSourceCommitOutcome.Superseded;
        }
    }

    /// <summary>退避阶梯：<c>base · 2^(attempts-1)</c>，封顶；attempts ≥ 1。</summary>
    internal static TimeSpan BackoffInterval(int attempts, TimeSpan retryBase, TimeSpan retryMax)
    {
        var interval = retryBase;
        var remaining = Math.Max(1, attempts) - 1;

        while (remaining > 0 && interval < retryMax)
        {
            var doubled = interval + interval;
            interval = doubled > retryMax ? retryMax : doubled;
            remaining--;
        }

        return interval;
    }

    private TimeSpan Backoff(int attempts) => BackoffInterval(attempts, _retryBase, _retryMax);
}
