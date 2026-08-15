using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PuddingCode.Diagnostics;
using PuddingCode.Platform;
using PuddingPlatform.Data;
using PuddingPlatform.Data.Entities;
using PuddingPlatform.Services.Diagnostics;

namespace PuddingPlatformTests.Services;

[TestClass]
public sealed class RuntimeTimelineQueryServiceTests
{
    [TestMethod]
    public async Task QueryTimelineAsync_UserDisplayMode_GroupsChatStreamFramesIntoMessageAndToolRows()
    {
        await using var scope = await CreateScopeAsync();
        await using (var db = await scope.Factory.CreateDbContextAsync())
        {
            db.RuntimeActivities.AddRange(
                CreateActivity("activity-delta", "session_state", "chat.stream.delta", "succeeded", "Appended session event delta", "2026-06-03T22:42:34.001Z", 1),
                CreateActivity("activity-tool-call", "session_state", "chat.stream.tool_call", "succeeded", "Appended session event tool_call", "2026-06-03T22:42:34.002Z", 1),
                CreateActivity("activity-tool-result", "session_state", "chat.stream.tool_result", "succeeded", "Appended session event tool_result", "2026-06-03T22:42:34.003Z", 1),
                CreateActivity("activity-usage", "session_state", "chat.stream.usage", "succeeded", "Appended session event usage", "2026-06-03T22:42:34.004Z", 0));
            await db.SaveChangesAsync();
        }

        var service = new RuntimeTimelineQueryService(scope.Factory, new ConversationDiagnosticEventProjector());

        var result = await service.QueryTimelineAsync(new RuntimeTimelineQueryDto
        {
            SessionId = "session_1",
            SortOrder = "asc",
            DisplayMode = "user",
        });

        Assert.AreEqual(3, result.Total);
        CollectionAssert.AreEqual(
            new[] { "message", "tool_call", "message" },
            result.Items.Select(i => i.Kind).ToArray());
        Assert.AreEqual("chat.message.stream", result.Items[0].Operation);
        Assert.AreEqual("chat.tool_call", result.Items[1].Operation);
        Assert.AreEqual("2", result.Items[1].Metadata["raw_count"]);
        Assert.AreEqual("tool_call, tool_result", result.Items[1].Metadata["event_types"]);
    }

    [TestMethod]
    public async Task QueryTimelineAsync_ConversationEventSource_ProjectsThroughProjector()
    {
        await using var scope = await CreateScopeAsync();
        await using (var db = await scope.Factory.CreateDbContextAsync())
        {
            db.ConversationEvents.Add(CreateConversationEvent(
                "evt-1", "conv-1", 1, ConversationEventTypes.TurnCompleted,
                "{\"reply\":\"hello\"}", "2026-06-03T22:42:34.000Z"));
            await db.SaveChangesAsync();
        }

        var service = new RuntimeTimelineQueryService(scope.Factory, new ConversationDiagnosticEventProjector());

        var result = await service.QueryTimelineAsync(new RuntimeTimelineQueryDto
        {
            SessionId = "conv-1",
            SortOrder = "asc",
            DisplayMode = "raw",
        });

        Assert.AreEqual(1, result.Total);
        var item = result.Items[0];
        Assert.AreEqual("conversation_event", item.Kind);
        Assert.AreEqual(ConversationEventTypes.TurnCompleted, item.Operation);
        Assert.AreEqual("completed", item.Status);
        Assert.AreEqual("evt-1", item.Id);
        Assert.AreEqual("conv-1", item.SessionId);
        Assert.AreEqual("trace-1", item.TraceId);
        Assert.AreEqual("hello", item.Summary);
        Assert.AreEqual("chat.acceptance", item.Component);
    }

    private static RuntimeActivityEntity CreateActivity(
        string id,
        string component,
        string operation,
        string status,
        string summary,
        string startedAtUtc,
        long durationMs)
        => new()
        {
            ActivityId = id,
            TraceId = "trace_1",
            CorrelationId = "correlation_1",
            SessionId = "session_1",
            WorkspaceId = "default",
            Component = component,
            Operation = operation,
            Status = status,
            StartedAtUtc = startedAtUtc,
            EndedAtUtc = startedAtUtc,
            DurationMs = durationMs,
            Severity = "info",
            Summary = summary,
            MetadataJson = "{\"eventType\":\"" + operation.Replace("chat.stream.", "") + "\"}",
        };

    private static ConversationEventEntity CreateConversationEvent(
        string eventId,
        string conversationId,
        long sequence,
        string type,
        string payload,
        string occurredAt)
        => new()
        {
            EventId = eventId,
            ConversationId = conversationId,
            Sequence = sequence,
            WorkspaceId = "default",
            TurnId = "turn-1",
            CommandId = "cmd-1",
            RunId = "run-1",
            MessageId = "msg-1",
            Type = type,
            SchemaVersion = 1,
            Payload = payload,
            OccurredAt = occurredAt,
            CommittedAt = occurredAt,
            CorrelationId = "corr-1",
            CausationId = "caus-1",
            ProducerEventId = null,
            AgentId = "agent-1",
            SourceKind = "agent",
            TraceId = "trace-1",
            ProducerComponent = "chat.acceptance",
        };

    private static async Task<TestScope> CreateScopeAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<PlatformDbContext>()
            .UseSqlite(connection)
            .Options;
        var factory = new TestDbContextFactory(options);

        await using (var db = await factory.CreateDbContextAsync())
        {
            await db.Database.EnsureCreatedAsync();
        }

        return new TestScope(connection, factory);
    }

    private sealed record TestScope(
        SqliteConnection Connection,
        IDbContextFactory<PlatformDbContext> Factory) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await Connection.DisposeAsync();
        }
    }

    private sealed class TestDbContextFactory(DbContextOptions<PlatformDbContext> options)
        : IDbContextFactory<PlatformDbContext>
    {
        public PlatformDbContext CreateDbContext() => new(options);

        public Task<PlatformDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(CreateDbContext());
    }
}
