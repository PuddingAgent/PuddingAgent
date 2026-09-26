using System.Globalization;
using System.Text;
using PuddingFullTextIndex.Contracts;
using PuddingFullTextIndex.Infrastructure.Search;
using PuddingFullTextIndex.Infrastructure.Supply;
using Directory = System.IO.Directory;

namespace PuddingFullTextIndex.Infrastructure.Maintenance;

/// <summary>
/// 局部维护的**循环本体**（方案 §3.3 / §3.4 / §3.5 / §3.7 + §S3）：把三个触发源汇成**同一份按路径的变更集**，
/// 交给已交付的 <see cref="IFullTextIndexMaintenanceEngine"/> 执行，并按 <c>CheckpointAdvancePolicy</c> 判定是否推进 checkpoint。
/// <para>
/// <b>三源与各自的产出</b>（三者产出同一种数据 <see cref="FullTextChangeSet"/>）：
/// <list type="number">
/// <item><description><b>watcher（低延迟）</b>：每 scope 一个 <see cref="FileSystemWatcher"/>（64 KB 内部缓冲），
/// 回调**只**把路径写进有界折叠缓冲（不读内容、不阻塞、不 await、全程 try/catch —— watcher 静默停止是最危险的失效模式）；
/// flush 时**重新观察最终状态**（方案 §3.1「flush 时重新观察」），按 per-path latest-wins 折叠，
/// rename 折叠成「新名 Upsert + 旧名 Delete」；<c>Error</c> / 缓冲溢出 ⇒ 计数 + 请求补偿。</description></item>
/// <item><description><b>mtime checkpoint 补偿扫描</b>：启动一次 / <c>RecoveryScanInterval</c> 周期一次 / 被 watcher 事件请求。
/// 判据复用 <see cref="MTimeComparison"/>（<c>&gt;=</c> + overlap + 取**扫描开始**时刻）；
/// 「索引路径 − 磁盘路径 = Delete 候选」的唯一来源是引擎的只读清册 <c>EnumerateIndexedPathsAsync</c>。</description></item>
/// <item><description><b>低优先级体检</b>：专用后台线程（<c>IsBackground=true</c> / <c>ThreadPriority.BelowNormal</c>），
/// 每 scope 每 <c>HealthCheckInterval</c> 跑一次**只读探针**；资源不可采样或超阈值 ⇒ 退避（如实写进 <c>LastMessage</c>）；
/// <c>Degraded</c> 时把差异按 <c>HealthCheckSliceFiles</c> **切片**发布修复；<c>ManualRebuildRequired</c> ⇒ 只标记，**绝不自动重建**。</description></item>
/// </list>
/// </para>
/// <para>
/// <b>★ 三源的优先级 / 合并 / 抑制策略</b>（逐条对应代码：<see cref="MergeRecoveryReason"/> / <see cref="Application"/> 各方法）：
/// <list type="number">
/// <item><description><b>合并</b>：同 scope 的补偿请求**不叠加**，两两合并成一个待办，取值按
/// <see cref="RecoveryReasonPriority"/> 取高者：<c>Startup(9) &gt; WatcherOverflow(8) &gt; WatcherError(7)
/// &gt; IndexGenerationChanged(6) &gt; CheckpointUnreadable(5) &gt; ManualRebuildCompleted(4) &gt; QueuePressure(3)
/// &gt; ManualRequest(2) &gt; Interval(1)</c>。方向是「**含义为『事件可能已丢、状态未知』的原因压过周期性原因**」
/// —— 被周期原因顶掉就会漏文件。</description></item>
/// <item><description><b>抑制</b>：watcher 与补偿扫描是同一份变更集的**两个来源**，前者**从不压制**后者的覆盖面：
/// watcher 触发批次**不推进 checkpoint**（水位线语义 = 「最近一次成功**补偿扫描**的开始时刻」），
/// 因此 watcher 永远不会「吃掉」补偿尚未覆盖到的窗口。抑制只发生在**路径粒度**：
/// 同一路径在一个 flush 窗口内只保留最后一个最终动作（latest-wins），
/// 补偿扫描里「水位线判定为未变且已在索引中」的文件**不产出观察**（模仿 git 依 mtime 判断文件是否更新）。</description></item>
/// <item><description><b>体检不参与正确性</b>：它只发布差异（<c>IntegrityCheck</c> 来源的同一变更集）或标记手动重建，
/// 不推进 checkpoint、不自动重建、不与补偿扫描抢执行权（每个 scope 只有一条执行路径：泵任务 / 体检线程各自串行调用引擎，
/// 引擎内部的 index-root 全局写者 gate 保证跨 scope 也串行）。</description></item>
/// </list>
/// </para>
/// <para>
/// <b>★ 默认关闭如何做到零副作用（§1.3 硬不变量 3）</b>（保证点都在代码里，不在文档里）：
/// <list type="number">
/// <item><description>构造函数**只**存字段：不 <c>Directory.Exists</c>、不枚举 scope、不推导索引目录、不读 checkpoint、
/// 不建目录、不建 watcher / 线程、不取租约。</description></item>
/// <item><description><see cref="StartAsync"/> 第一件事是 <c>if (!_options.Enabled) return;</c> ⇒ 开关关闭时连 scope 都不看。</description></item>
/// <item><description>全部路径推导（<c>FullTextIndexPaths.ResolveIndexDirectory</c> /
/// <c>MaintenanceCheckpoint.ResolveStateDirectory</c> / <c>ResolveCheckpointPath</c>）都发生在 <see cref="StartAsync"/> 内
/// （<c>CreateScopeRuntime</c>）。</description></item>
/// <item><description>checkpoint 目录只在该 scope **成功推进过一次** checkpoint 时创建（<c>WriteCheckpoint</c> 里唯一的
/// <c>Directory.CreateDirectory</c>）。</description></item>
/// <item><description>watcher 与泵任务只在 <see cref="StartAsync"/> 内创建、只在 <see cref="StopAsync"/> 内释放；
/// 未运行时 <see cref="GetSnapshot"/> 的 <c>Scopes</c> 为空 ⇒ 连 <c>Directory.Exists(索引目录)</c> 都不会发生。</description></item>
/// </list>
/// </para>
/// <para>
/// <b>checkpoint 所有权</b>：引擎只**产出** <c>FullTextMutationResult.CheckpointAdvanced</c>（判定唯一真源是
/// <c>CheckpointAdvancePolicy</c>），**写盘是维护层的职责**（<c>WriteCheckpoint</c>：临时文件 + 原子替换；
/// 写盘中断留下的临时残留绝不参与水位线判定）。推进判据 = 「本轮是补偿轮次」∧「语料走查完整」∧「无延迟 / 拒绝项」∧
/// 「每个批次 <c>CheckpointAdvanced</c> 均为 true」；任一不满足 ⇒ 水位线不动、下一轮整体重放
/// （**允许成功文件被重复处理，漏掉文件不允许**）。
/// </para>
/// <para>
/// ⚠️ 本片**不接宿主**：不注册 DI、不改 Hosting / Runtime / Host / Desktop / CLI；不起任何无主后台线程
/// （watcher 与泵任务只在 <see cref="StartAsync"/> 内创建、<see cref="StopAsync"/> 内确实释放）。
/// </para>
/// </summary>
public sealed class LuceneFullTextIndexMaintenance : IFullTextIndexMaintenance
{
    /// <summary>调度切片下限（防 0 超时自旋）。</summary>
    private static readonly TimeSpan MinSchedulerSlice = TimeSpan.FromMilliseconds(2);

    /// <summary>调度切片上限：即使期限很远也定期醒来一次（重算期限 / 观测停止）。</summary>
    private static readonly TimeSpan MaxSchedulerSlice = TimeSpan.FromSeconds(30);

    /// <summary><see cref="StopAsync"/> 等待泵任务 / 体检线程收尾的上界。</summary>
    private static readonly TimeSpan StopDrainTimeout = TimeSpan.FromSeconds(20);

    private const string BatchIdPrefix = "mnt";

    private readonly FullTextIndexOptions _indexOptions;
    private readonly MaintenanceOptions _options;
    private readonly IFullTextIndexMaintenanceEngine _engine;
    private readonly IResourcePressureProbe _pressureProbe;
    private readonly TimeProvider _time;

    /// <summary>写入 checkpoint 的过滤策略指纹（唯一真源：<see cref="FullTextPolicyFingerprint"/>）。</summary>
    private readonly string _policyFingerprint;

    private readonly object _lifecycle = new();
    private readonly ManualResetEventSlim _healthWake = new(false);
    private readonly ManualResetEventSlim _stopSignal = new(false);

    private bool _running;
    private DateTimeOffset? _startedUtc;
    private DateTimeOffset? _stoppedUtc;
    private string? _lastError;
    private long _appliedBatchCount;
    private long _failedBatchCount;
    private long _retainedOldWarningCount;
    private long _rejectedObservationCount;
    private long _deferredObservationCount;
    private List<ScopeRuntime> _scopes = new();
    private CancellationTokenSource? _cts;
    private Thread? _healthThread;
    private volatile bool _healthThreadRunning;
    private long _batchSequence;
    private long _watchersCreated;
    private long _liveWatchers;
    private long _pumpTasksCreated;
    private long _livePumpTasks;

