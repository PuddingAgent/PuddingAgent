using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PuddingPlatform.Data;

namespace PuddingPlatform.Services;

/// <summary>
/// platform.db 数据保留期裁剪后台服务。
///
/// 背景：platform.db 已膨胀到数 GB；保留期配置（Retention）早已定义但裁剪代码缺失，
/// 现有 DiagnosticRetentionService 只覆盖遥测/上下文指标/运行活动，session_event_log 与
/// conversation_events 从未被裁剪。本服务补齐全部四张表的保留期清理。
///
/// 设计要点：
/// 1) 配置读取：优先读取顶层 "Retention" 节（每张表一个 { "RetentionDays": N }），
///    若该节未定义则回退到既有 "Diagnostics:Retention:Tables"（已包含全部四张表）。
///    表名/列名只允许来自内置白名单（防 SQL 注入，配置值只影响 RetentionDays）。
/// 2) 宿主服务模式照抄 DiagnosticRetentionService：ExecuteAsync 首句 Task.Yield()，
///    绝不阻塞宿主启动（BackgroundService.StartAsync 会同步执行到第一个未完成 await）。
/// 3) SQLite 不支持 DELETE...LIMIT，因此用
///        DELETE FROM t WHERE rowid IN (SELECT rowid FROM t WHERE ts列&lt;@cutoff LIMIT @batch)
///    循环到 affected&lt;batch，批间限速，尊重 CancellationToken，用 ExecuteSqlRaw 不加载实体。
/// 4) 时间戳列均为 "O" 格式（DateTimeOffset.UtcNow.ToString("O")，如 2026-08-12T11:33:34.4448190+00:00），
///    ISO-8601 字典序比较即时间序比较；cutoff 同样按 "O" 格式化。列名来自实体映射核实：
///      session_event_log.recorded_at          （SessionEventLogEntity）
///      telemetry_metric_events.occurred_at_utc（TelemetryMetricEventEntity）
///      runtime_activity.started_at_utc        （RuntimeActivityEntity）
///      conversation_events.committed_at       （ConversationEventEntity，写入时与 occurred_at 同值）
/// 5) ChatMessages 永不裁剪：chat_messages 不在白名单内。
/// 6) 每 6 小时运行一次（Retention:RunIntervalHours 可覆盖）；最后按配置 VACUUM
///    （Retention:Vacuum:Enabled，默认 true —— 裁剪后需要 VACUUM 才能归还磁盘空间）。
/// </summary>
public sealed class RetentionPruningService : BackgroundService
{
    private const string SectionName = "Retention";
    private const string FallbackSection = "Diagnostics:Retention";

    private readonly IConfiguration _configuration;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<RetentionPruningService> _logger;
    private readonly RetentionArchiveWriter _archiveWriter;

