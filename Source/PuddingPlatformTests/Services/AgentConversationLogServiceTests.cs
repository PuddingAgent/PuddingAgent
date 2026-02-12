using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PuddingCode.Configuration;
using PuddingPlatform.Data;
using PuddingPlatform.Services;

namespace PuddingPlatformTests.Services;

[TestClass]
public sealed class AgentConversationLogServiceTests
{
    [TestMethod]
    public async Task PersistMessageAsync_Writes_DbAgentFields_And_AgentPrivateMessageLog()
    {
        await using var scope = await CreateScopeAsync();
        using var temp = new TempDataRoot();
        var service = new AgentConversationLogService(
            scope.Factory,
            temp.Paths,
            NullLogger<AgentConversationLogService>.Instance);

        await service.PersistMessageAsync(new AgentConversationLogWriteRequest(
            WorkspaceId: "workspace-1",
            AgentInstanceId: "agent-1",
            AgentTemplateId: "template-1",
            SessionId: "session-1",
            Role: "user",
            Content: "hello agent",
            CreatedAt: DateTimeOffset.Parse("2026-06-16T01:02:03Z").ToUnixTimeMilliseconds()));

        var row = await scope.Db.ChatMessages.AsNoTracking().SingleAsync();
        Assert.AreEqual("workspace-1", row.WorkspaceId);
        Assert.AreEqual("agent-1", row.AgentInstanceId);
        Assert.AreEqual("template-1", row.AgentTemplateId);
        Assert.AreEqual("session-1", row.SessionId);
        Assert.AreEqual("user", row.Role);
        Assert.AreEqual("hello agent", row.Content);

        var jsonlPath = Path.Combine(
            temp.Paths.AgentInstanceRoot("agent-1"),
            "logs",
            "messages",
            "2026-06-16",
            "session-1.jsonl");
        var mdPath = Path.Combine(
            temp.Paths.AgentInstanceRoot("agent-1"),
            "logs",
            "messages",
            "2026-06-16",
            "session-1.md");

        Assert.IsTrue(File.Exists(jsonlPath), "Agent-private message JSONL should be created.");
        Assert.IsTrue(File.Exists(mdPath), "Agent-private message Markdown transcript should be created.");

        var jsonLine = await File.ReadAllTextAsync(jsonlPath);
        using var doc = JsonDocument.Parse(jsonLine);
        Assert.AreEqual("workspace-1", doc.RootElement.GetProperty("workspaceId").GetString());
        Assert.AreEqual("agent-1", doc.RootElement.GetProperty("agentInstanceId").GetString());
        Assert.AreEqual("template-1", doc.RootElement.GetProperty("agentTemplateId").GetString());
        Assert.AreEqual("session-1", doc.RootElement.GetProperty("sessionId").GetString());
        Assert.AreEqual("user", doc.RootElement.GetProperty("role").GetString());
        Assert.AreEqual("hello agent", doc.RootElement.GetProperty("content").GetString());

        var md = await File.ReadAllTextAsync(mdPath);
        StringAssert.Contains(md, "## session-1");
        StringAssert.Contains(md, "[user @ 2026-06-16T01:02:03.0000000+00:00]");
        StringAssert.Contains(md, "hello agent");
    }

    [TestMethod]
    public async Task PersistMessageAsync_Skips_AgentPrivateFiles_WhenAgentInstanceIdMissing()
    {
        await using var scope = await CreateScopeAsync();
        using var temp = new TempDataRoot();
        var service = new AgentConversationLogService(
            scope.Factory,
            temp.Paths,
            NullLogger<AgentConversationLogService>.Instance);

        await service.PersistMessageAsync(new AgentConversationLogWriteRequest(
            WorkspaceId: "workspace-1",
            AgentInstanceId: "",
            AgentTemplateId: "template-1",
            SessionId: "session-1",
            Role: "agent",
            Content: "legacy row",
            CreatedAt: DateTimeOffset.Parse("2026-06-16T01:02:03Z").ToUnixTimeMilliseconds()));

        var row = await scope.Db.ChatMessages.AsNoTracking().SingleAsync();
        Assert.AreEqual("workspace-1", row.WorkspaceId);
        Assert.AreEqual("", row.AgentInstanceId);
        Assert.IsFalse(Directory.Exists(Path.Combine(temp.Paths.AgentInstancesRoot, "logs")));
    }

