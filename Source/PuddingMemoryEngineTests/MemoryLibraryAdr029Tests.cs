using System.Data.Common;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using PuddingCode.Abstractions;
using PuddingCode.Models;
using PuddingCode.Tools;
using PuddingMemoryEngine.Data;
using PuddingRuntime.Services.Tools;

namespace PuddingMemoryEngineTests;

/// <summary>
/// ADR-029 回归测试：schema 升级、workspace 隔离、Pointer 泛化。
/// </summary>
[TestClass]
public sealed class MemoryLibraryAdr029Tests
{
    // ── DbInitializer 测试 ────────────────────────────────────────────

    [TestMethod]
    public async Task DbInitializer_ShouldNotCrashOnDuplicateColumn()
    {
        // 模拟旧库：手工创建旧表并预先加上 Books.Embedding
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await SetupOldSchemaAsync(connection);
        // 制造 duplicate column
        using (var addEmbedCmd = connection.CreateCommand())
        {
            addEmbedCmd.CommandText = "ALTER TABLE Books ADD COLUMN Embedding BLOB;";
            await addEmbedCmd.ExecuteNonQueryAsync();
        }

        // 逐条执行 init_library.sql——不应崩溃
        var sqlPath = FindInitSqlPath();
        if (!File.Exists(sqlPath))
            Assert.Inconclusive($"init_library.sql not found at {sqlPath}");

        var sqlText = await File.ReadAllTextAsync(sqlPath);
        var statements = SplitSqlStatements(sqlText);
        foreach (var stmt in statements)
        {
            if (string.IsNullOrWhiteSpace(stmt)) continue;
            using var cmd = connection.CreateCommand();
            cmd.CommandText = stmt;
            try
            {
                await cmd.ExecuteNonQueryAsync();
            }
            catch (SqliteException ex) when (
                ex.Message.Contains("already exists", StringComparison.OrdinalIgnoreCase)
                || ex.Message.Contains("duplicate column name", StringComparison.OrdinalIgnoreCase)
                || ex.Message.Contains("no such column", StringComparison.OrdinalIgnoreCase))
            {
                // 幂等忽略
            }
        }

        // 断言后续表仍存在
        await AssertTableExistsAsync(connection, "SourceReferences");
        await AssertTableExistsAsync(connection, "MemoryTreeNodes");
        await AssertTableExistsAsync(connection, "BookTreeMounts");
    }

    [TestMethod]
    public async Task DbInitializer_ShouldCreateAdr028TablesOnFreshDatabase()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        // 仅执行 init_library.sql——不经过 EF EnsureCreated
        var sqlPath = FindInitSqlPath();
        if (!File.Exists(sqlPath))
            Assert.Inconclusive($"init_library.sql not found at {sqlPath}");

        var sqlText = await File.ReadAllTextAsync(sqlPath);
        var statements = SplitSqlStatements(sqlText);
        foreach (var stmt in statements)
        {
            if (string.IsNullOrWhiteSpace(stmt)) continue;
            using var cmd = connection.CreateCommand();
            cmd.CommandText = stmt;
            try
            {
                await cmd.ExecuteNonQueryAsync();
            }
            catch (SqliteException ex) when (
                ex.Message.Contains("already exists", StringComparison.OrdinalIgnoreCase)
                || ex.Message.Contains("duplicate column name", StringComparison.OrdinalIgnoreCase))
            {
                // 幂等忽略
            }
        }

