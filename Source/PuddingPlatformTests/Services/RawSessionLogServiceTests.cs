using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PuddingCode.Abstractions;
using PuddingPlatform.Data;
using PuddingPlatform.Data.Entities;
using PuddingPlatform.Services;

namespace PuddingPlatformTests.Services;

[TestClass]
public sealed class RawSessionLogServiceTests
{
    [TestMethod]
    public async Task GrepAsync_ReturnsOnlyCurrentWorkspaceEvidence()
    {
        await using var scope = await CreateScopeAsync();
        await SeedEventAsync(scope.Db, "ws-a", "session-a", 1, "message.delta", "needle alpha", "2026-06-02T08:00:00.0000000Z");
        await SeedEventAsync(scope.Db, "ws-b", "session-b", 1, "message.delta", "needle beta", "2026-06-02T08:01:00.0000000Z");

        var service = new RawSessionLogService(scope.Factory);

        var result = await service.GrepAsync(new RawSessionLogSearchRequest
        {
            WorkspaceId = "ws-a",
            Query = "needle",
            Day = "2026-06-02",
            Limit = 10,
        });

        Assert.AreEqual(1, result.Matches.Count);
        Assert.AreEqual("session-a", result.Matches[0].SessionId);
        Assert.AreEqual("ws-a", result.Matches[0].WorkspaceId);
        Assert.AreEqual(1, result.Matches[0].SequenceNum);
        StringAssert.Contains(result.Matches[0].Snippet, "needle alpha");
        StringAssert.StartsWith(result.Matches[0].EvidenceRef, "session-log:2026-06-02:session-a:1");
    }

    [TestMethod]
    public async Task ListDaysAndSessionsAsync_GroupByDayWithinWorkspace()
    {
        await using var scope = await CreateScopeAsync();
        await SeedEventAsync(scope.Db, "ws-a", "session-a", 1, "message.delta", "day one", "2026-06-01T08:00:00.0000000Z");
        await SeedEventAsync(scope.Db, "ws-a", "session-a", 2, "message.done", "day one done", "2026-06-01T08:01:00.0000000Z");
        await SeedEventAsync(scope.Db, "ws-a", "session-b", 1, "message.delta", "day two", "2026-06-02T09:00:00.0000000Z");
        await SeedEventAsync(scope.Db, "ws-b", "session-c", 1, "message.delta", "other workspace", "2026-06-02T09:30:00.0000000Z");

        var service = new RawSessionLogService(scope.Factory);

        var days = await service.ListDaysAsync("ws-a", fromDay: "2026-06-01", toDay: "2026-06-02", limit: 10);
        CollectionAssert.AreEqual(new[] { "2026-06-02", "2026-06-01" }, days.Days.Select(d => d.Day).ToArray());
        Assert.AreEqual(1, days.Days[0].SessionCount);
        Assert.AreEqual(1, days.Days[1].SessionCount);
        Assert.AreEqual(2, days.Days[1].EventCount);

        var sessions = await service.ListSessionsAsync("ws-a", "2026-06-01", limit: 10);
        Assert.AreEqual(1, sessions.Sessions.Count);
        Assert.AreEqual("session-a", sessions.Sessions[0].SessionId);
        Assert.AreEqual(2, sessions.Sessions[0].EventCount);
    }

    [TestMethod]
    public async Task ReadSessionAsync_PaginatesBySequenceAndPreservesRawData()
    {
        await using var scope = await CreateScopeAsync();
        await SeedEventAsync(scope.Db, "ws-a", "session-a", 1, "message.delta", "{\"text\":\"first\"}", "2026-06-02T08:00:00.0000000Z");
        await SeedEventAsync(scope.Db, "ws-a", "session-a", 2, "tool.result", "{\"text\":\"second\"}", "2026-06-02T08:01:00.0000000Z");
        await SeedEventAsync(scope.Db, "ws-b", "session-other", 1, "message.delta", "{\"text\":\"wrong workspace\"}", "2026-06-02T08:00:00.0000000Z");

        var service = new RawSessionLogService(scope.Factory);

        var page = await service.ReadSessionAsync("ws-a", "session-a", afterSequence: 1, limit: 10);

        Assert.AreEqual(1, page.Events.Count);
        Assert.AreEqual(2, page.Events[0].SequenceNum);
        Assert.AreEqual("tool.result", page.Events[0].EventType);
        StringAssert.Contains(page.Events[0].Data, "second");
        Assert.IsFalse(page.HasMore);
    }

