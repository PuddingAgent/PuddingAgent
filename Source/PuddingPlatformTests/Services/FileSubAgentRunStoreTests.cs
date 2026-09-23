using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using PuddingCode.Configuration;
using PuddingCode.Serialization;
using PuddingCode.SubAgents;
using PuddingCode.Platform;
using PuddingCode.Runtime;
using PuddingPlatform.Data;
using PuddingPlatform.Data.Entities;
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
                    parent_turn_id      TEXT,
                    parent_command_id   TEXT,
                    parent_run_id       TEXT,
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
                    // slice-4：实体含父执行身份列（parent_turn_id/parent_command_id/parent_run_id）。
                    // 2026-09-19 压缩后 bootstrapper 不再补列，fixture DDL 直接声明三列；
                    // bootstrapper 仅负责补建 EF 模型未声明的复合索引。
            await SubAgentRunSchemaBootstrapper.EnsureCreatedAsync(db);
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
            TimeoutSeconds = 1800,
            ExecutionDeadlineUtc = new DateTimeOffset(2026, 7, 19, 12, 34, 56, TimeSpan.Zero),
            ParentExecutionIdentity = new RuntimeExecutionIdentity
            {
                Kind = RuntimeExecutionKind.ConversationTurn,
                ConversationId = "parent-session",
                TurnId = "parent-turn",
                RunId = "parent-run",
                TraceId = null,
                ToolCallId = "parent-tool-call",
            },
        });

        var runJsonPath = Path.Combine(handle.ArchivePath, "run.json");
        var runJson = await File.ReadAllTextAsync(runJsonPath);
        Assert.IsTrue(runJson.Contains('\n'));
        StringAssert.Contains(runJson, "\"parentSessionId\"");
        StringAssert.Contains(runJson, "\"executionDeadlineUtc\"");

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
            conversationEvents.Appended.All(
                static item => item.ConversationId == "parent-session"));
        Assert.IsTrue(
            conversationEvents.Appended.All(
                static item => item.Event.TurnId == "parent-turn"));

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
        Assert.AreEqual(
            new DateTimeOffset(2026, 7, 19, 12, 34, 56, TimeSpan.Zero),
            manifest.ExecutionDeadlineUtc);
        Assert.AreEqual("plan_1", manifest.TaskPlanning["task_plan_id"]);
        Assert.AreEqual("task_1", manifest.TaskPlanning["task_node_id"]);
        Assert.AreEqual("2", manifest.TaskPlanning["max_delegation_depth"]);
    }

    [TestMethod]
    public async Task Projected_Run_Events_Carry_Parent_Trace_And_SubAgent_ProducerComponent()
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
            Task = "Trace projection",
            ParentExecutionIdentity = new RuntimeExecutionIdentity
            {
                Kind = RuntimeExecutionKind.ConversationTurn,
                ConversationId = "parent-session",
                TurnId = "parent-turn",
                RunId = "parent-run",
                TraceId = "trace-123",
                ToolCallId = "parent-tool-call",
            },
        });
        await store.AppendEventAsync(handle.RunId, ConversationEventTypes.SubAgentRoundStarted, new
        {
            round = 1,
        });

        Assert.IsTrue(conversationEvents.Appended.Count > 0);
        foreach (var item in conversationEvents.Appended)
        {
            Assert.AreEqual("trace-123", item.Event.TraceId);
            Assert.AreEqual("subagent.runtime", item.Event.ProducerComponent);
        }
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

    [TestMethod]
    public async Task CompleteRunAsync_Recovers_Observed_Totals_When_Cancellation_Loses_Runtime_Result()
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
            TemplateId = "developer",
            Task = "Preserve observed work on timeout",
        });
        await store.AppendEventAsync(handle.RunId, ConversationEventTypes.SubAgentRoundStarted, new
        {
            round = 3,
        });
        await store.AppendEventAsync(handle.RunId, ConversationEventTypes.SubAgentToolCompleted, new
        {
            round = 3,
            tool_call_id = "tool-1",
            output_length = 42,
            output_truncated = true,
        });
        await store.AppendEventAsync(handle.RunId, ConversationEventTypes.SubAgentToolFailed, new
        {
            round = 3,
            tool_call_id = "tool-2",
            output_length = 7,
            output_truncated = false,
            error = "build failed",
        });
        await Task.Delay(5);

        var result = await store.CompleteRunAsync(handle.RunId, new SubAgentRunCompletion
        {
            Status = "timed_out",
            ErrorMessage = "deadline reached",
        });

        Assert.AreEqual(SubAgentRunTerminalWriteResult.Applied, result);
        await using var verifyDb = new PlatformDbContext(options);
        var index = await verifyDb.SubAgentRuns.SingleAsync(r => r.RunId == handle.RunId);
        Assert.AreEqual("timed_out", index.Status);
        Assert.AreEqual(3, index.TotalRounds);
        Assert.AreEqual(2, index.TotalToolCalls);
        Assert.IsGreaterThan(0, index.TotalDurationMs);

        var terminalLine = (await File.ReadAllLinesAsync(
            Path.Combine(handle.ArchivePath, "events.jsonl")))[^1];
        using var terminalJson = JsonDocument.Parse(terminalLine);
        var payload = terminalJson.RootElement.GetProperty("payload");
        Assert.AreEqual(3, payload.GetProperty("total_rounds").GetInt32());
        Assert.AreEqual(2, payload.GetProperty("total_tool_calls").GetInt32());
        Assert.AreEqual(1, payload.GetProperty("tool_failure_count").GetInt32());
        Assert.AreEqual(1, payload.GetProperty("tool_output_truncated_count").GetInt32());
        Assert.AreEqual(49L, payload.GetProperty("tool_output_chars").GetInt64());
        Assert.AreEqual("build failed", payload.GetProperty("tool_failure_summary").GetString());
    }

    [TestMethod]
    public async Task ReplayPendingConversationEvents_RotatesAcrossBoundedRunBatches()
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
        var handles = new List<SubAgentRunHandle>();
        for (var index = 0; index < 3; index++)
        {
            handles.Add(await store.CreateRunAsync(new SubAgentRunCreateRequest
            {
                ParentSessionId = "parent-session",
                SubSessionId = $"sub-session-{index}",
                WorkspaceId = "default",
                AgentInstanceId = "agent-1",
                TemplateId = "developer",
                Task = $"Replay run {index}",
            }));
        }

        conversationEvents.Appended.Clear();
        foreach (var handle in handles)
        {
            await File.WriteAllTextAsync(
                Path.Combine(handle.ArchivePath, "conversation-projection.cursor"),
                "0");
        }

        for (var scan = 0; scan < handles.Count; scan++)
            Assert.AreEqual(1, await store.ReplayPendingConversationEventsAsync(maxRuns: 1));

        Assert.AreEqual(handles.Count, conversationEvents.Appended.Count);
        Assert.AreEqual(
            handles.Count,
            conversationEvents.Appended
                .Select(static item => item.Event.EventId)
                .Distinct(StringComparer.Ordinal)
                .Count());
        foreach (var handle in handles)
        {
            Assert.AreEqual(
                "1",
                await File.ReadAllTextAsync(
                    Path.Combine(handle.ArchivePath, "conversation-projection.cursor")));
        }
        // A settled sweep must not open payload files at all, even while another
        // process holds them exclusively (metadata remains readable on Windows).
        using (new FileStream(Path.Combine(handles[0].ArchivePath, "run.json"), FileMode.Open, FileAccess.Read, FileShare.None))
        using (new FileStream(Path.Combine(handles[0].ArchivePath, "events.jsonl"), FileMode.Open, FileAccess.Read, FileShare.None))
            Assert.AreEqual(0, await store.ReplayPendingConversationEventsAsync(maxRuns: 3));

        // Durable cursor edits invalidate the fast path; it must not hide recovery work.
        await File.WriteAllTextAsync(Path.Combine(handles[0].ArchivePath, "conversation-projection.cursor"), "0");
        Assert.AreEqual(1, await store.ReplayPendingConversationEventsAsync(maxRuns: 3));
    }

    [TestMethod]
    public async Task AppendEvent_WhenArchiveLocked_Degrades_Instead_Of_Throwing()
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

        var store = new FileSubAgentRunStore(
            paths,
            NullLogger<FileSubAgentRunStore>.Instance,
            new TestDbContextFactory(options),
            new RecordingConversationEventStore());
        var handle = await store.CreateRunAsync(new SubAgentRunCreateRequest
        {
            ParentSessionId = "parent-session",
            SubSessionId = "sub-session",
            WorkspaceId = "default",
            AgentInstanceId = "agent-1",
            TemplateId = "developer",
            Task = "Survive archive lock",
        });

        var eventsPath = Path.Combine(handle.ArchivePath, "events.jsonl");
        using (new FileStream(eventsPath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            // 模拟检查器/外部进程独占归档：append 重试耗尽后必须降级而不是抛出
            // （run_20260821_230951_dbff4b8075c1 曾因此被直接标记 failed）。
            await store.AppendEventAsync(handle.RunId, "subagent.round.started", new { round = 1 });
        }

        Assert.IsTrue(File.Exists(Path.Combine(handle.ArchivePath, "archive-degraded.json")));

        var degraded = await store.GetRunArchiveAsync(handle.RunId);
        Assert.IsNotNull(degraded);
        Assert.IsNotNull(degraded.Degraded);
        Assert.AreEqual(1, degraded.Degraded.DroppedEventCount);
        Assert.AreEqual("subagent.round.started", degraded.Degraded.LastEventType);

        // ADR-093 A93-0：丢弃必须**可枚举**，不能只有聚合计数（聚合回答「几条」、台账回答「哪几条」）。
        var ledger = await store.GetDroppedEventLedgerAsync(handle.RunId);
        Assert.AreEqual(1, ledger.Count);
        Assert.AreEqual("subagent.round.started", ledger[0].EventType);
        Assert.AreEqual(1, ledger[0].Seq);
        Assert.IsFalse(string.IsNullOrWhiteSpace(ledger[0].EventId), "台账必须携带事件身份");
        Assert.IsFalse(string.IsNullOrWhiteSpace(ledger[0].Error), "台账必须携带失败原因");
        Assert.AreEqual(0, store.DegradationMarkerWriteFailures);
        Assert.AreEqual(0, store.DegradationLedgerWriteFailures);

        // 锁释放后归档恢复写入，后续事件不再丢弃。
        await store.AppendEventAsync(handle.RunId, "subagent.round.completed", new { round = 1 });
        var recovered = await store.GetRunArchiveAsync(handle.RunId);
        Assert.IsNotNull(recovered);
        Assert.AreEqual(2, recovered.Events.Count);
        Assert.AreEqual(1, recovered.Degraded!.DroppedEventCount);
    }

    /// <summary>
    /// ADR-093 A93-0：连续丢弃必须留下**逐条**台账（各自的事件身份 + 递增序号），
    /// 而不是只把聚合计数从 1 加到 2。
    /// </summary>
    [TestMethod]
    public async Task DroppedEventLedger_Enumerates_EachDroppedEvent()
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

        var store = new FileSubAgentRunStore(
            paths,
            NullLogger<FileSubAgentRunStore>.Instance,
            new TestDbContextFactory(options),
            new RecordingConversationEventStore());
        var handle = await store.CreateRunAsync(new SubAgentRunCreateRequest
        {
            ParentSessionId = "parent-session",
            SubSessionId = "sub-session",
            WorkspaceId = "default",
            AgentInstanceId = "agent-1",
            TemplateId = "developer",
            Task = "Enumerate dropped events",
        });

        var eventsPath = Path.Combine(handle.ArchivePath, "events.jsonl");
        using (new FileStream(eventsPath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            await store.AppendEventAsync(handle.RunId, "subagent.round.started", new { round = 1 });
            await store.AppendEventAsync(handle.RunId, "subagent.tool.started", new { round = 1, tool = "read" });
        }

        var ledger = await store.GetDroppedEventLedgerAsync(handle.RunId);
        Assert.AreEqual(2, ledger.Count);
        Assert.AreEqual("subagent.round.started", ledger[0].EventType);
        Assert.AreEqual("subagent.tool.started", ledger[1].EventType);
        Assert.AreEqual(1, ledger[0].Seq);
        Assert.AreEqual(2, ledger[1].Seq);
        Assert.AreNotEqual(ledger[0].EventId, ledger[1].EventId, "每条丢弃必须有自己的事件身份");
    }

    /// <summary>
    /// 二次降级：聚合标记自身写不进去时**不得抛错**（抛错会杀死运行中的子代理，违反 ADR-093 决策 4），
    /// 但必须在健康面可查（计数器），且**台账与标记相互独立**：标记失败不得连累台账。
    /// 注入方式：把 `archive-degraded.json` 路径预先建成目录 ⇒ 写入必然失败（确定性，不依赖权限/磁盘状态）。
    /// </summary>
    [TestMethod]
    public async Task DegradationMarkerWriteFailure_IsCounted_AndDoesNotBlockLedger()
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

        var store = new FileSubAgentRunStore(
            paths,
            NullLogger<FileSubAgentRunStore>.Instance,
            new TestDbContextFactory(options),
            new RecordingConversationEventStore());
        var handle = await store.CreateRunAsync(new SubAgentRunCreateRequest
        {
            ParentSessionId = "parent-session",
            SubSessionId = "sub-session",
            WorkspaceId = "default",
            AgentInstanceId = "agent-1",
            TemplateId = "developer",
            Task = "Marker write failure must be observable",
        });

        Directory.CreateDirectory(Path.Combine(handle.ArchivePath, "archive-degraded.json"));

        var eventsPath = Path.Combine(handle.ArchivePath, "events.jsonl");
        using (new FileStream(eventsPath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            await store.AppendEventAsync(handle.RunId, "subagent.round.started", new { round = 1 });
        }

        Assert.AreEqual(1, store.DegradationMarkerWriteFailures, "标记写入失败必须可查（不得只留一行日志）");
        Assert.AreEqual(0, store.DegradationLedgerWriteFailures, "标记失败不得连累台账");

        var ledger = await store.GetDroppedEventLedgerAsync(handle.RunId);
        Assert.AreEqual(1, ledger.Count);
        Assert.AreEqual("subagent.round.started", ledger[0].EventType);
    }

    /// <summary>
    /// ADR-093 A93-0 第二刀：只读对账必须把四条链的事实摊开并**如实报告差异**。
    /// 差异用**确定性方式**制造（把投影游标写回 0），不依赖"刚写入的事件尚未被投影"这一时序假设。
    /// </summary>
    [TestMethod]
    public async Task InspectArchiveConsistency_ReportsWatermarksAndDivergence()
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

        var store = new FileSubAgentRunStore(
            paths,
            NullLogger<FileSubAgentRunStore>.Instance,
            new TestDbContextFactory(options),
            new RecordingConversationEventStore());
        var handle = await store.CreateRunAsync(new SubAgentRunCreateRequest
        {
            ParentSessionId = "parent-session",
            SubSessionId = "sub-session",
            WorkspaceId = "default",
            AgentInstanceId = "agent-1",
            TemplateId = "developer",
            Task = "Consistency reconciliation",
        });

        var report = await store.InspectArchiveConsistencyAsync(handle.RunId);
        Assert.IsTrue(report.ArchiveFound);
        Assert.IsTrue(report.IndexReadable, "索引不可读必须与“索引缺失”分开报告");
        Assert.IsTrue(report.IndexRowPresent, "CreateRunAsync 应已写入索引行");
        Assert.AreEqual("running", report.ArchiveStatus, "权威状态取自 run.json");
        Assert.AreEqual("running", report.IndexStatus);
        Assert.AreEqual(1, report.AuthoritativeEvents, "CreateRunAsync 写入 1 条基线事件");

        // 投影追平 ⇒ 游标必须等于权威事件数，且判定为一致
        await store.ReplayPendingConversationEventsAsync(maxRuns: 3);
        var settled = await store.InspectArchiveConsistencyAsync(handle.RunId);
        Assert.AreEqual(0, settled.UnprojectedEvents);
        Assert.AreEqual(settled.AuthoritativeEvents, settled.ProjectionCursor, "投影游标必须追平权威事件数");
        Assert.IsTrue(settled.IsConsistent, string.Join(" | ", settled.Findings));

        // 确定性制造「权威已前进、投影未跟上」：把持久游标写回 0
        await File.WriteAllTextAsync(
            Path.Combine(handle.ArchivePath, "conversation-projection.cursor"), "0");
        var diverged = await store.InspectArchiveConsistencyAsync(handle.RunId);
        Assert.AreEqual(diverged.AuthoritativeEvents, diverged.UnprojectedEvents, "游标为 0 ⇒ 全部事件未投影");
        Assert.IsTrue(diverged.UnprojectedEvents > 0);
        Assert.IsFalse(diverged.IsConsistent);
        StringAssert.Contains(diverged.Findings[0], "未投影");

        // 丢弃也必须在对账里可见（台账 + 聚合标记），并使一致性判定为 false
        var eventsPath = Path.Combine(handle.ArchivePath, "events.jsonl");
        using (new FileStream(eventsPath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            await store.AppendEventAsync(handle.RunId, "subagent.round.completed", new { round = 1 });
        }

        var degraded = await store.InspectArchiveConsistencyAsync(handle.RunId);
        Assert.AreEqual(1, degraded.DroppedEvents);
        Assert.AreEqual(1, degraded.DegradedDroppedEventCount);
        Assert.IsFalse(degraded.IsConsistent);
    }

    [TestMethod]
    public async Task Concurrent_Archive_Read_And_Event_Append_Never_SharingViolate()
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

        var store = new FileSubAgentRunStore(
            paths,
            NullLogger<FileSubAgentRunStore>.Instance,
            new TestDbContextFactory(options),
            new RecordingConversationEventStore());
        var handle = await store.CreateRunAsync(new SubAgentRunCreateRequest
        {
            ParentSessionId = "parent-session",
            SubSessionId = "sub-session",
            WorkspaceId = "default",
            AgentInstanceId = "agent-1",
            TemplateId = "developer",
            Task = "Concurrent inspector reads",
        });

        const int appendCount = 40;
        const int readCount = 40;
        var tasks = new List<Task>();
        for (var i = 0; i < appendCount; i++)
        {
            var toolCallId = $"tool-{i}";
            tasks.Add(store.AppendEventAsync(handle.RunId, "subagent.tool.started", new
            {
                round = 1,
                tool_call_id = toolCallId,
            }));
        }
        for (var i = 0; i < readCount; i++)
            tasks.Add(store.GetRunArchiveAsync(handle.RunId));

        await Task.WhenAll(tasks);

        var final = await store.GetRunArchiveAsync(handle.RunId);
        Assert.IsNotNull(final);
        Assert.AreEqual(appendCount + 1, final.Events.Count);
        Assert.IsNull(final.Degraded);
    }

        [TestMethod]
    public async Task CreateRunAsync_PersistsParentExecutionIdentityIntoDbIndex()
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

        var store = new FileSubAgentRunStore(
            paths,
            NullLogger<FileSubAgentRunStore>.Instance,
            new TestDbContextFactory(options),
            new RecordingConversationEventStore());

        var handle = await store.CreateRunAsync(BuildParentIdentityRequest("sub-1", "turn-1"));

        await using var verifyDb = new PlatformDbContext(options);
        var index = await verifyDb.SubAgentRuns.SingleAsync(r => r.RunId == handle.RunId);
        Assert.AreEqual("turn-1", index.ParentTurnId);
        Assert.AreEqual("command-1", index.ParentCommandId);
        Assert.AreEqual("run-parent-1", index.ParentRunId);
        Assert.AreEqual(SubAgentRunEntity.RunningStatus, index.Status);
    }

    [TestMethod]
    public async Task GetRunningCountByParentTurnAsync_CountsOnlyRunningRunsOfTheGivenTurn()
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

        var store = new FileSubAgentRunStore(
            paths,
            NullLogger<FileSubAgentRunStore>.Instance,
            new TestDbContextFactory(options),
            new RecordingConversationEventStore());

        var turnOneRun = await store.CreateRunAsync(BuildParentIdentityRequest("sub-1", "turn-1"));
        await store.CreateRunAsync(BuildParentIdentityRequest("sub-2", "turn-2"));
        await store.CreateRunAsync(BuildParentIdentityRequest("sub-3", null));

        Assert.AreEqual(1, await store.GetRunningCountByParentTurnAsync("turn-1"));
        Assert.AreEqual(1, await store.GetRunningCountByParentTurnAsync("turn-2"));
        Assert.AreEqual(0, await store.GetRunningCountByParentTurnAsync("turn-missing"));
        // 无父 Turn 归属的旧运行不得被算到任何 Turn 上（只能走会话粒度统计）。
        Assert.AreEqual(0, await store.GetRunningCountByParentTurnAsync(null));
        Assert.AreEqual(0, await store.GetRunningCountByParentTurnAsync("  "));

        await store.CompleteRunAsync(turnOneRun.RunId, new SubAgentRunCompletion
        {
            Status = "completed",
        });

        // 终态后不再计入运行中。
        Assert.AreEqual(0, await store.GetRunningCountByParentTurnAsync("turn-1"));
        Assert.AreEqual(1, await store.GetRunningCountByParentTurnAsync("turn-2"));
    }

    private static SubAgentRunCreateRequest BuildParentIdentityRequest(
        string subSessionSuffix,
        string? parentTurnId)
        => new()
        {
            ParentSessionId = "parent-session",
            SubSessionId = $"parent-session/sub/{subSessionSuffix}",
            WorkspaceId = "default",
            AgentInstanceId = "default.researcher-001",
            TemplateId = "researcher",
            Task = "Research the current architecture",
            ParentExecutionIdentity = parentTurnId is null
                ? null
                : new RuntimeExecutionIdentity
                {
                    Kind = RuntimeExecutionKind.ConversationTurn,
                    ConversationId = "parent-session",
                    TurnId = parentTurnId,
                    CommandId = "command-1",
                    RunId = "run-parent-1",
                    TraceId = null,
                },
        };

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
