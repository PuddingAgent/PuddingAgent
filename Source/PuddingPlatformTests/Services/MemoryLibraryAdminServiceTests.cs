using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PuddingCode.Abstractions;
using PuddingMemoryEngine.Data;
using PuddingMemoryEngine.Entities;
using PuddingPlatform.Services;

namespace PuddingPlatformTests.Services;

[TestClass]
public sealed class MemoryLibraryAdminServiceTests
{
    [TestMethod]
    public async Task GetOverview_ShouldCountLibrariesAndBooks()
    {
        await using var scope = await CreateAdminTestScopeAsync();
        var lib = await scope.Service.EnsureDefaultLibraryAsync("ws-admin", "agent-a");
        await scope.Library.CreateBookAsync(lib.LibraryId, "Book1", "Summary1");
        await scope.Library.CreateBookAsync(lib.LibraryId, "Book2", "Summary2");

        var overview = await scope.Service.GetOverviewAsync("ws-admin", "agent-a");

        Assert.AreEqual("ws-admin", overview.WorkspaceId);
        Assert.AreEqual("agent-a", overview.AgentId);
        Assert.AreEqual(1, overview.LibraryCount);
        Assert.AreEqual(2, overview.BookCount);
    }

    [TestMethod]
    public async Task GetOverview_ShouldNotCountOtherWorkspace()
    {
        await using var scope = await CreateAdminTestScopeAsync();
        var libA = await scope.Service.EnsureDefaultLibraryAsync("ws-a", "agent-a");
        await scope.Library.CreateBookAsync(libA.LibraryId, "BookA", "SummaryA");
        var libB = await scope.Service.EnsureDefaultLibraryAsync("ws-b", "agent-a");
        await scope.Library.CreateBookAsync(libB.LibraryId, "BookB", "SummaryB");

        var overviewA = await scope.Service.GetOverviewAsync("ws-a", "agent-a");
        Assert.AreEqual(1, overviewA.LibraryCount);
        Assert.AreEqual(1, overviewA.BookCount);
    }

    [TestMethod]
    public async Task GetTree_ShouldReturnRootNodes()
    {
        await using var scope = await CreateAdminTestScopeAsync();
        var lib = await scope.Service.EnsureDefaultLibraryAsync("ws-tree", "agent-a");
        await scope.Library.CreateTreeNodeAsync("ws-tree", lib.LibraryId, null, "系统记忆", null, "system");
        await scope.Library.CreateTreeNodeAsync("ws-tree", lib.LibraryId, null, "项目知识", null, "category");

        var tree = await scope.Service.GetTreeAsync("ws-tree", "agent-a", lib.LibraryId);

        Assert.HasCount(1, tree);
        Assert.HasCount(2, tree[0].Children);
        Assert.AreEqual("系统记忆", tree[0].Children[0].Title);
        Assert.AreEqual("项目知识", tree[0].Children[1].Title);
    }

    [TestMethod]
    public async Task GetTree_ShouldReturnNestedNodes()
    {
        await using var scope = await CreateAdminTestScopeAsync();
        var lib = await scope.Service.EnsureDefaultLibraryAsync("ws-nested", "agent-a");
        var parent = await scope.Library.CreateTreeNodeAsync("ws-nested", lib.LibraryId, null, "技术", null, "category");
        await scope.Library.CreateTreeNodeAsync("ws-nested", lib.LibraryId, parent.NodeId, "数据库", null, "topic");

        var tree = await scope.Service.GetTreeAsync("ws-nested", "agent-a", lib.LibraryId);

        Assert.HasCount(1, tree);
        Assert.HasCount(1, tree[0].Children);
        Assert.HasCount(1, tree[0].Children[0].Children);
        Assert.AreEqual("数据库", tree[0].Children[0].Children[0].Title);
    }

    [TestMethod]
    public async Task AgentGetTree_ShouldExposeLibraryRootAndUnmountedBooks()
    {
        await using var scope = await CreateAdminTestScopeAsync();
        var lib = await scope.Service.EnsureDefaultLibraryAsync("ws-agent-tree", "agent-a");
        var book = await scope.Library.CreateBookAsync(lib.LibraryId, "用户偏好", "稳定偏好");

        var tree = await scope.Service.GetTreeAsync("ws-agent-tree", "agent-a", lib.LibraryId);

        Assert.HasCount(1, tree);
        Assert.AreEqual("library", tree[0].Type);
        Assert.AreEqual(lib.LibraryId, tree[0].Id);

        var unmountedGroup = tree[0].Children.Single(x => x.Title == "未挂载 Book");
        var unmountedBook = unmountedGroup.Children.Single(x => x.BookId == book.BookId);
        Assert.AreEqual("book_page", unmountedBook.Type);
        Assert.AreEqual("用户偏好", unmountedBook.Title);
    }

