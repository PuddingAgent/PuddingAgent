using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using PuddingCode.Abstractions;
using PuddingMemoryEngine.Data;
using PuddingRuntime.Services;

namespace PuddingRuntimeTests.Services;

/// <summary>
/// 用户偏好管理服务测试：Save（新增/覆盖）、Load（Prefetch 文本块）、Delete。
/// </summary>
[TestClass]
public sealed class UserPreferenceServiceTests
{
    [TestMethod]
    public async Task Save_NewKey_Creates_Chapter_Under_PreferenceBook()
    {
        await using var scope = await CreateScopeAsync();
        var service = new UserPreferenceService(
            scope.Library, scope.Convenience, NullLogger<UserPreferenceService>.Instance);

        var result = await service.SavePreferenceAsync("ws-1", "language", "中文");

        Assert.AreEqual("language", result.Key);
        Assert.AreEqual("中文", result.Value);
        Assert.IsFalse(result.Updated, "首次写入应为新增而非覆盖");

        var book = await FindPreferenceBookAsync(scope.Library, "ws-1");
        Assert.IsNotNull(book, "应创建/找到「用户偏好」Book");
        var chapters = await scope.Library.ListChaptersAsync(book!.BookId);
        Assert.AreEqual(1, chapters.Count);
        Assert.AreEqual("language: 中文", chapters[0].Content);
        Assert.AreEqual(0.9, chapters[0].Importance, 0.001);
    }

    [TestMethod]
    public async Task Save_SameKey_Overwrites_Without_Duplicating()
    {
        await using var scope = await CreateScopeAsync();
        var service = new UserPreferenceService(
            scope.Library, scope.Convenience, NullLogger<UserPreferenceService>.Instance);

        var first = await service.SavePreferenceAsync("ws-1", "language", "中文");
        var second = await service.SavePreferenceAsync("ws-1", "language", "English");

        Assert.IsFalse(first.Updated);
        Assert.IsTrue(second.Updated, "同一 key 再次写入应为覆盖");
        Assert.AreEqual(first.ChapterId, second.ChapterId, "覆盖应复用原 Chapter");

        var book = await FindPreferenceBookAsync(scope.Library, "ws-1");
        var chapters = await scope.Library.ListChaptersAsync(book!.BookId);
        Assert.AreEqual(1, chapters.Count, "同一 key 不应产生重复章节");
        Assert.AreEqual("language: English", chapters[0].Content);
    }

    [TestMethod]
    public async Task Save_MultipleKeys_Appends_Distinct_Chapters()
    {
        await using var scope = await CreateScopeAsync();
        var service = new UserPreferenceService(
            scope.Library, scope.Convenience, NullLogger<UserPreferenceService>.Instance);

        await service.SavePreferenceAsync("ws-1", "language", "中文");
        await service.SavePreferenceAsync("ws-1", "tone", "简洁");

        var book = await FindPreferenceBookAsync(scope.Library, "ws-1");
        var chapters = await scope.Library.ListChaptersAsync(book!.BookId);
        Assert.AreEqual(2, chapters.Count);
        CollectionAssert.AreEquivalent(
            new[] { "language: 中文", "tone: 简洁" },
            chapters.Select(c => c.Content).ToArray());
    }

    [TestMethod]
    public async Task Load_Returns_Formatted_Block_With_KeyValue_Lines()
    {
        await using var scope = await CreateScopeAsync();
        var service = new UserPreferenceService(
            scope.Library, scope.Convenience, NullLogger<UserPreferenceService>.Instance);

        await service.SavePreferenceAsync("ws-1", "language", "中文");
        await service.SavePreferenceAsync("ws-1", "tone", "简洁");

        var block = await service.LoadPreferencesAsync("ws-1");

        Assert.IsNotNull(block);
        StringAssert.Contains(block!, "--- LAYER: USER-PREFERENCES ---");
        StringAssert.Contains(block!, "[USER PREFERENCES]");
        StringAssert.Contains(block!, "- **language**: 中文");
        StringAssert.Contains(block!, "- **tone**: 简洁");
    }

    [TestMethod]
    public async Task Load_NoPreferences_Returns_Null()
    {
        await using var scope = await CreateScopeAsync();
        var service = new UserPreferenceService(
            scope.Library, scope.Convenience, NullLogger<UserPreferenceService>.Instance);

        var block = await service.LoadPreferencesAsync("ws-empty");
        Assert.IsNull(block);
    }

