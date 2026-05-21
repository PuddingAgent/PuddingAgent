using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PuddingCode.Abstractions;
using PuddingMemoryEngine.Data;
using PuddingPlatform.Services;

namespace PuddingPlatformTests.Services;

[TestClass]
public sealed class MemoryLibraryAdminServiceTests
{
    [TestMethod]
    public async Task GetOverview_ShouldCountLibrariesAndBooks()
    {
        await using var scope = await CreateAdminTestScopeAsync();
        var lib = await scope.Library.CreateLibraryAsync("ws-admin", "LibA", null);
        await scope.Library.CreateBookAsync(lib.LibraryId, "Book1", "Summary1");
        await scope.Library.CreateBookAsync(lib.LibraryId, "Book2", "Summary2");

        var overview = await scope.Service.GetOverviewAsync("ws-admin");

        Assert.AreEqual("ws-admin", overview.WorkspaceId);
        Assert.AreEqual(1, overview.LibraryCount);
        Assert.AreEqual(2, overview.BookCount);
    }

    [TestMethod]
    public async Task GetOverview_ShouldNotCountOtherWorkspace()
    {
        await using var scope = await CreateAdminTestScopeAsync();
        var libA = await scope.Library.CreateLibraryAsync("ws-a", "LibA", null);
        await scope.Library.CreateBookAsync(libA.LibraryId, "BookA", "SummaryA");
        var libB = await scope.Library.CreateLibraryAsync("ws-b", "LibB", null);
        await scope.Library.CreateBookAsync(libB.LibraryId, "BookB", "SummaryB");

        var overviewA = await scope.Service.GetOverviewAsync("ws-a");
        Assert.AreEqual(1, overviewA.LibraryCount);
        Assert.AreEqual(1, overviewA.BookCount);
    }

    [TestMethod]
    public async Task GetTree_ShouldReturnRootNodes()
    {
        await using var scope = await CreateAdminTestScopeAsync();
        var lib = await scope.Library.CreateLibraryAsync("ws-tree", "TreeLib", null);
        await scope.Library.CreateTreeNodeAsync("ws-tree", lib.LibraryId, null, "系统记忆", null, "system");
        await scope.Library.CreateTreeNodeAsync("ws-tree", lib.LibraryId, null, "项目知识", null, "category");

        var tree = await scope.Service.GetTreeAsync("ws-tree", lib.LibraryId);

        Assert.HasCount(2, tree);
        Assert.AreEqual("系统记忆", tree[0].Title);
        Assert.AreEqual("项目知识", tree[1].Title);
    }

    [TestMethod]
    public async Task GetTree_ShouldReturnNestedNodes()
    {
        await using var scope = await CreateAdminTestScopeAsync();
        var lib = await scope.Library.CreateLibraryAsync("ws-nested", "NestedLib", null);
        var parent = await scope.Library.CreateTreeNodeAsync("ws-nested", lib.LibraryId, null, "技术", null, "category");
        await scope.Library.CreateTreeNodeAsync("ws-nested", lib.LibraryId, parent.NodeId, "数据库", null, "topic");

        var tree = await scope.Service.GetTreeAsync("ws-nested", lib.LibraryId);

        Assert.HasCount(1, tree);
        Assert.HasCount(1, tree[0].Children);
        Assert.AreEqual("数据库", tree[0].Children[0].Title);
    }

    [TestMethod]
    public async Task GetBookPage_ShouldReturnBookWithChapters()
    {
        await using var scope = await CreateAdminTestScopeAsync();
        var lib = await scope.Library.CreateLibraryAsync("ws-book", "BookLib", null);
        var book = await scope.Library.CreateBookAsync(lib.LibraryId, "测试手册", "一本测试手册");
        await scope.Library.AddChapterAsync(book.BookId, "第一章", "内容1", chapterOrder: 0);
        await scope.Library.AddChapterAsync(book.BookId, "第二章", "内容2", chapterOrder: 1);

        var page = await scope.Service.GetBookPageAsync("ws-book", book.BookId);

        Assert.AreEqual("测试手册", page.Title);
        Assert.AreEqual("一本测试手册", page.Summary);
        Assert.HasCount(2, page.Chapters);
        Assert.AreEqual("第一章", page.Chapters[0].Title);
    }

    [TestMethod]
    public async Task GetBookPage_ShouldThrowOnWrongWorkspace()
    {
        await using var scope = await CreateAdminTestScopeAsync();
        var lib = await scope.Library.CreateLibraryAsync("ws-book-a", "LibA", null);
        var book = await scope.Library.CreateBookAsync(lib.LibraryId, "BookA", "Summary");

        try
        {
            await scope.Service.GetBookPageAsync("ws-book-b", book.BookId);
            Assert.Fail("Expected UnauthorizedAccessException was not thrown.");
        }
        catch (UnauthorizedAccessException)
        {
            // Expected
        }
    }

    [TestMethod]
    public async Task SearchAsync_ShouldOnlyReturnCurrentWorkspace()
    {
        await using var scope = await CreateAdminTestScopeAsync(enableFts5: true);
        var libA = await scope.Library.CreateLibraryAsync("ws-admin-a", "A", null);
        var bookA = await scope.Library.CreateBookAsync(libA.LibraryId, "A", "A");
        await scope.Library.AddChapterAsync(bookA.BookId, "A", "shared needle");

        var libB = await scope.Library.CreateLibraryAsync("ws-admin-b", "B", null);
        var bookB = await scope.Library.CreateBookAsync(libB.LibraryId, "B", "B");
        await scope.Library.AddChapterAsync(bookB.BookId, "B", "shared needle");

        var hits = await scope.Service.SearchAsync("ws-admin-a", "shared", 10);

        Assert.IsTrue(hits.All(x => x.BookId == bookA.BookId));
    }

