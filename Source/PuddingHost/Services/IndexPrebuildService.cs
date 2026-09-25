using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PuddingFullTextIndex;
using PuddingFullTextIndex.Contracts;
using PuddingHost.Hosting;

namespace PuddingAgent.Services;

/// <summary>
/// 应用启动时在后台按**配置显式列出的 scope** 供给（预建）Lucene 全文索引。
/// <para>
/// 契约（U4-7 R4 + S5 R1/R3/R4/R5，硬约束）：
/// <list type="bullet">
/// <item><c>StartAsync</c> **永不阻塞宿主启动**（长活在启动路径之外，照
/// <c>CodeIndexMaintenanceHostedService</c> 的精神）。</item>
/// <item>**默认配置**（<c>system.json</c> 无 <c>FullTextIndex</c> 节 / <c>Enabled=false</c>）下
/// <c>StartAsync</c> 立即返回，**不建索引、零索引 I/O、不记 Error**，
/// 且**连供给组合都不构造**（不解析 scope、不碰索引根）。</item>
/// <item>校验不过 ⇒ fail-closed：记 Error 说清原因，然后**什么都不做**（绝不静默空转、绝不部分生效）。</item>
/// <item>**写路径只走协调器**（S5 R1）：向 <c>IFullTextIndexSupplyCoordinator</c> 提交 +
/// 轮询 <c>GetStatusAsync</c> 到终态（有超时上限），本服务**不再**直接调用
/// <c>IFullTextSearchEngine.BuildIndexAsync</c> —— 租约、预算硬限、staging 与原子切换
/// 都在组件内完成。</item>
/// </list>
/// </para>
/// <para>
/// 历史缺陷：
/// <list type="number">
/// <item>（U4-7 §1）旧实现用 <c>Directory.GetCurrentDirectory()</c> 当索引目标（本机 = 运行时
/// bin 目录），与其自身注释「工作区根目录」不符。现在目标**只能来自配置**
/// （<see cref="FullTextIndexSupplyOptions.Scopes"/>，相对项以显式声明的
/// <see cref="FullTextIndexSupplyOptions.WorkspaceRoot"/> 为基准），**不再读 CWD**。</item>
/// <item>（S5）旧实现绕过协调器直写 live 索引（无租约、无预算、无暂存、无原子切换），
/// 且用**索引根目录**的 mtime 判断所有 scope 的新鲜度 —— 建出一个 scope 就会让其余 scope
/// 全部被误判为「新鲜」。两处都已修：写路径改走协调器，新鲜度改为 per-scope
/// （见 <see cref="IFullTextIndexSupplyComposition.LiveIndexLastWriteUtc"/>）。</item>
/// </list>
/// </para>
/// </summary>
public sealed class IndexPrebuildService : IHostedService
{
    /// <summary>
    /// 预建开始前的默认延迟：先让 Kestrel 绑定端口（启动即 fire-and-forget 会在 THREAD_POOL 有限时饥饿绑定）。
    /// </summary>
    public static readonly TimeSpan DefaultStartupDelay = TimeSpan.FromSeconds(10);

    /// <summary>
    /// 轮询 job 状态的默认间隔（终态判定的粒度，不参与业务判定）。
    /// 取 250ms：对秒级~分钟级构建足够灵敏，又不至于把 <c>GetStatusAsync</c> 打成忙等。
    /// </summary>
    public static readonly TimeSpan DefaultStatusPollInterval = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// 单个 scope 等待供给 job 到**终态**的超时上限（R1 明确要求「必须有超时上限」）。
    /// 超时**不取消** job（组件仍在后台跑完），只是本服务不再观测它、并如实记一条超时日志；
    /// 一个 scope 超时不影响后续 scope。
    /// </summary>
    public static readonly TimeSpan DefaultBuildWaitTimeout = TimeSpan.FromMinutes(30);

    /// <summary>请求方标识（组件侧诊断字段，**不参与任何判定**）。</summary>
    private const string RequestedBy = "host.index-prebuild";

