using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using PuddingCode.Abstractions;
using PuddingCode.Tools;
using PuddingMemoryEngine.Data;
using PuddingRuntime.Services;
using PuddingRuntime.Services.Tools;

namespace PuddingRuntimeTests.Tools;

/// <summary>
/// save_preference 工具测试：Agent 感知用户偏好后自动存储（Sync 路径）。
/// </summary>
[TestClass]
public sealed class SavePreferenceToolTests
{
    [TestMethod]
    public void SavePreferenceTool_Uses_Strongly_Typed_Tool_Base()
    {
        Assert.IsTrue(
            typeof(PuddingToolBase<SavePreferenceArgs>).IsAssignableFrom(typeof(SavePreferenceTool)),
            "SavePreferenceTool should derive from PuddingToolBase<SavePreferenceArgs>.");
    }

    [TestMethod]
    public void SavePreferenceTool_Is_Auto_Registered_In_Tool_Registry()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IMemoryLibrary, PuddingMemoryEngine.Data.MemoryLibrary>();
        services.AddSingleton<IMemoryLibraryConvenience>(
            sp => new PuddingMemoryEngine.Data.MemoryLibraryConvenience(
                sp.GetRequiredService<IMemoryLibrary>()));
        services.AddSingleton<IUserPreferenceService, UserPreferenceService>();
        services.AddPuddingToolsFromAssembly(typeof(SavePreferenceTool).Assembly);
        services.AddPuddingToolRegistry();

        // 自动发现：SavePreferenceTool 被注册为 IPuddingTool 单例
        Assert.IsTrue(
            services.Any(d => d.ServiceType == typeof(IPuddingTool)
                              && d.ImplementationType == typeof(SavePreferenceTool)),
            "save_preference 应通过 [Tool] 特性被 AddPuddingToolsFromAssembly 自动发现注册。");

        // 描述符校验：ToolId / Category / 参数 Schema
        var descriptor = ToolDescriptorFactory.Create<SavePreferenceTool, SavePreferenceArgs>();
        Assert.AreEqual("save_preference", descriptor.ToolId);
        Assert.AreEqual(ToolCategory.Memory, descriptor.Category);
        CollectionAssert.AreEquivalent(
            new[] { "key", "value", "action", "source_reference" },
            descriptor.Parameters.Properties.Select(p => p.Name).ToArray());
    }

    [TestMethod]
    public async Task Save_Upsert_Stores_Preference_And_Returns_Metadata()
    {
        await using var scope = await CreateScopeAsync();
        var tool = new SavePreferenceTool(
            new UserPreferenceService(scope.Library, scope.Convenience, NullLogger<UserPreferenceService>.Instance),
            NullLogger<SavePreferenceTool>.Instance);

        var json = await ExecuteAsync(tool, """
        {
          "key": "language",
          "value": "中文"
        }
        """, workspaceId: "ws-tool");

        var root = JsonDocument.Parse(json).RootElement;
        Assert.AreEqual("ok", root.GetProperty("status").GetString());
        Assert.AreEqual("language", root.GetProperty("key").GetString());
        Assert.AreEqual("中文", root.GetProperty("value").GetString());
        Assert.IsFalse(root.GetProperty("updated").GetBoolean());
        Assert.IsFalse(string.IsNullOrWhiteSpace(root.GetProperty("chapterId").GetString()));

        // 再次写入同一 key → updated=true，且不产生重复章节
        var json2 = await ExecuteAsync(tool, """
        {
          "key": "language",
          "value": "English"
        }
        """, workspaceId: "ws-tool");
        var root2 = JsonDocument.Parse(json2).RootElement;
        Assert.IsTrue(root2.GetProperty("updated").GetBoolean());

        var book = await FindPreferenceBookAsync(scope.Library, "ws-tool");
        var chapters = await scope.Library.ListChaptersAsync(book!.BookId);
        Assert.AreEqual(1, chapters.Count, "同一 key 覆盖不应产生重复章节");
    }

    [TestMethod]
    public async Task Save_Delete_Removes_Preference()
    {
        await using var scope = await CreateScopeAsync();
        var service = new UserPreferenceService(
            scope.Library, scope.Convenience, NullLogger<UserPreferenceService>.Instance);
        var tool = new SavePreferenceTool(service, NullLogger<SavePreferenceTool>.Instance);

        await ExecuteAsync(tool, """{ "key": "tone", "value": "简洁" }""", workspaceId: "ws-tool");

        var delJson = await ExecuteAsync(tool, """
        {
          "action": "delete",
          "key": "tone"
        }
        """, workspaceId: "ws-tool");
        var delRoot = JsonDocument.Parse(delJson).RootElement;
        Assert.AreEqual("ok", delRoot.GetProperty("status").GetString());
        Assert.IsTrue(delRoot.GetProperty("deleted").GetBoolean());

        Assert.IsNull(await service.LoadPreferencesAsync("ws-tool"));
    }

    [TestMethod]
    public async Task Save_MissingKey_Returns_Fail()
    {
        await using var scope = await CreateScopeAsync();
        var tool = new SavePreferenceTool(
            new UserPreferenceService(scope.Library, scope.Convenience, NullLogger<UserPreferenceService>.Instance),
            NullLogger<SavePreferenceTool>.Instance);

        var result = await tool.ExecuteAsync(new ToolExecutionRequest
        {
            ToolCallId = "call-1",
            ArgumentsJson = """{ "value": "中文" }""",
            Context = new ToolExecutionContext
            {
                WorkspaceId = "ws-tool",
                SessionId = "s1",
                AgentInstanceId = "agent-1",
            },
        });

        Assert.IsFalse(result.Success);
        StringAssert.Contains(result.Error ?? "", "key is required");
    }

    [TestMethod]
    public async Task Save_Rejects_Unknown_Action()
    {
        await using var scope = await CreateScopeAsync();
        var tool = new SavePreferenceTool(
            new UserPreferenceService(scope.Library, scope.Convenience, NullLogger<UserPreferenceService>.Instance),
            NullLogger<SavePreferenceTool>.Instance);

        var result = await tool.ExecuteAsync(new ToolExecutionRequest
        {
            ToolCallId = "call-2",
            ArgumentsJson = """{ "action": "bogus", "key": "k", "value": "v" }""",
            Context = new ToolExecutionContext
            {
                WorkspaceId = "ws-tool",
                SessionId = "s1",
                AgentInstanceId = "agent-1",
            },
        });

        Assert.IsFalse(result.Success);
        StringAssert.Contains(result.Error ?? "", "Unsupported action");
    }

    // ── 测试基础设施 ─────────────────────────────────────────────────

    private static async Task<string> ExecuteAsync(
        SavePreferenceTool tool,
        string argumentsJson,
        string workspaceId)
    {
        var result = await tool.ExecuteAsync(new ToolExecutionRequest
        {
            ToolCallId = $"call-{Guid.NewGuid():N}",
            ArgumentsJson = argumentsJson,
            Context = new ToolExecutionContext
            {
                WorkspaceId = workspaceId,
                SessionId = $"session-{Guid.NewGuid():N}",
                AgentInstanceId = "agent-1",
            },
        });

        Assert.IsTrue(result.Success, $"Tool execution failed: {result.Error}");
        return result.Output;
    }

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

    private static async Task<PreferenceToolScope> CreateScopeAsync()
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
        return new PreferenceToolScope(connection, library, convenience);
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

    private sealed class PreferenceToolScope : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;

        public PreferenceToolScope(
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