    /// <summary>
    /// 构造维护器。**本构造函数零副作用**：不触盘、不建目录、不建线程、不推导任何路径
    /// （全部路径推导与 IO 都发生在 <see cref="StartAsync"/> 内 —— §1.3 硬不变量 3）。
    /// </summary>
    /// <param name="indexOptions">组件配置（索引根 + 噪声 / 扩展名 / 体积判定的唯一真源）。</param>
    /// <param name="options">维护配置（队列容量 / 去抖 / 周期 / 体检切片 / 告警阈值）。</param>
    /// <param name="engine">局部写执行层（S3a/S3b/S3c 已交付；本片只调用）。</param>
    /// <param name="pressureProbe">资源压力采样接缝（体检退避的唯一依据）。</param>
    /// <param name="timeProvider">业务时钟（checkpoint 水位线 / 去抖期限 / 快照时刻的唯一来源）。</param>
    public LuceneFullTextIndexMaintenance(
        FullTextIndexOptions indexOptions,
        MaintenanceOptions options,
        IFullTextIndexMaintenanceEngine engine,
        IResourcePressureProbe pressureProbe,
        TimeProvider timeProvider)
    {
        _indexOptions = indexOptions ?? throw new ArgumentNullException(nameof(indexOptions));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _pressureProbe = pressureProbe ?? throw new ArgumentNullException(nameof(pressureProbe));
        _time = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

        _policyFingerprint = FullTextPolicyFingerprint.ComputePatternFingerprint(null);
    }

    // ── IFullTextIndexMaintenance ────────────────────────────────────────

    /// <inheritdoc />
    public Task StartAsync(
        IReadOnlyList<FullTextMaintenanceScope> scopes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scopes);
        cancellationToken.ThrowIfCancellationRequested();

        if (!_options.Enabled)
        {
            lock (_lifecycle)
            {
                _lastError = "维护未启用（MaintenanceOptions.Enabled = false）：StartAsync 是空操作"
                    + "（不推导路径、不建 watcher、不建线程、不建目录、不访问索引根）。";
            }

            return Task.CompletedTask;
        }

        lock (_lifecycle)
        {
            if (_running)
            {
                // 幂等：已在运行 ⇒ 请求被合并，**不重复创建** watcher / 泵任务 / 体检线程。
                foreach (var scope in scopes)
                    TryAddScopeLocked(scope, _cts!.Token);
            }
            else
            {
                var cts = new CancellationTokenSource();
                _cts = cts;
                _running = true;
                _startedUtc = _time.GetUtcNow();
                _lastError = null;
                _scopes = new List<ScopeRuntime>();

                foreach (var scope in scopes)
                    TryAddScopeLocked(scope, cts.Token);

                if (_scopes.Count > 0)
                    StartHealthThreadLocked(cts.Token);
            }
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        List<ScopeRuntime> scopes;
        CancellationTokenSource? cts;
        Thread? healthThread;

        lock (_lifecycle)
        {
            if (!_running)
            {
                _stoppedUtc ??= _time.GetUtcNow();
                return;
            }

            _running = false;
            _stoppedUtc = _time.GetUtcNow();
            scopes = _scopes;
            _scopes = new List<ScopeRuntime>();
            cts = _cts;
            _cts = null;
            healthThread = _healthThread;
            _healthThread = null;
        }

        // ① 先叫醒体检线程、再释放 watcher：让「不再产生新事件」先于「等待收尾」。
        _stopSignal.Set();
        _healthWake.Set();

        foreach (var scope in scopes)
            ReleaseWatcher(scope);

        // ② 取消泵任务（在飞批次以 Cancelled 收口 ⇒ 不提交、不推进 checkpoint）。
        cts?.Cancel();

        foreach (var scope in scopes)
        {
            if (scope.PumpTask is not { } pump)
                continue;

            try
            {
                await pump.WaitAsync(StopDrainTimeout).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                lock (_lifecycle)
                {
                    _lastError = "StopAsync：泵任务在收尾上界内未结束（watcher 已释放、令牌已取消，仍如实登记）。";
                }
            }
            catch (OperationCanceledException)
            {
                // 泵任务因取消结束：这是预期终态（取消是控制流，不是失败）。
            }
        }

        if (healthThread is not null)
            healthThread.Join(StopDrainTimeout);

        cts?.Dispose();

        _stopSignal.Reset();
        _healthWake.Reset();
    }

    /// <inheritdoc />
    public ValueTask RequestRecoveryScanAsync(
        string scopeRoot,
        FullTextRecoveryReason reason,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scopeRoot);
        cancellationToken.ThrowIfCancellationRequested();

        lock (_lifecycle)
        {
            if (!_running)
            {
                // 明确拒绝（本片裁定并测试）：未运行时**不**受理，也不留下跨生命周期状态 ——
                // 否则「停止后受理、下次启动才跑」会与「StartAsync 自带 Startup 补偿」重复触发，语义含混。
                _lastError = $"RequestRecoveryScanAsync 被拒绝：维护未在运行（scopeRoot={scopeRoot}，reason={reason}）。";
                return default;
            }

            var scope = FindScopeLocked(scopeRoot);
            if (scope is null)
            {
                _lastError = $"RequestRecoveryScanAsync 被拒绝：scope 未注册（scopeRoot={scopeRoot}，reason={reason}）。";
                return default;
            }

            // 异步受理、不等待完成；同 scope 已有在跑的补偿 ⇒ 合并（不叠加）。
            QueueRecovery(scope, reason);
        }