    /// <summary>
    /// 预建开始前的延迟。生产用 <see cref="DefaultStartupDelay"/>；单测置 <c>TimeSpan.Zero</c> 以便在毫秒级
    /// 观测"后台路径到底做没做"（否则每个断言都要等 10 秒）。**只影响 Enabled=true 的路径**，
    /// 默认关闭时本属性根本不参与（<see cref="StartAsync"/> 立即返回）。
    /// </summary>
    public TimeSpan StartupDelay { get; init; } = DefaultStartupDelay;

    /// <summary>轮询间隔（测试可调小以在毫秒级观测轮询与超时）。见 <see cref="DefaultStatusPollInterval"/>。</summary>
    public TimeSpan StatusPollInterval { get; init; } = DefaultStatusPollInterval;

    /// <summary>单 scope 等待终态的超时上限（测试可调小以证明超时确实有界）。见 <see cref="DefaultBuildWaitTimeout"/>。</summary>
    public TimeSpan BuildWaitTimeout { get; init; } = DefaultBuildWaitTimeout;

    private readonly IFullTextSearchEngine _searchEngine;
    private readonly IOptions<FullTextIndexSupplyOptions> _supplyOptions;
    private readonly IFullTextIndexSupplyCompositionFactory _compositionFactory;
    private readonly ILogger<IndexPrebuildService> _logger;

    /// <summary>Creates the prebuild service.</summary>
    /// <param name="searchEngine">
    /// 全文索引引擎（生产实现为 Lucene）。**只用于只读判定**（<c>HasIndex</c>）与
    /// 「live 索引目录在哪」的映射；写索引一律经协调器（本服务不得再调用 <c>BuildIndexAsync</c>）。
    /// </param>
    /// <param name="supplyOptions">供给参数（Data 目录 <c>system.json</c> 的 <c>FullTextIndex</c> 节）。</param>
    /// <param name="compositionFactory">供给组合的惰性工厂（默认关闭时**永不被调用**）。</param>
    /// <param name="logger">Logger.</param>
    public IndexPrebuildService(
        IFullTextSearchEngine searchEngine,
        IOptions<FullTextIndexSupplyOptions> supplyOptions,
        IFullTextIndexSupplyCompositionFactory compositionFactory,
        ILogger<IndexPrebuildService> logger)
    {
        _searchEngine = searchEngine;
        _supplyOptions = supplyOptions;
        _compositionFactory = compositionFactory;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken ct)
    {
        var options = _supplyOptions.Value;
        var resolution = FullTextIndexSupplyResolver.Resolve(options);

        // 默认路径（Enabled=false）：立即返回，零索引 I/O，且**不记 Error** —— 「没开」不是错误。
        // 这里同时是「零调用」的边界：不解析 scope、不构造供给组合、不碰索引根。
        if (resolution.IsNoOp)
            return Task.CompletedTask;

        if (!resolution.Succeeded)
        {
            // fail-closed：配置开着但校验不过 ⇒ 大声拒绝，且什么都不做。
            // 禁止「静默空转」（会让「开了但不工作」不可见）与「部分生效」（会让配置错了不可见）。
            _logger.LogError(
                "[IndexPrebuild] Full-text index supply configuration rejected; nothing will be indexed. {Reasons}",
                resolution.Describe());
            return Task.CompletedTask;
        }

        _logger.LogInformation(
            "[IndexPrebuild] Full-text index supply enabled; {Count} scope(s) accepted: {Scopes}",
            resolution.AcceptedScopes.Count,
            string.Join(", ", resolution.AcceptedScopes));

        // 启动路径之外的后台作业；StartAsync 立即返回。
        _ = Task.Run(() => PrebuildAsync(resolution, ct), ct);
        return Task.CompletedTask;
    }