    [TestMethod]
    public async Task ChatTranscriptWriter_WithAgentLogService_WritesAgentPrivateMessageLogIdempotently()
    {
        await using var scope = await CreateScopeAsync();
        using var temp = new TempDataRoot();

        var services = new ServiceCollection();
        services.AddSingleton<IServiceScopeFactory>(new EmptyScopeFactory());
        services.AddSingleton(scope.Factory);
        services.AddSingleton(temp.Paths);
        services.AddSingleton<ILogger<AgentConversationLogService>>(NullLogger<AgentConversationLogService>.Instance);
        services.AddSingleton<ILogger<ChatTranscriptWriter>>(NullLogger<ChatTranscriptWriter>.Instance);
        services.AddSingleton<AgentConversationLogService>();
        services.AddSingleton<ChatTranscriptWriter>();
        await using var provider = services.BuildServiceProvider();

        var writer = provider.GetRequiredService<ChatTranscriptWriter>();
        var createdAt = DateTimeOffset.Parse("2026-06-16T02:03:04Z").ToUnixTimeMilliseconds();
        await writer.PersistMessageAsync(
            "session-2",
            "agent",
            "writer reply",
            createdAt,
            workspaceId: "workspace-1",
            agentInstanceId: "agent-1",
            agentTemplateId: "template-1");
        await writer.PersistMessageAsync(
            "session-2",
            "agent",
            "writer reply",
            createdAt + 100,
            workspaceId: "workspace-1",
            agentInstanceId: "agent-1",
            agentTemplateId: "template-1");

        var rows = await scope.Db.ChatMessages.AsNoTracking().ToListAsync();
        Assert.AreEqual(1, rows.Count);
        Assert.AreEqual("agent-1", rows[0].AgentInstanceId);

        var jsonlPath = temp.Paths.AgentInstanceMessageLogJsonlFile("agent-1", "2026-06-16", "session-2");
        var mdPath = temp.Paths.AgentInstanceMessageLogMarkdownFile("agent-1", "2026-06-16", "session-2");
        Assert.IsTrue(File.Exists(jsonlPath));
        Assert.IsTrue(File.Exists(mdPath));
        Assert.AreEqual(1, (await File.ReadAllLinesAsync(jsonlPath)).Length);
        StringAssert.Contains(await File.ReadAllTextAsync(mdPath), "writer reply");
    }

    private static async Task<TestScope> CreateScopeAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var options = new DbContextOptionsBuilder<PlatformDbContext>()
            .UseSqlite(connection)
            .Options;

        var db = new PlatformDbContext(options);
        await db.Database.EnsureCreatedAsync();

        return new TestScope(connection, db, new TestDbContextFactory(options));
    }

    private sealed record TestScope(
        SqliteConnection Connection,
        PlatformDbContext Db,
        IDbContextFactory<PlatformDbContext> Factory) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await Connection.DisposeAsync();
        }
    }

    private sealed class TestDbContextFactory(DbContextOptions<PlatformDbContext> options)
        : IDbContextFactory<PlatformDbContext>
    {
        public PlatformDbContext CreateDbContext() => new(options);
    }

    private sealed class EmptyScopeFactory : IServiceScopeFactory
    {
        public IServiceScope CreateScope() => new EmptyScope();
    }

    private sealed class EmptyScope : IServiceScope
    {
        public IServiceProvider ServiceProvider { get; } = new ServiceCollection().BuildServiceProvider();

        public void Dispose()
        {
        }
    }

    private sealed class TempDataRoot : IDisposable
    {
        public TempDataRoot()
        {
            Root = Path.Combine(Path.GetTempPath(), "pudding-agent-log-tests", Guid.NewGuid().ToString("N"));
            Paths = PuddingDataPaths.FromRoot(Root);
        }

        public string Root { get; }
        public PuddingDataPaths Paths { get; }

        public void Dispose()
        {
            if (Directory.Exists(Root))
                Directory.Delete(Root, recursive: true);
        }
    }
}
