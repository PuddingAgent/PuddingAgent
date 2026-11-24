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

    /// <summary>复用的子会话 ID 列（池条目复用同一 <c>sub_session_id</c>）。</summary>
    public const string SubSessionIdColumn = "sub_session_id";

    /// <summary>
    /// 子会话索引，服务管理视图的「按池条目查最近运行」查询。
    /// <para>
    /// 查询形态是 <c>WHERE sub_session_id = ? ORDER BY id DESC LIMIT n</c>。
    /// SQLite 的索引尾部隐含 rowid，因此单列索引即可同时满足过滤与倒序，
    /// 不需要额外复合 <c>id</c>，索引也保持最小。
    /// </para>
    /// </summary>
    public const string SubSessionIdIndex = "IX_sub_agent_runs_sub_session_id";

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

        // 同样未在 EF 模型（PlatformDbContext）中声明，新库与旧库都靠这里补齐。
        // 缺这条索引时上面的查询会退化成全表扫描（实测 EXPLAIN QUERY PLAN 为 SCAN）。
        await db.Database.ExecuteSqlRawAsync(
            $"CREATE INDEX IF NOT EXISTS \"{SubSessionIdIndex}\" ON \"{TableName}\" (\"{SubSessionIdColumn}\");",
            ct);

        logger?.LogDebug("[SubAgentRunSchema] Ensured index {Index}", SubSessionIdIndex);
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