    private async Task PrebuildAsync(FullTextIndexSupplyResolution resolution, CancellationToken ct)
    {
        try
        {
            await Task.Delay(StartupDelay, ct).ConfigureAwait(false);

            var options = _supplyOptions.Value;
            var minRebuildInterval = options.MinRebuildInterval;

            // 供给组合**只在这里**构造：默认关闭 / 配置被拒时根本走不到这一行（R4）。
            var composition = _compositionFactory.Create(options);

            _logger.LogInformation(
                "[IndexPrebuild] Supply composition ready: budget {Budget} bytes and min rebuild interval " +
                "{Interval} (both from configuration); staging supply {Staging}; {Count} scope(s) to check",
                composition.ComponentOptions.DefaultBudgetBytes,
                composition.ComponentOptions.MinRebuildInterval,
                composition.ComponentOptions.UseStaging ? "enabled" : "disabled",
                resolution.AcceptedScopes.Count);

            foreach (var scope in resolution.AcceptedScopes)
            {
                ct.ThrowIfCancellationRequested();
                await PrebuildScopeAsync(composition, scope, minRebuildInterval, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("[IndexPrebuild] Index build cancelled during shutdown");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[IndexPrebuild] Index build error (non-fatal)");
        }
    }

    /// <summary>
    /// 单个 scope：per-scope 新鲜度判定 → 提交给协调器 → 轮询至终态。**每个 scope 最多提交一次**
    /// （R5：禁止重试风暴），任何一类非成功结果都如实记录后继续下一个 scope。
    /// </summary>
    private async Task PrebuildScopeAsync(
        IFullTextIndexSupplyComposition composition,
        string scope,
        TimeSpan minRebuildInterval,
        CancellationToken ct)
    {
        // R3：新鲜度只看**这个 scope 自己**的 live 索引目录 mtime；读不到 ⇒ MinValue ⇒ 判需重建。
        var indexLastWriteUtc = composition.LiveIndexLastWriteUtc(scope);
        var hasIndex = _searchEngine.HasIndex(scope);

        if (!IndexPrebuildFreshness.ShouldRebuild(
                hasIndex, indexLastWriteUtc, DateTimeOffset.UtcNow, minRebuildInterval))
        {
            _logger.LogInformation(
                "[IndexPrebuild] Index for {Scope} is fresh (scope index dir mtime {LastWrite:o}, " +
                "min rebuild interval {Interval}), skipping",
                scope, indexLastWriteUtc, minRebuildInterval);
            return;
        }

        _logger.LogInformation(
            "[IndexPrebuild] Submitting index supply for {Scope} to the coordinator " +
            "(hasIndex {HasIndex}, scope index dir mtime {LastWrite:o})...",
            scope, hasIndex, indexLastWriteUtc);

        SupplyRequestOutcome outcome;
        try
        {
            outcome = await composition.Coordinator
                .BuildAsync(new SupplyScopeRequest(scope, requestedBy: RequestedBy), ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // 提交本身失败：如实记录，且**不重试**这个 scope（R5）。
            _logger.LogWarning(
                ex,
                "[IndexPrebuild] Supply submission failed for {Scope}; this scope is not retried",
                scope);
            return;
        }

        var detail = outcome.Scopes.Count == 1 ? outcome.Scopes[0] : null;

        switch (outcome.Outcome)
        {
            case SupplyOutcome.Rejected:
                // 含 OverBudget / scope 非法：**没有任何 job 被创建**，live 一字节未动。
                _logger.LogWarning(
                    "[IndexPrebuild] Supply rejected for {Scope} (no job created, nothing written): {Reason}",
                    scope,
                    outcome.Reason ?? detail?.Reason ?? "(组件未给出原因)");
                return;

            case SupplyOutcome.Busy:
                // 跨进程租约被别的 owner 占住：不构建、不写索引、不重试。
                _logger.LogWarning(
                    "[IndexPrebuild] Supply busy for {Scope}: another owner holds the scope lease " +
                    "({Holder}); no job created, nothing written, this scope is not retried",
                    scope,
                    DescribeHolder(outcome.Holder ?? detail?.Holder));
                return;

            default:
                var jobId = outcome.JobId ?? detail?.JobId;
                if (string.IsNullOrWhiteSpace(jobId))
                {
                    _logger.LogWarning(
                        "[IndexPrebuild] Supply {Outcome} for {Scope} returned no jobId; the outcome cannot be " +
                        "observed and this scope is not retried",
                        outcome.Outcome,
                        scope);
                    return;
                }

                _logger.LogInformation(
                    "[IndexPrebuild] Supply {Outcome} for {Scope}: job {JobId}{Reason}",
                    outcome.Outcome,
                    scope,
                    jobId,
                    string.IsNullOrWhiteSpace(detail?.Reason) ? string.Empty : $"（{detail!.Reason}）");

                await AwaitTerminalAsync(composition.Coordinator, scope, jobId, ct).ConfigureAwait(false);
                return;
        }
    }

    /// <summary>
    /// 轮询 job 到终态（<c>Succeeded|Failed|Cancelled</c>），带**有界**超时（R1）。
    /// 超时只停止观测，不取消 job（组件的构建不受调用方超时腰斩）。
    /// </summary>
    private async Task AwaitTerminalAsync(
        IFullTextIndexSupplyCoordinator coordinator,
        string scope,
        string jobId,
        CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow + BuildWaitTimeout;

        while (true)
        {
            SupplyJobStatus? status;
            try
            {
                status = await coordinator.GetStatusAsync(jobId, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "[IndexPrebuild] Status query failed for supply job {JobId} ({Scope}); the job keeps running " +
                    "in the component but this scope is no longer observed",
                    jobId,
                    scope);
                return;
            }

            if (status is null)
            {
                // 未知 job（被淘汰 / 从未登记）：如实记录，不猜、不重试。
                _logger.LogWarning(
                    "[IndexPrebuild] Supply job {JobId} ({Scope}) is unknown to the coordinator; " +
                    "the outcome cannot be verified",
                    jobId,
                    scope);
                return;
            }

            if (IsTerminal(status.State))
            {
                ReportTerminal(scope, status);
                return;
            }

            if (DateTimeOffset.UtcNow >= deadline)
            {
                // 超时如实记录（带 jobId 与最后状态），并继续下一个 scope。
                _logger.LogWarning(
                    "[IndexPrebuild] Timed out after {Timeout} waiting for supply job {JobId} ({Scope}) to reach a " +
                    "terminal state; last state {State}/{Phase}. The job keeps running in the component and this " +
                    "scope is not retried",
                    BuildWaitTimeout,
                    jobId,
                    scope,
                    status.State,
                    status.Phase);
                return;
            }

            await Task.Delay(StatusPollInterval, ct).ConfigureAwait(false);
        }
    }

    private static bool IsTerminal(SupplyJobState state) =>
        state is SupplyJobState.Succeeded or SupplyJobState.Failed or SupplyJobState.Cancelled;

    /// <summary>
    /// 终态如实上报（R5）：成功 / 取消 / 失败逐类记录，且**原因原文照登** ——
    /// <c>OverBudget</c>、<c>RolledBack</c>、切换失败等切换口径都在组件的 job 消息里
    /// （<c>SupplySwapReport.Describe()</c> 折进了终态消息），宿主不再自己编一套分类。
    /// </summary>
    private void ReportTerminal(string scope, SupplyJobStatus status)
    {
        switch (status.State)
        {
            case SupplyJobState.Succeeded:
                _logger.LogInformation(
                    "[IndexPrebuild] Supply job {JobId} for {Scope} succeeded: {Message}",
                    status.JobId, scope, status.Message);
                break;

            case SupplyJobState.Cancelled:
                _logger.LogWarning(
                    "[IndexPrebuild] Supply job {JobId} for {Scope} was cancelled: {Message}",
                    status.JobId, scope, status.Message);
                break;

            default:
                // 失败：原因（含 OverBudget / RolledBack / 切换失败 / 异常）逐字来自组件。
                _logger.LogWarning(
                    "[IndexPrebuild] Supply job {JobId} for {Scope} failed (phase {Phase}): {Message}",
                    status.JobId, scope, status.Phase, status.Message);
                break;
        }
    }

    private static string DescribeHolder(SupplyLeaseHolder? holder) =>
        holder is null
            ? "(占用者未知)"
            : $"{holder.OwnerId}, PID {holder.ProcessId} @ {holder.MachineName}, " +
              $"started {holder.StartedAtUtc:o}, heartbeat {holder.HeartbeatUtc:o}, job {holder.JobId ?? "(none)"}";

    /// <inheritdoc />
    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}
