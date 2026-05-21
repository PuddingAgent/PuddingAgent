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

    /// <summary>
    /// 按顶级 ; 拆分 SQL 语句。复合语句（CREATE TRIGGER ... BEGIN ... END）作为整体不被拆分。
    /// ADR-029 fix: 原 split-on-semicolon-line 会错误拆分触发器的 VALUES(...) 和 END 中间部分。
    /// </summary>
    private static List<string> SplitSqlStatements(string sql)
    {
        var statements = new List<string>();
        var depth = 0; // 跟踪 BEGIN..END 嵌套层级
        var current = new System.Text.StringBuilder();
        foreach (var ch in sql)
        {
            current.Append(ch);
            if (ch == ';' && depth == 0)
            {
                var stmt = current.ToString().Trim();
                if (!string.IsNullOrWhiteSpace(stmt) && !stmt.StartsWith("--"))
                    statements.Add(stmt);
                current.Clear();
            }
            // 追踪 BEGIN/END 边界（不区分大小写，简单状态机）
            else if (depth == 0 && CurrentEndsWith(current, "BEGIN"))
            {
                depth = 1;
            }
            else if (depth > 0 && CurrentEndsWith(current, "END"))
            {
                depth--;
            }
            else if (depth > 0 && CurrentEndsWith(current, "BEGIN"))
            {
                depth++;
            }
        }
        var tail = current.ToString().Trim();
        if (!string.IsNullOrWhiteSpace(tail))
            statements.Add(tail);
        return statements;
    }

    private static bool CurrentEndsWith(System.Text.StringBuilder sb, string suffix)
    {
        if (sb.Length < suffix.Length) return false;
        for (int i = 0; i < suffix.Length; i++)
        {
            var sbChar = char.ToUpperInvariant(sb[sb.Length - suffix.Length + i]);
            var sfxChar = char.ToUpperInvariant(suffix[i]);
            if (sbChar != sfxChar) return false;
        }
        return true;
    }
}
