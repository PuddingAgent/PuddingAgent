using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PuddingPlatform.Data;

namespace PuddingPlatform.Services.Scheduling;

/// <summary>
/// P0 Scheduler 事件驱动层：task_scheduler_intent_outcomes 的幂等 SQLite schema bootstrap。
/// <para>
/// 与 <see cref="TaskSchedulerIntentSchemaBootstrapper"/> 同风格：EF EnsureCreated 覆盖全新库，
/// 本 bootstrap 覆盖已有库与重入场景（CREATE TABLE IF NOT EXISTS + 幂等索引）。
/// 列合同来自实施方案 §5.2 的推荐 DDL，含两处按现状的工程修正：
/// ① decision_id 为 TEXT——task_scheduler_decisions.decision_id 是 GUID TEXT 主键（设计稿 INTEGER 为笔误）；
/// ② 增加 started_goal_run_id——§5.3 步骤 6 要求把 Assignment/Goal id 写入 outcome。
/// 表刻意不注册 EF 实体——读写全部走原生 SQL（TaskSchedulerIntentOutcomeStore）。
/// </para>
/// </summary>
public static class TaskSchedulerIntentOutcomeSchemaBootstrapper
{
    private static readonly string[] Ddl =
    [
        """
        CREATE TABLE IF NOT EXISTS task_scheduler_intent_outcomes (
            intent_id             TEXT    NOT NULL PRIMARY KEY,
            workspace_id          TEXT    NOT NULL,
            task_id               TEXT,
            outcome               TEXT    NOT NULL,
            decision_id           TEXT,
            scan_id               TEXT    NOT NULL,
            policy_revision       INTEGER NOT NULL,
            options_hash          TEXT    NOT NULL,
            reason_code           TEXT,
            started_assignment_id TEXT,
            started_goal_run_id   TEXT,
            created_at_utc        TEXT    NOT NULL,
            FOREIGN KEY(intent_id) REFERENCES task_scheduler_intents(intent_id)
        );
        """,
        "CREATE INDEX IF NOT EXISTS IX_task_scheduler_intent_outcomes_workspace_created ON task_scheduler_intent_outcomes(workspace_id, created_at_utc);",
        "CREATE INDEX IF NOT EXISTS IX_task_scheduler_intent_outcomes_task ON task_scheduler_intent_outcomes(task_id);",
        "CREATE INDEX IF NOT EXISTS IX_task_scheduler_intent_outcomes_workspace_outcome ON task_scheduler_intent_outcomes(workspace_id, outcome);",
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
                    "[TaskSchedulerIntentOutcomeSchema] bootstrap failed: {Ddl}",
                    ddl[..Math.Min(ddl.Length, 96)]);
                throw;
            }
        }
    }
}
