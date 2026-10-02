using Microsoft.Extensions.Logging;
using PuddingCodeIndex.Contracts;

namespace PuddingCodeIndex.Services.CodeIndex;

/// <summary>
/// 完整清单校准（D2 §2，2026-10-02）：把「磁盘上现在有什么」（<see cref="ICodeSourceScanner"/>）与
/// 「我们记得什么」（持久 manifest + 账本）核对成一份**真实变更集**，并把期望版本登记进账本。
/// <para>
/// 三件事它明确**不做**，以免与后续阶段混淆：
/// <list type="number">
///   <item><description><b>不读正文、不算 hash</b>：需要内容核验的候选一律返回
///     <c>RequiresContentHash=true</c>，由执行层稳定读取后再提交（本阶段不假装知道内容没变）。</description></item>
///   <item><description><b>不写索引</b>：删除/替换都由执行层经存储接缝完成；本服务只产出计划与账本版本。
///     因此「扫描发现了删除」与「删除已生效」是两件事。</description></item>
///   <item><description><b>不推进扫描水位</b>：水位只在执行层回报提交（<c>CompleteCommit</c>）时前进，
///     且要求扫描完整、没有未解决路径。扫描本身不推进，避免「扫到了但没提交」被记成已完成。</description></item>
/// </list>
/// </para>
/// <para>
/// 存储没有实现 <see cref="ICodeSourceMaintenanceStore"/> 时（<c>CapabilityMissing</c>）本服务仍然可以工作：
/// 它只产出「磁盘上现在有什么」，不猜 manifest；调用方据此保持既有行为。
/// </para>
/// </summary>
public sealed class CodeSourceScanService
{
    private readonly ICodeSourceScanner _scanner;
    private readonly ICodeSourceMaintenanceStore? _store;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _racyOverlap;
    private readonly ILogger<CodeSourceScanService>? _logger;

    /// <summary>创建校准服务。</summary>
    /// <param name="scanner">磁盘枚举端口（元数据 only）。</param>
    /// <param name="store">可选：源维护状态持久化能力（没有它则只产出磁盘事实）。</param>
    /// <param name="timeProvider">可选时钟（确定性测试）。</param>
    /// <param name="racyOverlap">mtime 重叠窗口覆盖。</param>
    /// <param name="logger">可选日志。</param>
    public CodeSourceScanService(
        ICodeSourceScanner scanner,
        ICodeSourceMaintenanceStore? store = null,
        TimeProvider? timeProvider = null,
        TimeSpan? racyOverlap = null,
        ILogger<CodeSourceScanService>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(scanner);

        _racyOverlap = racyOverlap ?? CodeSourceChangeDetector.DefaultRacyOverlap;
        if (_racyOverlap < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(racyOverlap), _racyOverlap, "Racy overlap must not be negative.");

        _scanner = scanner;
        _store = store;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger;
    }

    /// <summary>该实例是否能读写持久 manifest 与账本。</summary>
    public bool HasMaintenanceStore => _store is not null;

    /// <summary>
    /// 校准一个 scope：读取持久状态 → 枚举磁盘 → 判定真实变更 → 登记期望版本。
    /// </summary>
    /// <param name="workspaceId">工作空间。</param>
    /// <param name="projectId">范围（store 的 project id）。</param>
    /// <param name="rootPath">语义范围根。</param>
    /// <param name="options">watcher 提示、扫描期间变化、消费者输入与深度核验等事实。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>变更集、捕获版本与扫描开始时刻（供执行层回报提交）。</returns>
    public async Task<CodeSourceScanRun> RunAsync(
        string workspaceId,
        string projectId,
        string rootPath,
        CodeSourceScanOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);

        var effectiveOptions = options ?? new CodeSourceScanOptions();

        var snapshot = _store is null
            ? CreateEmptySnapshot(workspaceId, projectId)
            : await _store.LoadSourceMaintenanceAsync(workspaceId, projectId, cancellationToken).ConfigureAwait(false);

        var outcome = await ObserveAsync(rootPath, effectiveOptions, cancellationToken).ConfigureAwait(false);
        var scanStartedUtc = effectiveOptions.ScanStartedUtc ?? _timeProvider.GetUtcNow();

