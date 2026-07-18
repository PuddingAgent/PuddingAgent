using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace PuddingMemoryEngine.Data;

/// <summary>
/// 记忆图书馆数据库初始化器。
/// MemoryDbContext 和 MemoryLibraryDbContext 共享同一 SQLite 文件，因此不能依赖 EnsureCreated。
/// 本初始化器使用独立连接执行新版记忆图书馆 DDL。开发环境走空库冷启动，不再承载旧库迁移职责。
/// </summary>
public static class MemoryLibraryDbInitializer
{
    public static async Task InitializeAsync(
        IDbContextFactory<MemoryLibraryDbContext> dbContextFactory,
        ILogger? logger = null)
    {
        // 通过 DbContextFactory 获取 connection string
        await using var db = await dbContextFactory.CreateDbContextAsync();
        var connStr = db.Database.GetConnectionString();
        if (string.IsNullOrWhiteSpace(connStr)) return;

        using var conn = new SqliteConnection(connStr);
        await conn.OpenAsync();

        await ExecuteAsync(conn, "PRAGMA foreign_keys=ON;");

        await ExecuteAsync(conn, """
CREATE TABLE IF NOT EXISTS Libraries (
    LibraryId   TEXT PRIMARY KEY,
    WorkspaceId TEXT NOT NULL,
    AgentId     TEXT NULL,
    Name        TEXT NOT NULL,
    Description TEXT,
    CreatedAt   INTEGER NOT NULL,
    UpdatedAt   INTEGER NOT NULL
);
""");

        await ExecuteAsync(conn, """
CREATE TABLE IF NOT EXISTS Books (
    BookId         TEXT PRIMARY KEY,
    LibraryId      TEXT NOT NULL REFERENCES Libraries(LibraryId),
    Title          TEXT NOT NULL,
    Summary        TEXT NOT NULL DEFAULT '',
    Status         TEXT NOT NULL DEFAULT 'active',
    Version        INTEGER NOT NULL DEFAULT 1,
    AccessCount    INTEGER NOT NULL DEFAULT 0,
    LastAccessedAt INTEGER,
    Embedding      BLOB,
    CreatedAt      INTEGER NOT NULL,
    UpdatedAt      INTEGER NOT NULL
);
""");

        await ExecuteAsync(conn, """
CREATE TABLE IF NOT EXISTS BookIndexes (
    Id        INTEGER PRIMARY KEY AUTOINCREMENT,
    BookId    TEXT NOT NULL REFERENCES Books(BookId) ON DELETE CASCADE,
    TagPath   TEXT NOT NULL,
    Weight    INTEGER NOT NULL DEFAULT 1,
    CreatedAt INTEGER NOT NULL
);
""");

        await ExecuteAsync(conn, """
CREATE TABLE IF NOT EXISTS Chapters (
    ChapterId       TEXT PRIMARY KEY,
    BookId          TEXT NOT NULL REFERENCES Books(BookId) ON DELETE CASCADE,
    Title           TEXT NOT NULL,
    ChapterOrder    INTEGER NOT NULL DEFAULT 0,
    Content         TEXT NOT NULL,
    ContentType     TEXT NOT NULL DEFAULT 'markdown',
    Importance      REAL NOT NULL DEFAULT 0.5,
    Status          TEXT NOT NULL DEFAULT 'active',
    SupersededByChapterId TEXT,
    SupersededAt    INTEGER,
    SourceSessionId TEXT,
    WordCount       INTEGER NOT NULL DEFAULT 0,
    AgentInstanceId TEXT,
    TitleTokens     TEXT NOT NULL DEFAULT '',
    ContentTokens   TEXT NOT NULL DEFAULT '',
    Embedding       BLOB,
    SourceReference TEXT,
    ReferenceType   TEXT,
    CreatedAt       INTEGER NOT NULL,
    UpdatedAt       INTEGER NOT NULL
);
""");

        // ADR-042: Agent 记忆隔离。共享数据库初始化必须先检查元数据，
        // 不能把预期的 duplicate-column 异常当作正常控制流。
        await EnsureColumnAsync(conn, "Chapters", "AgentInstanceId", "TEXT", logger);

        await ExecuteAsync(conn, """
CREATE TABLE IF NOT EXISTS Pointers (
    PointerId   TEXT PRIMARY KEY,
    WorkspaceId TEXT NULL,
    SourceType  TEXT NULL,
    SourceId    TEXT NULL,
    ChapterId   TEXT NOT NULL REFERENCES Chapters(ChapterId) ON DELETE CASCADE,
    TargetType  TEXT NOT NULL,
    TargetId    TEXT NOT NULL,
    TargetLabel TEXT,
    Description TEXT,
    Relevance   INTEGER NOT NULL DEFAULT 5,
    CreatedAt   INTEGER NOT NULL
);
""");

        await ExecuteAsync(conn, """
CREATE TABLE IF NOT EXISTS SourceReferences (
    SourceReferenceId TEXT PRIMARY KEY,
    WorkspaceId       TEXT NOT NULL,
    OwnerType         TEXT NOT NULL,
    OwnerId           TEXT NOT NULL,
    TargetType        TEXT NOT NULL,
    TargetId          TEXT NOT NULL,
    TargetRange       TEXT,
    Label             TEXT,
    Description       TEXT,
    CreatedAt         INTEGER NOT NULL
);
""");

        await ExecuteAsync(conn, """
CREATE TABLE IF NOT EXISTS MemoryTreeNodes (
    NodeId       TEXT PRIMARY KEY,
    WorkspaceId  TEXT NOT NULL,
    LibraryId    TEXT NOT NULL,
    ParentNodeId TEXT,
    Path         TEXT NOT NULL,
    Name         TEXT NOT NULL,
    Summary      TEXT,
    NodeType     TEXT NOT NULL DEFAULT 'category',
    Status       TEXT NOT NULL DEFAULT 'active',
    SortOrder    INTEGER NOT NULL DEFAULT 0,
    CreatedAt    INTEGER NOT NULL,
    UpdatedAt    INTEGER NOT NULL
);
""");

        await ExecuteAsync(conn, """
CREATE TABLE IF NOT EXISTS BookTreeMounts (
    Id        TEXT PRIMARY KEY,
    BookId    TEXT NOT NULL,
    NodeId    TEXT NOT NULL,
    Weight    INTEGER NOT NULL DEFAULT 1,
    CreatedAt INTEGER NOT NULL
);
""");

        await ExecuteAsync(conn, """
CREATE TABLE IF NOT EXISTS Branches (
    BranchId   TEXT PRIMARY KEY,
    BookId     TEXT NOT NULL REFERENCES Books(BookId) ON DELETE CASCADE,
    BranchName TEXT NOT NULL,
    Description TEXT,
    CreatedBy  TEXT,
    MergedInto TEXT REFERENCES Branches(BranchId),
    IsDefault  INTEGER NOT NULL DEFAULT 0,
    CreatedAt  INTEGER NOT NULL
);
""");

        await ExecuteAsync(conn, """
CREATE TABLE IF NOT EXISTS MemorySpaces (
    MemorySpaceId TEXT PRIMARY KEY,
    WorkspaceId   TEXT NOT NULL,
    AgentId       TEXT NOT NULL,
    Name          TEXT NOT NULL,
    Description   TEXT,
    Status        TEXT NOT NULL DEFAULT 'active',
    CreatedAt     INTEGER NOT NULL,
    UpdatedAt     INTEGER NOT NULL
);
""");

        await ExecuteAsync(conn, """
CREATE TABLE IF NOT EXISTS MemoryFacts (
    FactId                TEXT PRIMARY KEY,
    WorkspaceId           TEXT NOT NULL,
    AgentId               TEXT NOT NULL,
    MemorySpaceId         TEXT NOT NULL,
    Statement             TEXT NOT NULL,
    StructuredPayloadJson TEXT,
    FactType              TEXT NOT NULL,
    Confidence            REAL NOT NULL,
    Status                TEXT NOT NULL DEFAULT 'pending',
    SupersededByFactId    TEXT,
    CreatedByType         TEXT NOT NULL,
    CreatedById           TEXT,
    CreatedAt             INTEGER NOT NULL,
    UpdatedAt             INTEGER NOT NULL,
    AcceptedAt            INTEGER,
    RejectedAt            INTEGER,
    ArchivedAt            INTEGER
);
""");

        await ExecuteAsync(conn, """
CREATE TABLE IF NOT EXISTS MemoryFactEvidence (
    EvidenceId    TEXT PRIMARY KEY,
    WorkspaceId   TEXT NOT NULL,
    AgentId       TEXT NOT NULL,
    MemorySpaceId TEXT NOT NULL,
    FactId        TEXT NOT NULL,
    SourceType    TEXT NOT NULL,
    SourceId      TEXT NOT NULL,
    SourceRange   TEXT,
    QuoteSummary  TEXT,
    EvidenceHash  TEXT,
    Confidence    REAL NOT NULL,
    CreatedAt     INTEGER NOT NULL
);
""");

        await ExecuteAsync(conn, """
CREATE TABLE IF NOT EXISTS MemoryFactContexts (
    ContextId     TEXT PRIMARY KEY,
    WorkspaceId   TEXT NOT NULL,
    AgentId       TEXT NOT NULL,
    MemorySpaceId TEXT NOT NULL,
    FactId        TEXT NOT NULL,
    ContextJson   TEXT NOT NULL,
    ContextHash   TEXT NOT NULL,
    CreatedAt     INTEGER NOT NULL,
    UpdatedAt     INTEGER NOT NULL
);
""");

        await ExecuteAsync(conn, """
CREATE TABLE IF NOT EXISTS MemoryFactFreshness (
    FreshnessId      TEXT PRIMARY KEY,
    WorkspaceId      TEXT NOT NULL,
    AgentId          TEXT NOT NULL,
    MemorySpaceId    TEXT NOT NULL,
    FactId           TEXT NOT NULL,
    ObservedAt       INTEGER,
    LastVerifiedAt   INTEGER,
    ValidFrom        INTEGER,
    ValidTo          INTEGER,
    HalfLifeSeconds  INTEGER,
    DecayKind        TEXT NOT NULL DEFAULT 'stable',
    StaleThreshold   REAL NOT NULL DEFAULT 0.5,
    ExpiredThreshold REAL NOT NULL DEFAULT 0.1,
    RefreshHint      TEXT,
    FreshnessReason  TEXT,
    CreatedAt        INTEGER NOT NULL,
    UpdatedAt        INTEGER NOT NULL
);
""");

        await ExecuteAsync(conn, """
CREATE TABLE IF NOT EXISTS MemoryFactEntityMentions (
    MentionId      TEXT PRIMARY KEY,
    WorkspaceId    TEXT NOT NULL,
    AgentId        TEXT NOT NULL,
    MemorySpaceId  TEXT NOT NULL,
    FactId         TEXT NOT NULL,
    EntityKey      TEXT NOT NULL,
    EntityType     TEXT NOT NULL,
    DisplayName    TEXT NOT NULL,
    Role           TEXT NOT NULL,
    AliasesJson    TEXT,
    PropertiesJson TEXT,
    Confidence     REAL NOT NULL,
    CreatedAt      INTEGER NOT NULL
);
""");

        await ExecuteAsync(conn, """
CREATE TABLE IF NOT EXISTS MemoryFactAssociations (
    AssociationId   TEXT PRIMARY KEY,
    WorkspaceId     TEXT NOT NULL,
    AgentId         TEXT NOT NULL,
    MemorySpaceId   TEXT NOT NULL,
    FactId          TEXT NOT NULL,
    SourceKind      TEXT NOT NULL,
    SourceKey       TEXT NOT NULL,
    TargetKind      TEXT NOT NULL,
    TargetKey       TEXT NOT NULL,
    AssociationType TEXT NOT NULL,
    Weight          REAL NOT NULL,
    Confidence      REAL NOT NULL,
    ContextJson     TEXT,
    EvidenceIdsJson TEXT,
    ObservedAt      INTEGER,
    HalfLifeSeconds INTEGER,
    Reason          TEXT,
    CreatedAt       INTEGER NOT NULL,
    UpdatedAt       INTEGER NOT NULL
);
""");

        await ExecuteAsync(conn, """
CREATE TABLE IF NOT EXISTS MemoryFactRevisions (
    RevisionId    TEXT PRIMARY KEY,
    WorkspaceId   TEXT NOT NULL,
    AgentId       TEXT NOT NULL,
    MemorySpaceId TEXT NOT NULL,
    FactId        TEXT NOT NULL,
    RevisionType  TEXT NOT NULL,
    BeforeJson    TEXT,
    AfterJson     TEXT,
    ActorType     TEXT NOT NULL,
    ActorId       TEXT,
    Reason        TEXT,
    CreatedAt     INTEGER NOT NULL
);
""");

        // ── Phase 1: ChapterRelations 知识图谱边表（幂等）
        await ExecuteAsync(conn, """
CREATE TABLE IF NOT EXISTS ChapterRelations (
    RelationId      TEXT PRIMARY KEY,
    SourceChapterId TEXT NOT NULL,
    TargetChapterId TEXT NOT NULL,
    RelationType    TEXT NOT NULL DEFAULT 'related_to',
    Description     TEXT,
    Weight          REAL NOT NULL DEFAULT 1.0,
    CreatedAt       INTEGER NOT NULL
);
""");

        // ── 兼容迁移：MemoryDbContext.EnsureCreated 先创建了 Libraries 表但不含 AgentId，
        //   此处确保列存在后再建索引（幂等）。
        await EnsureLibraryAgentIdColumnAsync(conn, logger);

        // ── 兼容迁移：旧版 Chapters 表缺少 TitleTokens / ContentTokens 分词列
        await EnsureChapterTokensColumnsAsync(conn, logger);

        // ── 兼容迁移：旧版 Chapters_fts 索引 Title/Content 列，需重建为 TitleTokens/ContentTokens
        await MigrateChaptersFtsColumnsAsync(conn, logger);

        // ── Phase 1: Chapter 元数据扩展 (Scene/Constraints/Tags)
        await EnsureChapterMetadataColumnsAsync(conn, logger);

        // ── R2: Chapter 版本链字段 (active/superseded)
        await EnsureChapterSupersessionColumnsAsync(conn, logger);

        // ── R1: active Book 标题在同一 Library 内唯一，用于并发写入去重
        await EnsureActiveBookTitleUniqueIndexAsync(conn, logger);

        foreach (var sql in IndexStatements)
        {
            await ExecuteAsync(conn, sql);
        }

        foreach (var sql in FtsStatements)
        {
            try
            {
                await ExecuteAsync(conn, sql);
            }
            catch (SqliteException ex) when (ex.SqliteErrorCode == 1)
            {
                logger?.LogWarning(ex, "Memory Library FTS 初始化语句执行失败，已跳过：{Sql}", sql);
            }
        }
    }

