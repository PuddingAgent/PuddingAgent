using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PuddingCode.Abstractions;
using PuddingMemoryEngine.Data;
using PuddingRuntime.Services;

namespace PuddingRuntimeTests.Services;

[TestClass]
public sealed class WikiPageWriteEntryTests
{
    [TestMethod]
    public async Task WriteAsync_ShouldCreateBookAndPage()
    {
        await using var scope = await CreateScopeAsync();
        var entry = new WikiPageWriteEntry(scope.Library);

        var results = await entry.WriteAsync(new WikiPageWriteRequest
        {
            WorkspaceId = "workspace-1",
            AgentId = "agent-1",
            SessionId = "session-1",
            Plan = CreatePlan("记忆系统设计", "/Memory v2/V1 原则", "# V1\n\n- 默认不做。"),
        });

        Assert.AreEqual(1, results.Count);
        Assert.IsTrue(results[0].CreatedBook);
        Assert.IsTrue(results[0].CreatedPage);

        var libraries = await scope.Library.ListLibrariesAsync("workspace-1");
        var books = await scope.Library.ListBooksAsync(libraries[0].LibraryId);
        var chapters = await scope.Library.ListChaptersAsync(books[0].BookId);

        Assert.AreEqual(1, books.Count);
        Assert.AreEqual("记忆系统设计", books[0].Title);
        Assert.AreEqual(1, chapters.Count);
        Assert.AreEqual("/Memory v2/V1 原则", chapters[0].Title);
        Assert.AreEqual("# V1\n\n- 默认不做。", chapters[0].Content);
    }

    [TestMethod]
    public async Task WriteAsync_ShouldReplaceExistingPageWithoutDuplicatingBookOrPage()
    {
        await using var scope = await CreateScopeAsync();
        var entry = new WikiPageWriteEntry(scope.Library);

        await entry.WriteAsync(new WikiPageWriteRequest
        {
            WorkspaceId = "workspace-1",
            AgentId = "agent-1",
            SessionId = "session-1",
            Plan = CreatePlan("记忆系统设计", "/Memory v2/V1 原则", "old"),
        });

        var second = await entry.WriteAsync(new WikiPageWriteRequest
        {
            WorkspaceId = "workspace-1",
            AgentId = "agent-1",
            SessionId = "session-2",
            Plan = CreatePlan("记忆系统设计", "Memory v2/V1 原则", "new"),
        });

        var libraries = await scope.Library.ListLibrariesAsync("workspace-1");
        var books = await scope.Library.ListBooksAsync(libraries[0].LibraryId);
        var chapters = await scope.Library.ListChaptersAsync(books[0].BookId);

        Assert.AreEqual(1, books.Count);
        Assert.AreEqual(1, chapters.Count);
        Assert.IsFalse(second[0].CreatedBook);
        Assert.IsFalse(second[0].CreatedPage);
        Assert.AreEqual("new", chapters[0].Content);
    }

    private static MemoryWikiPageUpdatePlan CreatePlan(string book, string page, string content)
        => new()
        {
            Updates =
            [
                new MemoryWikiPageUpdate
                {
                    Book = book,
                    Page = page,
                    Content = content,
                },
            ],
        };

    private static async Task<TestScope> CreateScopeAsync()
    {
        var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();

        var options = new DbContextOptionsBuilder<MemoryLibraryDbContext>()
            .UseSqlite(connection)
            .EnableSensitiveDataLogging()
            .Options;
        var factory = new TestDbContextFactory(options);

        await using (var db = factory.CreateDbContext())
        {
            await db.Database.EnsureCreatedAsync();
        }

        return new TestScope(connection, new MemoryLibrary(factory));
    }

    private sealed class TestScope : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;

        public TestScope(SqliteConnection connection, IMemoryLibrary library)
        {
            _connection = connection;
            Library = library;
        }

        public IMemoryLibrary Library { get; }

        public async ValueTask DisposeAsync()
        {
            await _connection.DisposeAsync();
        }
    }

    private sealed class TestDbContextFactory : IDbContextFactory<MemoryLibraryDbContext>
    {
        private readonly DbContextOptions<MemoryLibraryDbContext> _options;

        public TestDbContextFactory(DbContextOptions<MemoryLibraryDbContext> options)
        {
            _options = options;
        }

        public MemoryLibraryDbContext CreateDbContext() => new(_options);

        public Task<MemoryLibraryDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new MemoryLibraryDbContext(_options));
    }
}
