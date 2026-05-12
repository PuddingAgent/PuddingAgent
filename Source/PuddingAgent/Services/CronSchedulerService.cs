using PuddingCode.Abstractions;
using PuddingCode.Models;

namespace PuddingAgent.Services;

/// <summary>
/// Cron 定时任务调度服务 — 基于 Cron 表达式触发定时任务，
/// 通过 IInternalEventBus 发布 "cron.trigger" 事件，由 EventDispatcher 路由到 Agent 执行。
/// 
/// 支持 5 字段 Cron 格式：分 时 日 月 周
/// 支持通配符 * 和数字，不支持 / 步长。
/// 
/// ADR-016 V2: 不再直接调用 AgentExecutionService，改为事件驱动解耦。
/// </summary>
public sealed class CronSchedulerService : BackgroundService
{
    private readonly IConfiguration _configuration;
    private readonly IInternalEventBus _eventBus;
    private readonly ILogger<CronSchedulerService> _logger;
    private readonly List<CronJob> _jobs = [];

    public CronSchedulerService(
        IConfiguration configuration,
        IInternalEventBus eventBus,
        ILogger<CronSchedulerService> logger)
    {
        _configuration = configuration;
        _eventBus = eventBus;
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

            var isolation = ParseIsolation(cfg.Isolation);
            var priority = ParsePriority(cfg.Priority);

            _jobs.Add(new CronJob
            {
                Name = cfg.Name,
                Cron = cfg.Cron,
                Prompt = cfg.Prompt ?? "",
                WorkspaceId = cfg.WorkspaceId ?? "default",
                AgentId = cfg.AgentId,
                Isolation = isolation,
                Priority = priority,
                Minute = ParseField(fields[0], 0, 59),
                Hour = ParseField(fields[1], 0, 23),
                DayOfMonth = ParseField(fields[2], 1, 31),
                Month = ParseField(fields[3], 1, 12),
                DayOfWeek = ParseField(fields[4], 0, 6),
            });
        }
    }

    private static EventIsolationMode ParseIsolation(string? value) => value?.ToLowerInvariant() switch
    {
        "mainline" => EventIsolationMode.Mainline,
        _ => EventIsolationMode.Isolated,
    };

    private static EventPriorityLevel ParsePriority(string? value) => value?.ToLowerInvariant() switch
    {
        "urgent" => EventPriorityLevel.Urgent,
        "important" => EventPriorityLevel.Important,
        _ => EventPriorityLevel.Normal,
    };

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

        _logger.LogInformation("[Cron] 触发任务: {Name} prompt=\"{Prompt}\" isolation={Isolation} priority={Priority}",
            job.Name, job.Prompt, job.Isolation, job.Priority);

        try
        {
            var sessionId = $"cron-{job.Name}-{DateTime.Now:yyyyMMddHHmm}";
            var evt = new InternalEvent
            {
                Type = "cron.trigger",
                Priority = job.Priority,
                Isolation = job.Isolation,
                Source = new EventSource
                {
                    SourceType = "cron",
                    SourceId = job.Name,
                },
                SessionId = sessionId,
                WorkspaceId = job.WorkspaceId,
                AgentId = job.AgentId,
                Payload = new CronTriggerPayload
                {
                    Prompt = job.Prompt,
                    JobName = job.Name,
                },
                Metadata = new Dictionary<string, string>
                {
                    ["cron.expression"] = job.Cron,
                    ["cron.job_name"] = job.Name,
                },
            };

            await _eventBus.PublishAsync(evt, ct);

            _logger.LogInformation(
                "[Cron] 事件已发布: {Name} session={SessionId} isolation={Isolation}",
                job.Name, sessionId, job.Isolation);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Cron] 事件发布失败: {Name}", job.Name);
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

        /// <summary>目标 Workspace（默认 "default"）</summary>
        public string WorkspaceId { get; init; } = "default";

        /// <summary>目标 Agent ID（可选，不指定则工作区默认 Agent）</summary>
        public string? AgentId { get; init; }

        /// <summary>隔离模式（默认 Isolated，Cron 任务不污染主会话）</summary>
        public EventIsolationMode Isolation { get; init; } = EventIsolationMode.Isolated;

        /// <summary>优先级（默认 Normal）</summary>
        public EventPriorityLevel Priority { get; init; } = EventPriorityLevel.Normal;
    }

    private sealed class CronJobConfig
    {
        public string? Name { get; set; }
        public string? Cron { get; set; }
        public string? Prompt { get; set; }

        /// <summary>WorkspaceId（可选，默认 "default"）</summary>
        public string? WorkspaceId { get; set; }

        /// <summary>AgentId（可选）</summary>
        public string? AgentId { get; set; }

        /// <summary>隔离模式：mainline / isolated（默认 isolated）</summary>
        public string? Isolation { get; set; }

        /// <summary>优先级：normal / important / urgent（默认 normal）</summary>
        public string? Priority { get; set; }
    }

    /// <summary>
    /// Cron 触发事件负载。
    /// </summary>
    public sealed record CronTriggerPayload
    {
        public string Prompt { get; init; } = "";
        public string JobName { get; init; } = "";
    }
}
