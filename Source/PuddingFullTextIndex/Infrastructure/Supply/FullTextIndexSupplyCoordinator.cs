using System.Collections.Concurrent;
using PuddingFullTextIndex.Contracts;

namespace PuddingFullTextIndex.Infrastructure.Supply;

/// <summary>
/// 全文索引「供给协调器」——组件内<b>唯一</b>的供给入口实现。
/// <para>
/// 职责（A1 切片）：scope 规范化 → 同任务幂等合并 → 进程内单写者（每 scope 一把闸门）→
/// Windows 跨进程文件租约 → job 状态机 → Plan 干跑估算（零写入）。
/// </para>
/// <para>
/// 并发模型：<br/>
/// ① <b>进程内单写者</b>：每 scope 一个 <see cref="SemaphoreSlim"/>；同 scope 的并发提交在闸门内串行，
/// 后到者看到已有 <c>Queued|Running</c> job ⇒ 立即 <see cref="SupplyOutcome.Merged"/>（同一个 JobId），
/// 因此 N 次并发请求只执行一次构建。<br/>
/// ② <b>跨进程互斥</b>：取租约成功才建 job；失败 ⇒ <see cref="SupplyOutcome.Busy"/>（带 owner/PID/开始时间），
/// 不构建、不写索引。<br/>
/// ③ 构建在后台任务里执行：<c>Queued → Running → Succeeded|Failed|Cancelled</c>；
/// 无论成功失败取消，租约都在 finally 中释放（不得泄漏）。
/// </para>
/// <para>
/// ⚠️ A1 边界：不接宿主、不调真实 Lucene（由注入的 <see cref="IFullTextIndexBuilder"/> 决定）、
/// 不做 staging / 预算硬限 / 原子切换（A2）；<c>PlanAsync</c> 保证零写入。
/// </para>
/// </summary>
public sealed class FullTextIndexSupplyCoordinator : IFullTextIndexSupplyCoordinator
{
    private readonly IFullTextIndexSupplyInventory _inventory;
    private readonly IFullTextIndexBuilder _builder;
    private readonly IFullTextSupplyLease _lease;
    private readonly SupplyCoordinatorOptions _options;
    private readonly SupplyLeaseOwner _owner;
    private readonly SupplyJobStore _jobs;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _scopeGates = new(StringComparer.OrdinalIgnoreCase);

    public FullTextIndexSupplyCoordinator(
        IFullTextIndexSupplyInventory inventory,
        IFullTextIndexBuilder builder,
        IFullTextSupplyLease lease,
        SupplyCoordinatorOptions? options = null)
    {
        _inventory = inventory ?? throw new ArgumentNullException(nameof(inventory));
        _builder = builder ?? throw new ArgumentNullException(nameof(builder));
        _lease = lease ?? throw new ArgumentNullException(nameof(lease));
        _options = options ?? new SupplyCoordinatorOptions();

        ValidateOptions(_options);

        _owner = new SupplyLeaseOwner(_options.OwnerId, Environment.ProcessId, Environment.MachineName);
        _jobs = new SupplyJobStore(_options.MaxRetainedJobs, _options.MaxRetainedTransitionRejections);
    }

    /// <summary>非法状态转换的拒绝台账（含「对终态 job 取消」这类被拒请求）——用于证明「不得静默」。</summary>
    internal IReadOnlyList<string> TransitionRejections => _jobs.TransitionRejections;

    /// <summary>后台执行任务（测试/诊断用；不参与业务判定）。</summary>
    internal Task? GetWorkerTask(string jobId) => _jobs.Find(jobId)?.Worker;