    [TestMethod]
    public async Task Load_NullWorkspace_Returns_Null()
    {
        await using var scope = await CreateScopeAsync();
        var service = new UserPreferenceService(
            scope.Library, scope.Convenience, NullLogger<UserPreferenceService>.Instance);

        Assert.IsNull(await service.LoadPreferencesAsync(null));
    }

    [TestMethod]
    public async Task Delete_Removes_Preference_By_Key()
    {
        await using var scope = await CreateScopeAsync();
        var service = new UserPreferenceService(
            scope.Library, scope.Convenience, NullLogger<UserPreferenceService>.Instance);

        await service.SavePreferenceAsync("ws-1", "language", "中文");
        await service.SavePreferenceAsync("ws-1", "tone", "简洁");

        var deleted = await service.DeletePreferenceAsync("ws-1", "language");
        Assert.IsTrue(deleted);

        var block = await service.LoadPreferencesAsync("ws-1");
        Assert.IsNotNull(block);
        Assert.IsFalse(block!.Contains("language", StringComparison.Ordinal), "删除后不应再包含该偏好");
        StringAssert.Contains(block, "- **tone**: 简洁");

        Assert.IsFalse(await service.DeletePreferenceAsync("ws-1", "missing-key"));
    }

    [TestMethod]
    public async Task Full_Flow_Upsert_Load_Delete_Is_Consistent()
    {
        await using var scope = await CreateScopeAsync();
        var service = new UserPreferenceService(
            scope.Library, scope.Convenience, NullLogger<UserPreferenceService>.Instance);

        // 写入 → 读取 → 覆盖 → 读取 → 删除 → 读取为空
        await service.SavePreferenceAsync("ws-1", "reply_style", "Markdown 表格");
        var block1 = await service.LoadPreferencesAsync("ws-1");
        StringAssert.Contains(block1!, "- **reply_style**: Markdown 表格");

        await service.SavePreferenceAsync("ws-1", "reply_style", "简洁短句");
        var block2 = await service.LoadPreferencesAsync("ws-1");
        StringAssert.Contains(block2!, "- **reply_style**: 简洁短句");
        Assert.IsFalse(block2!.Contains("Markdown 表格", StringComparison.Ordinal));

        Assert.IsTrue(await service.DeletePreferenceAsync("ws-1", "reply_style"));
        Assert.IsNull(await service.LoadPreferencesAsync("ws-1"));
    }

    // ── 测试基础设施 ─────────────────────────────────────────────────

    private static async Task<BookRecord?> FindPreferenceBookAsync(IMemoryLibrary library, string workspaceId)
    {
        var libraries = await library.ListLibrariesAsync(workspaceId);
        foreach (var lib in libraries)
        {
            var book = await library.FindBookByTitleAsync(lib.LibraryId, "用户偏好");
            if (book is not null)
                return book;
        }
        return null;
    }

    private static async Task<PreferenceTestScope> CreateScopeAsync()
    {
        var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();
        await using (var pragma = connection.CreateCommand())
        {
            pragma.CommandText = "PRAGMA foreign_keys=ON;";
            await pragma.ExecuteNonQueryAsync();
        }

        var options = new DbContextOptionsBuilder<MemoryLibraryDbContext>()
            .UseSqlite(connection)
            .Options;
        var factory = new TestDbContextFactory(options);
        await using (var db = factory.CreateDbContext())
        {
            await db.Database.EnsureCreatedAsync();
        }

        var library = new MemoryLibrary(factory);
        var convenience = new MemoryLibraryConvenience(library);
        return new PreferenceTestScope(connection, library, convenience);
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

    private sealed class PreferenceTestScope : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;

        public PreferenceTestScope(
            SqliteConnection connection,
            IMemoryLibrary library,
            IMemoryLibraryConvenience convenience)
        {
            _connection = connection;
            Library = library;
            Convenience = convenience;
        }

        public IMemoryLibrary Library { get; }
        public IMemoryLibraryConvenience Convenience { get; }

        public async ValueTask DisposeAsync()
        {
            await _connection.DisposeAsync();
        }
    }
}
