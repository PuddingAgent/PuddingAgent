using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PuddingPlatform.Data;

namespace PuddingPlatform.Services;

/// <summary>
/// 会话首屏读模型的存量库就地补齐（Plan《会话首屏与渐进加载架构优化方案》§3.2/§6/§11.2）。
/// <para>
/// 职责边界：
/// <list type="bullet">
/// <item><see cref="EnsureCreatedAsync"/>：只创建两张**小表**及其索引，启动期成本有界，可每次启动幂等执行；</item>
/// <item><see cref="EnsureConversationEventIndexesAsync"/>：为 300 万行级的 <c>conversation_events</c> 建两条查询索引，
///   <b>必须作为显式维护窗口执行</b>（有创建耗时/磁盘成本），不在每次启动自动调用；</item>
/// <item>新库的表/索引由 EF 模型 <c>EnsureCreated</c> 创建，模型与这里引用同一份 <see cref="ConversationReadModelSchema"/> 名称。</item>
/// </list>
/// </para>
/// </summary>
public static class ConversationReadModelSchemaBootstrapper
{
    private const string ConversationEventsTable = "conversation_events";

    public static async Task EnsureCreatedAsync(
        PlatformDbContext db,
        ILogger? logger = null,
        CancellationToken ct = default)
    {
        if (!db.Database.IsSqlite())
            return;

        var createdTables = 0;
        foreach (var (tableName, ddl) in GetTableDdl())
        {
            if (await TableExistsAsync(db, tableName, ct))
                continue;

            await db.Database.ExecuteSqlRawAsync(ddl, ct);
            createdTables++;
            logger?.LogInformation("[ConversationReadModelSchema] Created table {Table}", tableName);
        }

        // 小表索引：CREATE INDEX IF NOT EXISTS 保证幂等；已有表也补齐（旧库）
        await db.Database.ExecuteSqlRawAsync(
            $"""
             CREATE INDEX IF NOT EXISTS "{ConversationReadModelSchema.TurnSummariesConversationLastSequenceIndex}"
             ON "{ConversationReadModelSchema.TurnSummariesTable}" ("conversation_id", "last_sequence");
             """,
            ct);

        logger?.LogDebug(
            "[ConversationReadModelSchema] Ensured read-model tables (newTables={CreatedTables})",
            createdTables);
    }

    /// <summary>
    /// 显式维护：为存量库的 conversation_events 创建消息/Turn 收敛索引。
    /// 不在启动期调用；调用方需在可观测的维护窗口内执行并承担建索引耗时与磁盘成本。
    /// </summary>
    public static async Task<ConversationEventIndexMaintenanceResult> EnsureConversationEventIndexesAsync(
        PlatformDbContext db,
        ILogger? logger = null,
        CancellationToken ct = default)
    {
        if (!db.Database.IsSqlite())
            return new ConversationEventIndexMaintenanceResult([], []);

        if (!await TableExistsAsync(db, ConversationEventsTable, ct))
        {
            logger?.LogWarning(
                "[ConversationReadModelSchema] {Table} not found; conversation index maintenance skipped",
                ConversationEventsTable);
            return new ConversationEventIndexMaintenanceResult([], []);
        }

        var created = new List<string>();
        var alreadyPresent = new List<string>();
        foreach (var index in GetConversationEventIndexDefinitions())
        {
            if (await IndexExistsAsync(db, index.Name, ct))
            {
                alreadyPresent.Add(index.Name);
                continue;
            }

            await db.Database.ExecuteSqlRawAsync(index.Ddl, ct);
            created.Add(index.Name);
            logger?.LogInformation(
                "[ConversationReadModelSchema] Created conversation_events index {Index}",
                index.Name);
        }

        return new ConversationEventIndexMaintenanceResult(created, alreadyPresent);
    }

