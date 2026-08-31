using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PuddingPlatform.Data;

namespace PuddingPlatform.Services.Scheduling;

/// <summary>
/// P0 Scheduler 决策持久化：task_scheduler_decisions 的幂等 SQLite schema bootstrap。
/// <para>
/// 与 <see cref="TaskSchedulerIntentSchemaBootstrapper"/> 同风格：EF EnsureCreated 覆盖全新库，
/// 本 bootstrap 覆盖已有库与重入场景（CREATE TABLE IF NOT EXISTS + 幂等索引）。
/// 决策表刻意不注册 EF 实体——调度器读写全部走原生 SQL（TaskSchedulerDecisionStore），
/// 避免 Data 层在途改动被本工作线触碰。
/// </para>
/// </summary>
public static class TaskSchedulerDecisionSchemaBootstrapper
{
    private static readonly string[] Ddl =
    [
        """
        CREATE TABLE IF NOT EXISTS task_scheduler_decisions (
            decision_id          TEXT NOT NULL PRIMARY KEY,
            workspace_id         TEXT NOT NULL,
            task_id              TEXT NOT NULL,
            phase                TEXT NOT NULL,
            mode                 TEXT NOT NULL,
            decision             TEXT NOT NULL,
            decision_code        TEXT NOT NULL,
            reason               TEXT,
            score_breakdown_json TEXT,
            next_eligible_at_utc TEXT,
            agent_id             TEXT,
            scan_id              TEXT NOT NULL,
            created_at_utc       TEXT NOT NULL
        );
        """,
        "CREATE INDEX IF NOT EXISTS IX_task_scheduler_decisions_ws_task_created ON task_scheduler_decisions(workspace_id, task_id, created_at_utc);",
        "CREATE UNIQUE INDEX IF NOT EXISTS UX_task_scheduler_decisions_scan_task_phase ON task_scheduler_decisions(scan_id, task_id, phase);",
        "CREATE INDEX IF NOT EXISTS IX_task_scheduler_decisions_scan ON task_scheduler_decisions(scan_id);",
        "CREATE INDEX IF NOT EXISTS IX_task_scheduler_decisions_decision_code ON task_scheduler_decisions(decision_code);",
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
                    "[TaskSchedulerDecisionSchema] bootstrap failed: {Ddl}",
                    ddl[..Math.Min(ddl.Length, 96)]);
                throw;
            }
        }
    }
}
