using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using PuddingCode.Configuration;
using PuddingCode.Serialization;
using PuddingCode.SubAgents;
using PuddingCode.Platform;
using PuddingPlatform.Data;
using PuddingPlatform.Services;

namespace PuddingPlatformTests.Services;

[TestClass]
public sealed class FileSubAgentRunStoreTests
{
    [TestMethod]
    public async Task SubAgentRunIndex_Maps_SnakeCase_Runtime_Columns_From_Existing_Schema()
    {
        using var temp = TemporaryDirectory.Create();
        var dbPath = Path.Combine(temp.Path, "platform.db");
        var options = new DbContextOptionsBuilder<PlatformDbContext>()
            .UseSqlite($"Data Source={dbPath}")
            .Options;

        await using (var db = new PlatformDbContext(options))
        {
            await db.Database.ExecuteSqlRawAsync("""
                CREATE TABLE sub_agent_runs (
                    id                  INTEGER PRIMARY KEY AUTOINCREMENT,
                    run_id              TEXT    NOT NULL UNIQUE,
                    parent_session_id   TEXT    NOT NULL,
                    sub_session_id      TEXT    NOT NULL,
                    workspace_id        TEXT    NOT NULL,
                    agent_instance_id   TEXT    NOT NULL,
                    template_id         TEXT    NOT NULL,
                    status              TEXT    NOT NULL DEFAULT 'running',
                    started_at          TEXT    NOT NULL,
                    completed_at        TEXT,
                    archive_path        TEXT    NOT NULL,
                    trace_id            TEXT,
                    correlation_id      TEXT,
                    error_message       TEXT,
                    task_planning_metadata_json TEXT,
                    total_rounds        INTEGER NOT NULL DEFAULT 0,
                    total_tool_calls    INTEGER NOT NULL DEFAULT 0,
                    total_duration_ms   INTEGER NOT NULL DEFAULT 0
                );
                """);
            await db.Database.ExecuteSqlRawAsync("""
                INSERT INTO sub_agent_runs (
                    run_id, parent_session_id, sub_session_id, workspace_id, agent_instance_id,
                    template_id, status, started_at, archive_path, total_rounds,
                    total_tool_calls, total_duration_ms
                ) VALUES (
                    'run-1', 'parent-1', 'sub-1', 'default', 'agent-1',
                    'template-1', 'failed', '2026-05-24T00:00:00Z', 'archive',
                    4, 2, 350
                );
                """);
        }

        await using var verifyDb = new PlatformDbContext(options);
        var index = await verifyDb.SubAgentRuns.SingleAsync(r => r.RunId == "run-1");

        Assert.AreEqual(4, index.TotalRounds);
        Assert.AreEqual(2, index.TotalToolCalls);
        Assert.AreEqual(350, index.TotalDurationMs);
    }