    public async Task<SupplyPlanResult> PlanAsync(SupplyScopeRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var budget = ResolveBudget(request);

        // live 侧用量只能由**真正持有索引根**的 builder（staged 供给）实测：
        // 协调器不持有 FullTextIndexOptions（它的组合根 = CLI 的 SupplyCliHost，属本切片红线不可改，
        // 无法给它补一个索引根参数）。直写 builder 不参与预算（A1 语义：Plan 只做报表），此时按 0 计。
        // ⚠️ 判定函数与构建侧同为 SupplyBudgetCalculator.Fits（R2：不得「报表说行、构建说不行」）。
        var liveIndexBytes = _builder is IFullTextIndexLiveUsage usage ? usage.MeasureLiveIndexBytes() : 0;

        var normalization = SupplyScopeNormalizer.Normalize(request.RootPaths);
        var planScopes = new List<SupplyPlanScope>(normalization.Accepted.Count);

        foreach (var scope in normalization.Accepted)
        {
            ct.ThrowIfCancellationRequested();

            // ⚠️ 这里只读语料（枚举 + 取文件长度）：不建目录、不写租约、不写任何文件。
            var inventory = await _inventory.MeasureAsync(scope.RootPath, ct).ConfigureAwait(false);
            var predictedIndexBytes = SupplyIndexSizeEstimator.PredictIndexBytes(inventory.TotalBytes);

            planScopes.Add(new SupplyPlanScope(
                scope.ScopeKey,
                scope.RootPath,
                inventory.FileCount,
                inventory.TotalBytes,
                predictedIndexBytes,
                budget,
                WithinBudget: SupplyBudgetCalculator.Fits(liveIndexBytes, predictedIndexBytes, budget),
                LiveIndexBytes: liveIndexBytes));
        }

        return new SupplyPlanResult(
            Accepted: normalization.Rejections.Count == 0 && planScopes.Count > 0,
            normalization.Rejections,
            planScopes,
            budget,
            SupplyIndexSizeEstimator.IndexToCorpusRatio);
    }

    public async Task<SupplyRequestOutcome> BuildAsync(SupplyScopeRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var normalization = SupplyScopeNormalizer.Normalize(request.RootPaths);
        if (normalization.Rejections.Count > 0)
        {
            var rejectionScopes = normalization.Rejections
                .Select(r => new SupplyScopeOutcome(r.Value, null, SupplyOutcome.Rejected, null, r.Message, null))
                .ToList();

            return new SupplyRequestOutcome(
                SupplyOutcome.Rejected,
                JobId: null,
                Reason: string.Join("；", normalization.Rejections.Select(r => r.Message)),
                Holder: null,
                rejectionScopes);
        }

        // 预算在受理时解析一次，并随 scope 载荷盖章（见 SupplyScope 的说明：builder 端口签名被 CLI 冻结）——
        // 保证「Plan 报表 / 预检 / 实测硬限」三处用的是同一个数值，不会各自解析出不同结果。
        var budget = ResolveBudget(request);

        // 不同 scope 互相独立（A6）：并发提交，各自在自己的闸门内串行；结果顺序与请求顺序一致。
        var submissions = normalization.Accepted.Select(scope => SubmitScopeAsync(scope, budget, ct)).ToArray();
        var outcomes = await Task.WhenAll(submissions).ConfigureAwait(false);

        return Aggregate(outcomes);
    }

    public Task<SupplyJobStatus?> GetStatusAsync(string jobId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(_jobs.Snapshot(jobId));
    }