    private static readonly string[] IndexStatements =
    [
        "CREATE INDEX IF NOT EXISTS IX_Libraries_Workspace ON Libraries(WorkspaceId);",
        "CREATE INDEX IF NOT EXISTS IX_Libraries_Workspace_Agent ON Libraries(WorkspaceId, AgentId);",
        "CREATE INDEX IF NOT EXISTS IX_Books_Library_UpdatedAt ON Books(LibraryId, UpdatedAt DESC);",
        "CREATE INDEX IF NOT EXISTS IX_Books_Status ON Books(Status, UpdatedAt DESC);",
        "CREATE INDEX IF NOT EXISTS IX_BookIndexes_TagPath ON BookIndexes(TagPath);",
        "CREATE INDEX IF NOT EXISTS IX_BookIndexes_BookId ON BookIndexes(BookId);",
        "CREATE INDEX IF NOT EXISTS IX_Chapters_Book_Order ON Chapters(BookId, ChapterOrder);",
        "CREATE INDEX IF NOT EXISTS IX_Chapters_Book_Status_Order ON Chapters(BookId, Status, ChapterOrder);",
        "CREATE INDEX IF NOT EXISTS IX_Chapters_SupersededBy ON Chapters(SupersededByChapterId);",
        "CREATE INDEX IF NOT EXISTS IX_Chapters_Tags ON Chapters(Tags);",
        "CREATE INDEX IF NOT EXISTS IX_Chapters_Scene ON Chapters(Scene);",
        "CREATE INDEX IF NOT EXISTS IX_ChapterRelations_Source ON ChapterRelations(SourceChapterId);",
        "CREATE INDEX IF NOT EXISTS IX_ChapterRelations_Target ON ChapterRelations(TargetChapterId);",
        "CREATE INDEX IF NOT EXISTS IX_ChapterRelations_Type ON ChapterRelations(SourceChapterId, RelationType);",
        "CREATE INDEX IF NOT EXISTS IX_Pointers_ChapterId ON Pointers(ChapterId);",
        "CREATE INDEX IF NOT EXISTS IX_Pointers_TargetType_TargetId ON Pointers(TargetType, TargetId);",
        "CREATE INDEX IF NOT EXISTS IX_Pointers_WorkspaceId ON Pointers(WorkspaceId);",
        "CREATE INDEX IF NOT EXISTS IX_Pointers_SourceType_SourceId ON Pointers(SourceType, SourceId);",
        "CREATE INDEX IF NOT EXISTS IX_SourceReferences_Owner ON SourceReferences(OwnerType, OwnerId);",
        "CREATE INDEX IF NOT EXISTS IX_SourceReferences_Workspace ON SourceReferences(WorkspaceId, TargetType, TargetId);",
        "CREATE INDEX IF NOT EXISTS IX_MemoryTreeNodes_WS_Lib ON MemoryTreeNodes(WorkspaceId, LibraryId);",
        "CREATE INDEX IF NOT EXISTS IX_MemoryTreeNodes_Parent ON MemoryTreeNodes(ParentNodeId);",
        "CREATE INDEX IF NOT EXISTS IX_MemoryTreeNodes_Path ON MemoryTreeNodes(Path);",
        "CREATE INDEX IF NOT EXISTS IX_BookTreeMounts_BookId ON BookTreeMounts(BookId);",
        "CREATE INDEX IF NOT EXISTS IX_BookTreeMounts_NodeId ON BookTreeMounts(NodeId);",
        "CREATE INDEX IF NOT EXISTS IX_Branches_BookId ON Branches(BookId);",
        "CREATE INDEX IF NOT EXISTS IX_MemorySpaces_Workspace_Agent_Status ON MemorySpaces(WorkspaceId, AgentId, Status);",
        "CREATE INDEX IF NOT EXISTS IX_MemoryFacts_Space_Status ON MemoryFacts(WorkspaceId, AgentId, MemorySpaceId, Status);",
        "CREATE INDEX IF NOT EXISTS IX_MemoryFacts_Type_Status ON MemoryFacts(WorkspaceId, AgentId, FactType, Status);",
        "CREATE INDEX IF NOT EXISTS IX_MemoryFacts_SupersededBy ON MemoryFacts(WorkspaceId, AgentId, SupersededByFactId);",
        "CREATE INDEX IF NOT EXISTS IX_MemoryFactEvidence_Fact ON MemoryFactEvidence(WorkspaceId, AgentId, FactId);",
        "CREATE INDEX IF NOT EXISTS IX_MemoryFactEvidence_Source ON MemoryFactEvidence(WorkspaceId, AgentId, SourceType, SourceId);",
        "CREATE INDEX IF NOT EXISTS IX_MemoryFactEvidence_Hash ON MemoryFactEvidence(WorkspaceId, AgentId, EvidenceHash);",
        "CREATE INDEX IF NOT EXISTS IX_MemoryFactContexts_Fact ON MemoryFactContexts(WorkspaceId, AgentId, FactId);",
        "CREATE INDEX IF NOT EXISTS IX_MemoryFactContexts_Hash ON MemoryFactContexts(WorkspaceId, AgentId, ContextHash);",
        "CREATE INDEX IF NOT EXISTS IX_MemoryFactFreshness_Fact ON MemoryFactFreshness(WorkspaceId, AgentId, FactId);",
        "CREATE INDEX IF NOT EXISTS IX_MemoryFactEntityMentions_Entity ON MemoryFactEntityMentions(WorkspaceId, AgentId, EntityKey);",
        "CREATE INDEX IF NOT EXISTS IX_MemoryFactEntityMentions_Fact ON MemoryFactEntityMentions(WorkspaceId, AgentId, FactId);",
        "CREATE INDEX IF NOT EXISTS IX_MemoryFactAssociations_Source ON MemoryFactAssociations(WorkspaceId, AgentId, SourceKind, SourceKey);",
        "CREATE INDEX IF NOT EXISTS IX_MemoryFactAssociations_Target ON MemoryFactAssociations(WorkspaceId, AgentId, TargetKind, TargetKey);",
        "CREATE INDEX IF NOT EXISTS IX_MemoryFactAssociations_Fact ON MemoryFactAssociations(WorkspaceId, AgentId, FactId);",
        "CREATE INDEX IF NOT EXISTS IX_MemoryFactRevisions_Fact_CreatedAt ON MemoryFactRevisions(WorkspaceId, AgentId, FactId, CreatedAt);",
    ];