    [TestMethod]
    public async Task RunArchive_Writes_Expected_File_Formats_And_Terminal_State_Is_Idempotent()
    {
        using var temp = TemporaryDirectory.Create();
        var paths = PuddingDataPaths.FromRoot(temp.Path);
        var dbPath = Path.Combine(temp.Path, "platform.db");
        var options = new DbContextOptionsBuilder<PlatformDbContext>()
            .UseSqlite($"Data Source={dbPath}")
            .Options;

        await using (var db = new PlatformDbContext(options))
        {
            await db.Database.EnsureCreatedAsync();
        }

        var conversationEvents = new RecordingConversationEventStore();
        var store = new FileSubAgentRunStore(
            paths,
            NullLogger<FileSubAgentRunStore>.Instance,
            new TestDbContextFactory(options),
            conversationEvents);

        var handle = await store.CreateRunAsync(new SubAgentRunCreateRequest
        {
            ParentSessionId = "parent-session",
            SubSessionId = "parent-session/sub/sub-agent",
            WorkspaceId = "default",
            AgentInstanceId = "default.researcher-001",
            TemplateId = "researcher",
            Task = "Research the current architecture",
            TaskPlanId = "plan_1",
            TaskNodeId = "task_1",
            ParentTaskNodeId = "task_parent",
            DelegationDepth = 1,
            MaxDelegationDepth = 2,
            RoleInPlan = "researcher",
            AllowSubDelegation = true,
            AllowAgentCreation = false,
            AssignedObjective = "Research the current architecture",
            ExpectedOutputContract = "Return findings.",
        });

        var runJsonPath = Path.Combine(handle.ArchivePath, "run.json");
        var runJson = await File.ReadAllTextAsync(runJsonPath);
        Assert.IsTrue(runJson.Contains('\n'));
        StringAssert.Contains(runJson, "\"parentSessionId\"");

        await store.AppendEventAsync(handle.RunId, "subagent.run.started", new
        {
            ParentSessionId = "parent-session",
            Detail = "line one\nline two",
        });

        await store.AppendToolAuditAsync(handle.RunId, new SubAgentToolAuditEntry
        {
            ToolCallId = "tool-1",
            ToolName = "file_read",
            ArgsHash = "sha256:abc",
            Success = true,
            DurationMs = 17,
            OutputLength = 128,
        });

        var eventsLines = await File.ReadAllLinesAsync(Path.Combine(handle.ArchivePath, "events.jsonl"));
        Assert.AreEqual(2, eventsLines.Length);
        Assert.IsTrue(eventsLines.All(static line => !line.Contains('\r')));
        Assert.IsTrue(eventsLines.All(static line => !line.Contains('\n')));
        StringAssert.Contains(eventsLines[1], "\\n");
        StringAssert.Contains(eventsLines[1], $"\"run_id\":\"{handle.RunId}\"");

        var applied = await store.CompleteRunAsync(handle.RunId, new SubAgentRunCompletion
        {
            Status = "completed",
            Output = "final output",
            TotalRounds = 3,
            TotalToolCalls = 1,
            TotalDurationMs = 250,
        });
        var alreadyTerminal = await store.CompleteRunAsync(handle.RunId, new SubAgentRunCompletion
        {
            Status = "failed",
            ErrorMessage = "late duplicate completion",
        });

        Assert.AreEqual(SubAgentRunTerminalWriteResult.Applied, applied);
        Assert.AreEqual(SubAgentRunTerminalWriteResult.AlreadyTerminal, alreadyTerminal);

        var archive = await store.GetRunArchiveAsync(handle.RunId);
        Assert.IsNotNull(archive);
        Assert.AreEqual("completed", archive.Manifest.Status);
        Assert.AreEqual(3, archive.Events.Count);
        Assert.AreEqual(1, archive.Tools.Count);
        Assert.AreEqual("final output", archive.Output);
        CollectionAssert.AreEqual(
            new[]
            {
                ConversationEventTypes.SubAgentRunCreated,
                ConversationEventTypes.SubAgentRunStarted,
                ConversationEventTypes.SubAgentRunCompleted,
            },
            conversationEvents.Appended.Select(static item => item.Event.Type).ToArray());
        Assert.IsTrue(
            conversationEvents.Appended.All(static item => item.ConversationId == "parent-session"));

        await using var verifyDb = new PlatformDbContext(options);
        var index = await verifyDb.SubAgentRuns.SingleAsync(r => r.RunId == handle.RunId);
        Assert.AreEqual("completed", index.Status);
        Assert.AreEqual(3, index.TotalRounds);
        Assert.AreEqual(1, index.TotalToolCalls);
        Assert.AreEqual(250, index.TotalDurationMs);
        Assert.IsNotNull(index.TaskPlanningMetadataJson);
        StringAssert.Contains(index.TaskPlanningMetadataJson!, "\"task_plan_id\":\"plan_1\"");
        StringAssert.Contains(index.TaskPlanningMetadataJson!, "\"task_node_id\":\"task_1\"");

        var manifest = JsonSerializer.Deserialize<SubAgentRunManifest>(
            await File.ReadAllTextAsync(runJsonPath),
            PuddingJsonContracts.PrettyJson);
        Assert.IsNotNull(manifest);
        Assert.AreEqual("completed", manifest.Status);
        Assert.AreEqual("plan_1", manifest.TaskPlanning["task_plan_id"]);
        Assert.AreEqual("task_1", manifest.TaskPlanning["task_node_id"]);
        Assert.AreEqual("2", manifest.TaskPlanning["max_delegation_depth"]);
    }

