using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using PuddingCode.Models;
using PuddingCode.Observability;
using PuddingCode.Platform;
using PuddingCode.Runtime;
using PuddingCode.Services;
using PuddingMemoryEngine.Data;
using PuddingMemoryEngine.Entities;
using PuddingRuntime.Services;
using PuddingRuntime.Services.AgentLoop;

namespace PuddingRuntimeTests.Services;

[TestClass]
public sealed class ContextWindowManagerTests
{
    [TestMethod]
    public void TrimHistory_Removes_Orphan_Tool_Messages_From_Context()
    {
        var manager = CreateManager();
        var history = new List<ChatMessage>
        {
            new(ChatRole.System, "system"),
        };

        for (var i = 0; i < 41; i++)
            history.Add(new ChatMessage(ChatRole.User, $"user-{i}"));

        history.Add(new ChatMessage(ChatRole.Tool, "orphan tool result", ToolCallId: "call-orphan"));

        manager.TrimHistory(history, maxTokenBudget: 8000);

        Assert.IsFalse(
            history.Any(m => m.Role == ChatRole.Tool),
            "Orphan tool messages must not be sent to OpenAI-compatible providers without a preceding assistant tool_call.");
    }

    [TestMethod]
    public async Task TrimHistoryAsync_AutoCompacts_When_ContextHealthRequiresIt()
    {
        var compaction = new FakeContextCompactionService(shouldAutoCompact: true);
        var manager = CreateManager(compaction);
        var history = new List<ChatMessage>
        {
            new(ChatRole.System, "system"),
            new(ChatRole.User, "user"),
            new(ChatRole.Assistant, "assistant"),
        };

        await manager.TrimHistoryAsync(
            "session-1",
            history,
            maxTokenBudget: 8000,
            preferDbContextWindow: false,
            workspaceId: "workspace-1",
            agentId: "agent-1",
            CancellationToken.None);

        Assert.AreEqual(1, compaction.CompactCalls.Count);
        Assert.AreEqual("workspace-1", compaction.CompactCalls[0].WorkspaceId);
        Assert.AreEqual("session-1", compaction.CompactCalls[0].SessionId);
        Assert.AreEqual("agent-1", compaction.CompactCalls[0].AgentId);
        Assert.AreEqual(ContextCompactionMode.Auto, compaction.CompactCalls[0].Mode);
        Assert.AreEqual(ContextCompactionLevel.Full, compaction.CompactCalls[0].Level);
    }

    [TestMethod]
    public async Task TrimHistoryAsync_EmitsAutoCompactionEvents_BeforeAndAfterCompaction()
    {
        var emitter = new RecordingCompactionEventEmitter(yieldBeforeRecord: true);
        var compaction = new FakeContextCompactionService(shouldAutoCompact: true)
        {
            OnCompact = () => emitter.Events.Count,
        };
        var manager = CreateManager(compaction, compactionEventEmitter: emitter);
        var history = new List<ChatMessage>
        {
            new(ChatRole.System, "system"),
            new(ChatRole.User, "user"),
            new(ChatRole.Assistant, "assistant"),
        };

        await manager.TrimHistoryAsync(
            "session-1",
            history,
            maxTokenBudget: 8000,
            preferDbContextWindow: false,
            workspaceId: "workspace-1",
            agentId: "agent-1",
            CancellationToken.None);

        Assert.AreEqual(1, compaction.EventCountAtCompact,
            "context.compaction.started must be persisted before CompactAsync begins.");
        CollectionAssert.AreEqual(
            new[]
            {
                SseEventTypes.ContextCompactionStarted,
                SseEventTypes.ContextCompactionCompleted,
            },
            emitter.Events.Select(e => e.EventType).ToArray());
    }

