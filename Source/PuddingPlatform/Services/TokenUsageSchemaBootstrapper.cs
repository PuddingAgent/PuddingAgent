using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PuddingPlatform.Data;

namespace PuddingPlatform.Services;

/// <summary>
/// Idempotently upgrades the token-usage ledger schema for existing SQLite databases.
/// EF EnsureCreated only creates a clean database; it does not add fields to an existing table.
/// </summary>
public static class TokenUsageSchemaBootstrapper
{
    private const string TableName = "TokenUsageEvents";
    private const string GatewayTableName = "llm_gateway_usage_events";
    private const string ContextLayerMetricTableName = "context_layer_metric_events";

    /// <summary>
    /// Nullable columns that exist on <see cref="Data.Entities.TokenUsageEventEntity"/> but
    /// were introduced after the last EF migration / model snapshot. Because EnsureCreated
    /// never alters an existing table, every one of them must be self-healed here.
    /// </summary>
    internal static readonly (string Name, string Definition)[] RequiredColumns =
    [
        ("ParentSessionId", "TEXT NULL"),
        // Prompt prefix 分层 token 分解簇
        ("MessageTokens", "INTEGER NULL"),
        ("ToolDefinitionTokens", "INTEGER NULL"),
        ("SystemMessageTokens", "INTEGER NULL"),
        ("HistoryMessageTokens", "INTEGER NULL"),
        // 熵探针簇
        ("HistoryMessageEntropy", "REAL NULL"),
        ("SystemMessageEntropy", "REAL NULL"),
        ("ToolDefinitionEntropy", "REAL NULL"),
        // agent loop 轮次/工具簇
        ("TurnRound", "INTEGER NULL"),
        ("ToolCallCount", "INTEGER NULL"),
        ("ToolNames", "TEXT NULL"),
        ("SubAgentId", "TEXT NULL"),
    ];

    /// <summary>
    /// Additive observability fields for the existing context-layer ledger. They
    /// contain byte counts and compression ratios only, never prompt text.
    /// </summary>
    internal static readonly (string Name, string Definition)[] RequiredContextLayerColumns =
    [
        ("raw_utf8_bytes", "INTEGER NOT NULL DEFAULT 0"),
        ("gzip_bytes", "INTEGER NOT NULL DEFAULT 0"),
        ("gzip_ratio", "REAL NULL"),
    ];