    private static IReadOnlyList<(string TableName, string Ddl)> GetTableDdl() =>
    [
        (
            ConversationReadModelSchema.TurnSummariesTable,
            """
            CREATE TABLE IF NOT EXISTS "conversation_turn_summaries" (
                "conversation_id" TEXT NOT NULL,
                "turn_id" TEXT NOT NULL,
                "root_run_id" TEXT NULL,
                "user_message_id" TEXT NULL,
                "assistant_message_id" TEXT NULL,
                "terminal_state" INTEGER NOT NULL,
                "has_assistant_body" INTEGER NOT NULL,
                "content_append_count" INTEGER NOT NULL,
                "thinking_append_count" INTEGER NOT NULL,
                "tool_call_requested_count" INTEGER NOT NULL,
                "tool_call_completed_count" INTEGER NOT NULL,
                "tool_call_failed_count" INTEGER NOT NULL,
                "sub_agent_run_count" INTEGER NOT NULL,
                "active_preview" TEXT NOT NULL,
                "active_preview_truncated" INTEGER NOT NULL,
                "first_sequence" INTEGER NOT NULL,
                "last_sequence" INTEGER NOT NULL,
                "summary_revision" INTEGER NOT NULL,
                "process_completeness" INTEGER NOT NULL,
                "incomplete_reason" TEXT NULL,
                "read_model_version" INTEGER NOT NULL,
                "updated_at" TEXT NOT NULL,
                PRIMARY KEY ("conversation_id", "turn_id")
            );
            """
        ),
        (
            ConversationReadModelSchema.ReadStateTable,
            """
            CREATE TABLE IF NOT EXISTS "conversation_read_state" (
                "conversation_id" TEXT NOT NULL PRIMARY KEY,
                "initialization_state" INTEGER NOT NULL,
                "current_turn_id" TEXT NULL,
                "current_root_run_id" TEXT NULL,
                "current_root_terminal_state" INTEGER NOT NULL,
                "compaction_running" INTEGER NOT NULL,
                "compaction_run_id" TEXT NULL,
                "compaction_started_sequence" INTEGER NOT NULL,
                "compaction_completed_sequence" INTEGER NULL,
                "compaction_last_attempt_failed" INTEGER NOT NULL,
                "sub_agent_runs_json" TEXT NOT NULL DEFAULT '[]',
                "sub_agent_runs_omitted" INTEGER NOT NULL,
                "read_model_version" INTEGER NOT NULL,
                "revision" INTEGER NOT NULL,
                "updated_at" TEXT NOT NULL
            );
            """
        ),
    ];

    private static IReadOnlyList<ConversationEventIndexDefinition> GetConversationEventIndexDefinitions() =>
    [
        new(
            ConversationReadModelSchema.ConversationEventsConversationMessageSequenceIndex,
            $"""
             CREATE INDEX IF NOT EXISTS "{ConversationReadModelSchema.ConversationEventsConversationMessageSequenceIndex}"
             ON "{ConversationEventsTable}" ("conversation_id", "message_id", "sequence");
             """
        ),
        new(
            ConversationReadModelSchema.ConversationEventsConversationTurnSequenceIndex,
            $"""
             CREATE INDEX IF NOT EXISTS "{ConversationReadModelSchema.ConversationEventsConversationTurnSequenceIndex}"
             ON "{ConversationEventsTable}" ("conversation_id", "turn_id", "sequence");
             """
        ),
        new(
            ConversationReadModelSchema.ConversationEventsCommandIdSequenceIndex,
            $"""
             CREATE INDEX IF NOT EXISTS "{ConversationReadModelSchema.ConversationEventsCommandIdSequenceIndex}"
             ON "{ConversationEventsTable}" ("command_id", "sequence");
             """
        ),
    ];

    private static async Task<bool> TableExistsAsync(
        PlatformDbContext db,
        string tableName,
        CancellationToken ct)
    {
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = $name LIMIT 1;";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "$name";
        parameter.Value = tableName;
        command.Parameters.Add(parameter);
        await EnsureOpenAsync(db.Database.GetDbConnection(), ct);
        return await command.ExecuteScalarAsync(ct) is not null;
    }

    private static async Task<bool> IndexExistsAsync(
        PlatformDbContext db,
        string indexName,
        CancellationToken ct)
    {
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'index' AND name = $name LIMIT 1;";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "$name";
        parameter.Value = indexName;
        command.Parameters.Add(parameter);
        await EnsureOpenAsync(db.Database.GetDbConnection(), ct);
        return await command.ExecuteScalarAsync(ct) is not null;
    }

    private static async Task EnsureOpenAsync(System.Data.Common.DbConnection connection, CancellationToken ct)
    {
        if (connection.State != ConnectionState.Open)
            await connection.OpenAsync(ct);
    }

    private sealed record ConversationEventIndexDefinition(string Name, string Ddl);
}

/// <summary>显式维护结果：本次创建/原本已存在的 conversation_events 索引名。</summary>
public sealed record ConversationEventIndexMaintenanceResult(
    IReadOnlyList<string> CreatedIndexes,
    IReadOnlyList<string> AlreadyPresentIndexes);