    private static readonly string[] FtsStatements =
    [
        "CREATE VIRTUAL TABLE IF NOT EXISTS Books_fts USING fts5(Title, Summary, BookId UNINDEXED, content=Books, content_rowid=rowid);",
        "CREATE TRIGGER IF NOT EXISTS trg_Books_ai AFTER INSERT ON Books BEGIN INSERT INTO Books_fts(rowid, Title, Summary, BookId) VALUES (new.rowid, new.Title, new.Summary, new.BookId); END;",
        "CREATE TRIGGER IF NOT EXISTS trg_Books_ad AFTER DELETE ON Books BEGIN INSERT INTO Books_fts(Books_fts, rowid, Title, Summary, BookId) VALUES ('delete', old.rowid, old.Title, old.Summary, old.BookId); END;",
        "CREATE TRIGGER IF NOT EXISTS trg_Books_au AFTER UPDATE ON Books BEGIN INSERT INTO Books_fts(Books_fts, rowid, Title, Summary, BookId) VALUES ('delete', old.rowid, old.Title, old.Summary, old.BookId); INSERT INTO Books_fts(rowid, Title, Summary, BookId) VALUES (new.rowid, new.Title, new.Summary, new.BookId); END;",
        "CREATE VIRTUAL TABLE IF NOT EXISTS Chapters_fts USING fts5(TitleTokens, ContentTokens, ChapterId UNINDEXED, content=Chapters, content_rowid=rowid);",
        "CREATE TRIGGER IF NOT EXISTS trg_Chapters_ai AFTER INSERT ON Chapters BEGIN INSERT INTO Chapters_fts(rowid, TitleTokens, ContentTokens, ChapterId) VALUES (new.rowid, new.TitleTokens, new.ContentTokens, new.ChapterId); END;",
        "CREATE TRIGGER IF NOT EXISTS trg_Chapters_ad AFTER DELETE ON Chapters BEGIN INSERT INTO Chapters_fts(Chapters_fts, rowid, TitleTokens, ContentTokens, ChapterId) VALUES ('delete', old.rowid, old.TitleTokens, old.ContentTokens, old.ChapterId); END;",
        "CREATE TRIGGER IF NOT EXISTS trg_Chapters_au AFTER UPDATE ON Chapters BEGIN INSERT INTO Chapters_fts(Chapters_fts, rowid, TitleTokens, ContentTokens, ChapterId) VALUES ('delete', old.rowid, old.TitleTokens, old.ContentTokens, old.ChapterId); INSERT INTO Chapters_fts(rowid, TitleTokens, ContentTokens, ChapterId) VALUES (new.rowid, new.TitleTokens, new.ContentTokens, new.ChapterId); END;",
    ];