    [TestMethod]
    public async Task SearchAsync_ShouldReturnEmptyWhenNoMatch()
    {
        await using var scope = await CreateAdminTestScopeAsync(enableFts5: true);
        var lib = await scope.Library.CreateLibraryAsync("ws-empty", "EmptyLib", null);
        var book = await scope.Library.CreateBookAsync(lib.LibraryId, "Empty", "Empty");
        await scope.Library.AddChapterAsync(book.BookId, "Empty", "nothing here");

        var hits = await scope.Service.SearchAsync("ws-empty", "zzznotfound", 10);

        Assert.HasCount(0, hits);
    }

    // ── Test Infrastructure ────────────────────────────────────────────

    private static async Task<AdminTestScope> CreateAdminTestScopeAsync(bool enableFts5 = false)
    {
        var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();

        using (var pragmaCmd = connection.CreateCommand())
        {
            pragmaCmd.CommandText = "PRAGMA foreign_keys=ON;";
            await pragmaCmd.ExecuteNonQueryAsync();
        }

        var options = new DbContextOptionsBuilder<MemoryLibraryDbContext>()
            .UseSqlite(connection)
            .EnableSensitiveDataLogging()
            .Options;

        var factory = new AdminTestDbContextFactory(options);

        await using (var db = factory.CreateDbContext())
        {
            await db.Database.EnsureCreatedAsync();
        }

        if (enableFts5)
        {
            await SetupFts5Async(connection);
        }

        var library = new MemoryLibrary(factory);
        var service = new MemoryLibraryAdminService(library);

        return new AdminTestScope(connection, factory, library, service);
    }

    private static async Task SetupFts5Async(SqliteConnection connection)
    {
        var statements = new[]
        {
            "CREATE VIRTUAL TABLE IF NOT EXISTS Books_fts USING fts5(Title, Summary, BookId UNINDEXED, content=Books, content_rowid=rowid)",
            "CREATE TRIGGER IF NOT EXISTS trg_Books_ai AFTER INSERT ON Books BEGIN INSERT INTO Books_fts(rowid, Title, Summary, BookId) VALUES (new.rowid, new.Title, new.Summary, new.BookId); END",
            "CREATE TRIGGER IF NOT EXISTS trg_Books_ad AFTER DELETE ON Books BEGIN INSERT INTO Books_fts(Books_fts, rowid, Title, Summary, BookId) VALUES ('delete', old.rowid, old.Title, old.Summary, old.BookId); END",
            "CREATE TRIGGER IF NOT EXISTS trg_Books_au AFTER UPDATE ON Books BEGIN INSERT INTO Books_fts(Books_fts, rowid, Title, Summary, BookId) VALUES ('delete', old.rowid, old.Title, old.Summary, old.BookId); INSERT INTO Books_fts(rowid, Title, Summary, BookId) VALUES (new.rowid, new.Title, new.Summary, new.BookId); END",
            "CREATE VIRTUAL TABLE IF NOT EXISTS Chapters_fts USING fts5(Title, Content, ChapterId UNINDEXED, content=Chapters, content_rowid=rowid)",
            "CREATE TRIGGER IF NOT EXISTS trg_Chapters_ai AFTER INSERT ON Chapters BEGIN INSERT INTO Chapters_fts(rowid, Title, Content, ChapterId) VALUES (new.rowid, new.Title, new.Content, new.ChapterId); END",
            "CREATE TRIGGER IF NOT EXISTS trg_Chapters_ad AFTER DELETE ON Chapters BEGIN INSERT INTO Chapters_fts(Chapters_fts, rowid, Title, Content, ChapterId) VALUES ('delete', old.rowid, old.Title, old.Content, old.ChapterId); END",
            "CREATE TRIGGER IF NOT EXISTS trg_Chapters_au AFTER UPDATE ON Chapters BEGIN INSERT INTO Chapters_fts(Chapters_fts, rowid, Title, Content, ChapterId) VALUES ('delete', old.rowid, old.Title, old.Content, old.ChapterId); INSERT INTO Chapters_fts(rowid, Title, Content, ChapterId) VALUES (new.rowid, new.Title, new.Content, new.ChapterId); END",
        };

        foreach (var sql in statements)
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = sql;
            await cmd.ExecuteNonQueryAsync();
        }
    }

    private sealed class AdminTestScope : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;

        public IDbContextFactory<MemoryLibraryDbContext> Factory { get; }
        public IMemoryLibrary Library { get; }
        public IMemoryLibraryAdminService Service { get; }

        public AdminTestScope(
            SqliteConnection connection,
            IDbContextFactory<MemoryLibraryDbContext> factory,
            IMemoryLibrary library,
            IMemoryLibraryAdminService service)
        {
            _connection = connection;
            Factory = factory;
            Library = library;
            Service = service;
        }

        public async ValueTask DisposeAsync()
        {
            await _connection.DisposeAsync();
        }
    }

    private sealed class AdminTestDbContextFactory : IDbContextFactory<MemoryLibraryDbContext>
    {
        private readonly DbContextOptions<MemoryLibraryDbContext> _options;

        public AdminTestDbContextFactory(DbContextOptions<MemoryLibraryDbContext> options)
        {
            _options = options;
        }

        public MemoryLibraryDbContext CreateDbContext()
        {
            return new MemoryLibraryDbContext(_options);
        }
    }
}