    [TestMethod]
    public async Task TrimHistoryAsync_RecordsAutoCompactionTelemetry()
    {
        var telemetry = new RecordingTelemetrySink();
        var compaction = new FakeContextCompactionService(shouldAutoCompact: true);
        var manager = CreateManager(compaction, telemetrySink: telemetry);
        var history = new List<ChatMessage>
        {
            new(ChatRole.System, "system"),
            new(ChatRole.User, "user"),
            new(ChatRole.Assistant, "assistant"),
        };

        await manager.TrimHistoryAsync(
            "session-1",
            history,
            maxTokenBudget: 8000,
            preferDbContextWindow: false,
            workspaceId: "workspace-1",
            agentId: "agent-1",
            CancellationToken.None);

        Assert.IsTrue(telemetry.Metrics.Any(m =>
            m.Category == TelemetryMetricCategories.Context
            && m.Name == "context.auto_compaction.health"
            && m.Status == TelemetryMetricStatuses.Recorded
            && m.Trace.SessionId == "session-1"));
        Assert.IsTrue(telemetry.Metrics.Any(m =>
            m.Name == "context.auto_compaction"
            && m.Status == TelemetryMetricStatuses.Started));
        Assert.IsTrue(telemetry.Metrics.Any(m =>
            m.Name == "context.auto_compaction"
            && m.Status == TelemetryMetricStatuses.Succeeded
            && m.CountValue == 10));
    }

    [TestMethod]
    public async Task TrimHistoryAsync_DoesNotAutoCompact_When_ContextHealthDoesNotRequireIt()
    {
        var compaction = new FakeContextCompactionService(shouldAutoCompact: false);
        var manager = CreateManager(compaction);
        var history = new List<ChatMessage>
        {
            new(ChatRole.System, "system"),
            new(ChatRole.User, "user"),
        };

        await manager.TrimHistoryAsync(
            "session-1",
            history,
            maxTokenBudget: 8000,
            preferDbContextWindow: false,
            workspaceId: "workspace-1",
            agentId: "agent-1",
            CancellationToken.None);

        Assert.AreEqual(0, compaction.CompactCalls.Count);
    }

    [TestMethod]
    public async Task TrimHistoryAsync_Uses_Runtime_Context_Budget_For_AutoCompaction_Health()
    {
        var compaction = new FakeContextCompactionService(
            usedTokens: 9000,
            defaultContextWindowTokens: 8192);
        var manager = CreateManager(compaction);
        var history = new List<ChatMessage>
        {
            new(ChatRole.System, "system"),
            new(ChatRole.User, "user"),
            new(ChatRole.Assistant, "assistant"),
        };

        await manager.TrimHistoryAsync(
            "session-1",
            history,
            maxTokenBudget: 128_000,
            preferDbContextWindow: false,
            workspaceId: "workspace-1",
            agentId: "agent-1",
            CancellationToken.None);

        Assert.AreEqual(0, compaction.CompactCalls.Count,
            "Auto compaction must evaluate health with the runtime context budget, not the fixed 8192-token fallback.");
        Assert.AreEqual(128_000, compaction.LastContextWindowTokens);
    }

        [TestMethod]
    public async Task TrimHistoryAsync_ReturnsFalse_After_InjectingWorkSummaryPrompt()
    {
        // 应注入提示词并返回 false，等待 Agent 生成工作总结
        var compaction = new FakeContextCompactionService(shouldAutoCompact: true);
        var emitter = new RecordingCompactionEventEmitter();
        var notifier = new AgentCompactionNotifier(NullLogger<AgentCompactionNotifier>.Instance);
        var manager = CreateManager(compaction, compactionNotifier: notifier, compactionEventEmitter: emitter);
        var sessionId = "session-ws-1";
        var history = manager.GetOrCreateHistory(sessionId);
        history.Add(new ChatMessage(ChatRole.System, "system"));
        history.Add(new ChatMessage(ChatRole.User, "user"));
        history.Add(new ChatMessage(ChatRole.Assistant, "assistant"));

        await manager.TrimHistoryAsync(
            sessionId,
            history,
            maxTokenBudget: 8000,
            preferDbContextWindow: false,
            workspaceId: "workspace-1",
            agentId: "agent-1",
            CancellationToken.None);

        // CompactAsync 不应该被调用（还在等待 Agent 生成工作总结）
        Assert.AreEqual(0, compaction.CompactCalls.Count,
            "After injecting work summary prompt, compaction should NOT proceed yet.");

        // 历史中应该有注入的系统提示词
        Assert.IsTrue(
            history.Any(m => m.Role == ChatRole.System && m.Content?.Contains("会话压缩即将触发") == true),
            "Work summary prompt should be injected into history.");
        Assert.AreEqual(0, emitter.Events.Count,
            "Waiting for an agent work summary must not emit a started event that has no matching completion.");
    }