    public Task<IReadOnlyList<SupplyJobStatus>> ListStatusAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(_jobs.SnapshotAll());
    }

    public Task<bool> CancelAsync(string jobId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);
        ct.ThrowIfCancellationRequested();

        var entry = _jobs.Find(jobId);
        if (entry is null)
        {
            _jobs.RecordRejection($"取消失败：未找到 job {jobId}（未知或被历史淘汰）。");
            return Task.FromResult(false);
        }

        if (SupplyJobStateMachine.IsTerminal(entry.State))
        {
            _jobs.RecordRejection(
                $"取消失败：job {jobId} 已处于终态 {entry.State}，拒绝重复流转（{SupplyJobStateMachine.DescribeIllegalTransition(entry.State, SupplyJobState.Cancelled)}）。");
            return Task.FromResult(false);
        }

        var cancelled = _jobs.TryTransition(
            entry,
            SupplyJobState.Cancelled,
            "已按 CancelAsync 请求取消；传给 builder 的取消令牌已触发。",
            DateTimeOffset.UtcNow);

        if (!cancelled)
            return Task.FromResult(false);

        // 真正把取消信号传到 builder（Running 中的构建会因此抛出 OperationCanceledException）。
        entry.Cancellation.Cancel();
        return Task.FromResult(true);
    }

    private async Task<SupplyScopeOutcome> SubmitScopeAsync(SupplyScope scope, long budgetBytes, CancellationToken ct)
    {
        var gate = _scopeGates.GetOrAdd(scope.ScopeKey, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);

        try
        {
            // ① 同任务幂等合并：同 scope 已有进行中的 job ⇒ 合并到同一个 JobId，不重复构建。
            var active = _jobs.FindActive(scope.ScopeKey);
            if (active is not null)
            {
                return new SupplyScopeOutcome(
                    scope.RootPath,
                    scope.ScopeKey,
                    SupplyOutcome.Merged,
                    active.JobId,
                    $"同 scope 已有进行中的 job {active.JobId}（{active.State}/{active.Phase}），本次合并、不重复构建。",
                    active.LeaseHolder);
            }

            // ② 未过期的成功终态同样合并（默认 12h，可注入），避免无谓的全量重建。
            var lastSucceeded = _jobs.FindLastSucceeded(scope.ScopeKey);
            if (lastSucceeded?.SucceededAt is { } succeededAt)
            {
                var age = DateTimeOffset.UtcNow - succeededAt;
                if (age < _options.MinRebuildInterval)
                {
                    return new SupplyScopeOutcome(
                        scope.RootPath,
                        scope.ScopeKey,
                        SupplyOutcome.Merged,
                        lastSucceeded.JobId,
                        $"同 scope 在 {age.TotalMinutes:F1} 分钟前成功构建（阈值 {_options.MinRebuildInterval}），本次合并、不重复构建。",
                        lastSucceeded.LeaseHolder);
                }
            }

            // ③ 跨进程租约：拿不到就 Busy —— 不构建、不写索引（也不创建 job）。
            var jobId = SupplyJobNaming.NewJobId();
            var acquired = await _lease.TryAcquireAsync(scope.ScopeKey, _owner, jobId, ct).ConfigureAwait(false);
            if (!acquired.Acquired || acquired.Lease is null)
            {
                return new SupplyScopeOutcome(
                    scope.RootPath,
                    scope.ScopeKey,
                    SupplyOutcome.Busy,
                    JobId: null,
                    Reason: $"scope 的跨进程租约不可用，本次不构建、不写索引：{DescribeHolder(acquired.Holder)} {acquired.Message}",
                    acquired.Holder);
            }

            var lease = acquired.Lease;
            var holder = new SupplyLeaseHolder(
                lease.Owner.OwnerId,
                lease.Owner.ProcessId,
                lease.Owner.MachineName,
                lease.StartedAtUtc,
                lease.HeartbeatUtc,
                lease.JobId,
                IsExpired: false,
                lease.TakeoverReason);

            // 本次 job 的载荷：生效预算 + job 标识（scope 身份字段不变；见 SupplyScope 的说明）。
            var jobScope = scope with { BudgetBytes = budgetBytes, JobId = jobId };

            var entry = _jobs.Create(jobScope, jobId, DateTimeOffset.UtcNow, holder);
            entry.Worker = Task.Run(() => RunJobAsync(entry, lease));

            var takeoverNote = lease.TakeoverReason is null ? string.Empty : $"；接管原因：{lease.TakeoverReason}";
            return new SupplyScopeOutcome(
                scope.RootPath,
                scope.ScopeKey,
                SupplyOutcome.Started,
                jobId,
                $"已受理并启动构建 job {jobId}（{_owner.OwnerId} 持有 scope 租约{takeoverNote}）。",
                holder);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task RunJobAsync(SupplyJobEntry entry, SupplyLease lease)
    {
        var ct = entry.Cancellation.Token;
        var renewLoop = RunLeaseRenewLoopAsync(entry);

        try
        {
            // Queued → Running。若 job 在 Queued 阶段已被取消，这里会被状态机拒绝 ⇒ 不执行构建。
            var started = _jobs.TryTransition(
                entry,
                SupplyJobState.Running,
                $"{lease.Owner.OwnerId} 取得 scope 租约，开始供给。",
                DateTimeOffset.UtcNow);
            if (!started)
                return;

            _jobs.SetPhase(entry, SupplyJobPhases.Discovering);
            var inventory = await _inventory.MeasureAsync(entry.Scope.RootPath, ct).ConfigureAwait(false);
            _jobs.SetInventory(entry, inventory.FileCount, inventory.TotalBytes);

            // 把清点到的语料字节回填进 job 载荷：staged 供给的**预检**（R2 第一道）用它预测索引体积，
            // 与 Plan 用同一个清点值 + 同一个系数 ⇒ 两条路线的预测值必然一致。
            _jobs.SetCorpusBytes(entry, inventory.TotalBytes);

            _jobs.SetPhase(entry, SupplyJobPhases.Building);
            var result = await _builder.BuildAsync(entry.Scope, ct).ConfigureAwait(false);

            _jobs.SetPhase(entry, SupplyJobPhases.Finalizing);

            // R6 可观察性：把切换口径（outcome / stagingBytes / liveBytesBefore / liveBytesAfter / budgetBytes）
            // 折进终态消息，让「为什么没成功」一眼可见，而不是只报一句 failed。
            var observation = result.Swap is null ? string.Empty : $"｜{result.Swap.Describe()}";

            if (result.Success)
            {
                _jobs.TryTransition(
                    entry,
                    SupplyJobState.Succeeded,
                    $"构建完成：{result.IndexedFileCount} 文件 / {result.TotalBytes} 字节 / {result.ElapsedMs} ms。{observation}",
                    DateTimeOffset.UtcNow);
            }
            else
            {
                _jobs.TryTransition(
                    entry,
                    SupplyJobState.Failed,
                    $"构建失败：{result.Error ?? "builder 返回 Success=false 但未给出原因"}{observation}",
                    DateTimeOffset.UtcNow);
            }
        }
        catch (OperationCanceledException) when (entry.Cancellation.IsCancellationRequested)
        {
            _jobs.TryTransition(
                entry,
                SupplyJobState.Cancelled,
                "已按取消请求终止；builder 收到的取消令牌已触发。",
                DateTimeOffset.UtcNow);
        }
        catch (Exception ex)
        {
            _jobs.TryTransition(
                entry,
                SupplyJobState.Failed,
                $"构建异常：{ex.GetType().Name}: {ex.Message}",
                DateTimeOffset.UtcNow);
        }
        finally
        {
            // 停止续期循环（避免成功后无限续期），随后**无论成败都必须释放租约**。
            try
            {
                entry.Cancellation.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // 未释放的 CTS 不会走到这里；防御性兜底，不改变语义。
            }

            await renewLoop.ConfigureAwait(false);

            try
            {
                await _lease.ReleaseAsync(entry.Scope.ScopeKey, _owner.OwnerId, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _jobs.SetMessage(
                    entry,
                    $"⚠️ 租约释放异常（需人工核对 .supply-leases）：{ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// job 运行期间的后台续期：长构建若不自刷心跳，会被其他进程按「过期」合法接管。
    /// 续期失败（例如已被接管）只记录消息、不中断构建 —— 硬护栏属 A2。
    /// </summary>
    private async Task RunLeaseRenewLoopAsync(SupplyJobEntry entry)
    {
        var ct = entry.Cancellation.Token;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(_options.LeaseRenewInterval, ct).ConfigureAwait(false);

                bool renewed;
                try
                {
                    renewed = await _lease.RenewAsync(entry.Scope.ScopeKey, _owner.OwnerId, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _jobs.SetMessage(entry, $"租约续期异常：{ex.GetType().Name}: {ex.Message}");
                    return;
                }

                if (!renewed)
                {
                    _jobs.SetMessage(
                        entry,
                        $"{_owner.OwnerId} 已不再持有 scope 租约（续期被拒，可能已被接管）；A1 只记录不中断构建。");
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 正常收尾。
        }
    }

    private static SupplyRequestOutcome Aggregate(IReadOnlyList<SupplyScopeOutcome> scopes)
    {
        if (scopes.Count == 0)
            return new SupplyRequestOutcome(SupplyOutcome.Rejected, null, "请求未包含任何 scope。", null, scopes);

        var rejected = scopes.Where(s => s.Outcome == SupplyOutcome.Rejected).ToList();
        if (rejected.Count > 0)
        {
            return new SupplyRequestOutcome(
                SupplyOutcome.Rejected,
                null,
                string.Join("；", rejected.Select(r => r.Reason)),
                rejected[0].Holder,
                scopes);
        }

        var busy = scopes.FirstOrDefault(s => s.Outcome == SupplyOutcome.Busy);
        if (busy is not null)
            return new SupplyRequestOutcome(SupplyOutcome.Busy, null, busy.Reason, busy.Holder, scopes);

        var distinctJobIds = scopes.Select(s => s.JobId).Distinct(StringComparer.Ordinal).ToList();
        var sharedJobId = distinctJobIds.Count == 1 ? distinctJobIds[0] : null;

        var started = scopes.FirstOrDefault(s => s.Outcome == SupplyOutcome.Started);
        if (started is not null)
            return new SupplyRequestOutcome(SupplyOutcome.Started, sharedJobId, started.Reason, null, scopes);

        return new SupplyRequestOutcome(SupplyOutcome.Merged, sharedJobId, scopes[0].Reason, null, scopes);
    }

    private long ResolveBudget(SupplyScopeRequest request)
    {
        var budget = request.BudgetBytes ?? _options.DefaultBudgetBytes;
        if (budget <= 0)
            throw new ArgumentOutOfRangeException(nameof(request), $"预算必须是正的字节数，收到 {budget}。");

        return budget;
    }

    private static string DescribeHolder(SupplyLeaseHolder? holder) =>
        holder is null
            ? "（占用者信息不可读）"
            : $"占用者 owner={holder.OwnerId} pid={holder.ProcessId} machine={holder.MachineName} startedAt={holder.StartedAtUtc:O} heartbeat={holder.HeartbeatUtc:O}";

    private static void ValidateOptions(SupplyCoordinatorOptions options)
    {
        if (options.DefaultBudgetBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(options), "DefaultBudgetBytes 必须为正。");

        if (options.MaxRetainedJobs < 1)
            throw new ArgumentOutOfRangeException(nameof(options), "MaxRetainedJobs 必须 ≥ 1。");

        if (options.MaxRetainedTransitionRejections < 1)
            throw new ArgumentOutOfRangeException(nameof(options), "MaxRetainedTransitionRejections 必须 ≥ 1。");

        if (options.MinRebuildInterval < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(options), "MinRebuildInterval 不能为负。");

        if (options.LeaseRenewInterval <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(options), "LeaseRenewInterval 必须为正。");

        if (options.StaleArtifactMaxAge <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(options), "StaleArtifactMaxAge 必须为正。");

        if (string.IsNullOrWhiteSpace(options.OwnerId))
            throw new ArgumentOutOfRangeException(nameof(options), "OwnerId 不能为空。");
    }
}
