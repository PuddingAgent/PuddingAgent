using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace PuddingMemoryEngine.Data;

/// <summary>
/// 记忆数据库初始化器——使用 EnsureCreated 确保基础表存在。
/// FTS5 等高级特性通过 init_memory.sql 手动执行。
/// </summary>
public static class MemoryDbInitializer
{
    /// <summary>
    /// 初始化记忆数据库——先 EnsureCreated 创建基础表，再无拆分执行 init_memory.sql。
    /// </summary>
    public static async Task InitializeAsync(IDbContextFactory<MemoryDbContext> dbContextFactory)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync();
        await db.Database.EnsureCreatedAsync();

        var schemaDir = Path.Combine(AppContext.BaseDirectory, "Schema");
        var sqlPath = Path.Combine(schemaDir, "init_memory.sql");
        if (!File.Exists(sqlPath)) return;

        var sql = await File.ReadAllTextAsync(sqlPath);
        // 确保连接已打开，使用原生 ADO.NET 执行多语句 SQL
        var conn = db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open)
            await conn.OpenAsync();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        try
        {
            await cmd.ExecuteNonQueryAsync();
        }
        catch (SqliteException ex) when (ex.Message.Contains("already exists")
            || ex.Message.Contains("duplicate column name"))
        {
            // 幂等：已存在的对象忽略
        }

        using var jobsCmd = conn.CreateCommand();
        jobsCmd.CommandText = """
CREATE TABLE IF NOT EXISTS SubconsciousJobs (
    JobId               TEXT PRIMARY KEY,
    JobType             TEXT NOT NULL,
    IdempotencyKey      TEXT NOT NULL UNIQUE,
    Status              TEXT NOT NULL DEFAULT 'pending',
    WorkspaceId         TEXT NOT NULL,
    SessionId           TEXT NOT NULL,
    AgentId             TEXT NOT NULL,
    AgentTemplateId     TEXT NOT NULL,
    SourceHookName      TEXT,
    SourceEventId       TEXT,
    SourceCompactionId  TEXT,
    PayloadJson         TEXT NOT NULL DEFAULT '{}',
    ResultJson          TEXT,
    RetryCount          INTEGER NOT NULL DEFAULT 0,
    LeaseOwner          TEXT,
    LeaseUntil          INTEGER,
    AvailableAt         INTEGER NOT NULL,
    StartedAt           INTEGER,
    CompletedAt         INTEGER,
    ErrorMessage        TEXT,
    CreatedAt           INTEGER NOT NULL,
    UpdatedAt           INTEGER NOT NULL
);
CREATE UNIQUE INDEX IF NOT EXISTS UX_SubconsciousJobs_IdempotencyKey
    ON SubconsciousJobs(IdempotencyKey);
CREATE INDEX IF NOT EXISTS IX_SubconsciousJobs_Status_AvailableAt
    ON SubconsciousJobs(Status, AvailableAt);
CREATE INDEX IF NOT EXISTS IX_SubconsciousJobs_LeaseUntil
    ON SubconsciousJobs(LeaseUntil);
CREATE INDEX IF NOT EXISTS IX_SubconsciousJobs_Workspace_Session
    ON SubconsciousJobs(WorkspaceId, SessionId);
""";
        await jobsCmd.ExecuteNonQueryAsync();
    }
}