    [TestMethod]
    public async Task TrimHistoryAsync_ProceedsWithoutSummary_After_MaxRetries()
    {
        // 模拟重试耗尽后强制压缩
        var compaction = new FakeContextCompactionService(shouldAutoCompact: true);
        var notifier = new AgentCompactionNotifier(NullLogger<AgentCompactionNotifier>.Instance);
        var options = new ContextCompactionOptions
        {
            MaxWorkSummaryRetries = 2,
            MaxWaitForWorkSummarySeconds = 999 // 用重试次数而非超时触发
        };
        var manager = CreateManager(compaction, compactionNotifier: notifier, compactionOptions: options);
        var sessionId = "session-ws-retry";
        var history = manager.GetOrCreateHistory(sessionId);
        history.Add(new ChatMessage(ChatRole.System, "system"));
        history.Add(new ChatMessage(ChatRole.User, "user"));
        history.Add(new ChatMessage(ChatRole.Assistant, "assistant"));

        // 第 1 次：注入提示词，return false
        await manager.TrimHistoryAsync(sessionId, history, 8000, false, "ws", "ag", CancellationToken.None);
        Assert.AreEqual(0, compaction.CompactCalls.Count, "Retry 1: should wait.");

        // 第 2 次：再次注入，return false
        await manager.TrimHistoryAsync(sessionId, history, 8000, false, "ws", "ag", CancellationToken.None);
        Assert.AreEqual(0, compaction.CompactCalls.Count, "Retry 2: should wait.");

        // 第 3 次：达到 maxRetries=2，强制执行压缩
        await manager.TrimHistoryAsync(sessionId, history, 8000, false, "ws", "ag", CancellationToken.None);
        Assert.AreEqual(1, compaction.CompactCalls.Count, "Retry 3: max retries reached, should compact.");
        Assert.IsNull(compaction.CompactCalls[0].AgentWorkSummary,
            "Compaction should proceed without work summary when retries exhausted.");
    }

    [TestMethod]
    public async Task TrimHistoryAsync_CompactsImmediately_WhenWorkSummaryFound()
    {
        // 历史中已包含工作总结，跳过注入直接压缩
        var compaction = new FakeContextCompactionService(shouldAutoCompact: true);
        var notifier = new AgentCompactionNotifier(NullLogger<AgentCompactionNotifier>.Instance);
        var manager = CreateManager(compaction, compactionNotifier: notifier);
        var sessionId = "session-ws-found";

        // 通过 manager 的内部历史，确保 ExtractAgentWorkSummaryFromHistory 能读到
        var history = manager.GetOrCreateHistory(sessionId);
        history.Add(new ChatMessage(ChatRole.System, "system"));
        history.Add(new ChatMessage(ChatRole.User, "user"));
        history.Add(new ChatMessage(ChatRole.Assistant, "assistant"));

        // 第 1 次：没有工作总结 → 注入提示词，return false
        await manager.TrimHistoryAsync(sessionId, history, 8000, false, "ws", "ag", CancellationToken.None);
        Assert.AreEqual(0, compaction.CompactCalls.Count);

        // 模拟 Agent 响应了工作总结（添加到 manager 的内部历史）
        history.Add(new ChatMessage(ChatRole.Assistant,
            "## 当前工作目标\n帮用户修 bug。\n## 已完成的工作\n修复了时序问题。\n## 关键信息记录\n路径：/src。\n## 未完成的工作\n无。\n## 下一步建议\n提交代码。"));

        // 第 2 次调用：提取到工作总结，立即压缩
        await manager.TrimHistoryAsync(sessionId, history, 8000, false, "ws", "ag", CancellationToken.None);

        Assert.AreEqual(1, compaction.CompactCalls.Count, "Should compact once work summary is found.");
        Assert.IsNotNull(compaction.CompactCalls[0].AgentWorkSummary,
            "AgentWorkSummary should be passed to CompactAsync.");
    }

