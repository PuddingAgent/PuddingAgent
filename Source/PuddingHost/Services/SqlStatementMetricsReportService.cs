using PuddingPlatform.Data.Diagnostics;

namespace PuddingHost.Services;

/// <summary>
/// 定时把语句级统计的 Top N 打进日志。
/// </summary>
/// <remarks>
/// <para>
/// 为什么需要它：<see cref="SqlStatementMetrics"/> 只负责采集，采集到的数据如果没有出口，
/// 等于没有。用日志而不是新端点，是因为日志已经在跑、已经会被收集，加一个 HTTP 端点反而
/// 要动鉴权与路由面。
/// </para>
/// <para>
/// <b>只在开关打开时注册</b>（由 DI 决定），所以默认部署下这个循环根本不存在。
/// </para>
/// <para>
/// <b>统计是累计的，不按窗口重置</b>：重置会丢掉"到底哪条最频繁"这个最有价值的信息。
/// 因此报告头里的 window 写 <c>since-start</c>，而不是假装成一个滑动窗口。
/// </para>
/// </remarks>
internal sealed class SqlStatementMetricsReportService : BackgroundService
{
    /// <summary>默认报告间隔。</summary>
    internal static readonly TimeSpan DefaultInterval = TimeSpan.FromMinutes(1);

    /// <summary>默认报告条数。</summary>
    internal const int DefaultTop = 10;

    private readonly SqlStatementMetrics _metrics;
    private readonly ILogger<SqlStatementMetricsReportService> _logger;
    private readonly TimeSpan _interval;

    /// <summary>创建报告服务。</summary>
    public SqlStatementMetricsReportService(
        SqlStatementMetrics metrics,
        ILogger<SqlStatementMetricsReportService> logger)
        : this(metrics, logger, DefaultInterval)
    {
    }

    /// <summary>创建报告服务并指定间隔（测试用）。</summary>
    internal SqlStatementMetricsReportService(
        SqlStatementMetrics metrics,
        ILogger<SqlStatementMetricsReportService> logger,
        TimeSpan interval)
    {
        ArgumentNullException.ThrowIfNull(metrics);
        ArgumentNullException.ThrowIfNull(logger);

        _metrics = metrics;
        _logger = logger;
        _interval = interval;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // 先等一个周期再报第一次：启动瞬间统计必然是空的，报了只是噪音。
        using var timer = new PeriodicTimer(_interval);

        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                Emit();
            }
        }
        catch (OperationCanceledException)
        {
            // 正常停机。
        }
    }

    /// <summary>
    /// 输出一次报告。单独抽出为 internal，便于测试在不启动宿主的情况下验证行为。
    /// </summary>
    internal void Emit()
    {
        // 统计为空时也输出一行：这说明"开关开了但一条语句都没记到"，
        // 那本身就是需要知道的信息（比静默更诚实）。
        var text = SqlStatementMetricsReport.Format(_metrics, DefaultTop, windowLabel: "since-start");
        _logger.LogInformation("{SqlStatementMetrics}", text);
    }
}
