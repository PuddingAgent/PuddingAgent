using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PuddingCode.Diagnostics;
using PuddingPlatform.Data;
using PuddingPlatform.Data.Entities;
using PuddingPlatform.Services.Diagnostics;

namespace PuddingPlatformTests.Services;

[TestClass]
public sealed class RuntimeTimelineQueryServiceTests
{
    [TestMethod]
    public async Task QueryTimelineAsync_UserDisplayMode_GroupsSessionFramesIntoMessageAndToolRows()
    {
        await using var scope = await CreateScopeAsync();
        await using (var db = await scope.Factory.CreateDbContextAsync())
        {
            db.RuntimeActivities.AddRange(
                CreateActivity("activity-delta", "session_state", "chat.stream.delta", "succeeded", "Appended session event delta", "2026-06-03T22:42:34.001Z", 1),
                CreateActivity("activity-tool-call", "session_state", "chat.stream.tool_call", "succeeded", "Appended session event tool_call", "2026-06-03T22:42:34.002Z", 1),
                CreateActivity("activity-tool-result", "session_state", "chat.stream.tool_result", "succeeded", "Appended session event tool_result", "2026-06-03T22:42:34.003Z", 1),
                CreateActivity("activity-usage", "session_state", "chat.stream.usage", "succeeded", "Appended session event usage", "2026-06-03T22:42:34.004Z", 0));

            db.SessionEventLogs.AddRange(
                CreateSessionFrame(1, "delta", "chat.stream.delta", "2026-06-03T22:42:34.001Z"),
                CreateSessionFrame(2, "tool_call", "chat.stream.tool_call", "2026-06-03T22:42:34.002Z"),
                CreateSessionFrame(3, "tool_result", "chat.stream.tool_result", "2026-06-03T22:42:34.003Z"),
                CreateSessionFrame(4, "usage", "chat.stream.usage", "2026-06-03T22:42:34.004Z"));
            await db.SaveChangesAsync();
        }

        var service = new RuntimeTimelineQueryService(scope.Factory);

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
        Assert.AreEqual("4", result.Items[1].Metadata["raw_count"]);
        Assert.AreEqual("tool_call, tool_result", result.Items[1].Metadata["event_types"]);
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

    private static SessionEventLogEntity CreateSessionFrame(
        long sequence,
        string eventType,
        string operation,
        string recordedAt)
        => new()
        {
            SessionId = "session_1",
            WorkspaceId = "default",
            SequenceNum = sequence,
            EventType = eventType,
            Data = "{}",
            RecordedAt = recordedAt,
            TraceId = "trace_1",
            CorrelationId = "correlation_1",
            Component = "agent_execution",
            Operation = operation,
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