    [TestMethod]
    public async Task TrimHistoryAsync_Hydrates_Compaction_Summary_After_AutoCompaction()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = CreateOptions(connection);
        await using var db = new MemoryDbContext(options);
        await db.Database.EnsureCreatedAsync();
        const int messageCount = 24;
        await SeedMessagesAsync(db, "session-1", messageCount, charsPerMessage: 900);

        var dbFactory = new TestMemoryDbContextFactory(options);
        var usageSnapshots = new ContextUsageSnapshotStore();
        usageSnapshots.RecordProviderUsage(
            "session-1",
            new TokenUsageDto
            {
                PromptTokens = 2_200,
                CompletionTokens = 50,
                TotalTokens = 2_250,
                ContextWindowTokens = 2_500,
            });

        var compaction = new ContextCompactionService(
            dbFactory,
            new FixedSummaryGenerator("## 当前工作状态\n自动压缩摘要已生成。"),
            NullLogger<ContextCompactionService>.Instance,
            contextUsageSnapshotStore: usageSnapshots);
        var manager = CreateManager(compaction, dbFactory);
        var history = new List<ChatMessage>
        {
            new(ChatRole.System, "system prompt"),
        };
        for (var i = 1; i <= messageCount; i++)
            history.Add(new ChatMessage(i % 2 == 0 ? ChatRole.Assistant : ChatRole.User, $"message {i}"));

        await manager.TrimHistoryAsync(
            "session-1",
            history,
            maxTokenBudget: 2_500,
            preferDbContextWindow: false,
            workspaceId: "workspace-1",
            agentId: "agent-1",
            CancellationToken.None);

