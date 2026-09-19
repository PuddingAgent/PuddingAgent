using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PuddingPlatform.Data;

namespace PuddingPlatform.Services;

/// <summary>
/// Ensures the sub_agent_runs composite index that is not declared in the EF model (idempotent).
/// <para>
/// 全新库的列由 EF EnsureCreated 依 PuddingPlatform.Data.Entities.SubAgentRunEntity 声明创建；
/// 一次性列迁移（旧库 ALTER 补列）已按 2026-09-19 压缩决策删除（产品未发布，不存在需升级的旧库）。
/// </para>
/// <para>
/// 父执行身份（parent_turn_id / parent_command_id / parent_run_id）是 slice-4
/// 「父 Turn 等待异步子代理」的归属依据：缺失时按父 Turn 统计运行中子代理只能退化为会话粒度，
/// 兄弟 Turn 的子代理会被误计。
/// </para>
/// </summary>
public static class SubAgentRunSchemaBootstrapper
{
    private const string TableName = "sub_agent_runs";

    /// <summary>父 Turn ID 列（RuntimeExecutionIdentity.TurnId）。</summary>
    public const string ParentTurnIdColumn = "parent_turn_id";

    /// <summary>「父 Turn + 状态」复合索引，服务「按父 Turn 统计运行中子代理」查询。</summary>
    public const string ParentTurnStatusIndex = "IX_sub_agent_runs_parent_turn_id_status";

    public static async Task EnsureCreatedAsync(
        PlatformDbContext db,
        ILogger? logger = null,
        CancellationToken ct = default)
    {
        if (!db.Database.IsSqlite())
            return;

        // 索引表不存在（旧库早于 ADR-021）时无事可做：EF EnsureCreated 不会给已存在的库补表，
        // 此时既没有可升级的列，也不该让启动失败。每次启动都会再试一次，表出现后自然补齐。
        if (!await TableExistsAsync(db, ct))
        {
            logger?.LogDebug(
                "[SubAgentRunSchema] {Table} not found; parent identity upgrade skipped",
                TableName);
            return;
        }

        // 该索引未在 EF 模型（PlatformDbContext）中声明，因此新库与旧库都靠这里补；
        // CREATE INDEX IF NOT EXISTS 保证重复调用幂等。
        // parent_turn_id 已由 EF EnsureCreated 依实体声明建列，索引可安全创建。
        await db.Database.ExecuteSqlRawAsync(
            $"CREATE INDEX IF NOT EXISTS \"{ParentTurnStatusIndex}\" ON \"{TableName}\" (\"{ParentTurnIdColumn}\", \"status\");",
            ct);

        logger?.LogDebug("[SubAgentRunSchema] Ensured index {Index}", ParentTurnStatusIndex);
    }

    private static async Task<bool> TableExistsAsync(PlatformDbContext db, CancellationToken ct)
    {
        var connection = db.Database.GetDbConnection();
        var shouldClose = connection.State != ConnectionState.Open;
        if (shouldClose)
            await connection.OpenAsync(ct);

        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText =
                "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = $name LIMIT 1;";
            var parameter = command.CreateParameter();
            parameter.ParameterName = "$name";
            parameter.Value = TableName;
            command.Parameters.Add(parameter);

            return await command.ExecuteScalarAsync(ct) is not null;
        }
        finally
        {
            if (shouldClose)
                await connection.CloseAsync();
        }
    }
}
