using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PuddingPlatform.Data;

namespace PuddingPlatform.Services.Diagnostics;

/// <summary>
/// 诊断 append-only 表保留期裁剪后台服务，遏制 platform.db 无限增长。
///
/// 设计要点：
/// 1) 宿主服务模式照抄 SessionChunkBackfillService：ExecuteAsync 首句 Task.Yield()，
///    绝不阻塞宿主启动（BackgroundService.StartAsync 会同步执行到第一个未完成 await）。
/// 2) SQLite 不支持 DELETE...LIMIT，因此用
///        DELETE FROM t WHERE rowid IN (SELECT rowid FROM t WHERE ts列&lt;@cutoff [AND 水位] LIMIT @batch)
///    循环到 affected&lt;batch，批间限速，尊重 CancellationToken，用 ExecuteSqlRaw 不加载实体。
/// 3) 时间戳列名来自实体映射（见 TableSpecs），不假设 CreatedAt；
///    服务运行前 CREATE INDEX IF NOT EXISTS 时间戳索引并记日志。
/// 4) 安全红线：session_event_log 与 conversation_events 是权威执行事实源，
///    不在后台保留期白名单内。ChatMessages 也绝不参与裁剪。
/// </summary>
public sealed class DiagnosticRetentionService : BackgroundService
{
    private readonly IDbContextFactory<PlatformDbContext> _platformDbFactory;
    private readonly IOptions<DiagnosticRetentionOptions> _options;
    private readonly ILogger<DiagnosticRetentionService> _logger;

    /// <summary>
    /// 受支持表的白名单（防 SQL 注入：表名/列名只能来自这里，不能来自配置）。
    /// 时间戳列名已按实体映射核实：
    ///   telemetry_metric_events.OccurredAtUtc → occurred_at_utc
    ///   runtime_activity.StartedAtUtc → started_at_utc
    ///   context_layer_metric_events.OccurredAtUtc → occurred_at_utc
    /// </summary>
    private static readonly IReadOnlyDictionary<string, RetentionTableSpec> TableSpecs =
        new Dictionary<string, RetentionTableSpec>(StringComparer.Ordinal)
        {
            ["telemetry_metric_events"] = new("telemetry_metric_events", "occurred_at_utc", RequiresWatermark: false),
            ["context_layer_metric_events"] = new("context_layer_metric_events", "occurred_at_utc", RequiresWatermark: false),
            ["runtime_activity"] = new("runtime_activity", "started_at_utc", RequiresWatermark: false),
        };