        Assert.IsTrue(
            history.Any(m => m.Content?.Contains("自动压缩摘要已生成", StringComparison.Ordinal) == true),
            "Auto compaction must replace compacted in-memory history with the persisted compact summary.");
    }

    [TestMethod]
    public async Task BuildContextFromDbAsync_Maps_Agent_Role_To_Assistant()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = CreateOptions(connection);
        await using var db = new MemoryDbContext(options);
        await db.Database.EnsureCreatedAsync();
        await SeedMessagesAsync(db, "session-agent-role", messageCount: 2, charsPerMessage: 0);

        var manager = CreateManager(null, new TestMemoryDbContextFactory(options));

        var history = await manager.BuildContextFromDbAsync("session-agent-role");

        Assert.AreEqual(ChatRole.Assistant, history[1].Role,
            "Persisted agent transcript rows must be restored as assistant messages for LLM context.");
    }

    [TestMethod]
    public async Task BuildContextFromDbAsync_DoesNotHydrate_ThinkingJson_As_ReasoningContent()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = CreateOptions(connection);
        await using var db = new MemoryDbContext(options);
        await db.Database.EnsureCreatedAsync();
        await SeedMessagesAsync(db, "session-thinking-json", messageCount: 2, charsPerMessage: 0);

        var assistantRow = await db.Messages.SingleAsync(m => m.MessageId == "msg-2");
        assistantRow.ThinkingJson = """[{"text":"stale hidden reasoning from an older task"}]""";
        await db.SaveChangesAsync();

        var manager = CreateManager(null, new TestMemoryDbContextFactory(options));

        var history = await manager.BuildContextFromDbAsync("session-thinking-json");

        Assert.IsNull(history[1].ReasoningContent,
            "Persisted ThinkingJson is UI/diagnostic data and must not re-enter later LLM prompts as reasoning_content.");
    }

    [TestMethod]
    public async Task BuildContextFromDbAsync_Filters_RuntimeFuse_AssistantMessages()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = CreateOptions(connection);
        await using var db = new MemoryDbContext(options);
        await db.Database.EnsureCreatedAsync();

        db.Sessions.Add(new SessionEntity
        {
            SessionId = "session-fuse-history",
            WorkspaceId = "workspace-1",
            AgentId = "agent-1",
            Status = "Active",
            CreatedAt = 1,
            LastActivityAt = 1,
        });
        db.Messages.AddRange(
            new MessageEntity
            {
                MessageId = "msg-1",
                SessionId = "session-fuse-history",
                Sequence = 1,
                Role = "user",
                ContentType = "text",
                Content = "开始提交",
                CreatedAt = 1,
            },
            new MessageEntity
            {
                MessageId = "msg-2",
                SessionId = "session-fuse-history",
                Sequence = 2,
                Role = "assistant",
                ContentType = "text",
                Content = "Session fuse triggered. Session: session-fuse-history State: Faulted Errors in window: 5 Action: stopped agent output, blocked further tool calls. Recovery: Send /resume to clear error counters and continue this session.",
                CreatedAt = 2,
            },
            new MessageEntity
            {
                MessageId = "msg-3",
                SessionId = "session-fuse-history",
                Sequence = 3,
                Role = "assistant",
                ContentType = "text",
                Content = "后续正常回复",
                CreatedAt = 3,
            });
        await db.SaveChangesAsync();

        var manager = CreateManager(null, new TestMemoryDbContextFactory(options));

        var history = await manager.BuildContextFromDbAsync("session-fuse-history");

        Assert.IsFalse(history.Any(m => m.Content?.StartsWith("Session fuse triggered.", StringComparison.Ordinal) == true),
            "Runtime fuse/control messages are UI diagnostics and must not be sent back to the LLM as assistant history.");
        Assert.IsTrue(history.Any(m => m.Content == "后续正常回复"));
    }

    [TestMethod]
    public async Task TryHydrateStreamHistoryFromDbAsync_Keeps_InMemoryHistory_When_PersistedContextIsShorter()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = CreateOptions(connection);
        await using var db = new MemoryDbContext(options);
        await db.Database.EnsureCreatedAsync();
        await SeedMessagesAsync(db, "session-short-db", messageCount: 2, charsPerMessage: 0);

        var manager = CreateManager(null, new TestMemoryDbContextFactory(options));
        var history = manager.GetOrCreateHistory("session-short-db");
        history.Add(new ChatMessage(ChatRole.System, "system"));
        history.Add(new ChatMessage(ChatRole.User, "memory-user-1"));
        history.Add(new ChatMessage(ChatRole.Assistant, "memory-assistant-1"));
        history.Add(new ChatMessage(ChatRole.User, "memory-user-2"));
        history.Add(new ChatMessage(ChatRole.Assistant, "memory-assistant-2"));

        await manager.TryHydrateStreamHistoryFromDbAsync(
            "session-short-db",
            history,
            maxTokenBudget: 8000,
            CancellationToken.None);

        Assert.IsTrue(history.Any(m => m.Content == "memory-assistant-2"),
            "Streaming hydration must not overwrite a richer in-memory session with a shorter persisted snapshot.");
    }

    [TestMethod]
    public async Task TryHydrateStreamHistoryFromDbAsync_RepairsIncompleteInMemoryToolRound_BeforeKeepingRicherHistory()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = CreateOptions(connection);
        await using var db = new MemoryDbContext(options);
        await db.Database.EnsureCreatedAsync();
        await SeedMessagesAsync(db, "session-corrupt-memory", messageCount: 2, charsPerMessage: 0);

        var manager = CreateManager(null, new TestMemoryDbContextFactory(options));
        var history = manager.GetOrCreateHistory("session-corrupt-memory");
        history.Add(new ChatMessage(ChatRole.System, "system"));
        history.Add(new ChatMessage(ChatRole.User, "memory-user-1"));
        history.Add(new ChatMessage(ChatRole.Assistant, "memory-assistant-1"));
        history.Add(new ChatMessage(ChatRole.User, "memory-user-2"));
        history.Add(new ChatMessage(
            ChatRole.Assistant,
            null,
            ToolCalls:
            [
                new ToolCall("call-1", "first", "{}"),
                new ToolCall("call-2", "second", "{}"),
            ]));
        history.Add(new ChatMessage(ChatRole.Tool, "partial", ToolCallId: "call-1"));

        await manager.TryHydrateStreamHistoryFromDbAsync(
            "session-corrupt-memory",
            history,
            maxTokenBudget: 8000,
            CancellationToken.None);

        Assert.IsTrue(history.Any(message => message.Content == "memory-user-2"));
        Assert.IsFalse(history.Any(message => message.Role == ChatRole.Tool));
        Assert.IsFalse(history.Any(message => message.ToolCalls is { Count: > 0 }));
    }

    [TestMethod]
    public async Task TryHydrateStreamHistoryFromDbAsync_UsesJsonl_When_DbSnapshotIsStale()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = CreateOptions(connection);
        await using var db = new MemoryDbContext(options);
        await db.Database.EnsureCreatedAsync();
        await SeedMessagesAsync(db, "session-jsonl-fresh", messageCount: 2, charsPerMessage: 0);

        var jsonlRoot = CreateTempJsonlRoot();
        try
        {
            var writer = new JsonlSessionWriter(jsonlRoot);
            writer.WriteEventLine("session-jsonl-fresh", "delta", """{"delta":"event frames are not chat history"}""", 1, "2026-06-27T00:00:00Z");
            writer.Enqueue("session-jsonl-fresh", new JsonlEntry
            {
                Type = "user",
                MessageId = "jsonl-user-1",
                SessionId = "session-jsonl-fresh",
                Role = "user",
                ContentType = "text",
                Content = "fresh token_alpha=red-tea-17",
                BranchType = "MAIN",
                CreatedAt = 10,
            });
            writer.Enqueue("session-jsonl-fresh", new JsonlEntry
            {
                Type = "assistant",
                MessageId = "jsonl-assistant-1",
                SessionId = "session-jsonl-fresh",
                Role = "assistant",
                ContentType = "text",
                Content = "alpha=red-tea-17",
                BranchType = "MAIN",
                CreatedAt = 11,
            });

            var manager = CreateManager(
                null,
                new TestMemoryDbContextFactory(options),
                jsonlReader: new JsonlSessionReader(jsonlRoot));
            var history = manager.GetOrCreateHistory("session-jsonl-fresh");

            await manager.TryHydrateStreamHistoryFromDbAsync(
                "session-jsonl-fresh",
                history,
                maxTokenBudget: 8000,
                CancellationToken.None);

            Assert.IsTrue(history.Any(m => m.Content == "fresh token_alpha=red-tea-17"),
                "Fresh JSONL chat messages must win over a stale memory DB snapshot.");
            Assert.IsFalse(history.Any(m => m.Content?.Contains("event frames are not chat history", StringComparison.Ordinal) == true),
                "Session event frames written to the JSONL file must not be treated as LLM chat history.");
        }
        finally
        {
            Directory.Delete(jsonlRoot, recursive: true);
        }
    }

    [TestMethod]
    public async Task TryHydrateStreamHistoryFromDbAsync_KeepsDb_When_DbSnapshotIsNewerThanJsonl()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = CreateOptions(connection);
        await using var db = new MemoryDbContext(options);
        await db.Database.EnsureCreatedAsync();
        await SeedMessagesAsync(db, "session-db-fresh", messageCount: 2, charsPerMessage: 0);
        db.Messages.Add(new MessageEntity
        {
            MessageId = "summary-newer",
            SessionId = "session-db-fresh",
            Sequence = 3,
            Role = "assistant",
            ContentType = "compact_summary",
            Content = "fresh compact summary",
            CreatedAt = 20,
        });
        await db.SaveChangesAsync();

        var jsonlRoot = CreateTempJsonlRoot();
        try
        {
            var writer = new JsonlSessionWriter(jsonlRoot);
            writer.Enqueue("session-db-fresh", new JsonlEntry
            {
                Type = "user",
                MessageId = "jsonl-old-user",
                SessionId = "session-db-fresh",
                Role = "user",
                ContentType = "text",
                Content = "older raw jsonl message",
                BranchType = "MAIN",
                CreatedAt = 10,
            });

            var manager = CreateManager(
                null,
                new TestMemoryDbContextFactory(options),
                jsonlReader: new JsonlSessionReader(jsonlRoot));
            var history = manager.GetOrCreateHistory("session-db-fresh");

            await manager.TryHydrateStreamHistoryFromDbAsync(
                "session-db-fresh",
                history,
                maxTokenBudget: 8000,
                CancellationToken.None);

            Assert.IsTrue(history.Any(m => m.Content == "fresh compact summary"),
                "A newer DB compact summary must keep precedence over older raw JSONL history.");
            Assert.IsFalse(history.Any(m => m.Content == "older raw jsonl message"));
        }
        finally
        {
            Directory.Delete(jsonlRoot, recursive: true);
        }
    }

    private static ContextWindowManager CreateManager()
        => CreateManager(compactionService: null);

        private static ContextWindowManager CreateManager(
        IContextCompactionService? compactionService,
        IDbContextFactory<MemoryDbContext>? memoryDbFactory = null,
        JsonlSessionReader? jsonlReader = null,
        AgentCompactionNotifier? compactionNotifier = null,
        ContextCompactionOptions? compactionOptions = null,
        ISessionCompactionEventEmitter? compactionEventEmitter = null,
        ITelemetryMetricSink? telemetrySink = null)
        => new(
            new AgentSessionManager(NullLogger<AgentSessionManager>.Instance),
            new InMemoryRuntimeSessionStore(),
            new ExecutionControlRegistry(),
            new ExecutionJournal(),
            NullLogger<ContextWindowManager>.Instance,
            memoryDbFactory: memoryDbFactory,
            jsonlReader: jsonlReader,
            compactionService: compactionService,
            compactionNotifier: compactionNotifier,
            compactionOptions: compactionOptions,
            compactionEventEmitter: compactionEventEmitter,
            telemetrySink: telemetrySink);

    private static string CreateTempJsonlRoot()
    {
        var path = Path.Combine(Path.GetTempPath(), "pudding-jsonl-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static async Task SeedMessagesAsync(
        MemoryDbContext db,
        string sessionId,
        int messageCount,
        int charsPerMessage)
    {
        db.Sessions.Add(new SessionEntity
        {
            SessionId = sessionId,
            WorkspaceId = "workspace-1",
            AgentId = "agent-1",
            Status = "Active",
            CreatedAt = 1,
            LastActivityAt = 1,
        });

        for (var i = 1; i <= messageCount; i++)
        {
            db.Messages.Add(new MessageEntity
            {
                MessageId = $"msg-{i}",
                SessionId = sessionId,
                Sequence = i,
                Role = i % 2 == 0 ? "agent" : "user",
                ContentType = "text",
                Content = $"message {i} " + new string('x', charsPerMessage),
                CreatedAt = i,
            });
        }

        await db.SaveChangesAsync();
    }

    private static DbContextOptions<MemoryDbContext> CreateOptions(SqliteConnection connection) =>
        new DbContextOptionsBuilder<MemoryDbContext>()
            .UseSqlite(connection)
            .Options;

    private sealed class FakeContextCompactionService(bool shouldAutoCompact) : IContextCompactionService
    {
        private readonly int? _usedTokens = null;
        private readonly int _defaultContextWindowTokens = 8192;

        public FakeContextCompactionService(int usedTokens, int defaultContextWindowTokens)
            : this(shouldAutoCompact: false)
        {
            _usedTokens = usedTokens;
            _defaultContextWindowTokens = defaultContextWindowTokens;
        }

        public List<ContextCompactionRequest> CompactCalls { get; } = [];
        public int? LastContextWindowTokens { get; private set; }
        public Func<int>? OnCompact { get; init; }
        public int? EventCountAtCompact { get; private set; }

                public Task<ContextHealthSnapshot> GetHealthAsync(
            string sessionId,
            CancellationToken ct = default,
            int? contextWindowTokens = null,
            int? maxOutputTokens = null,
            int toolCount = 0)
        {
            LastContextWindowTokens = contextWindowTokens;
            if (_usedTokens is not null)
            {
                return Task.FromResult(new ContextHealthEvaluator().Evaluate(
                    sessionId,
                    _usedTokens.Value,
                    contextWindowTokens ?? _defaultContextWindowTokens,
                    maxOutputTokens ?? 2048));
            }

            return Task.FromResult(new ContextHealthSnapshot(
                sessionId,
                UsedTokens: shouldAutoCompact ? 9000 : 1000,
                ContextWindowTokens: contextWindowTokens ?? _defaultContextWindowTokens,
                EffectiveWindowTokens: shouldAutoCompact ? 6144 : 5000,
                RemainingTokens: shouldAutoCompact ? 0 : 5000,
                UsageRatio: shouldAutoCompact ? 1.1 : 0.1,
                State: shouldAutoCompact ? ContextHealthState.Critical : ContextHealthState.Healthy,
                ShouldSuggestCompact: shouldAutoCompact,
                ShouldAutoCompact: shouldAutoCompact,
                ShouldBlockSend: false));
        }

        public Task<ContextCompactionResult> CompactAsync(
            ContextCompactionRequest request,
            CancellationToken ct = default)
        {
            EventCountAtCompact = OnCompact?.Invoke();
            CompactCalls.Add(request);
            return Task.FromResult(new ContextCompactionResult(
                request.SessionId,
                SummaryMessageId: "summary-1",
                request.Mode,
                request.Level,
                BeforeTokens: 9000,
                AfterTokens: 1000,
                CompactedMessageCount: 10,
                SummaryPreview: "summary",
                SummaryMarkdown: "summary"));
        }
    }

    private sealed class TestMemoryDbContextFactory(DbContextOptions<MemoryDbContext> options) : IDbContextFactory<MemoryDbContext>
    {
        public MemoryDbContext CreateDbContext() => new(options);

        public Task<MemoryDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }

    private sealed class FixedSummaryGenerator(string summary) : IContextCompactionSummaryGenerator
    {
        public Task<string> GenerateSummaryAsync(
            ContextCompactionSummaryRequest request,
            CancellationToken ct = default) =>
            Task.FromResult(summary);
    }

    private sealed class RecordingCompactionEventEmitter(bool yieldBeforeRecord = false) : ISessionCompactionEventEmitter
    {
        public List<(string SessionId, string WorkspaceId, string EventType)> Events { get; } = [];

        public async Task EmitAsync(
            string sessionId,
            string workspaceId,
            string eventType,
            object payload,
            CancellationToken ct = default)
        {
            if (yieldBeforeRecord)
                await Task.Yield();

            Events.Add((sessionId, workspaceId, eventType));
        }
    }

    private sealed class RecordingTelemetrySink : ITelemetryMetricSink
    {
        public List<TelemetryMetric> Metrics { get; } = [];

        public Task RecordAsync(TelemetryMetric metric, CancellationToken ct = default)
        {
            Metrics.Add(metric);
            return Task.CompletedTask;
        }
    }
}