    [TestMethod]
    public async Task GetBookPage_ShouldReturnBookWithChapters()
    {
        await using var scope = await CreateAdminTestScopeAsync();
        var lib = await scope.Service.EnsureDefaultLibraryAsync("ws-book", "agent-a");
        var book = await scope.Library.CreateBookAsync(lib.LibraryId, "测试手册", "一本测试手册");
        await scope.Library.AddChapterAsync(book.BookId, "第一章", "内容1", chapterOrder: 0);
        await scope.Library.AddChapterAsync(book.BookId, "第二章", "内容2", chapterOrder: 1);

        var page = await scope.Service.GetBookPageAsync("ws-book", "agent-a", book.BookId);

        Assert.AreEqual("测试手册", page.Title);
        Assert.AreEqual("一本测试手册", page.Summary);
        Assert.HasCount(2, page.Chapters);
        Assert.AreEqual("第一章", page.Chapters[0].Title);
    }

    [TestMethod]
    public async Task GetBookPage_ShouldThrowOnWrongWorkspace()
    {
        await using var scope = await CreateAdminTestScopeAsync();
        var lib = await scope.Service.EnsureDefaultLibraryAsync("ws-book-a", "agent-a");
        var book = await scope.Library.CreateBookAsync(lib.LibraryId, "BookA", "Summary");

        try
        {
            await scope.Service.GetBookPageAsync("ws-book-b", "agent-a", book.BookId);
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
        var libA = await scope.Service.EnsureDefaultLibraryAsync("ws-admin-a", "agent-a");
        var bookA = await scope.Library.CreateBookAsync(libA.LibraryId, "A", "A");
        await scope.Library.AddChapterAsync(bookA.BookId, "A", "shared needle");

        var libB = await scope.Service.EnsureDefaultLibraryAsync("ws-admin-b", "agent-a");
        var bookB = await scope.Library.CreateBookAsync(libB.LibraryId, "B", "B");
        await scope.Library.AddChapterAsync(bookB.BookId, "B", "shared needle");

        var hits = await scope.Service.SearchAsync("ws-admin-a", "agent-a", "shared", 10);

        Assert.IsTrue(hits.All(x => x.BookId == bookA.BookId));
    }

    [TestMethod]
    public async Task SearchAsync_ShouldReturnEmptyWhenNoMatch()
    {
        await using var scope = await CreateAdminTestScopeAsync(enableFts5: true);
        var lib = await scope.Service.EnsureDefaultLibraryAsync("ws-empty", "agent-a");
        var book = await scope.Library.CreateBookAsync(lib.LibraryId, "Empty", "Empty");
        await scope.Library.AddChapterAsync(book.BookId, "Empty", "nothing here");

        var hits = await scope.Service.SearchAsync("ws-empty", "agent-a", "zzznotfound", 10);

        Assert.HasCount(0, hits);
    }

    [TestMethod]
    public async Task AgentOverview_ShouldOnlyCountSelectedAgentLibraries()
    {
        await using var scope = await CreateAdminTestScopeAsync();
        var libA = await scope.Service.EnsureDefaultLibraryAsync("ws-agent", "agent-a");
        await scope.Library.CreateBookAsync(libA.LibraryId, "BookA", "SummaryA");

        var libB = await scope.Service.EnsureDefaultLibraryAsync("ws-agent", "agent-b");
        await scope.Library.CreateBookAsync(libB.LibraryId, "BookB", "SummaryB");

        var overview = await scope.Service.GetOverviewAsync("ws-agent", "agent-a");

        Assert.AreEqual("agent-a", overview.AgentId);
        Assert.AreEqual(1, overview.LibraryCount);
        Assert.AreEqual(1, overview.BookCount);
    }

    [TestMethod]
    public async Task AgentGetBookPage_ShouldRejectBookFromOtherAgent()
    {
        await using var scope = await CreateAdminTestScopeAsync();
        await scope.Service.EnsureDefaultLibraryAsync("ws-agent-book", "agent-a");
        var libB = await scope.Service.EnsureDefaultLibraryAsync("ws-agent-book", "agent-b");
        var bookB = await scope.Library.CreateBookAsync(libB.LibraryId, "BookB", "SummaryB");

        try
        {
            await scope.Service.GetBookPageAsync("ws-agent-book", "agent-a", bookB.BookId);
            Assert.Fail("Expected UnauthorizedAccessException was not thrown.");
        }
        catch (UnauthorizedAccessException)
        {
            // Expected
        }
    }

    [TestMethod]
    public async Task AgentSearch_ShouldOnlyReturnSelectedAgent()
    {
        await using var scope = await CreateAdminTestScopeAsync(enableFts5: true);
        var libA = await scope.Service.EnsureDefaultLibraryAsync("ws-agent-search", "agent-a");
        var bookA = await scope.Library.CreateBookAsync(libA.LibraryId, "BookA", "SummaryA");
        await scope.Library.AddChapterAsync(bookA.BookId, "ChapterA", "agent scoped needle");

        var libB = await scope.Service.EnsureDefaultLibraryAsync("ws-agent-search", "agent-b");
        var bookB = await scope.Library.CreateBookAsync(libB.LibraryId, "BookB", "SummaryB");
        await scope.Library.AddChapterAsync(bookB.BookId, "ChapterB", "agent scoped needle");

        var hits = await scope.Service.SearchAsync("ws-agent-search", "agent-a", "needle", 10);

        Assert.IsTrue(hits.Count > 0);
        Assert.IsTrue(hits.All(x => x.BookId == bookA.BookId));
    }

    [TestMethod]
    public async Task AgentLibraries_ShouldNotIncludeUnassignedWorkspaceLibraries()
    {
        await using var scope = await CreateAdminTestScopeAsync();
        await scope.Library.CreateLibraryAsync("ws-new-library", "未归属图书馆", null);
        var agentLibrary = await scope.Service.EnsureDefaultLibraryAsync("ws-new-library", "agent-a");

        var libraries = await scope.Service.GetLibrariesAsync("ws-new-library", "agent-a");

        Assert.HasCount(1, libraries);
        Assert.AreEqual(agentLibrary.LibraryId, libraries[0].LibraryId);
        Assert.AreEqual("agent-a", libraries[0].AgentId);
    }

    [TestMethod]
    public async Task CreateBook_ShouldRejectTreeNodeFromDifferentLibraryEvenForSameAgent()
    {
        await using var scope = await CreateAdminTestScopeAsync();
        var libA = await scope.Service.EnsureDefaultLibraryAsync("ws-cross-lib", "agent-a");
        var nodeA = await scope.Library.CreateTreeNodeAsync("ws-cross-lib", libA.LibraryId, null, "A Node");

        await using (var db = await scope.Factory.CreateDbContextAsync())
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            db.Libraries.Add(new LibraryEntity
            {
                LibraryId = "lib-agent-a-second",
                WorkspaceId = "ws-cross-lib",
                AgentId = "agent-a",
                Name = "Second",
                CreatedAt = now,
                UpdatedAt = now
            });
            await db.SaveChangesAsync();
        }

        try
        {
            await scope.Service.CreateBookAsync("ws-cross-lib", "agent-a", new CreateMemoryBookRequest(
                "ws-cross-lib",
                "lib-agent-a-second",
                nodeA.NodeId,
                "Cross library book",
                null));
            Assert.Fail("Expected UnauthorizedAccessException was not thrown.");
        }
        catch (UnauthorizedAccessException)
        {
            // Expected
        }
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
        var service = new MemoryLibraryAdminService(library, factory);

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
            "CREATE VIRTUAL TABLE IF NOT EXISTS Chapters_fts USING fts5(TitleTokens, ContentTokens, ChapterId UNINDEXED, content=Chapters, content_rowid=rowid)",
            "CREATE TRIGGER IF NOT EXISTS trg_Chapters_ai AFTER INSERT ON Chapters BEGIN INSERT INTO Chapters_fts(rowid, TitleTokens, ContentTokens, ChapterId) VALUES (new.rowid, new.TitleTokens, new.ContentTokens, new.ChapterId); END",
            "CREATE TRIGGER IF NOT EXISTS trg_Chapters_ad AFTER DELETE ON Chapters BEGIN INSERT INTO Chapters_fts(Chapters_fts, rowid, TitleTokens, ContentTokens, ChapterId) VALUES ('delete', old.rowid, old.TitleTokens, old.ContentTokens, old.ChapterId); END",
            "CREATE TRIGGER IF NOT EXISTS trg_Chapters_au AFTER UPDATE ON Chapters BEGIN INSERT INTO Chapters_fts(Chapters_fts, rowid, TitleTokens, ContentTokens, ChapterId) VALUES ('delete', old.rowid, old.TitleTokens, old.ContentTokens, old.ChapterId); INSERT INTO Chapters_fts(rowid, TitleTokens, ContentTokens, ChapterId) VALUES (new.rowid, new.TitleTokens, new.ContentTokens, new.ChapterId); END",
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