    [TestMethod]
    public async Task ReadMessagesAsync_ReturnsMaterializedTranscriptWithoutRawFrames()
    {
        await using var scope = await CreateScopeAsync();
        scope.Db.ChatMessages.AddRange(
            new ChatMessageEntity
            {
                SessionId = "session-a",
                Role = "user",
                Content = "what happened yesterday?",
                CreatedAt = 1000,
            },
            new ChatMessageEntity
            {
                SessionId = "session-a",
                Role = "agent",
                Content = "final answer only",
                CreatedAt = 2000,
                ThinkingJson = """[{"text":"hidden chain","timestamp":2000}]""",
            });
        await scope.Db.SaveChangesAsync();

        await SeedEventAsync(scope.Db, "ws-a", "session-a", 1, "thinking", "{\"delta\":\"hidden chain\"}", "2026-06-02T08:00:00.0000000Z");
        await SeedEventAsync(scope.Db, "ws-a", "session-a", 2, "tool_call", "{\"name\":\"shell\"}", "2026-06-02T08:00:01.0000000Z");

        var service = new RawSessionLogService(scope.Factory);

        var page = await service.ReadMessagesAsync("ws-a", "session-a", limit: 10);

        Assert.AreEqual(2, page.Messages.Count);
        CollectionAssert.AreEqual(new[] { "user", "agent" }, page.Messages.Select(m => m.Role).ToArray());
        CollectionAssert.AreEqual(new[] { "what happened yesterday?", "final answer only" }, page.Messages.Select(m => m.Content).ToArray());
        Assert.IsTrue(page.Messages.All(m => m.EventType == "message"));
        Assert.IsTrue(page.Messages.All(m => !m.Content.Contains("hidden chain", StringComparison.OrdinalIgnoreCase)));
    }

    [TestMethod]
    public async Task ReadMessagesAsync_FiltersMaterializedTranscriptByAgentInstance()
    {
        await using var scope = await CreateScopeAsync();
        scope.Db.ChatMessages.AddRange(
            new ChatMessageEntity
            {
                WorkspaceId = "ws-a",
                AgentInstanceId = "agent-1",
                SessionId = "session-a",
                Role = "agent",
                Content = "agent one transcript",
                CreatedAt = 1000,
            },
            new ChatMessageEntity
            {
                WorkspaceId = "ws-a",
                AgentInstanceId = "agent-2",
                SessionId = "session-a",
                Role = "agent",
                Content = "agent two transcript",
                CreatedAt = 2000,
            });
        await scope.Db.SaveChangesAsync();
        await SeedEventAsync(scope.Db, "ws-a", "agent-1", "session-a", 1, "done", "{}", "2026-06-02T08:00:00.0000000Z");
        await SeedEventAsync(scope.Db, "ws-a", "agent-2", "session-a", 2, "done", "{}", "2026-06-02T08:01:00.0000000Z");

        var service = new RawSessionLogService(scope.Factory);

        var page = await service.ReadMessagesAsync("ws-a", "session-a", agentInstanceId: "agent-1", limit: 10);

        Assert.AreEqual(1, page.Messages.Count);
        Assert.AreEqual("agent one transcript", page.Messages[0].Content);
    }

