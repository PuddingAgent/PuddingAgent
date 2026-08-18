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

        // additive 迁移：为既有数据库幂等补列/补表（不删除旧列/旧数据）。
        await EnsureCompactionGenerationColumnAsync(conn);
        await EnsureContextSegmentsTableAsync(conn);
        await EnsureMessageCompactionColumnsAsync(conn);
        await EnsureCompositionSnapshotsTableAsync(conn);
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

    /// <summary>
    /// 为既有数据库幂等补建 <c>ContextSegments</c> 表（P1-1 Task A，设计方案 §6.1）。
    /// 正常情况下 init_memory.sql 的 CREATE TABLE IF NOT EXISTS 已建表；
    /// 此处为防御性自愈：若旧库由更早版本 SQL 初始化（无 ContextSegments 表），
    /// 用 PRAGMA table_info 检测后补建，不删除旧列/旧数据。
    /// 建表 DDL 与 init_memory.sql 保持一致，修改时需两处同步。
    /// </summary>
    private static async Task EnsureContextSegmentsTableAsync(
        System.Data.Common.DbConnection conn)
    {
        var exists = false;
        using (var check = conn.CreateCommand())
        {
            check.CommandText = "PRAGMA table_info(ContextSegments);";
            await using var reader = await check.ExecuteReaderAsync();
            exists = await reader.ReadAsync();
        }

        if (exists)
            return;

        using var create = conn.CreateCommand();
        create.CommandText = """
            CREATE TABLE IF NOT EXISTS ContextSegments (
                SegmentId            TEXT PRIMARY KEY,
                SessionId            TEXT NOT NULL,
                RunId                TEXT,
                TurnId               TEXT,
                SourceKind           TEXT NOT NULL,
                SourceId             TEXT NOT NULL,
                SequenceStart        INTEGER NOT NULL,
                SequenceEnd          INTEGER NOT NULL,
                Role                 TEXT NOT NULL,
                ContentType          TEXT NOT NULL DEFAULT 'text',
                CanonicalContentHash TEXT NOT NULL,
                RawUtf8Bytes         INTEGER NOT NULL,
                EstimatedTokens      INTEGER,
                ProviderTokens       INTEGER,
                ArtifactRef          TEXT,
                ContextGeneration    INTEGER,
                CoveredByManifestId  TEXT,
                Tier                 TEXT NOT NULL DEFAULT 'T0',
                IsAtomicToolGroup    INTEGER NOT NULL DEFAULT 0,
                AuthorizationScope   TEXT,
                CreatedAt            INTEGER NOT NULL,
                Metadata             TEXT
            );
            """;
        await create.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// 为既有数据库幂等补建 <c>CompositionSnapshots</c> 表（P0-5 步骤 1）。
    /// 正常情况下 init_memory.sql 的 CREATE TABLE IF NOT EXISTS 已建表；
    /// 此处为防御性自愈：若旧库由更早版本 SQL 初始化（无 CompositionSnapshots 表），
    /// 用 PRAGMA table_info 检测后补建，不删除旧列/旧数据。
    /// 建表 DDL 与 init_memory.sql 保持一致，修改时需两处同步。
    /// </summary>
    private static async Task EnsureCompositionSnapshotsTableAsync(
        System.Data.Common.DbConnection conn)
    {
        var exists = false;
        using (var check = conn.CreateCommand())
        {
            check.CommandText = "PRAGMA table_info(CompositionSnapshots);";
            await using var reader = await check.ExecuteReaderAsync();
            exists = await reader.ReadAsync();
        }

        if (exists)
            return;

        using var create = conn.CreateCommand();
        create.CommandText = """
            CREATE TABLE IF NOT EXISTS CompositionSnapshots (
                SessionId               TEXT NOT NULL,
                CompositionVersion      INTEGER NOT NULL,
                SystemPromptHash        TEXT NOT NULL,
                ToolSpecHash            TEXT NOT NULL,
                PrefixHash              TEXT NOT NULL,
                SkillManifestHash       TEXT,
                SerializationVersion    TEXT NOT NULL DEFAULT 'prefix-v1',
                ToolIds                 TEXT,
                ChangeReason            TEXT,
                PermissionEpoch         INTEGER NOT NULL DEFAULT 0,
                CreatedAtUtc            INTEGER NOT NULL,
                CanonicalSystemPrefixHash TEXT,
                PRIMARY KEY (SessionId, CompositionVersion)
            );
            """;
        await create.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// 为既有 Messages 表幂等补 <c>ContextGeneration</c> 与 <c>CanonicalContentHash</c> 列
    /// （P1-1 Task B，设计方案 §9 同源去重锚点）。
    /// init_memory.sql 的 CREATE TABLE IF NOT EXISTS 不会改动已存在的表，
    /// 因此新增列必须由 PRAGMA table_info 检测后 ALTER TABLE 自愈。
    /// 两列各自独立检测、独立 ALTER，重复执行幂等，不删除旧列/旧数据。
    /// </summary>
    private static async Task EnsureMessageCompactionColumnsAsync(
        System.Data.Common.DbConnection conn)
    {
        var columns = new (string Name, string Ddl)[]
        {
            ("ContextGeneration", "ALTER TABLE Messages ADD COLUMN ContextGeneration INTEGER NULL;"),
            ("CanonicalContentHash", "ALTER TABLE Messages ADD COLUMN CanonicalContentHash TEXT NULL;"),
        };

        foreach (var (name, ddl) in columns)
        {
            var exists = false;
            using (var check = conn.CreateCommand())
            {
                check.CommandText = "PRAGMA table_info(Messages);";
                await using var reader = await check.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    if (reader.FieldCount > 1
                        && string.Equals(reader.GetString(1), name, StringComparison.OrdinalIgnoreCase))
                    {
                        exists = true;
                        break;
                    }
                }
            }

            if (exists)
                continue;

            using var alter = conn.CreateCommand();
            alter.CommandText = ddl;
            await alter.ExecuteNonQueryAsync();
        }
    }
}