    [TestMethod]
    public async Task Recovery_Marks_Previous_Process_NonTerminal_Run_As_Interrupted()
    {
        using var temp = TemporaryDirectory.Create();
        var paths = PuddingDataPaths.FromRoot(temp.Path);
        var options = new DbContextOptionsBuilder<PlatformDbContext>()
            .UseSqlite($"Data Source={Path.Combine(temp.Path, "platform.db")}")
            .Options;
        await using (var db = new PlatformDbContext(options))
        {
            await db.Database.EnsureCreatedAsync();
        }

        var conversationEvents = new RecordingConversationEventStore();
        var store = new FileSubAgentRunStore(
            paths,
            NullLogger<FileSubAgentRunStore>.Instance,
            new TestDbContextFactory(options),
            conversationEvents);
        var handle = await store.CreateRunAsync(new SubAgentRunCreateRequest
        {
            ParentSessionId = "parent-session",
            SubSessionId = "sub-session",
            WorkspaceId = "default",
            AgentInstanceId = "agent-1",
            TemplateId = "researcher",
            Task = "Recover me after restart",
        });
        await store.AppendEventAsync(handle.RunId, ConversationEventTypes.SubAgentRoundStarted, new
        {
            round = 2,
        });
        await store.AppendEventAsync(handle.RunId, ConversationEventTypes.SubAgentToolCompleted, new
        {
            round = 2,
            tool_call_id = "tool-1",
            tool_name = "file_read",
            output_length = 42,
        });

        var recovered = await store.RecoverInterruptedRunsAsync(
            DateTimeOffset.UtcNow.AddSeconds(1),
            maxRuns: 100);

        Assert.AreEqual(1, recovered);
        var archive = await store.GetRunArchiveAsync(handle.RunId);
        Assert.IsNotNull(archive);
        Assert.AreEqual("interrupted", archive.Manifest.Status);
        Assert.AreEqual(ConversationEventTypes.SubAgentRunInterrupted, conversationEvents.Appended[^1].Event.Type);

        await using var verifyDb = new PlatformDbContext(options);
        var index = await verifyDb.SubAgentRuns.SingleAsync(r => r.RunId == handle.RunId);
        Assert.AreEqual("interrupted", index.Status);
        Assert.AreEqual(2, index.TotalRounds);
        Assert.AreEqual(1, index.TotalToolCalls);
    }

    private sealed class TestDbContextFactory(DbContextOptions<PlatformDbContext> options)
        : IDbContextFactory<PlatformDbContext>
    {
        public PlatformDbContext CreateDbContext() => new(options);

        public Task<PlatformDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(CreateDbContext());
    }

    private sealed class RecordingConversationEventStore : IConversationEventStore
    {
        public List<(string ConversationId, NewConversationEvent Event)> Appended { get; } = [];

        public Task<AppendResult> AppendAsync(
            string conversationId,
            long expectedVersion,
            IReadOnlyList<NewConversationEvent> events,
            EventWriteCondition condition,
            CancellationToken ct)
        {
            foreach (var item in events)
            {
                if (Appended.All(existing => existing.Event.EventId != item.EventId))
                    Appended.Add((conversationId, item));
            }

            var last = Appended.Count;
            return Task.FromResult(new AppendResult(last, last, events.Count));
        }

        public Task<EventPage> ReadForwardAsync(
            string conversationId,
            long afterExclusive,
            long? throughInclusive,
            int limit,
            CancellationToken ct) =>
            Task.FromResult(new EventPage([], null, false));

        public Task<EventPage> ReadBackwardAsync(
            string conversationId,
            long beforeExclusive,
            int limit,
            CancellationToken ct) =>
            Task.FromResult(new EventPage([], null, false));

        public Task<EventPage> ReadByTypePrefixBackwardAsync(
            string conversationId,
            string typePrefix,
            long beforeExclusive,
            int limit,
            CancellationToken ct) =>
            Task.FromResult(new EventPage([], null, false));

        public Task<EventBounds> GetBoundsAsync(
            string conversationId,
            CancellationToken ct) =>
            Task.FromResult(new EventBounds(null, null));

        public Task EnsureTablesAsync(CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        private TemporaryDirectory(string path) => Path = path;

        public string Path { get; }

        public static TemporaryDirectory Create()
        {
            var path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "pudding-platform-tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return new TemporaryDirectory(path);
        }

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();

            if (Directory.Exists(Path))
                Directory.Delete(Path, recursive: true);
        }
    }
}
