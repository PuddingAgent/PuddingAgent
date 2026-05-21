using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace PuddingMemoryEngine.Data;

/// <summary>
/// 记忆图书馆数据库初始化器——使用 EnsureCreated 确保基础表存在。
/// FTS5 等高级特性通过 init_library.sql 逐条执行。
/// ADR-029: 逐条执行 SQL，单条 duplicate/already-exists 不阻断后续 migration。
/// </summary>
public static class MemoryLibraryDbInitializer
{
    /// <summary>
    /// 初始化记忆图书馆数据库——先 EnsureCreated 创建基础表，再逐条执行 init_library.sql。
    /// </summary>
    public static async Task InitializeAsync(
        IDbContextFactory<MemoryLibraryDbContext> dbContextFactory,
        ILogger? logger = null)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync();
        await db.Database.EnsureCreatedAsync();

        var schemaDir = Path.Combine(AppContext.BaseDirectory, "Schema");
        var sqlPath = Path.Combine(schemaDir, "init_library.sql");
        if (!File.Exists(sqlPath)) return;

        var sql = await File.ReadAllTextAsync(sqlPath);
        var conn = db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open)
            await conn.OpenAsync();

        // ADR-029: 逐条执行，单条 duplicate/already-exists 不阻断后续
        var statements = SplitSqlStatements(sql);
        foreach (var stmt in statements)
        {
            if (string.IsNullOrWhiteSpace(stmt)) continue;

            using var cmd = conn.CreateCommand();
            cmd.CommandText = stmt;
            try
            {
                await cmd.ExecuteNonQueryAsync();
            }
            catch (SqliteException ex) when (
                ex.Message.Contains("already exists", StringComparison.OrdinalIgnoreCase)
                || ex.Message.Contains("duplicate column name", StringComparison.OrdinalIgnoreCase))
            {
                logger?.LogDebug("[MemoryLibraryDbInitializer] Skipped (idempotent): {Error}", ex.Message);
            }
        }
    }

    /// <summary>按 ; 拆分 SQL 语句，跳过空白和注释块。</summary>
    private static List<string> SplitSqlStatements(string sql)
    {
        var statements = new List<string>();
        var current = new System.Text.StringBuilder();
        foreach (var line in sql.Split('\n'))
        {
            var trimmed = line.Trim();
            // 保留整行（含注释），交给 SQLite 自己处理
            current.AppendLine(line);
            if (trimmed.EndsWith(';') && !trimmed.StartsWith("--"))
            {
                statements.Add(current.ToString());
                current.Clear();
            }
        }
        if (current.Length > 0)
            statements.Add(current.ToString());
        return statements;
    }
}