        var request = new CodeSourceScanRequest(
            Observations: outcome.Entries
                .Select(entry => new CodeSourceObservation(entry.FilePath, entry.LastWriteTimeUtc, entry.Length))
                .ToArray(),
            Manifest: snapshot.Manifest,
            ObservedContentHashes: null,
            WatcherHints: effectiveOptions.WatcherHints,
            ChangedDuringScan: effectiveOptions.ChangedDuringScan,
            ConsumerInputs: effectiveOptions.ConsumerInputs,
            ScanStartedUtc: scanStartedUtc,
            PreviousWatermarkUtc: snapshot.Ledger.ScanWatermarkUtc,
            RacyOverlap: _racyOverlap,
            DeepVerify: effectiveOptions.DeepVerify,
            ScopeComplete: outcome.Complete,
            RootUsable: outcome.RootUsable);

        var changeSet = CodeSourceChangeDetector.Detect(request);

        var ledger = CodeSourceMaintenanceLedger.FromState(snapshot.Ledger, _timeProvider);

        // 没有需要动作的路径 ⇒ 不推进期望版本，也不写库（没有「工作」可登记）。
        var capturedVersion = changeSet.Changes.Count > 0
            ? ledger.RecordObservedChanges(changeSet.Changes)
            : ledger.DesiredVersion;

        if (_store is not null && changeSet.Changes.Count > 0)
            await _store.SaveMaintenanceLedgerAsync(workspaceId, projectId, ledger.Snapshot(), cancellationToken)
                .ConfigureAwait(false);

        _logger?.LogInformation(
            "[CodeSourceScan] Scope {ScopeId}: {Reindex} reindex, {Rebind} rebind, {Metadata} metadata refresh, {Deleted} delete, {Deferred} deferred, {Reuse} reuse (root usable={RootUsable}, complete={Complete}{Reason}); captured version {CapturedVersion}.",
            projectId,
            changeSet.ReindexCount,
            changeSet.RebindCount,
            changeSet.MetadataRefreshCount,
            changeSet.DeletedCount,
            changeSet.DeferredCount,
            changeSet.ReuseCount,
            outcome.RootUsable,
            outcome.Complete,
            outcome.IncompleteReason is { } reason ? $", reason={reason}" : string.Empty,
            capturedVersion);

        return new CodeSourceScanRun(
            changeSet,
            capturedVersion,
            scanStartedUtc,
            CapabilityMissing: _store is null);
    }

    /// <summary>
    /// 取本轮的磁盘事实。
    /// <para>
    /// <b>提示驱动</b>（<c>Targeted=true</c> 且有提示）：走**按路径观测**（若有该能力），只给这批提示取元数据，
    /// 不遍历整棵树 —— 否则「保存一个文件」就会触发一次全树枚举，新链路反而比旧路径更费磁盘。
    /// 这种观测必然不完整（<c>Complete=false</c>），所以删除仍然只能由周期性完整扫描确认。
    /// </para>
    /// <para>
    /// 其它情况（周期性校准、深度核验、没有按路径能力）走完整枚举。
    /// </para>
    /// </summary>
    private async Task<CodeSourceScanOutcome> ObserveAsync(
        string rootPath,
        CodeSourceScanOptions options,
        CancellationToken cancellationToken)
    {
        var hints = (options.WatcherHints ?? [])
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .ToArray();

        if (options.Targeted && hints.Length > 0 && _scanner is ICodeSourcePathProbe probe)
        {
            return await probe.ObserveAsync(hints, cancellationToken).ConfigureAwait(false);
        }

        return await _scanner.ScanAsync(rootPath ?? string.Empty, cancellationToken).ConfigureAwait(false);
    }

    private static CodeSourceMaintenanceSnapshot CreateEmptySnapshot(string workspaceId, string projectId) =>
        new(
            new Dictionary<string, CodeSourceEntry>(StringComparer.Ordinal),
            new CodeSourceMaintenanceLedgerState(
                workspaceId,
                projectId,
                Epoch: 0,
                DesiredVersion: 0,
                CommittedVersion: 0,
                ConsumerAppliedVersions: new Dictionary<string, long>(StringComparer.Ordinal),
                PendingRetries: new Dictionary<string, CodeSourceRetry>(StringComparer.Ordinal),
                ScanWatermarkUtc: null,
                DirtyAgain: false));
}