        await AssertTableExistsAsync(connection, "SourceReferences");
        await AssertTableExistsAsync(connection, "MemoryTreeNodes");
        await AssertTableExistsAsync(connection, "BookTreeMounts");
        await AssertColumnExistsAsync(connection, "Pointers", "WorkspaceId");
        await AssertColumnExistsAsync(connection, "Pointers", "SourceType");
        await AssertColumnExistsAsync(connection, "Pointers", "SourceId");
        await AssertColumnExistsAsync(connection, "Chapters", "SourceReference");
        await AssertColumnExistsAsync(connection, "Chapters", "ReferenceType");
    }

    // ── Workspace 隔离测试 ────────────────────────────────────────────

    [TestMethod]
    public async Task GrepMemory_ShouldNotReturnOtherWorkspace()
    {
        await using var scope = await MemoryLibraryTests.CreateLibraryScopeAsync(enableFts5: true);
        var libA = await scope.Library.CreateLibraryAsync("ws-a", "A", null);
        var bookA = await scope.Library.CreateBookAsync(libA.LibraryId, "A记忆", "隔离");
        await scope.Library.AddChapterAsync(bookA.BookId, "A章节", "唯一关键词XYZ789", 0);

        var libB = await scope.Library.CreateLibraryAsync("ws-b", "B", null);
        var bookB = await scope.Library.CreateBookAsync(libB.LibraryId, "B记忆", "无关");
        await scope.Library.AddChapterAsync(bookB.BookId, "B章节", "无关内容", 0);

        var tool = new GrepMemoryTool(scope.Convenience, scope.Library, NullLogger<GrepMemoryTool>.Instance);

        var skillResult = await ExecuteToolAsync(tool, "ws-a", new Dictionary<string, string>
        {
            ["action"] = "search",
            ["query"] = "唯一关键词XYZ789",
            ["top_k"] = "10"
        });
        Assert.IsTrue(skillResult.Success, skillResult.Error);
        using var resultDoc = JsonDocument.Parse(skillResult.Output!);
        Assert.AreEqual(1, resultDoc.RootElement.GetProperty("count").GetInt32(), "ws-a should find its own chapter");
        Assert.AreEqual("A章节", resultDoc.RootElement.GetProperty("results")[0].GetProperty("ChapterTitle").GetString());

        var skillResultB = await ExecuteToolAsync(tool, "ws-b", new Dictionary<string, string>
        {
            ["action"] = "search",
            ["query"] = "唯一关键词XYZ789",
            ["top_k"] = "10"
        });
        Assert.IsTrue(skillResultB.Success, skillResultB.Error);
        using var resultDocB = JsonDocument.Parse(skillResultB.Output!);
        Assert.AreEqual(0, resultDocB.RootElement.GetProperty("count").GetInt32(), "ws-b should NOT find ws-a chapters");
    }

    [TestMethod]
    public async Task SaveMemorySkill_ShouldInjectWorkspace()
    {
        await using var scope = await MemoryLibraryTests.CreateLibraryScopeAsync(enableFts5: true);
        var tool = new SaveMemoryTool(scope.Convenience, scope.Library, NullLogger<SaveMemoryTool>.Instance);

        var result = await ExecuteToolAsync(tool, "ws-tool-test", new Dictionary<string, string>
        {
            ["type"] = "fact",
            ["content"] = "workspace 注入验证内容"
        });

        Assert.IsTrue(result.Success, result.Error);
        var libs = await scope.Library.ListLibrariesAsync("ws-tool-test");
        Assert.AreEqual(1, libs.Count, "Should have created library in ws-tool-test");
    }

    // ── Pointer 泛化测试 ──────────────────────────────────────────────

    [TestMethod]
    public async Task CreateGeneralPointer_ShouldSupportBookToUrl()
    {
        await using var scope = await MemoryLibraryTests.CreateLibraryScopeAsync();
        var lib = await scope.Library.CreateLibraryAsync("ws-ptr", "PtrLib", null);
        var book = await scope.Library.CreateBookAsync(lib.LibraryId, "PtrBook", "Test");
        var chapter = await scope.Library.AddChapterAsync(book.BookId, "Ch", "content", 0);

        // 通过泛化 API 创建从 book 到 url 的指针
        var ptr = await scope.Library.CreateGeneralPointerAsync(
            "ws-ptr", "book", book.BookId,
            "url", "https://example.com/doc",
            "参考文档", "外部参考");

        Assert.IsNotNull(ptr);
        Assert.AreEqual("ws-ptr", ptr.WorkspaceId);
        Assert.AreEqual("book", ptr.SourceType);
        Assert.AreEqual(book.BookId, ptr.SourceId);
        Assert.AreEqual("url", ptr.TargetType);
        Assert.AreEqual("https://example.com/doc", ptr.TargetId);
    }

    [TestMethod]
    public async Task GetPointersBySource_ShouldBeWorkspaceScoped()
    {
        await using var scope = await MemoryLibraryTests.CreateLibraryScopeAsync();
        var libA = await scope.Library.CreateLibraryAsync("ws-pa", "PtrLibA", null);
        var bookA = await scope.Library.CreateBookAsync(libA.LibraryId, "BookA", "A");
        await scope.Library.CreateGeneralPointerAsync("ws-pa", "book", bookA.BookId, "url", "https://a.com");

        var libB = await scope.Library.CreateLibraryAsync("ws-pb", "PtrLibB", null);
        var bookB = await scope.Library.CreateBookAsync(libB.LibraryId, "BookB", "B");
        await scope.Library.CreateGeneralPointerAsync("ws-pb", "book", bookB.BookId, "url", "https://b.com");

        // ws-pa scope
        var ptrsA = await scope.Library.GetPointersBySourceAsync("ws-pa", "book", bookA.BookId);
        Assert.AreEqual(1, ptrsA.Count);
        Assert.AreEqual("https://a.com", ptrsA[0].TargetId);

        // ws-pb scope
        var ptrsB = await scope.Library.GetPointersBySourceAsync("ws-pb", "book", bookB.BookId);
        Assert.AreEqual(1, ptrsB.Count);
        Assert.AreEqual("https://b.com", ptrsB[0].TargetId);

        // cross-workspace should find nothing
        var ptrsCross = await scope.Library.GetPointersBySourceAsync("ws-pa", "book", bookB.BookId);
        Assert.AreEqual(0, ptrsCross.Count);
    }

    // ── Helpers ────────────────────────────────────────────────────────

    private static Task<ToolExecutionResult> ExecuteToolAsync(
        IPuddingTool tool,
        string workspaceId,
        Dictionary<string, string> parameters)
    {
        return tool.ExecuteAsync(new ToolExecutionRequest
        {
            ToolCallId = "test-call",
            ArgumentsJson = JsonSerializer.Serialize(parameters),
            Context = new ToolExecutionContext
            {
                AgentInstanceId = "test-agent",
                WorkspaceId = workspaceId,
                SessionId = "test-session",
            },
        });
    }

    private static async Task SetupOldSchemaAsync(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS Libraries (LibraryId TEXT PRIMARY KEY, WorkspaceId TEXT NOT NULL, Name TEXT NOT NULL, Description TEXT, CreatedAt INTEGER NOT NULL, UpdatedAt INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS Books (BookId TEXT PRIMARY KEY, LibraryId TEXT NOT NULL, Title TEXT NOT NULL, Summary TEXT NOT NULL DEFAULT '', Status TEXT NOT NULL DEFAULT 'active', Version INTEGER NOT NULL DEFAULT 1, AccessCount INTEGER NOT NULL DEFAULT 0, LastAccessedAt INTEGER, CreatedAt INTEGER NOT NULL, UpdatedAt INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS Chapters (ChapterId TEXT PRIMARY KEY, BookId TEXT NOT NULL, Title TEXT NOT NULL, ChapterOrder INTEGER NOT NULL DEFAULT 0, Content TEXT NOT NULL, ContentType TEXT NOT NULL DEFAULT 'markdown', Importance REAL NOT NULL DEFAULT 0.5, SourceSessionId TEXT, WordCount INTEGER NOT NULL DEFAULT 0, TitleTokens TEXT NOT NULL DEFAULT '', ContentTokens TEXT NOT NULL DEFAULT '', CreatedAt INTEGER NOT NULL, UpdatedAt INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS Pointers (PointerId TEXT PRIMARY KEY, ChapterId TEXT NOT NULL, TargetType TEXT NOT NULL, TargetId TEXT NOT NULL, TargetLabel TEXT, Description TEXT, Relevance INTEGER NOT NULL DEFAULT 5, CreatedAt INTEGER NOT NULL);
            """;
        await cmd.ExecuteNonQueryAsync();
    }

    private static string FindInitSqlPath()
    {
        var schemaDir = Path.Combine(AppContext.BaseDirectory, "Schema");
        var sqlPath = Path.Combine(schemaDir, "init_library.sql");
        if (File.Exists(sqlPath)) return sqlPath;

        // 开发环境 fallback
        sqlPath = Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..", "..",
            "Source", "PuddingMemoryEngine", "Schema", "init_library.sql");
        return sqlPath;
    }

    private static List<string> SplitSqlStatements(string sql)
    {
        var statements = new List<string>();
        var current = new System.Text.StringBuilder();
        var insideTrigger = false;
        foreach (var line in sql.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("CREATE TRIGGER", StringComparison.OrdinalIgnoreCase))
            {
                insideTrigger = true;
            }

            current.AppendLine(line);

            if (insideTrigger)
            {
                if (!trimmed.Equals("END;", StringComparison.OrdinalIgnoreCase))
                    continue;

                statements.Add(current.ToString());
                current.Clear();
                insideTrigger = false;
                continue;
            }

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

    private static async Task AssertTableExistsAsync(SqliteConnection conn, string tableName)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name=@name";
        cmd.Parameters.Add(new SqliteParameter("@name", tableName));
        var result = await cmd.ExecuteScalarAsync();
        Assert.IsNotNull(result, $"Table {tableName} should exist");
    }

    private static async Task AssertColumnExistsAsync(SqliteConnection conn, string tableName, string columnName)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info({tableName})";
        var exists = false;
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            if (reader.GetString(1) == columnName)
            {
                exists = true;
                break;
            }
        }
        Assert.IsTrue(exists, $"Column {tableName}.{columnName} should exist");
    }
}
