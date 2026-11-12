using System.Data.Common;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PuddingCode.Abstractions;
using PuddingMemoryEngine.Data;

namespace PuddingMemoryEngineTests;

/// <summary>
/// Chapters.SourceTurnId（Turn 级 provenance）的存储层契约：
/// 写入落库、默认不污染、读回映射、存量库幂等补列、便捷层透传。
/// </summary>
[TestClass]
public sealed class ChapterSourceTurnIdTests
{
    [TestMethod]
    public async Task AddChapter_ShouldPersistSourceTurnId()
    {
        await using var scope = await MemoryLibraryTests.CreateLibraryScopeAsync();
        var book = await CreateBookAsync(scope.Library);

        var chapter = await scope.Library.AddChapterAsync(
            book.BookId, "Turn 章节", "content",
            chapterOrder: 0, sourceSessionId: "session-1", ct: default, sourceTurnId: "t-1");

        Assert.AreEqual("t-1", chapter.SourceTurnId, "AddChapterAsync 返回值应带回 SourceTurnId");

        var stored = await scope.Library.GetChapterAsync(chapter.ChapterId);
        Assert.IsNotNull(stored);
        Assert.AreEqual("t-1", stored.SourceTurnId, "SourceTurnId 应已落到 Chapters 列");
    }

    [TestMethod]
    public async Task AddChapter_WithoutSourceTurnId_ShouldNotBePolluted()
    {
        await using var scope = await MemoryLibraryTests.CreateLibraryScopeAsync();
        var book = await CreateBookAsync(scope.Library);

        var chapter = await scope.Library.AddChapterAsync(book.BookId, "无 Turn 章节", "content");

        Assert.IsNull(chapter.SourceTurnId);

        // 直接读列，避免只验证映射层：不传时必须真的是 NULL，而不是空串或占位值。
        await using var db = await scope.Factory.CreateDbContextAsync();
        var raw = await ExecuteScalarAsync(
            db.Database.GetDbConnection(),
            "SELECT SourceTurnId FROM Chapters WHERE ChapterId = $id;",
            chapter.ChapterId);
        Assert.AreEqual(DBNull.Value, raw, "SourceTurnId 列应为 NULL");
    }

    [TestMethod]
    public async Task ChapterRecord_RoundTripsSourceTurnId()
    {
        await using var scope = await MemoryLibraryTests.CreateLibraryScopeAsync();
        var book = await CreateBookAsync(scope.Library);

        var chapter = await scope.Library.AddChapterAsync(
            book.BookId, "往返章节", "content",
            chapterOrder: 0, sourceSessionId: "session-2", ct: default, sourceTurnId: "t-round-trip");

        var byId = await scope.Library.GetChapterAsync(chapter.ChapterId);
        Assert.IsNotNull(byId);
        Assert.AreEqual("t-round-trip", byId.SourceTurnId);

        var listed = await scope.Library.ListChaptersAsync(book.BookId);
        Assert.HasCount(1, listed);
        Assert.AreEqual("t-round-trip", listed[0].SourceTurnId);
    }

