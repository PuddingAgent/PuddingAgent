using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using System.Threading.Channels;
using PuddingCode.Abstractions;
using PuddingCode.Configuration;
using PuddingCode.Observability;
using PuddingCode.Platform;
using PuddingCode.Services;
using PuddingPlatform.Data;
using PuddingPlatform.Data.Entities;
using PuddingPlatform.Services;
using PuddingPlatform.Services.Diagnostics;

namespace PuddingPlatformTests.Services;

/// <summary>
/// ADR-028：SessionStateManager 并发序号原子化测试。
/// 验证 per-session SemaphoreSlim 消除 unique constraint 竞争。
/// </summary>
[TestClass]
public sealed class SessionStateManagerSequenceTests
{
    private IServiceScopeFactory CreateScopeFactory(string dbPath)
    {
        var options = new DbContextOptionsBuilder<PlatformDbContext>()
            .UseSqlite($"Data Source={dbPath}")
            .Options;

        var services = new ServiceCollection();
        services.AddScoped(_ => new PlatformDbContext(options));
        var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<IServiceScopeFactory>();
    }

    private SessionStateManager CreateSsm(
        string dbPath,
        AgentRawLogMirrorService? rawLogMirror = null)
    {
        var scopeFactory = CreateScopeFactory(dbPath);

        // 确保表已创建
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        db.Database.EnsureCreated();

        var tmpDir = Path.Combine(Path.GetTempPath(), $"jsonl_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmpDir);

        var dataPaths = PuddingDataPaths.FromRoot(tmpDir);

        return new SessionStateManager(
            scopeFactory,
            NullLogger<SessionStateManager>.Instance,
            NullRuntimeActivitySink.Instance,
            new NoOpTraceAccessor(),
            new JsonlSessionWriter(tmpDir),
            new SessionStateStore(dataPaths, NullLogger<SessionStateStore>.Instance),
            new ConversationDiagnosticEventProjector(),
            rawLogMirror);
    }

    /// <summary>
    /// 同一 session 并发 50 个 append → 序列号连续递增、无重复、无不连续。
    /// </summary>
    [TestMethod]
    public async Task AppendAsync_ConcurrentSameSession_AssignsUniqueIncreasingSequences()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"test_{Guid.NewGuid():N}.db");
        try
        {
            var ssm = CreateSsm(dbPath);
            const string sessionId = "s1";
            const string workspaceId = "w1";

            var tasks = Enumerable.Range(0, 50)
                .Select(i => ssm.AppendAsync(
                    sessionId,
                    workspaceId,
                    new ServerSentEventFrame("delta", $$"""{"delta":"{{i}}"}""")))
                .ToArray();

            var sequences = await Task.WhenAll(tasks);

            // 50 个全部成功，无重复
            Assert.AreEqual(50, sequences.Distinct().Count());

            // 排序后应为 1..50 连续
            var sorted = sequences.OrderBy(x => x).ToArray();
            CollectionAssert.AreEqual(
                Enumerable.Range(1, 50).Select(i => (long)i).ToArray(),
                sorted);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(dbPath)) File.Delete(dbPath);
        }
    }

    /// <summary>
    /// 不同 session 并发 append → 各自从 1 开始，不互相阻塞。
    /// </summary>
    [TestMethod]
    public async Task AppendAsync_ConcurrentDifferentSessions_EachSessionStartsAtOne()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"test_{Guid.NewGuid():N}.db");
        try
        {
            var ssm = CreateSsm(dbPath);

            var results = await Task.WhenAll(
                ssm.AppendAsync("s1", "w1", new ServerSentEventFrame("delta", "{}")),
                ssm.AppendAsync("s2", "w1", new ServerSentEventFrame("delta", "{}")));

            Assert.AreEqual(1L, results[0]);
            Assert.AreEqual(1L, results[1]);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(dbPath)) File.Delete(dbPath);
        }
    }

    /// <summary>
    /// SQLite 中不存在重复 (session_id, sequence_num)。
    /// </summary>
    [TestMethod]
    public async Task AppendAsync_NoDuplicateSequenceNum_InSqlite()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"test_{Guid.NewGuid():N}.db");
        try
        {
            var ssm = CreateSsm(dbPath);
            const string sessionId = "s1";
            const string workspaceId = "w1";

            var tasks = Enumerable.Range(0, 30)
                .Select(i => ssm.AppendAsync(
                    sessionId,
                    workspaceId,
                    new ServerSentEventFrame("delta", $$"""{"delta":"{{i}}"}""")))
                .ToArray();

            await Task.WhenAll(tasks);

            var scopeFactory = CreateScopeFactory(dbPath);
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();

            var duplicates = await db.SessionEventLogs
                .GroupBy(e => new { e.SessionId, e.SequenceNum })
                .Where(g => g.Count() > 1)
                .CountAsync();

            Assert.AreEqual(0, duplicates);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(dbPath)) File.Delete(dbPath);
        }
    }

    /// <summary>
    /// 多个实时订阅者应各自收到同一帧；ChannelReader 不能共享消费，否则 SSE 重连会互相偷帧。
    /// </summary>
    [TestMethod]
    public async Task Subscribe_MultipleReaders_ReceivesBroadcastFrames()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"test_{Guid.NewGuid():N}.db");
        try
        {
            var ssm = CreateSsm(dbPath);
            const string sessionId = "s1";
            const string workspaceId = "w1";

            var reader1 = ssm.Subscribe(sessionId);
            var reader2 = ssm.Subscribe(sessionId);

            Assert.IsNotNull(reader1);
            Assert.IsNotNull(reader2);

            await ssm.AppendAsync(
                sessionId,
                workspaceId,
                new ServerSentEventFrame("delta", """{"delta":"a"}"""));

            var frame1 = await ReadOneAsync(reader1);
            var frame2 = await ReadOneAsync(reader2);

            Assert.AreEqual("delta", frame1.Event);
            Assert.AreEqual("delta", frame2.Event);
            Assert.AreEqual("""{"sequenceNum":1,"delta":"a"}""", frame1.Data);
            Assert.AreEqual("""{"sequenceNum":1,"delta":"a"}""", frame2.Data);

            ssm.Unsubscribe(sessionId, reader1);

            await ssm.AppendAsync(
                sessionId,
                workspaceId,
                new ServerSentEventFrame("delta", """{"delta":"b"}"""));

            var frameAfterUnsubscribe = await ReadOneAsync(reader2);
            Assert.AreEqual("""{"sequenceNum":2,"delta":"b"}""", frameAfterUnsubscribe.Data);
            Assert.IsFalse(reader1.TryRead(out _), "unsubscribed reader should not receive new frames");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(dbPath)) File.Delete(dbPath);
        }
    }

    [TestMethod]
    public async Task AppendAsync_WithAgentTrace_MirrorsRawEventToAgentPrivateFile()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"test_{Guid.NewGuid():N}.db");
        var dataRoot = Path.Combine(Path.GetTempPath(), $"raw_mirror_{Guid.NewGuid():N}");
        try
        {
            var paths = PuddingCode.Configuration.PuddingDataPaths.FromRoot(dataRoot);
            var mirror = new AgentRawLogMirrorService(
                paths,
                NullLogger<AgentRawLogMirrorService>.Instance);
            var ssm = CreateSsm(dbPath, mirror);
            var trace = RuntimeTraceContext.CreateNew(
                    sessionId: "s1",
                    workspaceId: "w1")
                .WithAgent("agent-1", "template-1");

            var sequence = await ssm.AppendAsync(
                "s1",
                "w1",
                new ServerSentEventFrame("tool_result", """{"ok":true}"""),
                trace: trace,
                component: RuntimeActivityComponents.AgentExecution,
                operation: "tool.result");

            Assert.AreEqual(1L, sequence);
            var rawRoot = paths.AgentInstanceRawLogsRoot("agent-1");
            var files = Directory.GetFiles(rawRoot, "s1.jsonl", SearchOption.AllDirectories);
            Assert.AreEqual(1, files.Length);

            var line = await File.ReadAllTextAsync(files[0]);
            StringAssert.Contains(line, "\"agentInstanceId\":\"agent-1\"");
            StringAssert.Contains(line, "\"eventType\":\"tool_result\"");
            StringAssert.Contains(line, "\"evidenceRef\":\"session-raw:");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(dbPath)) File.Delete(dbPath);
            if (Directory.Exists(dataRoot)) Directory.Delete(dataRoot, recursive: true);
        }
    }

    [TestMethod]
    public async Task TrackSubAgentStartAsync_ReusedSession_ResetsTerminalProjectionWithoutDuplicate()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"test_{Guid.NewGuid():N}.db");
        try
        {
            var ssm = CreateSsm(dbPath);
            const string parentSessionId = "parent-session";
            const string subSessionId = "parent-session-sub-reused";
            var firstStartedAt = DateTimeOffset.Parse("2026-07-19T08:00:00+00:00");
            var secondStartedAt = DateTimeOffset.Parse("2026-07-19T08:05:00+00:00");

            await ssm.TrackSubAgentStartAsync(parentSessionId, new SubAgentSpawnInfo
            {
                ParentSessionId = parentSessionId,
                ParentAgentId = "agent-a",
                SubSessionId = subSessionId,
                TemplateId = "planner",
                ModelId = "kimi-k3",
                TaskSummary = "first task",
                SpawnedAt = firstStartedAt,
            });
            await ssm.TrackSubAgentCompleteAsync(subSessionId, new SubAgentResult
            {
                Success = true,
                Reply = "first reply",
                Error = null,
                CompletedAt = firstStartedAt.AddMinutes(1),
            });

            await ssm.TrackSubAgentStartAsync(parentSessionId, new SubAgentSpawnInfo
            {
                ParentSessionId = parentSessionId,
                ParentAgentId = "agent-a",
                SubSessionId = subSessionId,
                TemplateId = "reviewer",
                ModelId = "deepseek-v4-pro",
                TaskSummary = "second task",
                SpawnedAt = secondStartedAt,
            });

            using var scope = CreateScopeFactory(dbPath).CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            var rows = await db.SessionSubAgents
                .Where(e => e.SubSessionId == subSessionId)
                .ToListAsync();

            Assert.AreEqual(1, rows.Count, "A reusable SubSessionId must have one current-state row.");
            var current = rows.Single();
            Assert.AreEqual(parentSessionId, current.ParentSessionId);
            Assert.AreEqual("running", current.Status);
            Assert.AreEqual("reviewer", current.TemplateId);
            Assert.AreEqual("deepseek-v4-pro", current.ModelId);
            Assert.AreEqual("second task", current.TaskSummary);
            Assert.AreEqual(secondStartedAt.ToString("O"), current.SpawnedAt);
            Assert.IsNull(current.CompletedAt);
            Assert.IsNull(current.Success);
            Assert.IsNull(current.ReplySummary);
            Assert.IsNull(current.ErrorSummary);
            Assert.IsNull(current.FullResultJson);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(dbPath)) File.Delete(dbPath);
        }
    }

    [TestMethod]
    public async Task TrackSubAgentStartAsync_SameSubSessionDifferentParent_RejectsRebinding()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"test_{Guid.NewGuid():N}.db");
        try
        {
            var ssm = CreateSsm(dbPath);
            const string subSessionId = "shared-sub-session";
            var startedAt = DateTimeOffset.Parse("2026-07-19T08:00:00+00:00");

            await ssm.TrackSubAgentStartAsync("parent-a", new SubAgentSpawnInfo
            {
                ParentSessionId = "parent-a",
                ParentAgentId = "agent-a",
                SubSessionId = subSessionId,
                TaskSummary = "owned by parent-a",
                SpawnedAt = startedAt,
            });

            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
                ssm.TrackSubAgentStartAsync("parent-b", new SubAgentSpawnInfo
                {
                    ParentSessionId = "parent-b",
                    ParentAgentId = "agent-b",
                    SubSessionId = subSessionId,
                    TaskSummary = "must not rebind",
                    SpawnedAt = startedAt.AddMinutes(1),
                }));

            using var scope = CreateScopeFactory(dbPath).CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            var rows = await db.SessionSubAgents
                .Where(e => e.SubSessionId == subSessionId)
                .ToListAsync();

            Assert.AreEqual(1, rows.Count);
            Assert.AreEqual("parent-a", rows[0].ParentSessionId);
            Assert.AreEqual("owned by parent-a", rows[0].TaskSummary);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(dbPath)) File.Delete(dbPath);
        }
    }

    [TestMethod]
    public async Task TrackSubAgentStartAsync_ConcurrentSameSession_UsesSingleCurrentStateRow()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"test_{Guid.NewGuid():N}.db");
        try
        {
            var ssm = CreateSsm(dbPath);
            const string parentSessionId = "parent-session";
            const string subSessionId = "parent-session-sub-concurrent";
            var startedAt = DateTimeOffset.Parse("2026-07-19T08:00:00+00:00");

            var starts = Enumerable.Range(0, 8)
                .Select(index => ssm.TrackSubAgentStartAsync(parentSessionId, new SubAgentSpawnInfo
                {
                    ParentSessionId = parentSessionId,
                    ParentAgentId = "agent-a",
                    SubSessionId = subSessionId,
                    TemplateId = "explorer",
                    ModelId = "deepseek-v4-flash",
                    TaskSummary = $"task-{index}",
                    SpawnedAt = startedAt.AddSeconds(index),
                }))
                .ToArray();

            await Task.WhenAll(starts);

            using var scope = CreateScopeFactory(dbPath).CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            var rows = await db.SessionSubAgents
                .Where(e => e.SubSessionId == subSessionId)
                .ToListAsync();

            Assert.AreEqual(1, rows.Count, "Atomic UPSERT must not create duplicate current-state rows.");
            Assert.AreEqual(parentSessionId, rows[0].ParentSessionId);
            Assert.AreEqual("running", rows[0].Status);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(dbPath)) File.Delete(dbPath);
        }
    }

    private static async Task<ServerSentEventFrame> ReadOneAsync(ChannelReader<ServerSentEventFrame> reader)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (await reader.WaitToReadAsync(cts.Token))
        {
            if (reader.TryRead(out var frame))
                return frame;
        }

        Assert.Fail("Expected one SSE frame.");
        throw new InvalidOperationException("Expected one SSE frame.");
    }

    [TestMethod]
    public async Task GetSubAgentsAsync_ReconcilesDispatcherFailedRunningSubAgent()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"test_{Guid.NewGuid():N}.db");
        try
        {
            var ssm = CreateSsm(dbPath);
            const string parentSessionId = "parent-session";
            const string subSessionId = "parent-session-sub-stale";
            const string workspaceId = "default";
            const string failedAt = "2026-06-12T13:31:49.9674180+00:00";

            using (var scope = CreateScopeFactory(dbPath).CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
                db.SessionSubAgents.Add(new SessionSubAgentEntity
                {
                    ParentSessionId = parentSessionId,
                    ParentAgentId = "agent-a",
                    SubSessionId = subSessionId,
                    Status = "running",
                    TemplateId = "workspace-task-agent",
                    ModelId = "deepseek-v4-flash",
                    TaskSummary = "stale child",
                    SpawnedAt = "2026-06-12T13:30:49.0577505+00:00",
                });
                db.SubAgentRuns.Add(new SubAgentRunEntity
                {
                    RunId = "run-stale",
                    ParentSessionId = parentSessionId,
                    SubSessionId = subSessionId,
                    WorkspaceId = workspaceId,
                    AgentInstanceId = "agent-a",
                    TemplateId = "workspace-task-agent",
                    Status = "running",
                    StartedAt = "2026-06-12T13:30:49.0577505+00:00",
                    ArchivePath = "archive",
                });
                db.RuntimeActivities.Add(new RuntimeActivityEntity
                {
                    ActivityId = "activity-stale",
                    TraceId = "trace-stale",
                    CorrelationId = "trace-stale",
                    SessionId = parentSessionId,
                    WorkspaceId = workspaceId,
                    ExecutionId = subSessionId,
                    SubAgentId = subSessionId,
                    EventId = "event-stale",
                    Component = RuntimeActivityComponents.EventDispatcher,
                    Operation = "dispatch",
                    Status = "failed",
                    StartedAtUtc = failedAt,
                    Severity = "info",
                    Summary = "Max retries exhausted",
                    MetadataJson = "{\"eventType\":\"subagent.run.created\"}",
                });
                await db.SaveChangesAsync();
            }

            var agents = await ssm.GetSubAgentsAsync(parentSessionId);
            var status = agents.Single();

            Assert.AreEqual("run-stale", status.RunId);
            Assert.AreEqual(parentSessionId, status.ParentSessionId);
            Assert.AreEqual("failed", status.Status);
            Assert.AreEqual(false, status.Success);
            Assert.AreEqual(DateTimeOffset.Parse(failedAt), status.CompletedAt);
            Assert.AreEqual(0, await ssm.GetRunningSubAgentCountAsync(parentSessionId));

            using var verifyScope = CreateScopeFactory(dbPath).CreateScope();
            var verifyDb = verifyScope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            var run = await verifyDb.SubAgentRuns.SingleAsync(r => r.SubSessionId == subSessionId);
            Assert.AreEqual("failed", run.Status);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(dbPath)) File.Delete(dbPath);
        }
    }

    /// <summary>
    /// P0-4f B3：GetTraceReportAsync 读 canonical conversation_events + 共享投影器，
    /// 验证 usage / tool / subagent / component 全部走投影器聚合，不再读 session_event_log。
    /// </summary>
    [TestMethod]
    public async Task GetTraceReportAsync_ConversationEventSource_ProjectsThroughProjector()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"test_{Guid.NewGuid():N}.db");
        try
        {
            const string sessionId = "conv-1";
            const string workspaceId = "default";

            var ssm = CreateSsm(dbPath);

            using (var scope = CreateScopeFactory(dbPath).CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
                db.Database.EnsureCreated();

                db.ConversationEvents.AddRange(
                    CreateConversationEvent(sessionId, workspaceId, 1, "turn-1", "run-1",
                        ConversationEventTypes.TurnStarted, "{}", "2026-06-03T22:42:34.000Z", "trace-1"),
                    CreateConversationEvent(sessionId, workspaceId, 2, "turn-1", "run-1",
                        ConversationEventTypes.ToolCallRequested, "{\"name\":\"list_dir\"}", "2026-06-03T22:42:35.000Z", "trace-1"),
                    CreateConversationEvent(sessionId, workspaceId, 3, "turn-1", "run-1",
                        ConversationEventTypes.ToolCallCompleted, "{\"name\":\"list_dir\",\"exitCode\":0,\"output\":\"ok\"}", "2026-06-03T22:42:36.000Z", "trace-1"),
                    CreateConversationEvent(sessionId, workspaceId, 4, "turn-1", "run-1",
                        ConversationEventTypes.UsageRecorded, "{\"modelId\":\"deepseek\",\"endpoint\":\"e1\",\"inputTokens\":10,\"outputTokens\":5,\"durationMs\":100}", "2026-06-03T22:42:37.000Z", "trace-1"),
                    CreateConversationEvent(sessionId, workspaceId, 5, "turn-1", "run-1",
                        ConversationEventTypes.TurnCompleted, "{\"reply\":\"hello\"}", "2026-06-03T22:42:38.000Z", "trace-1"),
                    CreateConversationEvent(sessionId, workspaceId, 6, "turn-2", "run-sub-1",
                        ConversationEventTypes.SubAgentRunCreated, "{\"subAgentId\":\"sub-1\"}", "2026-06-03T22:42:39.000Z", "trace-1"),
                    CreateConversationEvent(sessionId, workspaceId, 7, "turn-2", "run-sub-1",
                        ConversationEventTypes.SubAgentRunCompleted, "{\"subAgentId\":\"sub-1\"}", "2026-06-03T22:42:41.000Z", "trace-1"));

                await db.SaveChangesAsync();
            }

            var report = await ssm.GetTraceReportAsync(sessionId);

            Assert.AreEqual(sessionId, report.SessionId);
            Assert.AreEqual(1, report.TraceIds.Count);
            Assert.AreEqual("trace-1", report.TraceIds[0]);

            // 组件时序走投影器（turn.completed → completed）
            var turnCompleted = report.ComponentTimeline.Single(c => c.Operation == ConversationEventTypes.TurnCompleted);
            Assert.AreEqual("completed", turnCompleted.Status);
            Assert.AreEqual("chat.acceptance", turnCompleted.Component);

            // LLM 调用走 TryProjectUsage
            Assert.AreEqual(1, report.LlmCalls.Count);
            Assert.AreEqual("deepseek", report.LlmCalls[0].Model);
            Assert.AreEqual(10, report.LlmCalls[0].InputTokens);
            Assert.AreEqual(5, report.LlmCalls[0].OutputTokens);
            Assert.AreEqual(15, report.TotalTokens);

            // 工具调用走 TryProjectToolCall（配对 requested + completed）
            Assert.AreEqual(1, report.ToolCalls.Count);
            Assert.AreEqual("list_dir", report.ToolCalls[0].ToolName);
            Assert.IsTrue(report.ToolCalls[0].Success);
            Assert.AreEqual(1000, report.ToolCalls[0].DurationMs);

            // 子代理走 ExtractSubAgentId（配对 created + completed）
            Assert.AreEqual(1, report.SubAgents.Count);
            Assert.AreEqual("sub-1", report.SubAgents[0].SubAgentId);
            Assert.AreEqual("completed", report.SubAgents[0].Status);
            Assert.AreEqual(2000, report.SubAgents[0].DurationMs);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(dbPath)) File.Delete(dbPath);
        }
    }

    private static ConversationEventEntity CreateConversationEvent(
        string conversationId,
        string workspaceId,
        long sequence,
        string turnId,
        string runId,
        string type,
        string payload,
        string occurredAt,
        string traceId)
        => new()
        {
            ConversationId = conversationId,
            WorkspaceId = workspaceId,
            Sequence = sequence,
            TurnId = turnId,
            CommandId = "cmd-1",
            RunId = runId,
            MessageId = "msg-1",
            EventId = $"evt-{sequence}",
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
            TraceId = traceId,
            ProducerComponent = "chat.acceptance",
        };
}

/// <summary>
/// ADR-028 测试用 Null 桩：不记录任何运行时活动。
/// </summary>
file sealed class NullRuntimeActivitySink : IRuntimeActivitySink
{
    public static readonly NullRuntimeActivitySink Instance = new();

    public Task RecordAsync(RuntimeActivity activity, CancellationToken ct = default) => Task.CompletedTask;

    public Task<IReadOnlyList<RuntimeActivity>> QueryAsync(RuntimeActivityQuery query, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<RuntimeActivity>>(Array.Empty<RuntimeActivity>());
}

/// <summary>
/// ADR-028 测试用 NoOp 桩：返回空 TraceContext。
/// </summary>
file sealed class NoOpTraceAccessor : IRuntimeTraceAccessor
{
    public RuntimeTraceContext? Current { get; set; }
}
