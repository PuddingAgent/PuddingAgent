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
            Assert.AreEqual("""{"delta":"a"}""", frame1.Data);
            Assert.AreEqual("""{"delta":"a"}""", frame2.Data);

            ssm.Unsubscribe(sessionId, reader1);

            await ssm.AppendAsync(
                sessionId,
                workspaceId,
                new ServerSentEventFrame("delta", """{"delta":"b"}"""));

            var frameAfterUnsubscribe = await ReadOneAsync(reader2);
            Assert.AreEqual("""{"delta":"b"}""", frameAfterUnsubscribe.Data);
            Assert.IsFalse(reader1.TryRead(out _), "unsubscribed reader should not receive new frames");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(dbPath)) File.Delete(dbPath);
        }
    }

    [TestMethod]
    public async Task AppendAsync_AgentRealtimeDelta_FanoutBeforeBufferedPersistence()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"test_{Guid.NewGuid():N}.db");
        try
        {
            var ssm = CreateSsm(dbPath);
            const string sessionId = "s1";
            const string workspaceId = "w1";

            var reader = ssm.Subscribe(sessionId);
            Assert.IsNotNull(reader);

            var sequence = await ssm.AppendAsync(
                sessionId,
                workspaceId,
                new ServerSentEventFrame("delta", """{"delta":"buffered"}"""),
                component: RuntimeActivityComponents.AgentExecution,
                operation: "chat.stream.delta");

            Assert.AreEqual(1L, sequence);

            var liveFrame = await ReadOneAsync(reader);
            Assert.AreEqual("delta", liveFrame.Event);
            Assert.AreEqual("""{"delta":"buffered"}""", liveFrame.Data);

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            while (await ssm.GetEventCountAfterAsync(sessionId, 0, cts.Token) == 0)
                await Task.Delay(25, cts.Token);

            var replay = await ssm.ReplaySessionAsync(sessionId, fromSequenceNum: 0, limit: 10);
            Assert.AreEqual(1, replay.Events.Count);
            Assert.AreEqual(1L, replay.Events[0].SequenceNum);
            Assert.AreEqual("delta", replay.Events[0].EventType);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(dbPath)) File.Delete(dbPath);
        }
    }

    [TestMethod]
    public async Task AppendAsync_AgentRealtimeTerminal_FanoutBeforeFlushCompletes()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"test_{Guid.NewGuid():N}.db");
        try
        {
            var ssm = CreateSsm(dbPath);
            const string sessionId = "s1";
            const string workspaceId = "w1";

            var reader = ssm.Subscribe(sessionId);
            Assert.IsNotNull(reader);

            var deltaSeq = await ssm.AppendAsync(
                sessionId,
                workspaceId,
                new ServerSentEventFrame("delta", """{"delta":"buffered"}"""),
                component: RuntimeActivityComponents.AgentExecution,
                operation: "chat.stream.delta");
            var doneSeq = await ssm.AppendAsync(
                sessionId,
                workspaceId,
                new ServerSentEventFrame("done", """{"messageId":"m1"}"""),
                component: RuntimeActivityComponents.AgentExecution,
                operation: "chat.stream.done");

            Assert.AreEqual(1L, deltaSeq);
            Assert.AreEqual(2L, doneSeq);

            var liveDelta = await ReadOneAsync(reader);
            var liveDone = await ReadOneAsync(reader);
            Assert.AreEqual("delta", liveDelta.Event);
            Assert.AreEqual("done", liveDone.Event);
            Assert.AreEqual(SessionState.StreamCompleted, await ssm.GetSessionStateAsync(sessionId));

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            while (await ssm.GetEventCountAfterAsync(sessionId, 0, cts.Token) < 2)
                await Task.Delay(25, cts.Token);

            var replay = await ssm.ReplaySessionAsync(sessionId, fromSequenceNum: 0, limit: 10);
            Assert.AreEqual(2, replay.Events.Count);
            Assert.AreEqual("delta", replay.Events[0].EventType);
            Assert.AreEqual("done", replay.Events[1].EventType);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(dbPath)) File.Delete(dbPath);
        }
    }

    [TestMethod]
    public async Task AppendAsync_TerminalEventWhileFlushGateHeld_EventuallyPersistsTerminalEvent()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"test_{Guid.NewGuid():N}.db");
        try
        {
            var ssm = CreateSsm(dbPath);
            const string sessionId = "s1";
            const string workspaceId = "w1";

            // 订阅以启用 realtime buffered 路径
            var reader = ssm.Subscribe(sessionId);
            Assert.IsNotNull(reader);

            // 1. 追加一个 delta 创建 buffer，等待其延迟 flush 完成（gate 空闲，_flushScheduled=0）
            await ssm.AppendAsync(
                sessionId,
                workspaceId,
                new ServerSentEventFrame("delta", """{"delta":"a"}"""),
                component: RuntimeActivityComponents.AgentExecution,
                operation: "chat.stream.delta");
            await Task.Delay(400);

            // 2. 持有 gate 模拟正在进行的 flush
            var gateHolder = await ssm.HoldFlushGateForTestingAsync(sessionId);

            // 3. 追加 usage（非 terminal，低于阈值）→ ScheduleBufferedFlush → TrySchedule → _flushScheduled=1，调度 150ms 延迟 flush
            await ssm.AppendAsync(
                sessionId,
                workspaceId,
                new ServerSentEventFrame("usage", """{"promptTokens":10}"""),
                component: RuntimeActivityComponents.AgentExecution,
                operation: "chat.stream.usage");
            // 消费 live fanout 帧
            _ = await ReadOneAsync(reader);

            // 4. 等待延迟 flush 触发 → gate 仍被占用 → return（不重置 _flushScheduled）
            await Task.Delay(300);

            // 5. 追加 done（terminal）→ FlushBufferedSessionEventsAsync(waitForGate:false) → gate 占用 → return
            var doneSeq = await ssm.AppendAsync(
                sessionId,
                workspaceId,
                new ServerSentEventFrame("done", """{"messageId":"m1","reply":"ok"}"""),
                component: RuntimeActivityComponents.AgentExecution,
                operation: "chat.stream.done");
            // 消费 live fanout 帧
            _ = await ReadOneAsync(reader);

            // 6. 释放 gate
            gateHolder.Dispose();

            // 7. 等待重新调度的 flush 持久化（修复后应在 150ms+ 内完成）
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            bool donePersisted = false;
            while (!cts.IsCancellationRequested)
            {
                var replay = await ssm.ReplaySessionAsync(sessionId, fromSequenceNum: 0, limit: 50);
                donePersisted = replay.Events.Any(e => e.EventType == "done");
                if (donePersisted) break;
                await Task.Delay(50, cts.Token);
            }

            Assert.IsTrue(donePersisted,
                "done 事件必须在 gate 释放后被持久化到 session_event_log（修复后 _flushScheduled 不再卡死）");
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

    private static async Task<ServerSentEventFrame> ReadOneAsync(ChannelReader<ServerSentEventFrame> reader)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
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