    [TestMethod]
    public async Task Initializer_ShouldHealLegacyChaptersTable_AndBeIdempotent()
    {
        var dbPath = Path.Combine(
            Path.GetTempPath(),
            $"pudding-chapter-provenance-{Guid.NewGuid():N}.db");

        try
        {
            var connectionString = $"Data Source={dbPath}";
            await CreateLegacyChaptersTableAsync(connectionString);

            var options = new DbContextOptionsBuilder<MemoryLibraryDbContext>()
                .UseSqlite(connectionString)
                .Options;
            var factory = new SqliteDbContextFactory(options);

            await MemoryLibraryDbInitializer.InitializeAsync(factory);

            Assert.IsTrue(
                await ColumnExistsAsync(connectionString, "Chapters", "SourceTurnId"),
                "初始化后 Chapters.SourceTurnId 列应存在");
            Assert.AreEqual(
                1,
                await ColumnCountAsync(connectionString, "Chapters", "SourceTurnId"),
                "补列必须是新增一列，不能重复");

            // 存量行不能被重建表顺带丢掉，且新列在旧行上为 NULL。
            var legacyRow = await ReadLegacyRowAsync(connectionString);
            Assert.AreEqual("legacy-title", legacyRow.Title);
            Assert.AreEqual("legacy-content", legacyRow.Content);
            Assert.AreEqual(11, legacyRow.WordCount);
            Assert.AreEqual("legacy-session", legacyRow.SourceSessionId);
            Assert.IsNull(legacyRow.SourceTurnId);

            // 幂等：同一库再跑一次不抛错，也不会再补一列。
            await MemoryLibraryDbInitializer.InitializeAsync(factory);
            Assert.AreEqual(
                1,
                await ColumnCountAsync(connectionString, "Chapters", "SourceTurnId"));
            Assert.AreEqual("legacy-content", (await ReadLegacyRowAsync(connectionString)).Content);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(dbPath))
                File.Delete(dbPath);
        }
    }

    [TestMethod]
    public async Task UpsertExperience_ShouldPassThroughSourceTurnId()
    {
        await using var scope = await MemoryLibraryTests.CreateLibraryScopeAsync(enableFts5: true);

        var result = await scope.Convenience.UpsertExperienceAsync(
            "ws-turn",
            new ExperiencePackage
            {
                Title = "Turn 透传经验",
                Content = "便捷层必须把 ExperiencePackage.SourceTurnId 透传到章节。",
                SourceSessionId = "session-3",
                SourceTurnId = "t-3",
            });

        Assert.AreEqual("t-3", result.Chapter.SourceTurnId);

        var stored = await scope.Library.GetChapterAsync(result.Chapter.ChapterId);
        Assert.IsNotNull(stored);
        Assert.AreEqual("t-3", stored.SourceTurnId);
    }

    private static async Task<BookRecord> CreateBookAsync(IMemoryLibrary library)
    {
        var created = await library.CreateLibraryAsync("ws-turn", "Turn 图书馆", null);
        return await library.CreateBookAsync(created.LibraryId, "Turn 书", "摘要");
    }

    /// <summary>造一个 ADR-028 之前的存量库：Chapters 缺 SourceTurnId，且已有数据。</summary>
    private static async Task CreateLegacyChaptersTableAsync(string connectionString)
    {
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync();

        // Microsoft.Data.Sqlite 默认开启外键；夹具只造 Chapters（Books 由被测初始化器创建），
        // 故在造数据阶段关掉外键约束。
        await using (var pragma = connection.CreateCommand())
        {
            pragma.CommandText = "PRAGMA foreign_keys=OFF;";
            await pragma.ExecuteNonQueryAsync();
        }

        await using (var create = connection.CreateCommand())
        {
            create.CommandText = """
                CREATE TABLE Chapters (
                    ChapterId       TEXT PRIMARY KEY,
                    BookId          TEXT NOT NULL REFERENCES Books(BookId) ON DELETE CASCADE,
                    Title           TEXT NOT NULL,
                    ChapterOrder    INTEGER NOT NULL DEFAULT 0,
                    Content         TEXT NOT NULL,
                    ContentType     TEXT NOT NULL DEFAULT 'markdown',
                    Importance      REAL NOT NULL DEFAULT 0.5,
                    SourceSessionId TEXT,
                    WordCount       INTEGER NOT NULL DEFAULT 0,
                    CreatedAt       INTEGER NOT NULL,
                    UpdatedAt       INTEGER NOT NULL
                );
                """;
            await create.ExecuteNonQueryAsync();
        }

        await using (var insert = connection.CreateCommand())
        {
            insert.CommandText = """
                INSERT INTO Chapters
                    (ChapterId, BookId, Title, ChapterOrder, Content, ContentType,
                     Importance, SourceSessionId, WordCount, CreatedAt, UpdatedAt)
                VALUES
                    ('legacy-1', 'legacy-book', 'legacy-title', 0, 'legacy-content', 'markdown',
                     0.5, 'legacy-session', 11, 1700000000000, 1700000000000);
                """;
            await insert.ExecuteNonQueryAsync();
        }
    }

    private static async Task<bool> ColumnExistsAsync(
        string connectionString, string table, string column)
        => await ColumnCountAsync(connectionString, table, column) > 0;

    private static async Task<int> ColumnCountAsync(
        string connectionString, string table, string column)
    {
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info('{table}');";
        await using var reader = await command.ExecuteReaderAsync();
        var count = 0;
        while (await reader.ReadAsync())
        {
            if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase))
                count++;
        }
        return count;
    }

    private static async Task<(string Title, string Content, int WordCount, string? SourceSessionId, string? SourceTurnId)>
        ReadLegacyRowAsync(string connectionString)
    {
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Title, Content, WordCount, SourceSessionId, SourceTurnId
            FROM Chapters WHERE ChapterId = 'legacy-1';
            """;
        await using var reader = await command.ExecuteReaderAsync();
        Assert.IsTrue(await reader.ReadAsync(), "存量行 legacy-1 不应丢失");
        return (
            reader.GetString(0),
            reader.GetString(1),
            reader.GetInt32(2),
            reader.IsDBNull(3) ? null : reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetString(4));
    }

    private static async Task<object?> ExecuteScalarAsync(
        DbConnection connection, string sql, string chapterId)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var parameter = command.CreateParameter();
        parameter.ParameterName = "$id";
        parameter.Value = chapterId;
        command.Parameters.Add(parameter);
        return await command.ExecuteScalarAsync();
    }

    private sealed class SqliteDbContextFactory(DbContextOptions<MemoryLibraryDbContext> options)
        : IDbContextFactory<MemoryLibraryDbContext>
    {
        public MemoryLibraryDbContext CreateDbContext() => new(options);

        public Task<MemoryLibraryDbContext> CreateDbContextAsync(
            CancellationToken cancellationToken = default)
            => Task.FromResult(new MemoryLibraryDbContext(options));
    }
}
