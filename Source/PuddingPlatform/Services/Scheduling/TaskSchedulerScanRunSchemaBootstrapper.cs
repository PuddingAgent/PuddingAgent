using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PuddingPlatform.Data;

namespace PuddingPlatform.Services.Scheduling;

/// <summary>
/// P1-B Scan Run 持久化：task_scheduler_scan_runs 的幂等 SQLite schema bootstrap。
/// <para>
/// 与 <see cref="TaskSchedulerIntentSchemaBootstrapper"/> 同风格：
/// EF EnsureCreated 覆盖全新库，本 bootstrap 覆盖已有库（CREATE TABLE IF NOT EXISTS +
/// 幂等索引）。列定义逐字取实施方案 §7.1 DDL，列名与
/// <see cref="Data.Entities.TaskSchedulerScanRunEntity"/> 的 [Column] 严格一致。
/// </para>
/// </summary>
public static class TaskSchedulerScanRunSchemaBootstrapper
{
    private static readonly string[] Ddl =
    [
        """
        CREATE TABLE IF NOT EXISTS task_scheduler_scan_runs (
          scan_id TEXT PRIMARY KEY,
          workspace_id TEXT NOT NULL,
          trigger TEXT NOT NULL,
          mode TEXT NOT NULL,
          policy_revision INTEGER NOT NULL,
          host_boot_id TEXT NOT NULL,
          status TEXT NOT NULL,
          started_at_utc TEXT NOT NULL,
          completed_at_utc TEXT NULL,
          duration_ms INTEGER NULL,
          availability_refreshed INTEGER NOT NULL DEFAULT 0,
          idle_agents INTEGER NOT NULL DEFAULT 0,
          busy_agents INTEGER NOT NULL DEFAULT 0,
          unknown_agents INTEGER NOT NULL DEFAULT 0,
          backlog INTEGER NOT NULL DEFAULT 0,
          candidates INTEGER NOT NULL DEFAULT 0,
          eligible INTEGER NOT NULL DEFAULT 0,
          started INTEGER NOT NULL DEFAULT 0,
          tracked INTEGER NOT NULL DEFAULT 0,
          repaired INTEGER NOT NULL DEFAULT 0,
          decision_codes_json TEXT NULL,
          repair_codes_json TEXT NULL,
          error_code TEXT NULL,
          error_summary TEXT NULL
        );
        """,
        "CREATE INDEX IF NOT EXISTS IX_task_scheduler_scan_runs_workspace_started\n  ON task_scheduler_scan_runs(workspace_id, started_at_utc DESC);",
    ];

    public static async Task EnsureCreatedAsync(
        PlatformDbContext db,
        ILogger? logger = null,
        CancellationToken ct = default)
    {
        if (!db.Database.IsSqlite())
        {
            return;
        }

        foreach (var ddl in Ddl)
        {
            try
            {
                await db.Database.ExecuteSqlRawAsync(ddl, ct);
            }
            catch (Exception ex)
            {
                logger?.LogWarning(
                    ex,
                    "[TaskSchedulerScanRun] bootstrap failed: {Ddl}",
                    ddl[..Math.Min(ddl.Length, 96)]);
                throw;
            }
        }
    }
}
