using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace PuddingMemoryEngine.Data;

/// <summary>
/// 核心记忆数据库初始化器。
/// MemoryDbContext 和 MemoryLibraryDbContext 共享同一个 SQLite 文件，
/// 因此所有核心表和 FTS 对象都由 init_memory.sql 显式、幂等地创建，
/// 不允许依赖 EF Core EnsureCreated 的“空数据库”语义。
/// </summary>
public static class MemoryDbInitializer
{
    /// <summary>
    /// 初始化核心记忆 Schema。Schema 文件缺失或 DDL 执行失败时抛出异常，
    /// 由应用启动边界 fail-fast，禁止服务在不完整 Schema 上运行。
    /// </summary>
    public static async Task InitializeAsync(IDbContextFactory<MemoryDbContext> dbContextFactory)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync();

        var schemaDir = Path.Combine(AppContext.BaseDirectory, "Schema");
        var sqlPath = Path.Combine(schemaDir, "init_memory.sql");
        if (!File.Exists(sqlPath))
        {
            throw new FileNotFoundException(
                "Core memory schema file was not copied to the application output.",
                sqlPath);
        }

                var sql = await File.ReadAllTextAsync(sqlPath);
        var conn = db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open)
            await conn.OpenAsync();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();

        // additive 迁移：为既有数据库幂等补列（不删除旧列/旧数据）。
        await EnsureCompactionGenerationColumnAsync(conn);
    }

    /// <summary>
    /// 为既有 Sessions 表幂等补 <c>CompactionGeneration</c> 列。
    /// init_memory.sql 的 CREATE TABLE IF NOT EXISTS 不会改动已存在的表，
    /// 因此新增列必须由 PRAGMA table_info 检测后 ALTER TABLE 自愈。
    /// </summary>
    private static async Task EnsureCompactionGenerationColumnAsync(
        System.Data.Common.DbConnection conn)
    {
        var exists = false;
        using (var check = conn.CreateCommand())
        {
            check.CommandText = "PRAGMA table_info(Sessions);";
            await using var reader = await check.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                if (reader.FieldCount > 1
                    && string.Equals(reader.GetString(1), "CompactionGeneration", StringComparison.OrdinalIgnoreCase))
                {
                    exists = true;
                    break;
                }
            }
        }

        if (exists)
            return;

        using var alter = conn.CreateCommand();
        alter.CommandText = "ALTER TABLE Sessions ADD COLUMN CompactionGeneration INTEGER NOT NULL DEFAULT 0;";
        await alter.ExecuteNonQueryAsync();
    }
}
