using PuddingFullTextIndex.Contracts;
using PuddingFullTextIndex.Infrastructure.Maintenance;

namespace PuddingHost.Hosting;

/// <summary>
/// S3d（2026-10-28）：把**已经实现、但从未接线**的全文索引局部维护器接进宿主生命周期。
///
/// <para>
/// 背景（实测）：此前 <c>FullTextIndex:Maintenance:Enabled=true</c> 没有任何消费者 ——
/// <see cref="LuceneFullTextIndexMaintenance"/> 在全仓只被组件测试 <c>new</c> 过，宿主启动路径里
/// 既没有注册也没有 <c>StartAsync</c>。后果是配置打开后索引仍然永久冻结：新建文件 20 s 内
/// 观察不到任何一次索引提交（三态验收脚本报 <c>INCONCLUSIVE</c>）。
/// </para>
///
/// <para>
/// **默认关闭即零副作用**（组件硬不变量）：<see cref="MaintenanceOptions.Enabled"/> 为 false 时本服务
/// 在 <see cref="StartAsync"/> 里立即返回 —— 不规划 scope、不校验目录存在性、不碰索引根、
/// 不建 watcher、不起线程。三种「不启动」的情形都记**可区分**的日志，避免又变成静默：
/// </para>
/// <list type="number">
/// <item><description>未启用 → Information（这是正常配置，不是故障）；</description></item>
/// <item><description>配置非法 → Error（带逐条违规项，fail-closed 不启动）；</description></item>
/// <item><description>启用但没有任何可用 scope → Error（带逐条拒绝原因，不静默降级成「跑一个空维护器」）。</description></item>
/// </list>
///
/// <para>
/// 启动失败**不抛出**：抛出去会让整台宿主起不来，而增量维护是背景渐进路径，缺失只影响
/// 「索引新鲜度」，不该升级成「服务不可用」。因此这里 catch 后记 Error，运维可从日志定位。
/// </para>
/// </summary>
public sealed class FullTextIndexMaintenanceHostedService : IHostedService
{
    private readonly IFullTextIndexMaintenance _maintenance;
    private readonly MaintenanceOptions _options;
    private readonly ILogger<FullTextIndexMaintenanceHostedService> _logger;

    public FullTextIndexMaintenanceHostedService(
        IFullTextIndexMaintenance maintenance,
        MaintenanceOptions options,
        ILogger<FullTextIndexMaintenanceHostedService> logger)
    {
        _maintenance = maintenance ?? throw new ArgumentNullException(nameof(maintenance));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_options.Enabled)
        {
            _logger.LogInformation(
                "[FullTextMaintenance] disabled (FullTextIndex:Maintenance:Enabled=false); "
                + "no scope resolution, no watcher, no thread. Zero side effects.");
            return;
        }

        var validation = MaintenanceOptions.Validate(_options);
        if (!validation.IsValid)
        {
            // fail-closed：不「纠正后继续」。纠正会让配置与生效行为不一致，是最难排查的一类事故。
            foreach (var violation in validation.Violations)
            {
                _logger.LogError(
                    "[FullTextMaintenance] invalid config {Option}={Value}: {Message}",
                    violation.Option, violation.Value, violation.Message);
            }

            _logger.LogError("[FullTextMaintenance] maintenance NOT started (fail-closed).");
            return;
        }

        var plan = FullTextMaintenanceScopePlanner.Create(_options);

        foreach (var rejection in plan.Rejections)
            _logger.LogWarning("[FullTextMaintenance] scope rejected: {Rejection}", rejection);

        if (plan.Scopes.Count == 0)
        {
            // 启用但一个都没解析出来：这是配置错误（不是「没有配置」），必须报错而不是静默空转。
            _logger.LogError(
                "[FullTextMaintenance] enabled but no usable scope after normalization "
                + "({Rejected} rejection(s)); maintenance NOT started.",
                plan.Rejections.Count);
            return;
        }

        try
        {
            await _maintenance.StartAsync(plan.Scopes, cancellationToken).ConfigureAwait(false);

            _logger.LogInformation(
                "[FullTextMaintenance] started with {ScopeCount} scope(s): {Scopes}",
                plan.Scopes.Count,
                string.Join(" | ", plan.Scopes.Select(s => s.RootPath)));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "[FullTextMaintenance] start failed; incremental index maintenance is NOT running "
                + "(the index will stay frozen until the next successful start).");
        }
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => _maintenance.StopAsync(cancellationToken);
}