    private static async Task EnsureLibraryAgentIdColumnAsync(SqliteConnection conn, ILogger? logger)
    {
        try
        {
            using var checkCmd = conn.CreateCommand();
            checkCmd.CommandText = "PRAGMA table_info('Libraries');";
            await using var reader = await checkCmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                if (string.Equals(reader.GetString(1), "AgentId", StringComparison.OrdinalIgnoreCase))
                    return;
            }
            reader.Close();

            using var alterCmd = conn.CreateCommand();
            alterCmd.CommandText = "ALTER TABLE Libraries ADD COLUMN AgentId TEXT NULL;";
            await alterCmd.ExecuteNonQueryAsync();
            logger?.LogInformation("[MemoryLibrary] 已补 Libraries.AgentId 列");
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "[MemoryLibrary] 检查/补列 Libraries.AgentId 失败（继续启动）");
        }
    }

    private static async Task EnsureChapterTokensColumnsAsync(SqliteConnection conn, ILogger? logger)
    {
        try
        {
            // 检查 ContentTokens 列是否存在
            var hasContentTokens = false;
            using var checkCmd = conn.CreateCommand();
            checkCmd.CommandText = "PRAGMA table_info('Chapters');";
            await using var reader = await checkCmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var colName = reader.GetString(1);
                if (string.Equals(colName, "ContentTokens", StringComparison.OrdinalIgnoreCase))
                    hasContentTokens = true;
            }
            reader.Close();

            if (!hasContentTokens)
            {
                using var alter1 = conn.CreateCommand();
                alter1.CommandText = "ALTER TABLE Chapters ADD COLUMN TitleTokens TEXT NOT NULL DEFAULT '';";
                await alter1.ExecuteNonQueryAsync();

                using var alter2 = conn.CreateCommand();
                alter2.CommandText = "ALTER TABLE Chapters ADD COLUMN ContentTokens TEXT NOT NULL DEFAULT '';";
                await alter2.ExecuteNonQueryAsync();

                logger?.LogInformation("[MemoryLibrary] 已补 Chapters.TitleTokens + ContentTokens 列");
            }
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "[MemoryLibrary] 补列 Chapters 失败（继续启动）");
        }
    }

    /// <summary>
    /// Phase 1: 确保 Chapters 表有 Scene / Constraints / Tags 列（幂等）。
    /// </summary>
    private static async Task EnsureChapterMetadataColumnsAsync(SqliteConnection conn, ILogger? logger)
    {
        var columnsToAdd = new[]
        {
            ("Scene",       "TEXT"),  // 适用场景
            ("Constraints", "TEXT"),  // 约束条件
            ("Tags",        "TEXT"),  // 逗号分隔的标签
        };

        // 获取当前列名集合
        var existingColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var pragmaCmd = conn.CreateCommand())
        {
            pragmaCmd.CommandText = "PRAGMA table_info('Chapters');";
            await using var reader = await pragmaCmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                existingColumns.Add(reader.GetString(1));
            }
        }

        foreach (var (colName, colType) in columnsToAdd)
        {
            if (existingColumns.Contains(colName))
                continue;

            try
            {
                using var alterCmd = conn.CreateCommand();
                alterCmd.CommandText = $"ALTER TABLE Chapters ADD COLUMN {colName} {colType};";
                await alterCmd.ExecuteNonQueryAsync();
                logger?.LogInformation("[MemoryLibrary] 已补 Chapters.{Column} 列", colName);
            }
            catch (SqliteException ex) when (ex.Message.Contains("duplicate column name"))
            {
                logger?.LogDebug(ex, "[MemoryLibrary] 跳过重复列: Chapters.{Column}", colName);
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "[MemoryLibrary] 补列 Chapters.{Column} 失败（继续启动）", colName);
            }
        }
    }

    /// <summary>
    /// R1: 为 active Book 建 Library+Title 唯一索引。若历史脏数据已重复，跳过索引以避免启动失败。
    /// </summary>
    private static async Task EnsureActiveBookTitleUniqueIndexAsync(SqliteConnection conn, ILogger? logger)
    {
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                CREATE UNIQUE INDEX IF NOT EXISTS UX_Books_Library_Title_Active
                    ON Books(LibraryId, Title)
                    WHERE Status = 'active';
                """;
            await cmd.ExecuteNonQueryAsync();
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
        {
            logger?.LogWarning(ex,
                "[MemoryLibrary] 跳过 active Book 标题唯一索引：已有重复的 LibraryId+Title active 数据，请先清理重复 Book");
        }
    }

    /// <summary>
    /// R2: 确保 Chapters 表有版本链列（幂等）。
    /// </summary>
    private static async Task EnsureChapterSupersessionColumnsAsync(SqliteConnection conn, ILogger? logger)
    {
        var columnsToAdd = new[]
        {
            ("Status", "TEXT NOT NULL DEFAULT 'active'"),
            ("SupersededByChapterId", "TEXT"),
            ("SupersededAt", "INTEGER"),
        };

        var existingColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var pragmaCmd = conn.CreateCommand())
        {
            pragmaCmd.CommandText = "PRAGMA table_info('Chapters');";
            await using var reader = await pragmaCmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                existingColumns.Add(reader.GetString(1));
            }
        }

        foreach (var (colName, colType) in columnsToAdd)
        {
            if (existingColumns.Contains(colName))
                continue;

            try
            {
                using var alterCmd = conn.CreateCommand();
                alterCmd.CommandText = $"ALTER TABLE Chapters ADD COLUMN {colName} {colType};";
                await alterCmd.ExecuteNonQueryAsync();
                logger?.LogInformation("[MemoryLibrary] 已补 Chapters.{Column} 列", colName);
            }
            catch (SqliteException ex) when (ex.Message.Contains("duplicate column name"))
            {
                logger?.LogDebug(ex, "[MemoryLibrary] 跳过重复列: Chapters.{Column}", colName);
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "[MemoryLibrary] 补列 Chapters.{Column} 失败（继续启动）", colName);
            }
        }
    }

    private static async Task EnsureColumnAsync(
        SqliteConnection conn,
        string tableName,
        string columnName,
        string columnDefinition,
        ILogger? logger)
    {
        using (var checkCmd = conn.CreateCommand())
        {
            checkCmd.CommandText = $"PRAGMA table_info('{tableName}');";
            await using var reader = await checkCmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                if (string.Equals(
                        reader.GetString(1),
                        columnName,
                        StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }
            }
        }

        using var alterCmd = conn.CreateCommand();
        alterCmd.CommandText =
            $"ALTER TABLE {tableName} ADD COLUMN {columnName} {columnDefinition};";
        await alterCmd.ExecuteNonQueryAsync();
        logger?.LogInformation(
            "[MemoryLibrary] 已补 {Table}.{Column} 列",
            tableName,
            columnName);
    }

    /// <summary>
    /// 兼容迁移：旧版 Chapters_fts 使用 Title/Content 列，新版本使用 TitleTokens/ContentTokens。
    /// 检测旧表结构并按需重建 FTS5 虚拟表并回填分词数据。
    /// </summary>
    private static async Task MigrateChaptersFtsColumnsAsync(SqliteConnection conn, ILogger? logger)
    {
        try
        {
            // 检查 Chapters_fts 表是否存在
            using var checkCmd = conn.CreateCommand();
            checkCmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name='Chapters_fts';";
            var tableName = await checkCmd.ExecuteScalarAsync() as string;
            if (string.IsNullOrEmpty(tableName))
                return; // 表不存在，后续 CREATE IF NOT EXISTS 会创建新版

            // 检查旧版列名（Content 表示旧版，ContentTokens 表示已是新版）
            using var pragmaCmd = conn.CreateCommand();
            pragmaCmd.CommandText = "PRAGMA table_info('Chapters_fts');";
            await using var reader = await pragmaCmd.ExecuteReaderAsync();
            var hasOldContent = false;
            while (await reader.ReadAsync())
            {
                var colName = reader.GetString(1);
                if (string.Equals(colName, "Content", StringComparison.OrdinalIgnoreCase))
                    hasOldContent = true;
                if (string.Equals(colName, "ContentTokens", StringComparison.OrdinalIgnoreCase))
                    return; // 已是新版，无需迁移
            }
            reader.Close();

            if (!hasOldContent)
                return; // 没有旧列也无需迁移

            logger?.LogInformation("[MemoryLibrary] 检测到旧版 Chapters_fts (Title/Content)，开始重建为 TitleTokens/ContentTokens...");

            // 删除旧版 triggers 和 FTS 表
            await ExecuteAsync(conn, "DROP TRIGGER IF EXISTS trg_Chapters_ai;");
            await ExecuteAsync(conn, "DROP TRIGGER IF EXISTS trg_Chapters_ad;");
            await ExecuteAsync(conn, "DROP TRIGGER IF EXISTS trg_Chapters_au;");
            await ExecuteAsync(conn, "DROP TABLE IF EXISTS Chapters_fts;");

            // 重新创建新版 FTS5 表（复用 FtsStatements 中的定义）
            foreach (var sql in FtsStatements)
            {
                if (sql.Contains("Chapters_fts") || sql.Contains("trg_Chapters_"))
                {
                    try
                    {
                        await ExecuteAsync(conn, sql);
                    }
                    catch (SqliteException ex) when (ex.SqliteErrorCode == 1)
                    {
                        logger?.LogWarning(ex, "[MemoryLibrary] Chapters_fts 重建语句执行失败: {Sql}", sql);
                    }
                }
            }

            // 回填存量数据
            await ExecuteAsync(conn, @"
                INSERT INTO Chapters_fts(rowid, TitleTokens, ContentTokens, ChapterId)
                SELECT rowid, TitleTokens, ContentTokens, ChapterId FROM Chapters;");

            logger?.LogInformation("[MemoryLibrary] Chapters_fts 重建完成，已迁移至 TitleTokens/ContentTokens");
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "[MemoryLibrary] Chapters_fts 列迁移失败（继续启动）");
        }
    }

    private static async Task ExecuteAsync(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

}