    public DiagnosticRetentionService(
        IDbContextFactory<PlatformDbContext> platformDbFactory,
        IOptions<DiagnosticRetentionOptions> options,
        ILogger<DiagnosticRetentionService> logger)
    {
        _platformDbFactory = platformDbFactory;
        _options = options;
        _logger = logger;
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
            _logger.LogInformation("[DiagnosticRetention] cancelled by host shutdown");
        }
        catch (Exception ex)
        {
            // 健康自证：裁剪失败不拖垮宿主，但必须留下错误日志。
            _logger.LogError(ex, "[DiagnosticRetention] service failed");
        }
    }

    /// <summary>
    /// 主循环：启动延迟后每 RunIntervalHours 跑一次裁剪。
    /// public 便于测试直调 RunOnceAsync。
    /// </summary>
    public async Task RunLoopAsync(CancellationToken ct = default)
    {
        var options = _options.Value;
        if (!options.Enabled)
        {
            _logger.LogInformation("[DiagnosticRetention] disabled (Enabled=false), skip retention sweep");
            return;
        }

        var startupDelay = TimeSpan.FromSeconds(Math.Max(0, options.StartupDelaySeconds));
        if (startupDelay > TimeSpan.Zero)
            await Task.Delay(startupDelay, ct);

        var interval = TimeSpan.FromHours(Math.Max(1, options.RunIntervalHours));
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
                _logger.LogError(ex, "[DiagnosticRetention] sweep failed; will retry next interval");
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
        var options = _options.Value;
        if (!options.Enabled)
        {
            _logger.LogInformation("[DiagnosticRetention] disabled (Enabled=false), skip retention sweep");
            return;
        }

        var batchSize = Math.Max(1, options.BatchSize);
        var batchDelayMs = Math.Max(0, options.BatchDelayMs);
        var cutoff = DateTimeOffset.UtcNow;

        foreach (var (tableName, tableOptions) in options.Tables)
        {
            ct.ThrowIfCancellationRequested();

            if (!tableOptions.Enabled || tableOptions.RetentionDays <= 0)
            {
                _logger.LogDebug(
                    "[DiagnosticRetention] skip table={Table} enabled={Enabled} retentionDays={RetentionDays}",
                    tableName, tableOptions.Enabled, tableOptions.RetentionDays);
                continue;
            }

            if (!TableSpecs.TryGetValue(tableName, out var spec))
            {
                _logger.LogWarning(
                    "[DiagnosticRetention] unknown table={Table} — not in whitelist, skipped (no-op)",
                    tableName);
                continue;
            }

            if (spec.RequiresWatermark && !await HasUsableSessionEventWatermarkAsync(ct))
            {
                _logger.LogWarning(
                    "[DiagnosticRetention] session_event_log requires projection watermark " +
                    "(session_projection_cursors written by SessionProjectionStore) but none is " +
                    "available in this database — table NOT trimmed (ADR-056 authoritative source).");
                continue;
            }

            var tableCutoff = cutoff.AddDays(-tableOptions.RetentionDays);

            await EnsureTimestampIndexAsync(spec, ct);
            await TrimTableAsync(spec, tableCutoff, batchSize, batchDelayMs, ct);
        }

        if (options.Vacuum?.Enabled == true)
        {
            await VacuumAsync(ct);
        }
    }

    private async Task TrimTableAsync(
        RetentionTableSpec spec,
        DateTimeOffset cutoff,
        int batchSize,
        int batchDelayMs,
        CancellationToken ct)
    {
        long totalDeleted = 0;
        int batches = 0;
        var cutoffString = cutoff.ToString("O");

        await using (var db = await _platformDbFactory.CreateDbContextAsync(ct))
        {
            while (!ct.IsCancellationRequested)
            {
                var sql = spec.RequiresWatermark
                    ? $$"""
                       DELETE FROM "{{spec.TableName}}" WHERE rowid IN (
                         SELECT rowid FROM "{{spec.TableName}}" t
                         WHERE t."{{spec.TimestampColumn}}" < {0}
                           AND EXISTS (
                             SELECT 1 FROM session_projection_cursors c
                             WHERE c.session_id = t.session_id
                               AND c.projected_through_sequence >= t.sequence_num
                           )
                         LIMIT {1}
                       )
                       """
                    : $$"""
                       DELETE FROM "{{spec.TableName}}" WHERE rowid IN (
                         SELECT rowid FROM "{{spec.TableName}}" t
                         WHERE t."{{spec.TimestampColumn}}" < {0}
                         LIMIT {1}
                       )
                       """;

                var affected = await db.Database.ExecuteSqlRawAsync(sql, cutoffString, batchSize);
                totalDeleted += affected;
                batches++;

                if (affected < batchSize)
                    break;

                if (batchDelayMs > 0)
                    await Task.Delay(batchDelayMs, ct);
            }
        }

        _logger.LogInformation(
            "[DiagnosticRetention] trimmed table={Table} cutoff={Cutoff:O} deleted={Deleted} batches={Batches}",
            spec.TableName, cutoff, totalDeleted, batches);
    }

    private async Task EnsureTimestampIndexAsync(RetentionTableSpec spec, CancellationToken ct)
    {
        var indexName = $"ix_retention_{spec.TableName}_{spec.TimestampColumn}";
        await using var db = await _platformDbFactory.CreateDbContextAsync(ct);
        await db.Database.ExecuteSqlRawAsync(
            $"""CREATE INDEX IF NOT EXISTS "{indexName}" ON "{spec.TableName}" ("{spec.TimestampColumn}")""",
            ct);
        _logger.LogInformation(
            "[DiagnosticRetention] ensured timestamp index={Index} on table={Table}",
            indexName, spec.TableName);
    }

    private async Task<bool> HasUsableSessionEventWatermarkAsync(CancellationToken ct)
    {
        // session_event_log 是 ADR-056 权威事实源（投影尾部读取）。可删范围必须
        // 同时受投影水位约束：只删比 min(保留期截止线, 最老未消费水位) 更旧的行。
        // 当前实现要求 session_projection_cursors 表存在且至少有一行水位记录
        // （说明 SessionProjectionStore 有写入方在推进），否则该表默认不删。
        await using var db = await _platformDbFactory.CreateDbContextAsync(ct);
        try
        {
            var tableExists = await db.Database.SqlQueryRaw<int>(
                """
                SELECT COUNT(*) AS Value
                FROM sqlite_master
                WHERE type = 'table' AND name = 'session_projection_cursors'
                """).SingleAsync(ct) > 0;
            if (!tableExists)
                return false;

            var watermarkRows = await db.Database.SqlQueryRaw<long>(
                """
                SELECT COUNT(*) AS Value
                FROM session_projection_cursors
                WHERE projected_through_sequence > 0
                """).SingleAsync(ct);
            return watermarkRows > 0;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "[DiagnosticRetention] watermark probe failed — session_event_log NOT trimmed");
            return false;
        }
    }

    private async Task VacuumAsync(CancellationToken ct)
    {
        _logger.LogInformation("[DiagnosticRetention] VACUUM started");
        await using var db = await _platformDbFactory.CreateDbContextAsync(ct);
        await db.Database.ExecuteSqlRawAsync("VACUUM", ct);
        _logger.LogInformation("[DiagnosticRetention] VACUUM completed");
    }

    private sealed record RetentionTableSpec(
        string TableName,
        string TimestampColumn,
        bool RequiresWatermark);
}