    /// <summary>
    /// 受支持表白名单：表名 → 时间戳列名（防注入；列名已按实体映射核实）。
    /// 不在白名单内的表（含 chat_messages）一律跳过并告警。
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> TableTimestampColumns =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["session_event_log"] = "recorded_at",
            ["telemetry_metric_events"] = "occurred_at_utc",
            ["runtime_activity"] = "started_at_utc",
            ["conversation_events"] = "committed_at",
        };

    /// <summary>
    /// DELETE 前需要归档的证据事件流表（append-only 恢复）。
    /// telemetry_metric_events / runtime_activity 是遥测非证据，本批保持现状（不归档、照旧 DELETE）。
    /// </summary>
    private static readonly IReadOnlySet<string> ArchiveTables =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "session_event_log",
            "conversation_events",
        };

    public RetentionPruningService(
        IConfiguration configuration,
        IServiceScopeFactory scopeFactory,
        ILogger<RetentionPruningService> logger,
        RetentionArchiveWriter archiveWriter)
    {
        _configuration = configuration;
        _scopeFactory = scopeFactory;
        _logger = logger;
        _archiveWriter = archiveWriter;
    }

    /// <summary>
    /// 配置读取链：优先顶层 "Retention" 节，缺省项回退到既有
    /// "Diagnostics:Retention"（仓库内已定义的完整配置），最后落到任务默认值。
    /// </summary>
    private T GetSetting<T>(string key, T defaultValue)
    {
        var primary = _configuration.GetValue<T?>($"{SectionName}:{key}");
        if (primary is not null)
            return primary;

        var fallback = _configuration.GetValue<T?>($"{FallbackSection}:{key}");
        return fallback is not null ? fallback : defaultValue;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // BackgroundService.StartAsync invokes ExecuteAsync synchronously until the
        // first incomplete await. Yield first so the retention sweep can never delay
        // WebApplication.StartAsync or the Desktop Ready signal.
        await Task.Yield();

        try
        {
            await RunLoopAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            _logger.LogInformation("[RetentionPruning] cancelled by host shutdown");
        }
        catch (Exception ex)
        {
            // 健康自证：裁剪失败不拖垮宿主，但必须留下错误日志。
            _logger.LogError(ex, "[RetentionPruning] service failed");
        }
    }

    /// <summary>
    /// 主循环：启动延迟后每 RunIntervalHours 跑一次裁剪。public 便于测试直调。
    /// </summary>
    public async Task RunLoopAsync(CancellationToken ct = default)
    {
        var startupDelaySeconds = Math.Max(0, GetSetting("StartupDelaySeconds", 60));
        if (startupDelaySeconds > 0)
            await Task.Delay(TimeSpan.FromSeconds(startupDelaySeconds), ct);

        var intervalHours = Math.Max(1, GetSetting("RunIntervalHours", 6));
        var interval = TimeSpan.FromHours(intervalHours);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await RunOnceAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[RetentionPruning] sweep failed; will retry next interval");
            }

            await Task.Delay(interval, ct);
        }
    }

    /// <summary>
    /// 单轮裁剪：逐表创建时间戳索引并按批删除过期行；最后按配置执行 VACUUM。
    /// public 便于测试直调。
    /// </summary>
    public async Task RunOnceAsync(CancellationToken ct = default)
    {
        var tables = ReadRetentionTables();
        if (tables.Count == 0)
        {
            _logger.LogInformation("[RetentionPruning] no retention tables configured — sweep no-op");
            return;
        }

        var batchSize = Math.Max(1, GetSetting("BatchSize", 5000));
        var batchDelayMs = Math.Max(0, GetSetting("BatchDelayMs", 100));
        var cutoffBase = DateTimeOffset.UtcNow;

        foreach (var (tableName, retentionDays) in tables)
        {
            ct.ThrowIfCancellationRequested();

            if (retentionDays <= 0)
            {
                _logger.LogDebug(
                    "[RetentionPruning] skip table={Table} retentionDays={RetentionDays}",
                    tableName, retentionDays);
                continue;
            }

            if (!TableTimestampColumns.TryGetValue(tableName, out var timestampColumn))
            {
                _logger.LogWarning(
                    "[RetentionPruning] table={Table} not in whitelist — skipped (ChatMessages 等永不裁剪)",
                    tableName);
                continue;
            }

            var cutoff = cutoffBase.AddDays(-retentionDays);
            await EnsureTimestampIndexAsync(tableName, timestampColumn, ct);
            await TrimTableAsync(tableName, timestampColumn, cutoff, batchSize, batchDelayMs, ct);
        }

        if (GetSetting("Vacuum:Enabled", true))
        {
            await VacuumAsync(ct);
        }
    }

    /// <summary>
    /// 读取保留期配置：优先顶层 "Retention" 节（task 规范形态），回退既有
    /// "Diagnostics:Retention:Tables"（仓库内已定义的完整四表配置）。
    /// </summary>
    private Dictionary<string, int> ReadRetentionTables()
    {
        var tables = new Dictionary<string, int>(StringComparer.Ordinal);

        var primaryChildren = _configuration.GetSection(SectionName).GetChildren().ToList();
        foreach (var child in primaryChildren)
        {
            var days = child.GetValue<int?>("RetentionDays");
            if (days is null)
                continue; // 跳过 RunIntervalHours/BatchSize/Vacuum 等非表配置项
            tables[child.Key] = days.Value;
        }

        if (tables.Count > 0)
            return tables;

        foreach (var child in _configuration.GetSection($"{FallbackSection}:Tables").GetChildren())
        {
            var days = child.GetValue<int?>("RetentionDays");
            if (days is null)
                continue;
            tables[child.Key] = days.Value;
        }

        return tables;
    }

    private async Task TrimTableAsync(
        string tableName,
        string timestampColumn,
        DateTimeOffset cutoff,
        int batchSize,
        int batchDelayMs,
        CancellationToken ct)
    {
        long totalDeleted = 0;
        int batches = 0;
        var cutoffString = cutoff.ToString("O");

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();

        var archivable = ArchiveTables.Contains(tableName);

        while (!ct.IsCancellationRequested)
        {
            int affected;

            if (archivable)
            {
                // 证据流表：先 SELECT 完整字段 → 归档（失败抛异常中止本批）→ 再按 rowid 精确删除
                affected = await ArchiveAndDeleteBatchAsync(db, tableName, cutoff, cutoffString, batchSize, ct);
            }
            else
            {
                var sql = $$"""
                    DELETE FROM "{{tableName}}" WHERE rowid IN (
                      SELECT rowid FROM "{{tableName}}" t
                      WHERE t."{{timestampColumn}}" < {0}
                      LIMIT {1}
                    )
                    """;

                affected = await db.Database.ExecuteSqlRawAsync(sql, cutoffString, batchSize);
            }

            totalDeleted += affected;
            batches++;

            if (affected < batchSize)
                break;

            if (batchDelayMs > 0)
                await Task.Delay(batchDelayMs, ct);
        }

        _logger.LogInformation(
            "[RetentionPruning] trimmed table={Table} cutoff={Cutoff:O} deleted={Deleted} batches={Batches}",
            tableName, cutoff, totalDeleted, batches);
    }

    /// <summary>
    /// 对证据流表（session_event_log / conversation_events）按批执行：
    /// SELECT 完整字段（时间戳早于 cutoff，ORDER BY rowid，LIMIT batch）→ 归档 → DELETE 该批精确 rowid。
    /// 归档写文件失败会抛异常（由 RunLoopAsync 捕获），对应批次绝不先删后归档。
    /// </summary>
    private async Task<int> ArchiveAndDeleteBatchAsync(
        PlatformDbContext db,
        string tableName,
        DateTimeOffset cutoff,
        string cutoffString,
        int batchSize,
        CancellationToken ct)
    {
        switch (tableName)
        {
            case "session_event_log":
            {
                var rows = await db.SessionEventLogs
                    .AsNoTracking()
                    .Where(e => string.Compare(e.RecordedAt, cutoffString) < 0)
                    .OrderBy(e => e.Id)
                    .Take(batchSize)
                    .ToListAsync(ct);

                if (rows.Count == 0)
                    return 0;

                await _archiveWriter.ArchiveBatchAsync(tableName, rows, cutoff, ct);

                var ids = rows.Select(r => r.Id).ToList();
                return await db.SessionEventLogs
                    .Where(e => ids.Contains(e.Id))
                    .ExecuteDeleteAsync(ct);
            }

            case "conversation_events":
            {
                var rows = await db.ConversationEvents
                    .AsNoTracking()
                    .Where(e => string.Compare(e.CommittedAt, cutoffString) < 0)
                    .OrderBy(e => e.Id)
                    .Take(batchSize)
                    .ToListAsync(ct);

                if (rows.Count == 0)
                    return 0;

                await _archiveWriter.ArchiveBatchAsync(tableName, rows, cutoff, ct);

                var ids = rows.Select(r => r.Id).ToList();
                return await db.ConversationEvents
                    .Where(e => ids.Contains(e.Id))
                    .ExecuteDeleteAsync(ct);
            }

            default:
                throw new InvalidOperationException(
                    $"Table '{tableName}' is marked archivable but has no archive mapping.");
        }
    }

    private async Task EnsureTimestampIndexAsync(string tableName, string timestampColumn, CancellationToken ct)
    {
        var indexName = $"ix_prune_{tableName}_{timestampColumn}";
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        await db.Database.ExecuteSqlRawAsync(
            $"""CREATE INDEX IF NOT EXISTS "{indexName}" ON "{tableName}" ("{timestampColumn}")""",
            ct);
        _logger.LogInformation(
            "[RetentionPruning] ensured timestamp index={Index} on table={Table}",
            indexName, tableName);
    }

    private async Task VacuumAsync(CancellationToken ct)
    {
        _logger.LogInformation("[RetentionPruning] VACUUM started");
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        await db.Database.ExecuteSqlRawAsync("VACUUM", ct);
        _logger.LogInformation("[RetentionPruning] VACUUM completed");
    }
}
