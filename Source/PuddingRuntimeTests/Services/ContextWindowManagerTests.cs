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
    public async Task TrimHistoryAsync_PassesAgentTemplateId_ToCompactionRequest()
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
            CancellationToken.None,
            agentTemplateId: "template-1");

        Assert.AreEqual(1, compaction.CompactCalls.Count);
        Assert.AreEqual("template-1", compaction.CompactCalls[0].AgentTemplateId);
    }

    [TestMethod]
    public async Task TrimHistoryAsync_PassesPreCompactionFacts_ToCompactionRequest()
    {
        var compaction = new FakeContextCompactionService(shouldAutoCompact: true);
        var flush = new FakePreCompactionFlushService(
        [
            "项目路径：E:/github/AgentNetworkPlan/PuddingAgent",
            "用户偏好：每个原子任务 commit 后立即 push",
        ]);
        var manager = CreateManager(compaction, preCompactionFlushService: flush);
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
            CancellationToken.None,
            agentTemplateId: "template-1");

        Assert.AreEqual(1, compaction.CompactCalls.Count);
        Assert.IsNotNull(compaction.CompactCalls[0].PreCompactionFacts);
        Assert.AreEqual(2, compaction.CompactCalls[0].PreCompactionFacts!.Count);
        StringAssert.Contains(compaction.CompactCalls[0].PreCompactionFacts![0], "项目路径");
        Assert.IsNotNull(flush.LastRequest);
        Assert.AreEqual("template-1", flush.LastRequest!.AgentTemplateId);
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
    public async Task BuildContextFromDbAsync_TierFill_KeepsNewestTurns_WhenBudgetIsTight()
    {
        // 3 轮（user+agent），每条约 300 字符 ≈ 100 tokens；预算 100 只够 T0 + 保底部分 T1。
        // Tier 化填充应保留最新轮（T0：user3/agent3）、裁掉最旧轮（user1/agent1）——
        // 与旧逻辑「从旧到新累加、超预算丢弃最新」方向相反。
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = CreateOptions(connection);
        await using var db = new MemoryDbContext(options);
        await db.Database.EnsureCreatedAsync();
        await SeedTieredMessagesAsync(db, "session-tier-tight", [
            ("user", "user1:" + new string('x', 294)),
            ("agent", "agent1:" + new string('x', 294)),
            ("user", "user2:" + new string('x', 294)),
            ("agent", "agent2:" + new string('x', 294)),
            ("user", "user3:" + new string('x', 294)),
            ("agent", "agent3:" + new string('x', 294)),
        ]);

        var manager = CreateManager(null, new TestMemoryDbContextFactory(options));
        var history = await manager.BuildContextFromDbAsync("session-tier-tight", maxTokenBudget: 100);

        Assert.AreEqual(3, history.Count, "T0 两条 + T1 保底一条");
        Assert.IsTrue(history.Any(m => m.Content?.StartsWith("user3:", StringComparison.Ordinal) == true), "最新轮 user3 必须保留");
        Assert.IsTrue(history.Any(m => m.Content?.StartsWith("agent3:", StringComparison.Ordinal) == true), "最新轮 agent3 必须保留");
        Assert.IsFalse(history.Any(m => m.Content?.StartsWith("user1:", StringComparison.Ordinal) == true), "最旧轮 user1 必须被裁");
        Assert.IsFalse(history.Any(m => m.Content?.StartsWith("agent1:", StringComparison.Ordinal) == true), "最旧轮 agent1 必须被裁");

        // 输出仍按 Sequence 升序（旧→新），而非按 tier 分组排列。
        StringAssert.StartsWith(history[0].Content, "agent2:");
        StringAssert.StartsWith(history[1].Content, "user3:");
        StringAssert.StartsWith(history[2].Content, "agent3:");
    }

    [TestMethod]
    public async Task BuildContextFromDbAsync_TierFill_PreservesSequenceOrder_WhenWithinBudget()
    {
        // 预算充足时，新逻辑输出与旧逻辑一致的「按 Sequence 升序」全量消息。
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = CreateOptions(connection);
        await using var db = new MemoryDbContext(options);
        await db.Database.EnsureCreatedAsync();
        await SeedTieredMessagesAsync(db, "session-tier-order", [
            ("user", "user1:" + new string('x', 27)),
            ("agent", "agent1:" + new string('x', 27)),
            ("user", "user2:" + new string('x', 27)),
            ("agent", "agent2:" + new string('x', 27)),
            ("user", "user3:" + new string('x', 27)),
            ("agent", "agent3:" + new string('x', 27)),
        ]);

        var manager = CreateManager(null, new TestMemoryDbContextFactory(options));
        var history = await manager.BuildContextFromDbAsync("session-tier-order", maxTokenBudget: 1_000_000);

        Assert.AreEqual(6, history.Count);
        var expected = new[] { "user1:", "agent1:", "user2:", "agent2:", "user3:", "agent3:" };
        for (var i = 0; i < expected.Length; i++)
            StringAssert.StartsWith(history[i].Content, expected[i]);
    }

    [TestMethod]
    public async Task BuildContextFromDbAsync_TierFill_KeepsLargeLatestTurn_OverSmallColdTurns()
    {
        // 最近轮（T0）消息巨大（≈300 tokens/条），更冷轮消息很小（≈10 tokens/条）。
        // 预算 645：T0（≈602）+ T1 全部（≈40）刚好容纳；T2（最冷轮）即使消息小也被裁。
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = CreateOptions(connection);
        await using var db = new MemoryDbContext(options);
        await db.Database.EnsureCreatedAsync();
        await SeedTieredMessagesAsync(db, "session-tier-large-latest", [
            ("user", "turn0-user:" + new string('x', 21)),
            ("agent", "turn0-agent:" + new string('x', 21)),
            ("user", "turn1-user:" + new string('x', 21)),
            ("agent", "turn1-agent:" + new string('x', 21)),
            ("user", "turn2-user:" + new string('x', 21)),
            ("agent", "turn2-agent:" + new string('x', 21)),
            ("user", "turn3-user:" + new string('x', 894)),
            ("agent", "turn3-agent:" + new string('x', 893)),
        ]);

        var manager = CreateManager(null, new TestMemoryDbContextFactory(options));
        var history = await manager.BuildContextFromDbAsync("session-tier-large-latest", maxTokenBudget: 645);

        Assert.IsTrue(history.Any(m => m.Content?.StartsWith("turn3-user:", StringComparison.Ordinal) == true), "T0 大消息 turn3-user 必须全保真");
        Assert.IsTrue(history.Any(m => m.Content?.StartsWith("turn3-agent:", StringComparison.Ordinal) == true), "T0 大消息 turn3-agent 必须全保真");
        Assert.IsFalse(history.Any(m => m.Content?.StartsWith("turn0-", StringComparison.Ordinal) == true), "最冷轮 turn0 必须被裁");
    }

    [TestMethod]
    public void TrimHistory_TierFill_CutsColdestTurns_First()
    {
        var manager = CreateManager();
        var history = new List<ChatMessage> { new(ChatRole.System, "system") };
        for (var turn = 0; turn < 5; turn++)
        {
            history.Add(new ChatMessage(ChatRole.User, $"turn{turn}-user"));
            for (var j = 0; j < 9; j++)
                history.Add(new ChatMessage(ChatRole.Assistant, $"turn{turn}-a{j}"));
        }

        // maxTokenBudget=100_000 → maxMessages=40；51 条 > 41，触发 Tier 化裁剪 10 条。
        manager.TrimHistory(history, maxTokenBudget: 100_000);

        Assert.AreEqual(41, history.Count, "system + 40 条非 system");
        Assert.AreEqual(ChatRole.System, history[0].Role, "system 消息必须保留在首位");
        Assert.IsFalse(history.Any(m => m.Content?.StartsWith("turn0-", StringComparison.Ordinal) == true), "最冷轮 turn0 必须先被裁");
        Assert.IsTrue(history.Any(m => m.Content == "turn4-user"), "最近轮 turn4 必须保留");
        Assert.IsTrue(history.Any(m => m.Content == "turn4-a8"), "最近轮最后一条必须保留");
    }

    [TestMethod]
    public void TrimHistory_TierFill_KeepsRecentTurns_WhenHistoryIsLarge()
    {
        var manager = CreateManager();
        var history = new List<ChatMessage> { new(ChatRole.System, "system") };
        // 10 轮 × 8 条 = 80 条非 system；maxMessages=40 → 裁 40 条，仅保留最近 5 轮（T0+T1）。
        for (var turn = 0; turn < 10; turn++)
        {
            history.Add(new ChatMessage(ChatRole.User, $"turn{turn}-user"));
            for (var j = 0; j < 7; j++)
                history.Add(new ChatMessage(ChatRole.Assistant, $"turn{turn}-a{j}"));
        }

        manager.TrimHistory(history, maxTokenBudget: 100_000);

        Assert.AreEqual(41, history.Count);
        Assert.IsFalse(history.Any(m => m.Content?.StartsWith("turn0-", StringComparison.Ordinal) == true), "最早的轮必须被裁");
        Assert.IsFalse(history.Any(m => m.Content?.StartsWith("turn1-", StringComparison.Ordinal) == true));
        Assert.IsFalse(history.Any(m => m.Content?.StartsWith("turn2-", StringComparison.Ordinal) == true));
        Assert.IsFalse(history.Any(m => m.Content?.StartsWith("turn3-", StringComparison.Ordinal) == true));
        Assert.IsFalse(history.Any(m => m.Content?.StartsWith("turn4-", StringComparison.Ordinal) == true));
        Assert.IsTrue(history.Any(m => m.Content == "turn5-user"), "保底后的最近轮必须保留");
        Assert.IsTrue(history.Any(m => m.Content == "turn9-a6"), "最后一条必须保留");
    }

    [TestMethod]
    public void ExtractQueryHits_NullOrWhitespace_ReturnsEmpty()
    {
        Assert.AreEqual(0, ContextWindowManager.ExtractQueryHits(null).Count);
        Assert.AreEqual(0, ContextWindowManager.ExtractQueryHits(string.Empty).Count);
        Assert.AreEqual(0, ContextWindowManager.ExtractQueryHits("   ").Count);
    }

    [TestMethod]
    public void ExtractQueryHits_FiltersNumericAndSymbolTokens_KeepsMeaningfulOnes()
    {
        var hits = ContextWindowManager.ExtractQueryHits("Token 优化 缓存 命中率 123 !!!");

        // "123"（纯数字）与 "!!!"（纯符号）无检索语义 → 过滤；其余小写保留。
        CollectionAssert.AreEquivalent(new[] { "token", "优化", "缓存", "命中率" }, hits.ToList());
    }

    [TestMethod]
    public void ExtractQueryHits_NoValidToken_FallsBackToWholeQuery()
    {
        var numeric = ContextWindowManager.ExtractQueryHits("12345");
        CollectionAssert.AreEqual(new[] { "12345" }, numeric.ToList());

        var symbols = ContextWindowManager.ExtractQueryHits("!!!??");
        CollectionAssert.AreEqual(new[] { "!!!??" }, symbols.ToList());
    }

    [TestMethod]
    public void ExtractQueryHits_DeduplicatesTokens_IgnoreCase()
    {
        var hits = ContextWindowManager.ExtractQueryHits("Token token TOKEN 优化");

        CollectionAssert.AreEquivalent(new[] { "token", "优化" }, hits.ToList());
    }

    [TestMethod]
    public void TrimHistory_QueryHit_PromotesMatchedOldTurn_OverUnmatched()
    {
        var manager = CreateManager();
        var history = new List<ChatMessage> { new(ChatRole.System, "system") };
        history.AddRange(CreateTenTurnHistory(hitTurn0: true));

        // 80 条非 system；maxMessages=40 → 裁 40 条。
        // turn0 整轮正文都含 "缓存命中率" → query 命中后整轮晋升 T1，免于 T2 裁剪。
        manager.TrimHistory(history, maxTokenBudget: 100_000, query: "缓存命中率 优化");

        Assert.AreEqual(41, history.Count);
        Assert.IsTrue(history.Any(m => m.Content?.StartsWith("turn0-", StringComparison.Ordinal) == true),
            "query 命中的旧轮必须晋升保留");
    }

    [TestMethod]
    public void TrimHistory_QueryNull_BehavesLikeBefore_AndCutsColdestTurn()
    {
        var manager = CreateManager();
        var history = new List<ChatMessage> { new(ChatRole.System, "system") };
        history.AddRange(CreateTenTurnHistory(hitTurn0: true));

        // query 默认 null（旧调用方式）：命中判定关闭，turn0 照常被裁 → 与改造前一致。
        manager.TrimHistory(history, maxTokenBudget: 100_000);

        Assert.AreEqual(41, history.Count);
        Assert.IsFalse(history.Any(m => m.Content?.StartsWith("turn0-", StringComparison.Ordinal) == true),
            "query=null 时旧轮照常被裁（回归）");
        Assert.IsTrue(history.Any(m => m.Content == "turn6-user"), "保底后的最近轮必须保留");
    }

    [TestMethod]
    public async Task BuildContextFromDbAsync_QueryHit_PromotesColdTurn_WhenBudgetIsTight()
    {
        // 5 轮（user+agent），每条约 100~101 tokens；预算 704：
        // T0(turn4)=201 + T1(turn2/turn3)=402 → 603，T2 内只够 1 条（Sequence 降序 → turn1-agent）。
        // 无 query：turn0（T2 最冷）全裁，turn1-agent（Sequence 较新）抢到 T2 名额。
        // query 命中 turn0-user → 晋升进 T1（T0+T1=703 ≤ 704）→ turn0-user 保留，turn1 全裁；turn0-agent 未命中仍被裁。
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = CreateOptions(connection);
        await using var db = new MemoryDbContext(options);
        await db.Database.EnsureCreatedAsync();
        await SeedTieredMessagesAsync(db, "session-query-hit", [
            ("user", "turn0-user 缓存命中率 " + new string('x', 285)),
            ("agent", "turn0-agent " + new string('x', 291)),
            ("user", "turn1-user " + new string('x', 291)),
            ("agent", "turn1-agent " + new string('x', 291)),
            ("user", "turn2-user " + new string('x', 291)),
            ("agent", "turn2-agent " + new string('x', 291)),
            ("user", "turn3-user " + new string('x', 291)),
            ("agent", "turn3-agent " + new string('x', 291)),
            ("user", "turn4-user " + new string('x', 291)),
            ("agent", "turn4-agent " + new string('x', 291)),
        ]);

        var manager = CreateManager(null, new TestMemoryDbContextFactory(options));

        // 1) 无 query：turn0（最冷 T2）被裁，turn1-agent（Sequence 较新）抢到 T2 名额
        var baseline = await manager.BuildContextFromDbAsync("session-query-hit", maxTokenBudget: 704);
        Assert.IsFalse(baseline.Any(m => m.Content?.StartsWith("turn0-", StringComparison.Ordinal) == true),
            "无 query 时 turn0 必须被裁");
        Assert.IsTrue(baseline.Any(m => m.Content?.StartsWith("turn1-agent", StringComparison.Ordinal) == true),
            "无 query 时 turn1-agent 抢到 T2 名额");

        // 2) query 命中 turn0-user → 晋升 T1，turn0-user 保留；turn1 未命中且预算紧张 → 全裁
        var promoted = await manager.BuildContextFromDbAsync("session-query-hit", maxTokenBudget: 704, query: "缓存命中率 提升");
        Assert.IsTrue(promoted.Any(m => m.Content?.StartsWith("turn0-user", StringComparison.Ordinal) == true),
            "query 命中后 turn0-user 必须晋升保留");
        Assert.IsFalse(promoted.Any(m => m.Content?.StartsWith("turn1-", StringComparison.Ordinal) == true),
            "query 未命中的 turn1 在预算紧张时必须被裁");
        Assert.IsFalse(promoted.Any(m => m.Content?.StartsWith("turn0-agent", StringComparison.Ordinal) == true),
            "turn0-agent 未命中不得晋升，预算紧张时仍被裁");
    }

    /// <summary>
    /// 构造 10 轮 × 8 条（user + 7 assistant）历史；hitTurn0=true 时 turn0 整轮正文含 "缓存命中率"。
    /// </summary>
    private static List<ChatMessage> CreateTenTurnHistory(bool hitTurn0)
    {
        var history = new List<ChatMessage>();
        if (hitTurn0)
        {
            history.Add(new ChatMessage(ChatRole.User, "turn0-user 缓存命中率"));
            for (var j = 0; j < 7; j++)
                history.Add(new ChatMessage(ChatRole.Assistant, $"turn0-缓存命中率-a{j}"));
        }
        else
        {
            history.Add(new ChatMessage(ChatRole.User, "turn0-user"));
            for (var j = 0; j < 7; j++)
                history.Add(new ChatMessage(ChatRole.Assistant, $"turn0-a{j}"));
        }

        for (var turn = 1; turn < 10; turn++)
        {
            history.Add(new ChatMessage(ChatRole.User, $"turn{turn}-user"));
            for (var j = 0; j < 7; j++)
                history.Add(new ChatMessage(ChatRole.Assistant, $"turn{turn}-a{j}"));
        }

        return history;
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


    [TestMethod]
    public void TryMarkMessageDispatched_FirstCall_ReturnsTrue()
    {
        var manager = CreateManager();
        var result = manager.TryMarkMessageDispatched("session-1", "msg-1");
        Assert.IsTrue(result, "First dispatch of a message should return true.");
    }

    [TestMethod]
    public void TryMarkMessageDispatched_DuplicateCall_ReturnsFalse()
    {
        var manager = CreateManager();
        manager.TryMarkMessageDispatched("session-1", "msg-1");
        var result = manager.TryMarkMessageDispatched("session-1", "msg-1");
        Assert.IsFalse(result, "Duplicate message dispatch should return false.");
    }

    [TestMethod]
    public void TryMarkMessageDispatched_DifferentSessions_Independent()
    {
        var manager = CreateManager();
        Assert.IsTrue(manager.TryMarkMessageDispatched("session-1", "msg-1"));
        Assert.IsTrue(manager.TryMarkMessageDispatched("session-2", "msg-1"),
            "Same message ID in different sessions should not collide.");
    }

    [TestMethod]
    public void TryMarkMessageDispatched_NullOrEmptyMessageId_ReturnsTrue()
    {
        var manager = CreateManager();
        Assert.IsTrue(manager.TryMarkMessageDispatched("session-1", null!),
            "Null message ID should bypass dedup.");
        Assert.IsTrue(manager.TryMarkMessageDispatched("session-1", ""),
            "Empty message ID should bypass dedup.");
        Assert.IsTrue(manager.TryMarkMessageDispatched("session-1", "   "),
            "Whitespace message ID should bypass dedup.");
    }

    [TestMethod]
    public void UnmarkMessageDispatched_AllowsRetry()
    {
        var manager = CreateManager();
        Assert.IsTrue(manager.TryMarkMessageDispatched("session-1", "msg-1"));
        manager.UnmarkMessageDispatched("session-1", "msg-1");
        Assert.IsTrue(manager.TryMarkMessageDispatched("session-1", "msg-1"),
            "After unmark, the message should be dispatchable again.");
    }

    [TestMethod]
    public void UnmarkMessageDispatched_NonExistentSession_DoesNotThrow()
    {
        var manager = CreateManager();
        manager.UnmarkMessageDispatched("nonexistent", "msg-1");
        // Should not throw
    }

    [TestMethod]
    public void TryMarkMessageDispatched_DifferentMessages_SameSession()
    {
        var manager = CreateManager();
        Assert.IsTrue(manager.TryMarkMessageDispatched("session-1", "msg-1"));
        Assert.IsTrue(manager.TryMarkMessageDispatched("session-1", "msg-2"),
            "Different messages in the same session should both be allowed.");
        }

    // ── History Pruning (Batch2-4) ──

    [TestMethod]
    public async Task TryHydrateStreamHistoryFromDbAsync_PrunesHeartbeatAndSystemMessages_WhenPruningEnabled()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = CreateOptions(connection);
        await using var db = new MemoryDbContext(options);
        await db.Database.EnsureCreatedAsync();

        db.Sessions.Add(new SessionEntity
        {
            SessionId = "session-prune-heartbeat",
            WorkspaceId = "workspace-1",
            AgentId = "agent-1",
            Status = "Active",
            CreatedAt = 1,
            LastActivityAt = 1,
        });
        db.Messages.Add(new MessageEntity { MessageId = "msg-1", SessionId = "session-prune-heartbeat", Sequence = 1, Role = "user", ContentType = "text", Content = "real user question", CreatedAt = 1 });
        db.Messages.Add(new MessageEntity { MessageId = "msg-2", SessionId = "session-prune-heartbeat", Sequence = 2, Role = "agent", ContentType = "text", Content = "[HEARTBEAT] ping", CreatedAt = 2 });
        db.Messages.Add(new MessageEntity { MessageId = "msg-3", SessionId = "session-prune-heartbeat", Sequence = 3, Role = "user", ContentType = "text", Content = "[SYSTEM] auto notification", CreatedAt = 3 });
        db.Messages.Add(new MessageEntity { MessageId = "msg-4", SessionId = "session-prune-heartbeat", Sequence = 4, Role = "agent", ContentType = "text", Content = "real assistant answer", CreatedAt = 4 });
        db.Messages.Add(new MessageEntity { MessageId = "msg-5", SessionId = "session-prune-heartbeat", Sequence = 5, Role = "tool", ContentType = "text", Content = "tool result", CreatedAt = 5 });
        await db.SaveChangesAsync();

        var compactionOpts = new ContextCompactionOptions { EnableHistoryPruning = true, HistoryPruningMaxMessages = 50 };
        var manager = CreateManager(null, new TestMemoryDbContextFactory(options), compactionOptions: compactionOpts);
        var history = manager.GetOrCreateHistory("session-prune-heartbeat");

        await manager.TryHydrateStreamHistoryFromDbAsync("session-prune-heartbeat", history, maxTokenBudget: 8000, CancellationToken.None);

        Assert.IsTrue(history.Any(m => m.Content == "real user question"), "Real user message should survive pruning");
        Assert.IsTrue(history.Any(m => m.Content == "real assistant answer"), "Real assistant message should survive pruning");
        Assert.IsFalse(history.Any(m => m.Content?.StartsWith("[HEARTBEAT]", StringComparison.OrdinalIgnoreCase) == true), "HEARTBEAT messages must be pruned");
        Assert.IsFalse(history.Any(m => m.Content?.StartsWith("[SYSTEM]", StringComparison.OrdinalIgnoreCase) == true), "SYSTEM messages must be pruned");
        Assert.IsFalse(history.Any(m => m.Role == ChatRole.Tool), "Tool messages must be pruned");
    }

    [TestMethod]
    public async Task TryHydrateStreamHistoryFromDbAsync_TruncatesLongMessages_WhenPruningEnabled()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = CreateOptions(connection);
        await using var db = new MemoryDbContext(options);
        await db.Database.EnsureCreatedAsync();

        db.Sessions.Add(new SessionEntity
        {
            SessionId = "session-prune-long",
            WorkspaceId = "workspace-1",
            AgentId = "agent-1",
            Status = "Active",
            CreatedAt = 1,
            LastActivityAt = 1,
        });
        var longContent = new string('A', 2500);
        db.Messages.Add(new MessageEntity { MessageId = "msg-1", SessionId = "session-prune-long", Sequence = 1, Role = "user", ContentType = "text", Content = longContent, CreatedAt = 1 });
        await db.SaveChangesAsync();

        var compactionOpts = new ContextCompactionOptions { EnableHistoryPruning = true, HistoryPruningMaxMessages = 50 };
        var manager = CreateManager(null, new TestMemoryDbContextFactory(options), compactionOptions: compactionOpts);
        var history = manager.GetOrCreateHistory("session-prune-long");

        await manager.TryHydrateStreamHistoryFromDbAsync("session-prune-long", history, maxTokenBudget: 8000, CancellationToken.None);

        Assert.AreEqual(1, history.Count);
        var content = history[0].Content!;
        Assert.IsTrue(content.EndsWith("..."), "Long messages should be truncated with trailing ellipsis");
        Assert.IsTrue(content.Length <= 2003, $"Truncated length {content.Length} should be <= 2003 (2000 + '...')");
    }

    [TestMethod]
    public async Task TryHydrateStreamHistoryFromDbAsync_RespectsHistoryPruningMaxMessages_WhenPruningEnabled()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = CreateOptions(connection);
        await using var db = new MemoryDbContext(options);
        await db.Database.EnsureCreatedAsync();

        db.Sessions.Add(new SessionEntity
        {
            SessionId = "session-prune-limit",
            WorkspaceId = "workspace-1",
            AgentId = "agent-1",
            Status = "Active",
            CreatedAt = 1,
            LastActivityAt = 1,
        });
        for (var i = 1; i <= 20; i++)
        {
            db.Messages.Add(new MessageEntity { MessageId = $"msg-{i}", SessionId = "session-prune-limit", Sequence = i, Role = i % 2 == 0 ? "agent" : "user", ContentType = "text", Content = $"message {i}", CreatedAt = i });
        }
        await db.SaveChangesAsync();

        var compactionOpts = new ContextCompactionOptions { EnableHistoryPruning = true, HistoryPruningMaxMessages = 5 };
        var manager = CreateManager(null, new TestMemoryDbContextFactory(options), compactionOptions: compactionOpts);
        var history = manager.GetOrCreateHistory("session-prune-limit");

        await manager.TryHydrateStreamHistoryFromDbAsync("session-prune-limit", history, maxTokenBudget: 8000, CancellationToken.None);

        Assert.AreEqual(5, history.Count, "Only 5 most recent messages should survive");
        Assert.AreEqual("message 20", history[4].Content, "Most recent message should be preserved");
        Assert.AreEqual("message 16", history[0].Content, "The 5th-from-last message should be the oldest survivor");
    }

    [TestMethod]
    public async Task TryHydrateStreamHistoryFromDbAsync_NoPruning_WhenPruningDisabled()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = CreateOptions(connection);
        await using var db = new MemoryDbContext(options);
        await db.Database.EnsureCreatedAsync();

        db.Sessions.Add(new SessionEntity
        {
            SessionId = "session-no-prune",
            WorkspaceId = "workspace-1",
            AgentId = "agent-1",
            Status = "Active",
            CreatedAt = 1,
            LastActivityAt = 1,
        });
        db.Messages.Add(new MessageEntity { MessageId = "msg-1", SessionId = "session-no-prune", Sequence = 1, Role = "user", ContentType = "text", Content = "real user question", CreatedAt = 1 });
        db.Messages.Add(new MessageEntity { MessageId = "msg-2", SessionId = "session-no-prune", Sequence = 2, Role = "agent", ContentType = "text", Content = "[HEARTBEAT] ping", CreatedAt = 2 });
        db.Messages.Add(new MessageEntity { MessageId = "msg-3", SessionId = "session-no-prune", Sequence = 3, Role = "user", ContentType = "text", Content = "[SYSTEM] auto notification", CreatedAt = 3 });
        db.Messages.Add(new MessageEntity { MessageId = "msg-4", SessionId = "session-no-prune", Sequence = 4, Role = "agent", ContentType = "text", Content = "real assistant answer", CreatedAt = 4 });
        await db.SaveChangesAsync();

        var compactionOpts = new ContextCompactionOptions { EnableHistoryPruning = false, HistoryPruningMaxMessages = 50 };
        var manager = CreateManager(null, new TestMemoryDbContextFactory(options), compactionOptions: compactionOpts);
        var history = manager.GetOrCreateHistory("session-no-prune");

        await manager.TryHydrateStreamHistoryFromDbAsync("session-no-prune", history, maxTokenBudget: 8000, CancellationToken.None);

        Assert.IsTrue(history.Any(m => m.Content?.StartsWith("[HEARTBEAT]", StringComparison.OrdinalIgnoreCase) == true), "HEARTBEAT should survive when pruning is disabled");
        Assert.IsTrue(history.Any(m => m.Content?.StartsWith("[SYSTEM]", StringComparison.OrdinalIgnoreCase) == true), "SYSTEM should survive when pruning is disabled");
    }

    [TestMethod]
    public async Task TryHydrateStreamHistoryFromDbAsync_NoPruning_WhenOptionsIsNull()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = CreateOptions(connection);
        await using var db = new MemoryDbContext(options);
        await db.Database.EnsureCreatedAsync();

        db.Sessions.Add(new SessionEntity
        {
            SessionId = "session-null-opts",
            WorkspaceId = "workspace-1",
            AgentId = "agent-1",
            Status = "Active",
            CreatedAt = 1,
            LastActivityAt = 1,
        });
        db.Messages.Add(new MessageEntity { MessageId = "msg-1", SessionId = "session-null-opts", Sequence = 1, Role = "user", ContentType = "text", Content = "real user question", CreatedAt = 1 });
        db.Messages.Add(new MessageEntity { MessageId = "msg-2", SessionId = "session-null-opts", Sequence = 2, Role = "agent", ContentType = "text", Content = "[HEARTBEAT] ping", CreatedAt = 2 });
        db.Messages.Add(new MessageEntity { MessageId = "msg-3", SessionId = "session-null-opts", Sequence = 3, Role = "user", ContentType = "text", Content = "[SYSTEM] auto notification", CreatedAt = 3 });
        db.Messages.Add(new MessageEntity { MessageId = "msg-4", SessionId = "session-null-opts", Sequence = 4, Role = "agent", ContentType = "text", Content = "real assistant answer", CreatedAt = 4 });
        await db.SaveChangesAsync();

        var manager = CreateManager(null, new TestMemoryDbContextFactory(options), compactionOptions: null);
        var history = manager.GetOrCreateHistory("session-null-opts");

        await manager.TryHydrateStreamHistoryFromDbAsync("session-null-opts", history, maxTokenBudget: 8000, CancellationToken.None);

        Assert.IsTrue(history.Any(m => m.Content?.StartsWith("[HEARTBEAT]", StringComparison.OrdinalIgnoreCase) == true), "HEARTBEAT should survive when options is null");
        Assert.IsTrue(history.Any(m => m.Content?.StartsWith("[SYSTEM]", StringComparison.OrdinalIgnoreCase) == true), "SYSTEM should survive when options is null");
    }

    [TestMethod]
    public async Task TryHydrateStreamHistoryFromDbAsync_CustomMaxMessages_TakesEffect()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = CreateOptions(connection);
        await using var db = new MemoryDbContext(options);
        await db.Database.EnsureCreatedAsync();

        db.Sessions.Add(new SessionEntity
        {
            SessionId = "session-custom-max",
            WorkspaceId = "workspace-1",
            AgentId = "agent-1",
            Status = "Active",
            CreatedAt = 1,
            LastActivityAt = 1,
        });
        for (var i = 1; i <= 15; i++)
        {
            db.Messages.Add(new MessageEntity { MessageId = $"msg-{i}", SessionId = "session-custom-max", Sequence = i, Role = i % 2 == 0 ? "agent" : "user", ContentType = "text", Content = $"message {i}", CreatedAt = i });
        }
        await db.SaveChangesAsync();

        var compactionOpts = new ContextCompactionOptions { EnableHistoryPruning = true, HistoryPruningMaxMessages = 3 };
        var manager = CreateManager(null, new TestMemoryDbContextFactory(options), compactionOptions: compactionOpts);
        var history = manager.GetOrCreateHistory("session-custom-max");

        await manager.TryHydrateStreamHistoryFromDbAsync("session-custom-max", history, maxTokenBudget: 8000, CancellationToken.None);

        Assert.AreEqual(3, history.Count, "Custom HistoryPruningMaxMessages=3 should leave exactly 3 messages");
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
        ITelemetryMetricSink? telemetrySink = null,
        IPreCompactionFlushService? preCompactionFlushService = null)
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
            telemetrySink: telemetrySink,
            preCompactionFlushService: preCompactionFlushService);

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

    /// <summary>
    /// 按显式 (Role, Content) 序列播种消息（Sequence=1..N，CreatedAt=Sequence）。
    /// Role 用 "user"/"agent"，与 <see cref="SeedMessagesAsync"/> 保持一致。
    /// </summary>
    private static async Task SeedTieredMessagesAsync(
        MemoryDbContext db,
        string sessionId,
        IReadOnlyList<(string Role, string Content)> messages)
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

        for (var i = 0; i < messages.Count; i++)
        {
            var sequence = i + 1L;
            db.Messages.Add(new MessageEntity
            {
                MessageId = $"msg-{sequence}",
                SessionId = sessionId,
                Sequence = sequence,
                Role = messages[i].Role,
                ContentType = "text",
                Content = messages[i].Content,
                CreatedAt = sequence,
            });
        }

        await db.SaveChangesAsync();
    }

    private static DbContextOptions<MemoryDbContext> CreateOptions(SqliteConnection connection) =>
        new DbContextOptionsBuilder<MemoryDbContext>()
            .UseSqlite(connection)
            .Options;

    private sealed class FakePreCompactionFlushService(IReadOnlyList<string> facts) : IPreCompactionFlushService
    {
        public PreCompactionFlushRequest? LastRequest { get; private set; }

        public Task<PreCompactionFlushResult> FlushAsync(
            PreCompactionFlushRequest request,
            CancellationToken ct = default)
        {
            LastRequest = request;
            var content = facts.Count > 0 ? string.Join(" | ", facts) : null;
            return Task.FromResult(new PreCompactionFlushResult(facts.Count, 1, content, facts));
        }
    }

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
            int? maxInputTokens = null,
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
        public List<(string SessionId, string WorkspaceId, string EventType, string? TraceId)> Events { get; } = [];

        public async Task EmitAsync(
            string sessionId,
            string workspaceId,
            string eventType,
            object payload,
            string? traceId,
            CancellationToken ct = default)
        {
            if (yieldBeforeRecord)
                await Task.Yield();

            Events.Add((sessionId, workspaceId, eventType, traceId));
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
