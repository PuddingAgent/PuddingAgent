using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace PuddingMemoryEngine.Data;

/// <summary>
/// 记忆图书馆数据库初始化器。
/// MemoryDbContext 和 MemoryLibraryDbContext 共享同一 SQLite 文件，因此不能依赖 EnsureCreated。
/// 本初始化器使用独立连接执行幂等 DDL，兼容旧 workspace-only 图书馆和 ADR-030 agent-bound 图书馆。
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
        await EnsureColumnAsync(conn, "Libraries", "AgentId", "TEXT NULL", logger);

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
        await EnsureColumnAsync(conn, "Books", "Embedding", "BLOB", logger);

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
    SourceSessionId TEXT,
    WordCount       INTEGER NOT NULL DEFAULT 0,
    Embedding       BLOB,
    SourceReference TEXT,
    ReferenceType   TEXT,
    CreatedAt       INTEGER NOT NULL,
    UpdatedAt       INTEGER NOT NULL
);
""");
        await EnsureColumnAsync(conn, "Chapters", "Embedding", "BLOB", logger);
        await EnsureColumnAsync(conn, "Chapters", "SourceReference", "TEXT", logger);
        await EnsureColumnAsync(conn, "Chapters", "ReferenceType", "TEXT", logger);

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
        await EnsureColumnAsync(conn, "Pointers", "WorkspaceId", "TEXT", logger);
        await EnsureColumnAsync(conn, "Pointers", "SourceType", "TEXT", logger);
        await EnsureColumnAsync(conn, "Pointers", "SourceId", "TEXT", logger);

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
    ];

    private static readonly string[] FtsStatements =
    [
        "CREATE VIRTUAL TABLE IF NOT EXISTS Books_fts USING fts5(Title, Summary, BookId UNINDEXED, content=Books, content_rowid=rowid);",
        "CREATE TRIGGER IF NOT EXISTS trg_Books_ai AFTER INSERT ON Books BEGIN INSERT INTO Books_fts(rowid, Title, Summary, BookId) VALUES (new.rowid, new.Title, new.Summary, new.BookId); END;",
        "CREATE TRIGGER IF NOT EXISTS trg_Books_ad AFTER DELETE ON Books BEGIN INSERT INTO Books_fts(Books_fts, rowid, Title, Summary, BookId) VALUES ('delete', old.rowid, old.Title, old.Summary, old.BookId); END;",
        "CREATE TRIGGER IF NOT EXISTS trg_Books_au AFTER UPDATE ON Books BEGIN INSERT INTO Books_fts(Books_fts, rowid, Title, Summary, BookId) VALUES ('delete', old.rowid, old.Title, old.Summary, old.BookId); INSERT INTO Books_fts(rowid, Title, Summary, BookId) VALUES (new.rowid, new.Title, new.Summary, new.BookId); END;",
        "CREATE VIRTUAL TABLE IF NOT EXISTS Chapters_fts USING fts5(Title, Content, ChapterId UNINDEXED, content=Chapters, content_rowid=rowid);",
        "CREATE TRIGGER IF NOT EXISTS trg_Chapters_ai AFTER INSERT ON Chapters BEGIN INSERT INTO Chapters_fts(rowid, Title, Content, ChapterId) VALUES (new.rowid, new.Title, new.Content, new.ChapterId); END;",
        "CREATE TRIGGER IF NOT EXISTS trg_Chapters_ad AFTER DELETE ON Chapters BEGIN INSERT INTO Chapters_fts(Chapters_fts, rowid, Title, Content, ChapterId) VALUES ('delete', old.rowid, old.Title, old.Content, old.ChapterId); END;",
        "CREATE TRIGGER IF NOT EXISTS trg_Chapters_au AFTER UPDATE ON Chapters BEGIN INSERT INTO Chapters_fts(Chapters_fts, rowid, Title, Content, ChapterId) VALUES ('delete', old.rowid, old.Title, old.Content, old.ChapterId); INSERT INTO Chapters_fts(rowid, Title, Content, ChapterId) VALUES (new.rowid, new.Title, new.Content, new.ChapterId); END;",
    ];

    private static async Task ExecuteAsync(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task EnsureColumnAsync(
        SqliteConnection conn,
        string tableName,
        string columnName,
        string definition,
        ILogger? logger)
    {
        if (await ColumnExistsAsync(conn, tableName, columnName)) return;

        var sql = $"ALTER TABLE \"{tableName}\" ADD COLUMN \"{columnName}\" {definition};";
        await ExecuteAsync(conn, sql);
        logger?.LogInformation("[MemoryLibrarySchema] 已补列：{Sql}", sql);
    }

    private static async Task<bool> ColumnExistsAsync(SqliteConnection conn, string tableName, string columnName)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info(\"{tableName}\");";
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            if (string.Equals(reader.GetString(1), columnName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