    public static async Task EnsureCreatedAsync(
        PlatformDbContext db,
        ILogger? logger = null,
        CancellationToken ct = default)
    {
        if (!db.Database.IsSqlite())
            return;

        foreach (var (columnName, columnDefinition) in RequiredColumns)
        {
            if (!await ColumnExistsAsync(db, TableName, columnName, ct))
            {
#pragma warning disable EF1002 // Column names/definitions are compile-time constants from RequiredColumns
                await db.Database.ExecuteSqlRawAsync(
                    $"""
                    ALTER TABLE "TokenUsageEvents"
                    ADD COLUMN "{columnName}" {columnDefinition};
                    """,
                    ct);
#pragma warning restore EF1002

                logger?.LogInformation(
                    "[TokenUsageSchema] Added {Table}.{Column}",
                    TableName,
                    columnName);
            }
        }

        if (await TableExistsAsync(db, ContextLayerMetricTableName, ct))
        {
            foreach (var (columnName, columnDefinition) in RequiredContextLayerColumns)
            {
                if (!await ColumnExistsAsync(db, ContextLayerMetricTableName, columnName, ct))
                {
#pragma warning disable EF1002 // Column names/definitions are compile-time constants from RequiredContextLayerColumns
                    await db.Database.ExecuteSqlRawAsync(
                        $"ALTER TABLE \"{ContextLayerMetricTableName}\" ADD COLUMN \"{columnName}\" {columnDefinition};",
                        ct);
#pragma warning restore EF1002
                    logger?.LogInformation(
                        "[TokenUsageSchema] Added {Table}.{Column}",
                        ContextLayerMetricTableName,
                        columnName);
                }
            }
        }

        await db.Database.ExecuteSqlRawAsync(
            """
            CREATE INDEX IF NOT EXISTS "IX_TokenUsageEvents_ParentSessionId"
            ON "TokenUsageEvents" ("ParentSessionId");
            """,
            ct);

        await db.Database.ExecuteSqlRawAsync(
            $$"""
            CREATE TABLE IF NOT EXISTS "{{GatewayTableName}}" (
                "Id" INTEGER NOT NULL CONSTRAINT "PK_{{GatewayTableName}}" PRIMARY KEY AUTOINCREMENT,
                "source_id" TEXT NOT NULL,
                "operation" TEXT NOT NULL,
                "workspace_id" TEXT NULL,
                "session_id" TEXT NULL,
                "agent_template_id" TEXT NULL,
                "provider_id" TEXT NOT NULL,
                "model_id" TEXT NOT NULL,
                "occurred_at_utc" TEXT NOT NULL,
                "year_month" TEXT NOT NULL,
                "prompt_tokens" INTEGER NOT NULL,
                "completion_tokens" INTEGER NOT NULL,
                "total_tokens" INTEGER NOT NULL,
                "cache_hit_tokens" INTEGER NOT NULL,
                "cache_miss_tokens" INTEGER NOT NULL,
                "input_cost" decimal(18,10) NOT NULL,
                "output_cost" decimal(18,10) NOT NULL,
                "cache_hit_cost" decimal(18,10) NOT NULL,
                "total_cost" decimal(18,10) NOT NULL,
                "raw_usage_json" TEXT NULL,
                "created_at_utc" TEXT NOT NULL
            );
            CREATE UNIQUE INDEX IF NOT EXISTS "IX_llm_gateway_usage_events_source_id"
                ON "{{GatewayTableName}}" ("source_id");
            CREATE INDEX IF NOT EXISTS "IX_llm_gateway_usage_events_year_month"
                ON "{{GatewayTableName}}" ("year_month");
            CREATE INDEX IF NOT EXISTS "IX_llm_gateway_usage_events_occurred_at_utc"
                ON "{{GatewayTableName}}" ("occurred_at_utc");
            CREATE INDEX IF NOT EXISTS "IX_llm_gateway_usage_events_provider_model"
                ON "{{GatewayTableName}}" ("provider_id", "model_id");
            """,
            ct);

        await db.Database.ExecuteSqlRawAsync(
            """
            CREATE TABLE IF NOT EXISTS "llm_usage_daily_aggregates" (
                "Id" INTEGER NOT NULL CONSTRAINT "PK_llm_usage_daily_aggregates" PRIMARY KEY AUTOINCREMENT,
                "day_utc" TEXT NOT NULL,
                "year_month" TEXT NOT NULL,
                "source" TEXT NOT NULL,
                "provider_id" TEXT NOT NULL,
                "model_id" TEXT NOT NULL,
                "prompt_tokens" INTEGER NOT NULL,
                "completion_tokens" INTEGER NOT NULL,
                "cache_hit_tokens" INTEGER NOT NULL,
                "cache_miss_tokens" INTEGER NOT NULL,
                "request_count" INTEGER NOT NULL,
                "input_cost" decimal(18,10) NOT NULL,
                "cache_hit_cost" decimal(18,10) NOT NULL,
                "output_cost" decimal(18,10) NOT NULL,
                "total_cost" decimal(18,10) NOT NULL,
                "built_at_utc" TEXT NOT NULL
            );
            CREATE UNIQUE INDEX IF NOT EXISTS "IX_llm_usage_daily_aggregates_DayUtc_Source_ProviderId_ModelId"
                ON "llm_usage_daily_aggregates" ("day_utc", "source", "provider_id", "model_id");
            CREATE INDEX IF NOT EXISTS "IX_llm_usage_daily_aggregates_YearMonth"
                ON "llm_usage_daily_aggregates" ("year_month");
            CREATE TABLE IF NOT EXISTS "context_layer_daily_rollups" (
                "Id" INTEGER NOT NULL CONSTRAINT "PK_context_layer_daily_rollups" PRIMARY KEY AUTOINCREMENT,
                "day_utc" TEXT NOT NULL,
                "layer_name" TEXT NOT NULL,
                "layer_order" INTEGER NOT NULL,
                "layer_role" TEXT NOT NULL,
                "provider_id" TEXT NULL,
                "model_id" TEXT NULL,
                "payload_json" TEXT NOT NULL,
                "built_at_utc" TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS "IX_context_layer_daily_rollups_DayUtc"
                ON "context_layer_daily_rollups" ("day_utc");
            CREATE TABLE IF NOT EXISTS "stats_daily_cache_days" (
                "cache_key" TEXT NOT NULL,
                "day_utc" TEXT NOT NULL,
                "built_at_utc" TEXT NOT NULL,
                CONSTRAINT "PK_stats_daily_cache_days" PRIMARY KEY ("cache_key", "day_utc")
            );
            """,
            ct);
    }

    private static async Task<bool> ColumnExistsAsync(
        DbContext db,
        string tableName,
        string columnName,
        CancellationToken ct)
    {
        var connection = db.Database.GetDbConnection();
        var shouldClose = connection.State != ConnectionState.Open;
        if (shouldClose)
            await connection.OpenAsync(ct);

        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = $"PRAGMA table_info({QuoteIdentifier(tableName)});";
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                if (reader.FieldCount > 1
                    && string.Equals(reader.GetString(1), columnName, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }
        finally
        {
            if (shouldClose)
                await connection.CloseAsync();
        }
    }

    private static async Task<bool> TableExistsAsync(
        DbContext db,
        string tableName,
        CancellationToken ct)
    {
        var connection = db.Database.GetDbConnection();
        var shouldClose = connection.State != ConnectionState.Open;
        if (shouldClose)
            await connection.OpenAsync(ct);

        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = $name LIMIT 1;";
            var parameter = command.CreateParameter();
            parameter.ParameterName = "$name";
            parameter.Value = tableName;
            command.Parameters.Add(parameter);
            return await command.ExecuteScalarAsync(ct) is not null;
        }
        finally
        {
            if (shouldClose)
                await connection.CloseAsync();
        }
    }

    private static string QuoteIdentifier(string identifier)
        => $"\"{identifier.Replace("\"", "\"\"")}\"";
}
