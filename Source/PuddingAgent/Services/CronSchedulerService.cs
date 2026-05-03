using PuddingCode.Platform;
using PuddingRuntime.Services;

namespace PuddingAgent.Services;

/// <summary>
/// Cron 定时任务调度服务 — 基于 Cron 表达式触发定时任务，
/// 将预设 prompt 发送给 Agent Runtime 执行。
/// 
/// 支持 5 字段 Cron 格式：分 时 日 月 周
/// 支持通配符 * 和数字，不支持 / 步长和 , 列表。
/// </summary>
public sealed class CronSchedulerService : BackgroundService
{
    private readonly IConfiguration _configuration;
    private readonly AgentExecutionService _executionService;
    private readonly ILogger<CronSchedulerService> _logger;
    private readonly List<CronJob> _jobs = [];

    public CronSchedulerService(
        IConfiguration configuration,
        AgentExecutionService executionService,
        ILogger<CronSchedulerService> logger)
    {
        _configuration = configuration;
        _executionService = executionService;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        LoadJobs();

        if (_jobs.Count == 0)
        {
            _logger.LogInformation("[Cron] 无定时任务配置，调度器空闲。");
            return;
        }

        _logger.LogInformation("[Cron] 调度器启动，共 {Count} 个任务。", _jobs.Count);
        foreach (var job in _jobs)
            _logger.LogInformation("[Cron]   - {Name}: {Cron} → \"{Prompt}\"", job.Name, job.Cron, job.Prompt);

        // 主循环：每分钟检查一次
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var now = DateTime.Now;
                foreach (var job in _jobs)
                {
                    if (stoppingToken.IsCancellationRequested)
                        break;

                    if (ShouldFire(job, now))
                    {
                        _ = Task.Run(() => FireJobAsync(job, stoppingToken), stoppingToken);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Cron] 调度循环异常");
            }

            // 等待到下一分钟的 0 秒
            var delay = 60 - DateTime.Now.Second;
            if (delay > 0)
                await Task.Delay(TimeSpan.FromSeconds(delay), stoppingToken);
        }
    }

    private void LoadJobs()
    {
        var section = _configuration.GetSection("CronJobs");
        var jobs = section.Get<List<CronJobConfig>>();
        if (jobs == null) return;

        foreach (var cfg in jobs)
        {
            if (string.IsNullOrWhiteSpace(cfg.Name) || string.IsNullOrWhiteSpace(cfg.Cron))
            {
                _logger.LogWarning("[Cron] 跳过无效配置: Name={Name} Cron={Cron}", cfg.Name, cfg.Cron);
                continue;
            }

            var fields = cfg.Cron.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length != 5)
            {
                _logger.LogWarning("[Cron] 跳过无效 Cron 表达式（需要 5 字段）: {Name} → \"{Cron}\"", cfg.Name, cfg.Cron);
                continue;
            }

            _jobs.Add(new CronJob
            {
                Name = cfg.Name,
                Cron = cfg.Cron,
                Prompt = cfg.Prompt ?? "",
                Minute = ParseField(fields[0], 0, 59),
                Hour = ParseField(fields[1], 0, 23),
                DayOfMonth = ParseField(fields[2], 1, 31),
                Month = ParseField(fields[3], 1, 12),
                DayOfWeek = ParseField(fields[4], 0, 6),
            });
        }
    }

    /// <summary>
    /// 判断当前时间是否匹配 Cron 表达式。
    /// 简单比较：每个字段检查当前值是否在允许集合中。
    /// </summary>
    private static bool ShouldFire(CronJob job, DateTime now)
    {
        // 防抖：如果这一分钟已经触发过，跳过
        var thisMinute = new DateTime(now.Year, now.Month, now.Day, now.Hour, now.Minute, 0);
        if (job.LastFiredMinute.HasValue && job.LastFiredMinute.Value >= thisMinute)
            return false;

        return job.Minute.Contains(now.Minute)
            && job.Hour.Contains(now.Hour)
            && job.DayOfMonth.Contains(now.Day)
            && job.Month.Contains(now.Month)
            && job.DayOfWeek.Contains((int)now.DayOfWeek);
    }

    private async Task FireJobAsync(CronJob job, CancellationToken ct)
    {
        job.LastFiredMinute = new DateTime(DateTime.Now.Year, DateTime.Now.Month,
            DateTime.Now.Day, DateTime.Now.Hour, DateTime.Now.Minute, 0);

        _logger.LogInformation("[Cron] 触发任务: {Name} prompt=\"{Prompt}\"", job.Name, job.Prompt);

        try
        {
            var sessionId = $"cron-{job.Name}-{DateTime.Now:yyyyMMddHHmm}";
            var dispatchRequest = new RuntimeDispatchRequest
            {
                SessionId = sessionId,
                WorkspaceId = "default",
                AgentTemplateId = "workspace-service-agent",
                MessageText = job.Prompt,
            };

            var result = await _executionService.ExecuteAsync(dispatchRequest, ct);

            _logger.LogInformation(
                "[Cron] 任务完成: {Name} session={SessionId} success={Success} reply=\"{Reply}\"",
                job.Name, sessionId, result.IsSuccess,
                (result.ReplyText ?? result.ErrorMessage ?? "")[..Math.Min(200, (result.ReplyText ?? result.ErrorMessage ?? "").Length)]);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Cron] 任务执行失败: {Name}", job.Name);
        }
    }

    /// <summary>
    /// 解析单个 Cron 字段，支持 * 和数字。返回该字段允许值的集合。
    /// "*" 返回 null 表示匹配所有值（调用方用完整集合替换）。
    /// </summary>
    private static HashSet<int> ParseField(string field, int min, int max)
    {
        if (field == "*")
        {
            var all = new HashSet<int>();
            for (var i = min; i <= max; i++)
                all.Add(i);
            return all;
        }

        var values = new HashSet<int>();
        foreach (var part in field.Split(','))
        {
            if (int.TryParse(part.Trim(), out var num) && num >= min && num <= max)
                values.Add(num);
        }
        return values;
    }

    private sealed class CronJob
    {
        public string Name { get; init; } = "";
        public string Cron { get; init; } = "";
        public string Prompt { get; init; } = "";
        public HashSet<int> Minute { get; init; } = [];
        public HashSet<int> Hour { get; init; } = [];
        public HashSet<int> DayOfMonth { get; init; } = [];
        public HashSet<int> Month { get; init; } = [];
        public HashSet<int> DayOfWeek { get; init; } = [];
        public DateTime? LastFiredMinute { get; set; }
    }

    private sealed class CronJobConfig
    {
        public string? Name { get; set; }
        public string? Cron { get; set; }
        public string? Prompt { get; set; }
    }
}