    [TestMethod]
    public async Task GrepMessagesAsync_FiltersByAgentInstance()
    {
        await using var scope = await CreateScopeAsync();
        scope.Db.ChatMessages.AddRange(
            new ChatMessageEntity
            {
                WorkspaceId = "ws-a",
                AgentInstanceId = "agent-1",
                SessionId = "session-a",
                Role = "agent",
                Content = "needle from agent one",
                CreatedAt = DateTimeOffset.Parse("2026-06-02T08:00:00.0000000Z").ToUnixTimeMilliseconds(),
            },
            new ChatMessageEntity
            {
                WorkspaceId = "ws-a",
                AgentInstanceId = "agent-2",
                SessionId = "session-b",
                Role = "agent",
                Content = "needle from agent two",
                CreatedAt = DateTimeOffset.Parse("2026-06-02T08:01:00.0000000Z").ToUnixTimeMilliseconds(),
            });
        await scope.Db.SaveChangesAsync();
        await SeedEventAsync(scope.Db, "ws-a", "agent-1", "session-a", 1, "done", "{}", "2026-06-02T08:00:00.0000000Z");
        await SeedEventAsync(scope.Db, "ws-a", "agent-2", "session-b", 1, "done", "{}", "2026-06-02T08:01:00.0000000Z");

        var service = new RawSessionLogService(scope.Factory);

        var result = await service.GrepMessagesAsync(new RawSessionLogSearchRequest
        {
            WorkspaceId = "ws-a",
            AgentInstanceId = "agent-1",
            Query = "needle",
            Day = "2026-06-02",
            Limit = 10,
        });

        Assert.AreEqual(1, result.Matches.Count);
        Assert.AreEqual("session-a", result.Matches[0].SessionId);
        StringAssert.Contains(result.Matches[0].Snippet, "agent one");
    }

    [TestMethod]
    public async Task ReadMessagesAsync_FallbackSynthesizesAssistantReplyAndSkipsThinkingAndTools()
    {
        await using var scope = await CreateScopeAsync();
        await SeedEventAsync(scope.Db, "ws-a", "session-a", 1, "thinking", "{\"delta\":\"private reasoning\"}", "2026-06-02T08:00:00.0000000Z");
        await SeedEventAsync(scope.Db, "ws-a", "session-a", 2, "delta", "{\"delta\":\"hello \"}", "2026-06-02T08:00:01.0000000Z");
        await SeedEventAsync(scope.Db, "ws-a", "session-a", 3, "tool_call", "{\"name\":\"shell\"}", "2026-06-02T08:00:02.0000000Z");
        await SeedEventAsync(scope.Db, "ws-a", "session-a", 4, "delta", "{\"delta\":\"world\"}", "2026-06-02T08:00:03.0000000Z");
        await SeedEventAsync(scope.Db, "ws-a", "session-a", 5, "done", "{}", "2026-06-02T08:00:04.0000000Z");

        var service = new RawSessionLogService(scope.Factory);

        var page = await service.ReadMessagesAsync("ws-a", "session-a", limit: 10);

        Assert.AreEqual(1, page.Messages.Count);
        Assert.AreEqual("agent", page.Messages[0].Role);
        Assert.AreEqual("hello world", page.Messages[0].Content);
        Assert.AreEqual("message", page.Messages[0].EventType);
        Assert.IsFalse(page.Messages[0].Content.Contains("private reasoning", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(page.Messages[0].Content.Contains("shell", StringComparison.OrdinalIgnoreCase));
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

    private static async Task SeedEventAsync(
        PlatformDbContext db,
        string workspaceId,
        string sessionId,
        long sequenceNum,
        string eventType,
        string data,
        string recordedAt)
    {
        db.SessionEventLogs.Add(new SessionEventLogEntity
        {
            WorkspaceId = workspaceId,
            SessionId = sessionId,
            SequenceNum = sequenceNum,
            EventType = eventType,
            Data = data,
            RecordedAt = recordedAt,
        });
        await db.SaveChangesAsync();
    }

    private static async Task SeedEventAsync(
        PlatformDbContext db,
        string workspaceId,
        string agentInstanceId,
        string sessionId,
        long sequenceNum,
        string eventType,
        string data,
        string recordedAt)
    {
        db.SessionEventLogs.Add(new SessionEventLogEntity
        {
            WorkspaceId = workspaceId,
            AgentInstanceId = agentInstanceId,
            SessionId = sessionId,
            SequenceNum = sequenceNum,
            EventType = eventType,
            Data = data,
            RecordedAt = recordedAt,
        });
        await db.SaveChangesAsync();
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
}