        return default;
    }

    /// <inheritdoc />
    public FullTextMaintenanceSnapshot GetSnapshot()
    {
        lock (_lifecycle)
        {
            var scopes = new FullTextMaintenanceScopeSnapshot[_scopes.Count];
            for (var i = 0; i < _scopes.Count; i++)
            {
                var scope = _scopes[i];
                scopes[i] = new FullTextMaintenanceScopeSnapshot(
                    scope.ScopeKey,
                    scope.RootPath,
                    scope.IndexDirectory,
                    Directory.Exists(scope.IndexDirectory),
                    scope.Checkpoint?.WatermarkUtc,
                    scope.Checkpoint?.Generation ?? 0,
                    scope.Checkpoint?.LastCompletedBatchId,
                    scope.PendingCount,
                    scope.OverflowCount,
                    scope.LastAppliedUtc,
                    scope.LastProbeState,
                    scope.LastMessage);
            }

            return new FullTextMaintenanceSnapshot(
                _running,
                scopes,
                _startedUtc,
                _stoppedUtc,
                Interlocked.Read(ref _appliedBatchCount),
                Interlocked.Read(ref _failedBatchCount),
                _lastError);
        }
    }

    // ── 组件内诊断 / 驱动接缝（internal；沿用本工程既有习惯「internal + InternalsVisibleTo 组件测试」）──

    /// <summary>
    /// 直接驱动**一次** watcher 折叠缓冲的 flush（与泵任务走**同一实现**）。
    /// <para>存在意义：让「同文件多次快速写入 ⇒ 合并成一次处理」「rename 折叠」成为**确定性**断言，
    /// 而不是靠 sleep 去撞 FSW 的异步投递时序。</para>
    /// </summary>
    /// <returns>本批是否真的产生了变更（空批 ⇒ false）。</returns>
    internal Task<bool> FlushPendingAsync(string scopeRoot, CancellationToken cancellationToken = default)
        => FlushPendingCoreAsync(RequireScope(scopeRoot), cancellationToken);

    /// <summary>直接驱动**一次**补偿扫描（与 <see cref="RequestRecoveryScanAsync"/> 受理后的执行路径同一实现）。</summary>
    /// <returns>本轮 checkpoint 是否被推进。</returns>
    internal Task<bool> RunRecoveryScanAsync(
        string scopeRoot,
        FullTextRecoveryReason reason,
        CancellationToken cancellationToken = default)
        => RunRecoveryScanCoreAsync(RequireScope(scopeRoot), reason, cancellationToken);

    /// <summary>直接驱动**一次**低优先级体检切片（与体检线程调用的是同一实现）。</summary>
    internal Task<MaintenanceHealthCheckSlice> RunHealthCheckSliceAsync(
        string scopeRoot,
        CancellationToken cancellationToken = default)
        => RunHealthCheckSliceCoreAsync(RequireScope(scopeRoot), cancellationToken);

    /// <summary>把一条路径观察投递进 watcher 折叠缓冲（与 FSW 回调走同一实现）。</summary>
    internal void SubmitPathObservation(string scopeRoot, string fullPath)
        => Submit(RequireScope(scopeRoot), fullPath, FullTextChangeSource.Watcher);

    /// <summary>等待维护器**排空**（无待 flush、无待补偿、无在飞批次）；超时返回 false（不抛）。</summary>
    internal async Task<bool> WaitUntilIdleAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            List<ScopeRuntime> scopes;
            lock (_lifecycle)
            {
                scopes = new List<ScopeRuntime>(_scopes);
            }

            var idle = true;
            foreach (var scope in scopes)
            {
                if (!scope.IsIdle)
                {
                    idle = false;
                    break;
                }
            }

            if (idle)
                return true;

            await Task.Delay(5, cancellationToken).ConfigureAwait(false);
        }

        return false;
    }

    /// <summary>运行期诊断计数（M1/M2/M8 的「计数探针」，也可用于运维观测）。</summary>
    internal MaintenanceDiagnostics GetDiagnostics()
    {
        int? priority;
        try
        {
            priority = (int?)_healthThread?.Priority;
        }
        catch (ThreadStateException)
        {
            priority = null;
        }

        return new MaintenanceDiagnostics(
            (int)Interlocked.Read(ref _watchersCreated),
            (int)Interlocked.Read(ref _liveWatchers),
            (int)Interlocked.Read(ref _pumpTasksCreated),
            (int)Interlocked.Read(ref _livePumpTasks),
            _healthThreadRunning,
            priority is { } value ? (ThreadPriority)value : null,
            Interlocked.Read(ref _appliedBatchCount),
            Interlocked.Read(ref _failedBatchCount),
            Interlocked.Read(ref _retainedOldWarningCount),
            Interlocked.Read(ref _rejectedObservationCount),
            Interlocked.Read(ref _deferredObservationCount));
    }

    // ── 补偿请求的优先级 / 合并（纯策略，零 IO；唯一真源）─────────────

    /// <summary>
    /// 同 scope 两个补偿请求合并时的**优先级取值**（大者胜）。方向：含义是「事件可能已丢 / 状态未知」的原因
    /// 压过周期性原因 —— 被周期原因顶掉就会漏文件。
    /// </summary>
    internal static int RecoveryReasonPriority(FullTextRecoveryReason reason) => reason switch
    {
        FullTextRecoveryReason.Startup => 9,
        FullTextRecoveryReason.WatcherOverflow => 8,
        FullTextRecoveryReason.WatcherError => 7,
        FullTextRecoveryReason.IndexGenerationChanged => 6,
        FullTextRecoveryReason.CheckpointUnreadable => 5,
        FullTextRecoveryReason.ManualRebuildCompleted => 4,
        FullTextRecoveryReason.QueuePressure => 3,
        FullTextRecoveryReason.ManualRequest => 2,
        FullTextRecoveryReason.Interval => 1,
        _ => 0,
    };

    /// <summary>合并策略（唯一真源）：取优先级更高者；相等则保留先到的（不叠加、不排队）。</summary>
    internal static FullTextRecoveryReason MergeRecoveryReason(
        FullTextRecoveryReason current,
        FullTextRecoveryReason incoming)
        => RecoveryReasonPriority(incoming) > RecoveryReasonPriority(current) ? incoming : current;

    // ── scope 生命周期 ──────────────────────────────────────────────────

    private void TryAddScopeLocked(FullTextMaintenanceScope? scope, CancellationToken stopToken)
    {
        if (scope is null || string.IsNullOrWhiteSpace(scope.RootPath) || string.IsNullOrWhiteSpace(scope.ScopeKey))
        {
            _lastError = "scope 被拒绝：RootPath / ScopeKey 为空。";
            return;
        }

        foreach (var existing in _scopes)
        {
            if (string.Equals(existing.ScopeKey, scope.ScopeKey, StringComparison.Ordinal))
                return; // 幂等：同 scope 已在运行 ⇒ 请求被合并，不重复创建 watcher / 泵任务。
        }

        _scopes.Add(CreateScopeRuntime(scope, stopToken));
    }

    /// <summary>
    /// 创建 scope 运行体：**这里**才做路径推导（<c>FullTextIndexPaths</c> / <c>MaintenanceCheckpoint</c> 单一真源）、
    /// 挂 watcher、投递 Startup 补偿、启动泵任务。全部发生在 <see cref="StartAsync"/> 内。
    /// </summary>
    private ScopeRuntime CreateScopeRuntime(FullTextMaintenanceScope scope, CancellationToken stopToken)
    {
        var rootPath = scope.RootPath;

        // IndexDirectory 为 null ⇒ 经**同一真源**推导；绝不复刻命名哈希。
        var indexDirectory = string.IsNullOrWhiteSpace(scope.IndexDirectory)
            ? FullTextIndexPaths.ResolveIndexDirectory(_indexOptions.IndexRootDirectory, rootPath)
            : scope.IndexDirectory!;

        var stateDirectory = MaintenanceCheckpoint.ResolveStateDirectory(_indexOptions.IndexRootDirectory, rootPath);
        var checkpointPath = MaintenanceCheckpoint.ResolveCheckpointPath(_indexOptions.IndexRootDirectory, rootPath);

        var runtime = new ScopeRuntime(
            scope,
            indexDirectory,
            stateDirectory,
            checkpointPath,
            _options.Debounce,
            _options.MaxCoalesceWait,
            _time);

        // 读 checkpoint（只读；缺失 / 损坏 ⇒ 水位线为 null ⇒ 全范围校准，绝不按「无需维护」处理）。
        var (checkpoint, readNote) = ReadCheckpoint(runtime);
        runtime.Checkpoint = checkpoint;

        var now = _time.GetUtcNow();
        runtime.NextRecoveryDueUtc = now + _options.RecoveryScanInterval;
        runtime.NextHealthDueUtc = now + _options.HealthCheckInterval;

        if (Directory.Exists(rootPath))
        {
            runtime.Watcher = CreateWatcher(runtime);
            Interlocked.Increment(ref _watchersCreated);
            Interlocked.Increment(ref _liveWatchers);
        }
        else
        {
            runtime.LastMessage = $"语料根不存在，未创建 watcher（补偿扫描仍会登记并如实报告）：{rootPath}";
        }

        if (readNote is not null)
            runtime.LastMessage = runtime.LastMessage is null ? readNote : runtime.LastMessage + " " + readNote;

        // ★ 重开维护器时的 Startup 补偿：宿主停机期的变更只能靠它恢复（§S3 完成标准的可测形式）。
        var startupReason = checkpoint is null && readNote is not null
            ? FullTextRecoveryReason.CheckpointUnreadable
            : FullTextRecoveryReason.Startup;
        QueueRecovery(runtime, startupReason);

        Interlocked.Increment(ref _pumpTasksCreated);
        Interlocked.Increment(ref _livePumpTasks);
        runtime.PumpTask = Task.Run(
            async () =>
            {
                try
                {
                    await PumpLoopAsync(runtime, stopToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // 停止是预期终态。
                }
                catch (Exception ex)
                {
                    runtime.RecordError($"泵任务异常终止：{ex.GetType().Name}: {ex.Message}");
                }
                finally
                {
                    Interlocked.Decrement(ref _livePumpTasks);
                }
            },
            CancellationToken.None);

        return runtime;
    }

    private FileSystemWatcher CreateWatcher(ScopeRuntime scope)
    {
        var watcher = new FileSystemWatcher(scope.RootPath)
        {
            // 64 KB 内部缓冲（与已验证的 CodeIndexWatcher 行为合同一致：缓冲越大，溢出越少）。
            InternalBufferSize = 64 * 1024,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
            IncludeSubdirectories = true,
        };

        watcher.Created += (_, e) => SafeSubmit(scope, e.FullPath);
        watcher.Changed += (_, e) => SafeSubmit(scope, e.FullPath);
        watcher.Deleted += (_, e) => SafeSubmit(scope, e.FullPath);

        // rename 折叠：旧名 Delete + 新名 Upsert（同一次 flush 内由 coalescer 折叠成两条最终动作）。
        watcher.Renamed += (_, e) =>
        {
            SafeSubmit(scope, e.OldFullPath);
            SafeSubmit(scope, e.FullPath);
        };

        watcher.Error += (_, e) =>
        {
            try
            {
                var exception = e.GetException();
                var isOverflow = exception is InternalBufferOverflowException;
                var reason = isOverflow ? FullTextRecoveryReason.WatcherOverflow : FullTextRecoveryReason.WatcherError;

                scope.RecordOverflow($"{reason}：{exception?.GetType().Name}: {exception?.Message}");
                QueueRecovery(scope, reason);
            }
            catch
            {
                // watcher 回调**绝不**允许把异常抛出去（未观察的异常会静默停掉 watcher）。
            }
        };

        watcher.EnableRaisingEvents = true;
        return watcher;
    }

    private void ReleaseWatcher(ScopeRuntime scope)
    {
        var watcher = scope.Watcher;
        scope.Watcher = null;
        if (watcher is null)
            return;

        Interlocked.Decrement(ref _liveWatchers);

        try
        {
            watcher.EnableRaisingEvents = false;
            watcher.Dispose();
        }
        catch (Exception ex) when (MaintenanceCorpusScan.IsFileSystemError(ex))
        {
            scope.RecordError($"watcher 释放失败（已置为不再产生事件）：{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>watcher 回调的安全边界：任何异常都不许外抛（静默停止的 watcher 是最危险的失效模式）。</summary>
    private void SafeSubmit(ScopeRuntime scope, string fullPath)
    {
        try
        {
            Submit(scope, fullPath, FullTextChangeSource.Watcher);
        }
        catch
        {
            // 有意吞掉：回调上下文里抛异常会让 FileSystemWatcher 静默停止。
            scope.RecordError($"watcher 回调处理失败（已隔离，watcher 继续工作）：{fullPath}");
        }
    }

    /// <summary>把一条路径塞进该 scope 的有界折叠缓冲（溢出 ⇒ 计数 + 请求补偿，绝不静默丢弃）。</summary>
    private void Submit(ScopeRuntime scope, string fullPath, FullTextChangeSource source)
    {
        if (string.IsNullOrWhiteSpace(fullPath))
            return;

        string key;
        try
        {
            key = FullTextChangeCoalescer.NormalizeComparisonKey(fullPath);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            Interlocked.Increment(ref _rejectedObservationCount);
            scope.RecordError($"路径无法规范化，未进入折叠缓冲：{fullPath}（{ex.GetType().Name}）");
            return;
        }

        if (!FullTextChangeCoalescer.IsWithinScope(scope.RootPath, fullPath))
        {
            Interlocked.Increment(ref _rejectedObservationCount);
            scope.RecordError($"越界路径，未进入折叠缓冲：{fullPath}");
            return;
        }

        var result = scope.Enqueue(key, fullPath, source, _options.QueueCapacity, _time.GetUtcNow());

        if (result == EnqueueResult.Overflowed)
        {
            Interlocked.Increment(ref _rejectedObservationCount);
            scope.RecordOverflow(
                $"有界队列溢出（容量 {_options.QueueCapacity}）：丢弃细粒度事件，改请求补偿扫描"
                + $"（{FullTextRecoveryReason.QueuePressure}）—— 绝不静默丢弃。");
            QueueRecovery(scope, FullTextRecoveryReason.QueuePressure);
            return;
        }

        SignalWork(scope);
    }

    private void QueueRecovery(ScopeRuntime scope, FullTextRecoveryReason reason)
    {
        scope.SetPendingReason(reason, MergeRecoveryReason);
        SignalWork(scope);
    }

    private static void SignalWork(ScopeRuntime scope)
    {
        if (scope.Signal.CurrentCount > 0)
            return;

        try
        {
            scope.Signal.Release();
        }
        catch (SemaphoreFullException)
        {
            // 已有信号在途：合并即可。
        }
    }

    // ── 泵任务（每 scope 一条；只负责 watcher flush 与补偿扫描）──────────

    private async Task PumpLoopAsync(ScopeRuntime scope, CancellationToken stopToken)
    {
        while (!stopToken.IsCancellationRequested)
        {
            var wait = scope.ComputeNextWait(_time.GetUtcNow(), MinSchedulerSlice, MaxSchedulerSlice);

            try
            {
                await scope.Signal.WaitAsync(wait, stopToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            try
            {
                var now = _time.GetUtcNow();

                // 「在飞」计数包住整个处理段：否则「取走补偿请求」与「真正开始扫描」之间存在一个极短的窗口，
                // 会让 WaitUntilIdleAsync 在那个窗口里误判“已排空”。
                scope.EnterWork();
                try
                {
                    if (scope.TryTakeRecovery(out var reason))
                    {
                        await RunRecoveryScanCoreAsync(scope, reason, stopToken).ConfigureAwait(false);
                        continue;
                    }

                    if (scope.RecoveryDue(now))
                    {
                        QueueRecovery(scope, FullTextRecoveryReason.Interval);
                        continue;
                    }

                    if (scope.FlushDue(now))
                        await FlushPendingCoreAsync(scope, stopToken).ConfigureAwait(false);
                }
                finally
                {
                    scope.ExitWork();
                }
            }
            catch (OperationCanceledException) when (stopToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                scope.RecordError($"维护轮次异常（已隔离，循环继续）：{ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    // ── 源 ①：watcher 折叠缓冲的 flush ──────────────────────────────────

    private async Task<bool> FlushPendingCoreAsync(ScopeRuntime scope, CancellationToken cancellationToken)
    {
        scope.EnterWork();
        try
        {
            return await FlushPendingBodyAsync(scope, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            scope.ExitWork();
        }
    }

    private async Task<bool> FlushPendingBodyAsync(ScopeRuntime scope, CancellationToken cancellationToken)
    {
        var taken = scope.TakePending();
        if (taken.Count == 0)
            return false;

        var scanStartedUtc = _time.GetUtcNow();

        var observations = new List<FullTextChangeObservation>(taken.Count);
        foreach (var entry in taken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            observations.Add(BuildObservation(scope, entry.FullPath, entry.Sources));
        }

        // 三源共用同一份折叠实现（per-path latest-wins / Sources 位或 / 越界拒绝）。
        var coalesced = FullTextChangeCoalescer.Coalesce(observations, scope.RootPath);
        scope.ObserveRejections(coalesced.RejectedPaths);

        // watcher 批次**不推进 checkpoint**：水位线的语义是「最近一次成功**补偿扫描**的开始时刻」，
        // 让 watcher 批次推进会把「补偿尚未覆盖到的窗口」一并跳过（= 漏文件）。
        var application = await ApplyCoalescedAsync(
                scope,
                coalesced,
                scanStartedUtc,
                scope.Checkpoint?.WatermarkUtc,
                requiresCheckpointAdvance: false,
                label: "watcher 批次",
                cancellationToken)
            .ConfigureAwait(false);

        if (application.BatchCount == 0 && coalesced.DeferredPaths.Count > 0)
        {
            scope.LastMessage = $"watcher 批次：{coalesced.DeferredPaths.Count} 个路径暂时不可读 ⇒ 保留旧文档、不产出任何动作"
                + "（下一次补偿扫描会重试它们；本轮不推进 checkpoint）。";
        }

        return coalesced.Changes.Count > 0;
    }

    // ── 源 ②：mtime checkpoint 补偿扫描 ─────────────────────────────────

    private async Task<bool> RunRecoveryScanCoreAsync(
        ScopeRuntime scope,
        FullTextRecoveryReason reason,
        CancellationToken cancellationToken)
    {
        scope.EnterWork();
        try
        {
            return await RunRecoveryScanBodyAsync(scope, reason, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            scope.ExitWork();
        }
    }

    private async Task<bool> RunRecoveryScanBodyAsync(
        ScopeRuntime scope,
        FullTextRecoveryReason reason,
        CancellationToken cancellationToken)
    {
        var scanStartedUtc = _time.GetUtcNow();

        // ① checkpoint（fail-closed）：读不出来 ⇒ 水位线为 null ⇒ 全范围局部校准，绝不按「无需维护」处理。
        var (checkpoint, readNote) = ReadCheckpoint(scope);
        scope.Checkpoint = checkpoint;

        if (readNote is not null)
            scope.LastMessage = readNote;

        var previousWatermarkUtc = checkpoint?.WatermarkUtc;

        if (checkpoint is not null
            && !string.Equals(checkpoint.PolicyFingerprint, _policyFingerprint, StringComparison.Ordinal))
        {
            // 过滤策略变了 ⇒ 旧水位线不可信（沿用会让「新纳入白名单但 mtime 更老」的文件被永久跳过）。
            previousWatermarkUtc = null;
            scope.LastMessage = "checkpoint 的策略指纹与本次生效值不一致 ⇒ 本轮按全范围局部校准（不沿用旧 watermark）。";
        }

        if (!Directory.Exists(scope.IndexDirectory))
        {
            // §3.5：索引不存在 ⇒ 需手动重建；**绝不**通过局部写偷偷创建一份「初始全库索引」。
            scope.LastMessage = $"补偿扫描（{reason}）：live 索引目录不存在（{scope.IndexDirectory}）"
                + "⇒ 需手动重建；本轮不产出任何变更集、不推进 checkpoint。";
            return false;
        }

        // ② 磁盘侧走查（单次遍历 + 噪声目录剪枝 + 逐目录异常隔离）。
        var scan = MaintenanceCorpusScan.EnumerateFiles(scope.RootPath, _indexOptions, cancellationToken);
        if (!scan.IsComplete)
        {
            // 走查不完整 ⇒ 既不能算 Delete 候选，也不能推进水位线（把一次 IO 故障放大成整片删除是最坏结局）。
            scope.LastMessage = $"补偿扫描（{reason}）：语料走查不完整（失败目录 {scan.FailedDirectoryCount} 个：{scan.FirstFailure}）"
                + "⇒ 不计算 Delete 候选、不推进 checkpoint。";
            return false;
        }

        // ③ 索引侧清册（只读；「索引路径 − 磁盘路径 = Delete 候选」的**唯一**来源）。
        var indexPaths = new HashSet<string>(StringComparer.Ordinal);
        var indexDisplayPaths = new Dictionary<string, string>(StringComparer.Ordinal);
        await foreach (var entry in _engine
                           .EnumerateIndexedPathsAsync(scope.RootPath, cancellationToken)
                           .ConfigureAwait(false))
        {
            indexPaths.Add(entry.NormalizedPath);
            indexDisplayPaths[entry.NormalizedPath] = entry.FullPath;
        }

        // ④ 逐文件观察（mtime 判定复用 MTimeComparison；未变的成功文件**不产出观察** ⇒ 不重复写）。
        var observations = new List<FullTextChangeObservation>();
        var seenOnDisk = new HashSet<string>(StringComparer.Ordinal);

        foreach (var file in scan.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();

            string key;
            try
            {
                key = FullTextChangeCoalescer.NormalizeComparisonKey(file);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                Interlocked.Increment(ref _rejectedObservationCount);
                continue;
            }

            seenOnDisk.Add(key);

            var observed = ObservePath(file, scope.RootPath, _indexOptions);
            var inIndex = indexPaths.Contains(key);

            if (observed.Kind == FullTextPathObservation.IndexableFile)
            {
                // 「索引里没有它」⇒ 无条件纳入（否则一个 mtime 早于水位线、却从未被索引过的文件会被永久跳过）。
                if (inIndex
                    && !MTimeComparison.RequiresProcessing(observed.LastWriteUtc, previousWatermarkUtc, _options.MTimeOverlap))
                {
                    continue;
                }
            }
            else if (observed.Kind != FullTextPathObservation.Unreadable && !inIndex)
            {
                // 「不该出现在索引里」的形态（不存在 / 目录 / 扩展名不再允许 / 噪声 / 空文件 / 超限）：
                // 只对**索引里已有的路径**产出 Delete（否则每个 .png 都会变成一条 no-op Delete，把批次撑爆）。
                continue;
            }

            observations.Add(new FullTextChangeObservation(
                file,
                FullTextChangeSource.MTimeRecovery,
                observed.Kind,
                observed.LastWriteUtc,
                observed.Length));
        }

        // ⑤ 索引里有、磁盘上没有 ⇒ Delete 候选（离线删除 / rename 只收到一侧事件的唯一可靠来源）。
        foreach (var key in indexPaths)
        {
            if (seenOnDisk.Contains(key))
                continue;

            observations.Add(new FullTextChangeObservation(
                indexDisplayPaths[key],
                FullTextChangeSource.MTimeRecovery,
                FullTextPathObservation.Missing,
                LastWriteUtc: null,
                Length: null));
        }

        var coalesced = FullTextChangeCoalescer.Coalesce(observations, scope.RootPath);
        scope.ObserveRejections(coalesced.RejectedPaths);

        var application = await ApplyCoalescedAsync(
                scope,
                coalesced,
                scanStartedUtc,
                previousWatermarkUtc,
                requiresCheckpointAdvance: true,
                label: $"补偿扫描（{reason}）",
                cancellationToken)
            .ConfigureAwait(false);

        var canAdvance = application.AllAdvanced
            && application.DeferredCount == 0
            && application.RejectedCount == 0;

        if (!canAdvance)
        {
            // 保留既有说明（尤其 RetainedOld 告警）—— 本轮小结插在前面，不得把它冲掉。
            var summary = $"补偿扫描（{reason}）：本轮**不推进** checkpoint —— {DescribeRoundBlock(application)}"
                + "；下一轮重放同一批变更（允许成功文件被重复处理，漏掉文件不允许）。";
            scope.LastMessage = string.IsNullOrWhiteSpace(scope.LastMessage)
                ? summary
                : summary + " " + scope.LastMessage;
            return false;
        }

        var batchId = application.LastBatchId ?? NewBatchId();
        WriteCheckpoint(scope, scanStartedUtc, _time.GetUtcNow(), batchId);
        scope.LastMessage = $"补偿扫描（{reason}）：已推进 checkpoint（generation={scope.Checkpoint?.Generation}，"
            + $"watermark={scope.Checkpoint?.WatermarkUtc:O}，批次 {application.BatchCount} 个，变更 {coalesced.Changes.Count} 条）。";
        return true;
    }

    private static string DescribeRoundBlock(RoundApplication application)
    {
        if (application.DeferredCount > 0)
            return $"{application.DeferredCount} 个路径暂时不可读（保留旧文档、待下轮重试）";

        if (application.RejectedCount > 0)
            return $"{application.RejectedCount} 条观察被折叠层拒绝（越界 / 空路径 / 无来源）";

        if (!application.AllAdvanced)
            return "至少一个批次的 CheckpointAdvanced 为 false（部分成功 / 被拒 / 互斥 / 取消 / 致命失败）";

        return "未满足完整补偿轮次的前置条件";
    }

    // ── 变更集的执行（三源共用；引擎只执行，checkpoint 由本层写）────────

    private async Task<RoundApplication> ApplyCoalescedAsync(
        ScopeRuntime scope,
        FullTextCoalesceResult coalesced,
        DateTimeOffset scanStartedUtc,
        DateTimeOffset? previousWatermarkUtc,
        bool requiresCheckpointAdvance,
        string label,
        CancellationToken cancellationToken)
    {
        var deferredCount = coalesced.DeferredPaths.Count;
        if (deferredCount > 0)
            Interlocked.Add(ref _deferredObservationCount, deferredCount);

        if (coalesced.Changes.Count == 0)
        {
            // 空变更集：不打开 writer、不 commit（无谓的 commit 会平白改变索引字节）。
            scope.LastMessage = deferredCount > 0 || coalesced.RejectedPaths.Count > 0
                ? $"{label}：无动作可产（{deferredCount} 个待重试 / {coalesced.RejectedPaths.Count} 个被拒）；保留旧文档，本轮不推进 checkpoint。"
                : $"{label}：无可应用变更（水位线以内的文件均未变化）。";

            return new RoundApplication(
                BatchCount: 0,
                AllAdvanced: true,
                LastBatchId: null,
                DeferredCount: deferredCount,
                RejectedCount: coalesced.RejectedPaths.Count);
        }

        var batchCount = 0;
        var allAdvanced = true;
        string? lastBatchId = null;

        var index = 0;
        while (index < coalesced.Changes.Count)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var take = Math.Min(_options.MaxBatchPaths, coalesced.Changes.Count - index);
            var slice = new FullTextFileChange[take];
            for (var i = 0; i < take; i++)
                slice[i] = coalesced.Changes[index + i];
            index += take;

            var batchId = NewBatchId();
            lastBatchId = batchId;

            var changeSet = new FullTextChangeSet(
                batchId,
                scope.RootPath,
                scope.ScopeKey,
                slice,
                scanStartedUtc,
                previousWatermarkUtc,
                requiresCheckpointAdvance);

            var result = await _engine
                .ApplyChangesAsync(changeSet, BuildBudget(scope), cancellationToken)
                .ConfigureAwait(false);

            batchCount++;
            RecordBatchResult(scope, result);

            if (!result.CheckpointAdvanced)
                allAdvanced = false;
        }

        return new RoundApplication(
            batchCount,
            allAdvanced,
            lastBatchId,
            deferredCount,
            coalesced.RejectedPaths.Count);
    }

    private FullTextMutationBudget BuildBudget(ScopeRuntime scope)
    {
        long liveBytes;
        try
        {
            liveBytes = SupplyIndexDirectoryLayout.MeasureLiveIndexBytes(_indexOptions.IndexRootDirectory);
        }
        catch (Exception ex) when (MaintenanceCorpusScan.IsFileSystemError(ex))
        {
            // 量不出来就按 0 交给引擎：引擎会在临界区内**重测**并取更严的一侧（量不出时 fail-closed 拒绝本批）。
            liveBytes = 0;
        }

        return new FullTextMutationBudget(scope.MaxIndexBytes, liveBytes, _options.MaxBatchPaths);
    }

    private void RecordBatchResult(ScopeRuntime scope, FullTextMutationResult result)
    {
        if (result.CommitMilliseconds is not null)
        {
            Interlocked.Increment(ref _appliedBatchCount);
            scope.LastAppliedUtc = _time.GetUtcNow();
        }

        if (result.State == FullTextMutationState.PartiallyApplied)
            Interlocked.Increment(ref _failedBatchCount);

        if (result.State is FullTextMutationState.Failed or FullTextMutationState.Rejected
            or FullTextMutationState.Busy or FullTextMutationState.Cancelled)
        {
            lock (_lifecycle)
            {
                _lastError = $"[{result.ScopeKey}] {result.State}：{result.Message}";
            }
        }

        if (!string.IsNullOrWhiteSpace(result.Message))
            scope.LastMessage = result.Message;

        // ★ 裁定③：同一路径连续 N 轮 RetainedOld ⇒ 告警；**绝不**实现成「自动跳过该文件」。
        var warnings = scope.UpdateRetainedStreaks(result, _options.RetainedOldWarnThreshold);
        if (warnings > 0)
        {
            Interlocked.Add(ref _retainedOldWarningCount, warnings);
            var warning = $"⚠ 告警：{warnings} 个路径已连续 {_options.RetainedOldWarnThreshold} 轮以上 RetainedOld"
                + "（提取 / 读取持续失败）。它们**仍会**在每轮被重放（不自动跳过）；"
                + "长期不排除会让 checkpoint 永不推进（这是 fail-closed 方向的已知代价）。";
            scope.LastMessage = string.IsNullOrWhiteSpace(scope.LastMessage)
                ? warning
                : scope.LastMessage + " " + warning;
        }
    }

    private string NewBatchId() =>
        BatchIdPrefix + "-" + Interlocked.Increment(ref _batchSequence).ToString("D6", CultureInfo.InvariantCulture);

    // ── checkpoint 读 / 写（写盘是维护层职责）──────────────────────────

    private (MaintenanceCheckpoint? Checkpoint, string? Note) ReadCheckpoint(ScopeRuntime scope)
    {
        string? text = null;
        try
        {
            if (File.Exists(scope.CheckpointPath))
                text = File.ReadAllText(scope.CheckpointPath, Encoding.UTF8);
        }
        catch (Exception ex) when (MaintenanceCorpusScan.IsFileSystemError(ex))
        {
            return (null, $"checkpoint 读取失败（{ex.GetType().Name}: {ex.Message}）⇒ 按陈旧处理（全范围局部校准），"
                + "绝不按「无需维护」处理。");
        }

        var status = MaintenanceCheckpoint.TryParse(text, out var checkpoint);
        switch (status)
        {
            case MaintenanceCheckpointReadStatus.Ok when checkpoint is not null:
                return MaintenanceCheckpoint.MatchesScope(checkpoint, scope.RootPath)
                    ? (checkpoint, null)
                    : (null, $"checkpoint 属于另一个 scope（{checkpoint.ScopeRoot}）⇒ 按陈旧处理"
                        + "（跨 scope 沿用 watermark 会漏处理）。");

            case MaintenanceCheckpointReadStatus.Missing:
                return (null, "首次建立维护状态（无 checkpoint）⇒ 本轮按全范围局部校准，绝不按「无需维护」处理。");

            case MaintenanceCheckpointReadStatus.UnsupportedVersion:
                return (null, "checkpoint 版本不受支持 ⇒ 按陈旧处理（全范围局部校准）。");

            default:
                return (null, "checkpoint 内容损坏 / 必填字段缺失 ⇒ 按陈旧处理（全范围局部校准）。");
        }
    }

    private void WriteCheckpoint(
        ScopeRuntime scope,
        DateTimeOffset scanStartedUtc,
        DateTimeOffset scanFinishedUtc,
        string batchId)
    {
        var next = MaintenanceCheckpoint.Advance(
            scope.Checkpoint,
            scope.RootPath,
            _policyFingerprint,
            scanStartedUtc,
            scanFinishedUtc,
            batchId,
            _time.GetUtcNow());

        // ★ 唯一一处创建 checkpoint 目录：只在「本轮真的允许推进」时发生（未 StartAsync 时不存在这条路径）。
        Directory.CreateDirectory(scope.StateDirectory);

        var temporaryPath = MaintenanceCheckpoint.ResolveTemporaryCheckpointPath(
            _indexOptions.IndexRootDirectory,
            scope.RootPath,
            batchId);

        File.WriteAllText(temporaryPath, next.ToJson(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        File.Move(temporaryPath, scope.CheckpointPath, overwrite: true);

        scope.Checkpoint = next;

        // 推进成功后清零重试计数（本轮确实全部成功）。
        scope.ClearRetainedStreaks();
    }

    // ── 源 ③：低优先级体检（专用 BelowNormal 后台线程 + 小切片 + 退避）──

    private void StartHealthThreadLocked(CancellationToken stopToken)
    {
        var thread = new Thread(() => HealthCheckLoop(stopToken))
        {
            IsBackground = true,
            Name = "pudding-fts-maintenance-health",
            // 方案 §3.7：低优先级线程，让位于交互负载。
            Priority = ThreadPriority.BelowNormal,
        };

        _healthThread = thread;
        thread.Start();
    }

    private void HealthCheckLoop(CancellationToken stopToken)
    {
        _healthThreadRunning = true;
        try
        {
            while (!stopToken.IsCancellationRequested && !_stopSignal.IsSet)
            {
                try
                {
                    RunDueHealthChecks(stopToken);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception ex)
                {
                    lock (_lifecycle)
                    {
                        _lastError = $"体检轮次异常（已隔离）：{ex.GetType().Name}: {ex.Message}";
                    }
                }

                var wait = ComputeHealthWait();
                _healthWake.Reset();
                try
                {
                    _healthWake.Wait(wait);
                }
                catch (ObjectDisposedException)
                {
                    return;
                }
            }
        }
        finally
        {
            _healthThreadRunning = false;
        }
    }

    private TimeSpan ComputeHealthWait()
    {
        List<ScopeRuntime> scopes;
        lock (_lifecycle)
        {
            scopes = new List<ScopeRuntime>(_scopes);
        }

        if (scopes.Count == 0)
            return MaxSchedulerSlice;

        var now = _time.GetUtcNow();
        var earliest = DateTimeOffset.MaxValue;
        foreach (var scope in scopes)
        {
            if (scope.NextHealthDueUtc < earliest)
                earliest = scope.NextHealthDueUtc;
        }

        var wait = earliest - now;
        if (wait < MinSchedulerSlice)
            return MinSchedulerSlice;

        return wait > MaxSchedulerSlice ? MaxSchedulerSlice : wait;
    }

    private void RunDueHealthChecks(CancellationToken stopToken)
    {
        List<ScopeRuntime> scopes;
        lock (_lifecycle)
        {
            scopes = new List<ScopeRuntime>(_scopes);
        }

        foreach (var scope in scopes)
        {
            if (_time.GetUtcNow() < scope.NextHealthDueUtc)
                continue;

            // 先排下一轮（正常周期），切片内部的退避（PressureBackoff）会覆盖它。
            scope.NextHealthDueUtc = _time.GetUtcNow() + _options.HealthCheckInterval;

            RunHealthCheckSliceCoreAsync(scope, stopToken).GetAwaiter().GetResult();
        }
    }

    private async Task<MaintenanceHealthCheckSlice> RunHealthCheckSliceCoreAsync(
        ScopeRuntime scope,
        CancellationToken cancellationToken)
    {
        scope.EnterWork();
        try
        {
            return await RunHealthCheckSliceBodyAsync(scope, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            scope.ExitWork();
        }
    }

    private async Task<MaintenanceHealthCheckSlice> RunHealthCheckSliceBodyAsync(
        ScopeRuntime scope,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // ① 资源压力：不可采样 / 超阈值 ⇒ 退避（方案 §3.7「资源采样失败按系统繁忙处理」）。
        var sample = _pressureProbe.Sample();
        var backoff = DescribePressureBackoff(sample);
        if (backoff is not null)
        {
            scope.LastMessage = $"体检退避：{backoff}（下一轮再试，绝不强行运行）。";
            scope.NextHealthDueUtc = _time.GetUtcNow() + _options.PressureBackoff;
            return new MaintenanceHealthCheckSlice(false, true, scope.LastProbeState, 0, 0, 0, scope.LastMessage);
        }

        // ② 只读完整性探针（**绝不**自动重建、绝不写索引）。
        var probe = await _engine.ProbeIntegrityAsync(scope.RootPath, cancellationToken).ConfigureAwait(false);
        scope.LastProbeState = probe.State;

        if (probe.State == FullTextIndexIntegrityState.ManualRebuildRequired)
        {
            scope.LastMessage = $"体检：{probe.State} —— {probe.Message}（只标记，绝不自动重建）。";
            return new MaintenanceHealthCheckSlice(
                true, false, probe.State, probe.CheckedPathCount, probe.MismatchCount, 0, scope.LastMessage);
        }

        if (probe.State == FullTextIndexIntegrityState.Healthy)
        {
            scope.LastMessage = $"体检：Healthy（核对 {probe.CheckedPathCount} 个路径，无差异）。";
            return new MaintenanceHealthCheckSlice(
                true, false, probe.State, probe.CheckedPathCount, 0, 0, scope.LastMessage);
        }

        // ③ Degraded ⇒ 把差异按 HealthCheckSliceFiles **切片**发布（避免长持 writer lock）。
        var repaired = 0;
        if (probe.MismatchedPaths.Count > 0)
        {
            var take = Math.Min(_options.HealthCheckSliceFiles, probe.MismatchedPaths.Count);
            var observations = new List<FullTextChangeObservation>(take);
            for (var i = 0; i < take; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                observations.Add(BuildObservation(scope, probe.MismatchedPaths[i], FullTextChangeSource.IntegrityCheck));
            }

            var coalesced = FullTextChangeCoalescer.Coalesce(observations, scope.RootPath);
            repaired = coalesced.Changes.Count;

            await ApplyCoalescedAsync(
                    scope,
                    coalesced,
                    _time.GetUtcNow(),
                    scope.Checkpoint?.WatermarkUtc,
                    // 体检不是完整补偿轮次（只覆盖一个切片）⇒ **不推进** checkpoint。
                    requiresCheckpointAdvance: false,
                    label: "体检切片",
                    cancellationToken)
                .ConfigureAwait(false);

            scope.LastMessage = $"体检：Degraded（差异 {probe.MismatchCount} 条，本切片发布修复 {repaired} 条，"
                + $"切片上限 {_options.HealthCheckSliceFiles}）；不推进 checkpoint、不自动重建。";
        }

        return new MaintenanceHealthCheckSlice(
            true, false, probe.State, probe.CheckedPathCount, probe.MismatchCount, repaired, scope.LastMessage);
    }

    private string? DescribePressureBackoff(ResourcePressureSample? sample)
    {
        if (sample is null)
            return "资源不可采样（探针返回 null ⇒ 按系统繁忙处理）";

        if (sample.CpuPercent is { } cpu && cpu >= _options.CpuHighWatermarkPercent)
            return $"系统 CPU {cpu.ToString("F1", CultureInfo.InvariantCulture)}% ≥ 阈值 {_options.CpuHighWatermarkPercent}%";

        if (sample.DiskBusyPercent is { } disk && disk >= _options.DiskHighWatermarkPercent)
            return $"磁盘忙碌 {disk.ToString("F1", CultureInfo.InvariantCulture)}% ≥ 阈值 {_options.DiskHighWatermarkPercent}%";

        return null;
    }

    // ── 路径再观察（三源共用同一判定体）────────────────────────────────

    /// <summary>
    /// 把一条路径按**当前磁盘事实**重新观察成 coalescer 的输入（方案 §3.1「flush 时重新观察最终状态」）。
    /// <para>
    /// 判定顺序是 fail-closed 的：目录 ⇒ <c>Directory</c>；不存在 ⇒ <c>Missing</c>；
    /// 策略拒绝 ⇒ 对应 Delete 形态；**读不到（含「连读句柄都拿不到」）⇒ <c>Unreadable</c>** ——
    /// 后者绝不折叠成 <c>Missing</c>（那会把「读不到」当「不存在」，误删仍然有效的旧文档）。
    /// </para>
    /// </summary>
    private static PathObservation ObservePath(
        string fullPath,
        string scopeRoot,
        FullTextIndexOptions options)
    {
        FileAttributes attributes;
        try
        {
            attributes = File.GetAttributes(fullPath);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return new PathObservation(FullTextPathObservation.Missing, null, null, "not-found");
        }
        catch (Exception ex) when (MaintenanceCorpusScan.IsFileSystemError(ex))
        {
            return new PathObservation(
                UnreadableKind(),
                BestEffortLastWriteUtc(fullPath),
                BestEffortLength(fullPath),
                $"{ex.GetType().Name}: {ex.Message}");
        }

        if ((attributes & FileAttributes.Directory) != 0)
            return new PathObservation(FullTextPathObservation.Directory, null, null, "directory");

        if (FileCandidateCollector.TryCollectCandidate(
                fullPath,
                scopeRoot,
                options,
                patterns: null,
                out var entry,
                out var skipReason))
        {
            // 元数据可读 **不等于** 现在真的读得出来：连读句柄都拿不到（写者独占 / 权限）⇒ 归到「不可读」，
            // 让它走「保留旧文档 + 待重试」，而不是把一次注定失败的提取塞给执行层。
            if (!CanOpenForRead(fullPath))
            {
                return new PathObservation(
                    UnreadableKind(),
                    new DateTimeOffset(entry.LastWrite),
                    entry.Size,
                    "read-open-failed");
            }

            return new PathObservation(
                FullTextPathObservation.IndexableFile,
                new DateTimeOffset(entry.LastWrite),
                entry.Size,
                null);
        }

        return new PathObservation(
            MapSkipReason(skipReason),
            BestEffortLastWriteUtc(fullPath),
            BestEffortLength(fullPath),
            skipReason);
    }

    /// <summary>
    /// 「读不到」的**唯一收敛点**：任何不可读（元数据读失败 / 内容读句柄拿不到 / 未知跳过原因）都经此归到
    /// <see cref="FullTextPathObservation.Unreadable"/>。集中在一处，是为了让「把不可读当不存在」这种回归
    /// **只有一行**可以改（可被变异取红精确命中）。
    /// </summary>
    private static FullTextPathObservation UnreadableKind() => FullTextPathObservation.Unreadable;

    private static FullTextPathObservation MapSkipReason(string? skipReason)
    {
        if (string.IsNullOrEmpty(skipReason) || FileCandidateCollector.IsErrorSkip(skipReason))
            return UnreadableKind();

        return skipReason switch
        {
            CandidateSkipReasons.NotIndexableExtension => FullTextPathObservation.ExtensionNotAllowed,
            CandidateSkipReasons.PatternMismatch => FullTextPathObservation.ExtensionNotAllowed,
            CandidateSkipReasons.ExcludedPath => FullTextPathObservation.Noisy,
            CandidateSkipReasons.EmptyFile => FullTextPathObservation.Empty,
            CandidateSkipReasons.TooLarge => FullTextPathObservation.Oversized,
            _ => UnreadableKind(),
        };
    }

    private static bool CanOpenForRead(string fullPath)
    {
        try
        {
            // 用**最宽松**的共享模式打开：只有连最宽松的读都拿不到时才判「不可读」，
            // 因此本判定是执行层提取（FileShare.Read）的**严格前置**，不会比执行层更严格。
            using var stream = new FileStream(
                fullPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            return true;
        }
        catch (Exception ex) when (MaintenanceCorpusScan.IsFileSystemError(ex))
        {
            return false;
        }
    }

    private static DateTimeOffset? BestEffortLastWriteUtc(string fullPath)
    {
        try
        {
            return new DateTimeOffset(File.GetLastWriteTimeUtc(fullPath));
        }
        catch (Exception ex) when (MaintenanceCorpusScan.IsFileSystemError(ex))
        {
            return null;
        }
    }

    private static long? BestEffortLength(string fullPath)
    {
        try
        {
            return new FileInfo(fullPath).Length;
        }
        catch (Exception ex) when (MaintenanceCorpusScan.IsFileSystemError(ex))
        {
            return null;
        }
    }

    private FullTextChangeObservation BuildObservation(ScopeRuntime scope, string fullPath, FullTextChangeSource source)
    {
        var observed = ObservePath(fullPath, scope.RootPath, _indexOptions);
        return new FullTextChangeObservation(fullPath, source, observed.Kind, observed.LastWriteUtc, observed.Length);
    }

    private ScopeRuntime RequireScope(string scopeRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scopeRoot);

        lock (_lifecycle)
        {
            return FindScopeLocked(scopeRoot)
                ?? throw new InvalidOperationException($"scope 未注册或维护未在运行：{scopeRoot}");
        }
    }

    private ScopeRuntime? FindScopeLocked(string scopeRoot)
    {
        string key;
        try
        {
            key = FullTextChangeCoalescer.NormalizeComparisonKey(scopeRoot);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }

        foreach (var scope in _scopes)
        {
            if (string.Equals(scope.ScopeKey, key, StringComparison.Ordinal))
                return scope;
        }

        return null;
    }

    // ── 内部数据 ────────────────────────────────────────────────────────

    private enum EnqueueResult
    {
        Added = 0,
        Merged = 1,
        Overflowed = 2,
    }

    private readonly record struct PendingEntry(string FullPath, FullTextChangeSource Sources);

    private readonly record struct PathObservation(
        FullTextPathObservation Kind,
        DateTimeOffset? LastWriteUtc,
        long? Length,
        string? Note);

    private readonly record struct RoundApplication(
        int BatchCount,
        bool AllAdvanced,
        string? LastBatchId,
        int DeferredCount,
        int RejectedCount);

    /// <summary>
    /// 一个 scope 的运行体：折叠缓冲 + 期限 + 观测字段 + watcher + 泵任务。
    /// 折叠缓冲 / 期限 / 重试计数经 <see cref="StateLock"/> 保护；观测字段是「尽力快照」语义。
    /// </summary>
    private sealed class ScopeRuntime
    {
        private readonly Dictionary<string, PendingEntry> _pending = new(StringComparer.Ordinal);
        private readonly List<string> _order = new();
        private readonly HashSet<string> _orderKeys = new(StringComparer.Ordinal);
        private readonly Dictionary<string, int> _retainedStreak = new(StringComparer.Ordinal);
        private readonly TimeSpan _debounce;
        private readonly TimeSpan _maxCoalesceWait;
        private readonly TimeProvider _time;

        private DateTimeOffset? _dirtyFirstUtc;
        private DateTimeOffset? _dirtyLastUtc;
        private int _inFlight;

        internal ScopeRuntime(
            FullTextMaintenanceScope scope,
            string indexDirectory,
            string stateDirectory,
            string checkpointPath,
            TimeSpan debounce,
            TimeSpan maxCoalesceWait,
            TimeProvider time)
        {
            RootPath = scope.RootPath;
            ScopeKey = scope.ScopeKey;
            MaxIndexBytes = scope.MaxIndexBytes;
            IndexDirectory = indexDirectory;
            StateDirectory = stateDirectory;
            CheckpointPath = checkpointPath;
            _debounce = debounce;
            _maxCoalesceWait = maxCoalesceWait;
            _time = time;
        }

        internal object StateLock { get; } = new();

        internal SemaphoreSlim Signal { get; } = new(0);

        internal string RootPath { get; }

        internal string ScopeKey { get; }

        internal long MaxIndexBytes { get; }

        internal string IndexDirectory { get; }

        internal string StateDirectory { get; }

        internal string CheckpointPath { get; }

        internal FileSystemWatcher? Watcher { get; set; }

        internal Task? PumpTask { get; set; }

        internal MaintenanceCheckpoint? Checkpoint { get; set; }

        internal FullTextRecoveryReason? PendingReason { get; set; }

        internal DateTimeOffset NextRecoveryDueUtc { get; set; }

        internal DateTimeOffset NextHealthDueUtc { get; set; }

        internal DateTimeOffset? LastAppliedUtc { get; set; }

        internal FullTextIndexIntegrityState? LastProbeState { get; set; }

        internal string? LastMessage { get; set; }

        internal long OverflowCount { get; private set; }

        internal int PendingCount
        {
            get
            {
                lock (StateLock)
                {
                    return _pending.Count;
                }
            }
        }

        internal bool IsIdle
        {
            get
            {
                lock (StateLock)
                {
                    return _pending.Count == 0 && PendingReason is null && _inFlight == 0;
                }
            }
        }

        /// <summary>进入一个工作段（补偿扫描 / flush / 体检切片）：让「已排空」的判定不被在飞工作骗过。</summary>
        internal void EnterWork()
        {
            lock (StateLock)
            {
                _inFlight++;
            }
        }

        internal void ExitWork()
        {
            lock (StateLock)
            {
                _inFlight--;
            }
        }

        internal EnqueueResult Enqueue(
            string key,
            string displayPath,
            FullTextChangeSource source,
            int queueCapacity,
            DateTimeOffset now)
        {
            lock (StateLock)
            {
                _dirtyFirstUtc ??= now;
                _dirtyLastUtc = now;

                if (_pending.TryGetValue(key, out var existing))
                {
                    // per-path latest-wins（大小写 / 分隔符差异收敛到同一个规范化键）。
                    _pending[key] = new PendingEntry(displayPath, existing.Sources | source);
                    return EnqueueResult.Merged;
                }

                if (_pending.Count >= queueCapacity)
                {
                    // 有界队列溢出：丢掉细粒度事件，但**必须**保留「需要补偿扫描」这一事实
                    // （计数与 LastMessage 由调用方 Submit 统一登记，避免两处各自加一）。
                    return EnqueueResult.Overflowed;
                }

                _pending[key] = new PendingEntry(displayPath, source);
                if (_orderKeys.Add(key))
                    _order.Add(key);

                return EnqueueResult.Added;
            }
        }

        internal List<PendingEntry> TakePending()
        {
            lock (StateLock)
            {
                var taken = new List<PendingEntry>(_order.Count);
                foreach (var key in _order)
                {
                    if (_pending.TryGetValue(key, out var entry))
                        taken.Add(entry);
                }

                _pending.Clear();
                _order.Clear();
                _orderKeys.Clear();
                _dirtyFirstUtc = null;
                _dirtyLastUtc = null;
                return taken;
            }
        }

        internal void SetPendingReason(
            FullTextRecoveryReason reason,
            Func<FullTextRecoveryReason, FullTextRecoveryReason, FullTextRecoveryReason> merge)
        {
            lock (StateLock)
            {
                PendingReason = PendingReason is { } current ? merge(current, reason) : reason;
            }
        }

        internal bool TryTakeRecovery(out FullTextRecoveryReason reason)
        {
            lock (StateLock)
            {
                if (PendingReason is { } pending)
                {
                    PendingReason = null;
                    reason = pending;
                    return true;
                }
            }

            reason = FullTextRecoveryReason.Interval;
            return false;
        }

        internal bool RecoveryDue(DateTimeOffset now) => now >= NextRecoveryDueUtc;

        internal bool FlushDue(DateTimeOffset now)
        {
            lock (StateLock)
            {
                if (_pending.Count == 0)
                    return false;

                var last = _dirtyLastUtc ?? now;
                var first = _dirtyFirstUtc ?? now;
                return now - last >= _debounce || now - first >= _maxCoalesceWait;
            }
        }

        internal TimeSpan ComputeNextWait(DateTimeOffset now, TimeSpan min, TimeSpan max)
        {
            var candidate = NextRecoveryDueUtc - now;

            lock (StateLock)
            {
                if (_pending.Count > 0)
                {
                    var last = _dirtyLastUtc ?? now;
                    var first = _dirtyFirstUtc ?? now;
                    var debounceRemaining = last + _debounce - now;
                    var coalesceRemaining = first + _maxCoalesceWait - now;
                    if (debounceRemaining < candidate)
                        candidate = debounceRemaining;
                    if (coalesceRemaining < candidate)
                        candidate = coalesceRemaining;
                }
            }

            if (candidate < min)
                return min;

            return candidate > max ? max : candidate;
        }

        internal void RecordOverflow(string message)
        {
            lock (StateLock)
            {
                OverflowCount++;
                LastMessage = message;
            }
        }

        internal void RecordError(string message) => LastMessage = message;

        internal void ObserveRejections(IReadOnlyList<FullTextRejectedPath> rejected)
        {
            if (rejected.Count == 0)
                return;

            LastMessage = $"折叠层拒绝了 {rejected.Count} 条观察（首批：{rejected[0].Reason} {rejected[0].FullPath}）；"
                + "拒绝项不进入变更集，本轮判定为不完整（不推进 checkpoint）。";
        }

        /// <summary>
        /// 更新「连续 RetainedOld 轮数」（★ 裁定③）。**只计数、绝不跳过**：返回本轮新触发的告警数。
        /// 入参同时覆盖 <c>RetainedOldPaths</c> 与 <c>FailedPaths</c>（二者同构：本批未能落到索引）。
        /// </summary>
        internal int UpdateRetainedStreaks(FullTextMutationResult result, int warnThreshold)
        {
            if (warnThreshold <= 0)
                return 0;

            var troubled = new HashSet<string>(StringComparer.Ordinal);
            foreach (var path in result.RetainedOldPaths)
                troubled.Add(path);

            var warnings = 0;

            lock (StateLock)
            {
                // 本批成功的路径 ⇒ 清零（不在 troubled 里的都算成功）。
                if (_retainedStreak.Count > 0)
                {
                    var keys = new List<string>(_retainedStreak.Keys);
                    foreach (var key in keys)
                    {
                        if (!troubled.Contains(key))
                            _retainedStreak.Remove(key);
                    }
                }

                foreach (var path in troubled)
                {
                    var count = _retainedStreak.TryGetValue(path, out var seen) ? seen + 1 : 1;
                    _retainedStreak[path] = count;
                    if (count == warnThreshold)
                        warnings++;
                }
            }

            return warnings;
        }

        internal void ClearRetainedStreaks()
        {
            lock (StateLock)
            {
                _retainedStreak.Clear();
            }
        }
    }
}

/// <summary>运行期诊断计数（M1/M2/M8 的「计数探针」；也可用于运维观测）。</summary>
/// <param name="WatchersCreated">累计创建的 watcher 实例数。</param>
/// <param name="LiveWatchers">当前未释放的 watcher 数（<c>StopAsync</c> 之后必须为 0）。</param>
/// <param name="PumpTasksCreated">累计创建的泵任务数。</param>
/// <param name="LivePumpTasks">当前未结束的泵任务数（<c>StopAsync</c> 之后必须为 0）。</param>
/// <param name="HealthThreadRunning">体检线程是否在运行。</param>
/// <param name="HealthThreadPriority">体检线程优先级（未运行时为 null）。</param>
/// <param name="BatchesApplied">累计成功提交的批次数（含部分成功批次）。</param>
/// <param name="BatchesFailed">累计「部分成功」批次数。</param>
/// <param name="RetainedOldWarnings">累计触发的 RetainedOld 连续告警数。</param>
/// <param name="RejectedObservations">累计被拒绝 / 溢出的观察数。</param>
/// <param name="DeferredObservations">累计被延迟（暂时不可读）的观察数。</param>
internal sealed record MaintenanceDiagnostics(
    int WatchersCreated,
    int LiveWatchers,
    int PumpTasksCreated,
    int LivePumpTasks,
    bool HealthThreadRunning,
    ThreadPriority? HealthThreadPriority,
    long BatchesApplied,
    long BatchesFailed,
    long RetainedOldWarnings,
    long RejectedObservations,
    long DeferredObservations);

/// <summary>一次体检切片的结果（诊断用；不进入契约面）。</summary>
/// <param name="Ran">本切片是否真的跑了（退避时为 false）。</param>
/// <param name="BackedOff">是否因资源压力 / 不可采样而退避。</param>
/// <param name="State">本切片观测到的三态（退避时为上一次结论）。</param>
/// <param name="CheckedPathCount">只读探针实际核对的路径数。</param>
/// <param name="MismatchCount">探针发现的差异条数。</param>
/// <param name="RepairedPathCount">本切片实际发布出去的修复条数（≤ <c>HealthCheckSliceFiles</c>）。</param>
/// <param name="Message">可读说明。</param>
internal sealed record MaintenanceHealthCheckSlice(
    bool Ran,
    bool BackedOff,
    FullTextIndexIntegrityState? State,
    int CheckedPathCount,
    int MismatchCount,
    int RepairedPathCount,
    string? Message);
