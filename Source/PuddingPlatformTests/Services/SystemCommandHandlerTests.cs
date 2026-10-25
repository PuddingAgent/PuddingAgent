using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using PuddingCode.Abstractions;
using PuddingCode.Goals;
using PuddingCode.Platform;
using PuddingCode.Runtime;
using PuddingCode.Tools;
using PuddingPlatform.Data;
using PuddingPlatform.Services.Conversation;

namespace PuddingPlatformTests.Services;

[TestClass]
public sealed class SystemCommandHandlerTests
{
    [TestMethod]
    public async Task Yolo_PersistsSystemTranscript_WithoutCreatingAgentExecution()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<PlatformDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var db = new PlatformDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var runtime = new RuntimeControlService();
        var accessLevels = new StubAgentAccessLevelService();
        var handler = CreateHandler(db, runtime, accessLevels: accessLevels);

        var result = await handler.HandleAsync(
            new SystemCommandRequest(
                ConversationId: "conversation-1",
                WorkspaceId: "default",
                AgentId: "agent-1",
                UserId: "admin",
                ClientRequestId: "request-1",
                ClientMessageId: "user-message-1",
                ResponseMessageId: "system-message-1",
                CommandText: "/yolo"));

        // 用户 2026-09-19：/yolo 已归并到 Agent 级权限控制体系，不再切进程态运行时模式。
        Assert.AreEqual("Normal", result.RuntimeMode);
        Assert.AreEqual(RuntimeExecutionMode.Normal, runtime.Mode);
        Assert.AreEqual(1, accessLevels.SetCalls.Count);
        Assert.AreEqual("agent-1", accessLevels.SetCalls[0].AgentId);
        Assert.AreEqual(AgentAccessLevel.Full, accessLevels.SetCalls[0].Level);
        Assert.IsNull(accessLevels.SetCalls[0].Ttl);
        Assert.AreEqual(2, await db.ChatMessages.CountAsync());
        Assert.AreEqual(0, await db.ChatExecutionCommands.CountAsync());
        Assert.AreEqual(0, await db.ConversationTurns.CountAsync());

        var response = await db.ChatMessages
            .SingleAsync(message => message.MessageId == "system-message-1");
        Assert.AreEqual("agent", response.Role);
        StringAssert.Contains(response.MetadataJson, "\"sourceType\":\"system_command\"");
    }

    [TestMethod]
    public async Task Yolo_IsIdempotentByClientRequestAndResponseMessage()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<PlatformDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var db = new PlatformDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var runtime = new RuntimeControlService();
        var accessLevels = new StubAgentAccessLevelService();
        var handler = CreateHandler(db, runtime, accessLevels: accessLevels);
        var request = new SystemCommandRequest(
            "conversation-1",
            "default",
            "agent-1",
            "admin",
            "request-1",
            "user-message-1",
            "system-message-1",
            "/yolo");

        await handler.HandleAsync(request);
        Assert.AreEqual(1, accessLevels.SetCalls.Count);

        runtime.SetMode(RuntimeExecutionMode.Normal, "simulate process-local state reset");
        await handler.HandleAsync(request);

        // 重放不再重新授予：状态已落盘持久，重写会把已到期的临时授权续期，反而放大权限。
        Assert.AreEqual(1, accessLevels.SetCalls.Count);
        Assert.AreEqual(2, await db.ChatMessages.CountAsync());
        Assert.AreEqual(0, await db.ChatExecutionCommands.CountAsync());
    }

    [TestMethod]
    public async Task Yolo_WithDuration_GrantsTemporaryAgentLevelFullAccess()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<PlatformDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var db = new PlatformDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var runtime = new RuntimeControlService();
        var accessLevels = new StubAgentAccessLevelService();
        var handler = CreateHandler(db, runtime, accessLevels: accessLevels);

        var result = await handler.HandleAsync(
            new SystemCommandRequest(
                ConversationId: "conversation-1",
                WorkspaceId: "default",
                AgentId: "agent-1",
                UserId: "admin",
                ClientRequestId: "request-1",
                ClientMessageId: "user-message-1",
                ResponseMessageId: "system-message-1",
                CommandText: "/yolo 5m"));

        Assert.AreEqual(1, accessLevels.SetCalls.Count);
        Assert.AreEqual(AgentAccessLevel.Full, accessLevels.SetCalls[0].Level);
        Assert.AreEqual(TimeSpan.FromMinutes(5), accessLevels.SetCalls[0].Ttl);
        Assert.AreEqual(RuntimeExecutionMode.Normal, runtime.Mode);
        StringAssert.Contains(result.Message, "临时授权");
    }

    [TestMethod]
    public async Task YoloOff_RevokesAgentLevelFullAccess()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<PlatformDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var db = new PlatformDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var runtime = new RuntimeControlService();
        var accessLevels = new StubAgentAccessLevelService();
        var handler = CreateHandler(db, runtime, accessLevels: accessLevels);

        var result = await handler.HandleAsync(
            new SystemCommandRequest(
                ConversationId: "conversation-1",
                WorkspaceId: "default",
                AgentId: "agent-1",
                UserId: "admin",
                ClientRequestId: "request-1",
                ClientMessageId: "user-message-1",
                ResponseMessageId: "system-message-1",
                CommandText: "/yolo off"));

        Assert.AreEqual(1, accessLevels.SetCalls.Count);
        Assert.AreEqual(AgentAccessLevel.Auto, accessLevels.SetCalls[0].Level);
        Assert.IsNull(accessLevels.SetCalls[0].Ttl);
        StringAssert.Contains(result.Message, "已撤销");
    }

    [TestMethod]
    public async Task Yolo_FromNonWhitelistedFeishuUser_IsRecordedButDoesNotChangeMode()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<PlatformDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var db = new PlatformDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var runtime = new RuntimeControlService();
        var handler = CreateHandler(db, runtime);

        var result = await handler.HandleAsync(
            new SystemCommandRequest(
                ConversationId: "conversation-feishu",
                WorkspaceId: "default",
                AgentId: "agent-1",
                UserId: "gateway:user-hash",
                ClientRequestId: "request-denied",
                ClientMessageId: "user-message-denied",
                ResponseMessageId: "system-message-denied",
                CommandText: "/yolo",
                IsPrivilegedUser: false,
                SourceChannel: "feishu",
                ExternalUserId: "ou_not_allowed"));

        Assert.AreEqual(RuntimeExecutionMode.Normal, runtime.Mode);
        Assert.AreEqual("Normal", result.RuntimeMode);
        Assert.IsFalse(result.ForwardToAgent);
        StringAssert.Contains(result.Message, "Permission denied");
        Assert.AreEqual(2, await db.ChatMessages.CountAsync());
        Assert.AreEqual(0, await db.ChatExecutionCommands.CountAsync());
        Assert.AreEqual(0, await db.ConversationTurns.CountAsync());

        var response = await db.ChatMessages.SingleAsync(message =>
            message.MessageId == "system-message-denied");
        StringAssert.Contains(response.MetadataJson, "\"sourceChannel\":\"feishu\"");
        StringAssert.Contains(response.MetadataJson, "\"privilegedUser\":false");
    }

    [TestMethod]
    public async Task Help_FromNonWhitelistedFeishuUser_RemainsReadOnlyAndAvailable()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<PlatformDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var db = new PlatformDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var runtime = new RuntimeControlService();
        var handler = CreateHandler(db, runtime);

        var result = await handler.HandleAsync(
            new SystemCommandRequest(
                ConversationId: "conversation-feishu-help",
                WorkspaceId: "default",
                AgentId: "agent-1",
                UserId: "gateway:user-hash",
                ClientRequestId: "request-help",
                ClientMessageId: "user-message-help",
                ResponseMessageId: "system-message-help",
                CommandText: "/help",
                IsPrivilegedUser: false,
                SourceChannel: "feishu",
                ExternalUserId: "ou_not_allowed"));

        Assert.AreEqual(RuntimeExecutionMode.Normal, runtime.Mode);
        Assert.IsFalse(result.ForwardToAgent);
        StringAssert.Contains(result.Message, "System commands:");
        Assert.AreEqual(2, await db.ChatMessages.CountAsync());
        Assert.AreEqual(0, await db.ChatExecutionCommands.CountAsync());
        Assert.AreEqual(0, await db.ConversationTurns.CountAsync());
    }

    [TestMethod]
    public async Task WhoAmI_FromFeishu_EchoesExternalUserIdWithoutAgentExecution()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<PlatformDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var db = new PlatformDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var runtime = new RuntimeControlService();
        var handler = CreateHandler(db, runtime);

        var result = await handler.HandleAsync(
            new SystemCommandRequest(
                ConversationId: "conversation-feishu-whoami",
                WorkspaceId: "default",
                AgentId: "agent-1",
                UserId: "gateway:user-hash",
                ClientRequestId: "request-whoami",
                ClientMessageId: "user-message-whoami",
                ResponseMessageId: "system-message-whoami",
                CommandText: "/whoami",
                IsPrivilegedUser: false,
                SourceChannel: "feishu",
                ExternalUserId: "ou_current_sender"));

        Assert.AreEqual(RuntimeExecutionMode.Normal, runtime.Mode);
        Assert.IsFalse(result.ForwardToAgent);
        StringAssert.Contains(result.Message, "open_id");
        StringAssert.Contains(result.Message, "ou_current_sender");
        Assert.AreEqual(2, await db.ChatMessages.CountAsync());
        Assert.AreEqual(0, await db.ChatExecutionCommands.CountAsync());
        Assert.AreEqual(0, await db.ConversationTurns.CountAsync());
    }

    [TestMethod]
    public async Task Status_FromNonWhitelistedFeishuUser_ReturnsSharedSnapshotWithoutAgentExecution()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<PlatformDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var db = new PlatformDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var runtime = new RuntimeControlService();
        var status = new RecordingSystemStatusSnapshotProvider(
            new SystemStatusSnapshot(
                "default",
                "conversation-feishu-status",
                "agent-1",
                "Default Assistant",
                "global:general-assistant",
                SessionState.WaitingForUser,
                RunningSubAgents: 2,
                RuntimeExecutionMode.Normal,
                ActiveRuntimeSessions: 1,
                SessionWindowErrorCount: 0,
                SessionFaultSummary: null,
                ProviderId: "openai",
                ModelId: "gpt-test",
                CapabilityCount: 12,
                ContextHealth: new ContextHealthSnapshot(
                    "conversation-feishu-status",
                    UsedTokens: 31_400,
                    ContextWindowTokens: 1_048_576,
                    EffectiveWindowTokens: 1_000_000,
                    RemainingTokens: 968_600,
                    UsageRatio: 0.0314,
                    ContextHealthState.Healthy,
                    ShouldSuggestCompact: false,
                    ShouldAutoCompact: false,
                    ShouldBlockSend: false)
                {
                    UsageSource = "provider_usage",
                    UsageConfidence = "exact",
                },
                Warnings: []));
        var handler = CreateHandler(db, runtime, statusSnapshotProvider: status);

        var result = await handler.HandleAsync(
            new SystemCommandRequest(
                ConversationId: "conversation-feishu-status",
                WorkspaceId: "default",
                AgentId: "agent-1",
                UserId: "gateway:user-hash",
                ClientRequestId: "request-status",
                ClientMessageId: "user-message-status",
                ResponseMessageId: "system-message-status",
                CommandText: "/status",
                IsPrivilegedUser: false,
                SourceChannel: "feishu",
                ExternalUserId: "ou_not_allowed"));

        Assert.HasCount(1, status.Requests);
        Assert.AreEqual("conversation-feishu-status", status.Requests.Single().ConversationId);
        Assert.IsFalse(result.ForwardToAgent);
        StringAssert.Contains(result.Message, "Pudding status");
        StringAssert.Contains(result.Message, "Default Assistant");
        StringAssert.Contains(result.Message, "WaitingForUser");
        StringAssert.Contains(result.Message, "968.6k remaining / 1000.0k effective");
        StringAssert.Contains(result.Message, "openai/gpt-test");
        StringAssert.Contains(result.Message, "2 running sub-agent(s)");
        Assert.AreEqual(2, await db.ChatMessages.CountAsync());
        Assert.AreEqual(0, await db.ChatExecutionCommands.CountAsync());
        Assert.AreEqual(0, await db.ConversationTurns.CountAsync());
    }

    [TestMethod]
    public async Task Compact_FromWhitelistedFeishuUser_UsesManualCompactionBoundaryAndPersistsReply()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<PlatformDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var db = new PlatformDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var runtime = new RuntimeControlService();
        var compaction = new RecordingRequestCompactionHandler();
        var handler = CreateHandler(db, runtime, compaction);

        var request = new SystemCommandRequest(
            ConversationId: "conversation-feishu-compact",
            WorkspaceId: "default",
            AgentId: "agent-1",
            UserId: "gateway:user-hash",
            ClientRequestId: "request-compact",
            ClientMessageId: "user-message-compact",
            ResponseMessageId: "system-message-compact",
            CommandText: "/compact",
            IsPrivilegedUser: true,
            SourceChannel: "feishu",
            ExternalUserId: "ou_allowed");
        var result = await handler.HandleAsync(request);
        var replay = await handler.HandleAsync(
            request with { ConversationId = "conversation-feishu-compact-next" });

        Assert.HasCount(1, compaction.Requests);
        var command = compaction.Requests.Single();
        Assert.AreEqual("conversation-feishu-compact", command.ConversationId);
        Assert.AreEqual("agent-1", command.AgentId);
        Assert.AreEqual(ContextCompactionLevel.Full, command.Level);
        Assert.AreEqual("request-compact", command.CompactionId);
        // P0-4f: 系统命令边界创建根 Trace（入站 SystemCommandRequest 无 trace 字段可继承）。
        Assert.IsNotNull(command.TraceId);
        Assert.IsTrue(
            Guid.TryParseExact(command.TraceId, "N", out _),
            $"系统 /compact 入口必须创建 Guid-N 根 Trace，实际为 '{command.TraceId}'。");
        StringAssert.Contains(command.Reason, "feishu");
        StringAssert.Contains(result.Message, "Compacted 8 messages");
        StringAssert.Contains(result.Message, "1200 -> 240");
        StringAssert.Contains(result.Message, "conversation-feishu-compact-next");
        Assert.AreEqual("conversation-feishu-compact", replay.ConversationId);
        Assert.AreEqual(result.Message, replay.Message);
        Assert.IsFalse(result.ForwardToAgent);
        Assert.AreEqual(2, await db.ChatMessages.CountAsync());
        Assert.AreEqual(0, await db.ChatExecutionCommands.CountAsync());
        Assert.AreEqual(0, await db.ConversationTurns.CountAsync());
    }

    [TestMethod]
    public async Task Compact_CreatesDistinctRootTraces_WhenInboundMessageHasNoTraceField()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<PlatformDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var db = new PlatformDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var runtime = new RuntimeControlService();
        var compaction = new RecordingRequestCompactionHandler();
        var handler = CreateHandler(db, runtime, compaction);

        var request1 = new SystemCommandRequest(
            "conversation-web-1",
            "default",
            "agent-1",
            "admin",
            "request-web-1",
            "user-message-web-1",
            "system-message-web-1",
            "/compact");
        var request2 = new SystemCommandRequest(
            "conversation-web-2",
            "default",
            "agent-1",
            "admin",
            "request-web-2",
            "user-message-web-2",
            "system-message-web-2",
            "/compact");

        await handler.HandleAsync(request1);
        await handler.HandleAsync(request2);

        Assert.HasCount(2, compaction.Requests);
        var trace1 = compaction.Requests[0].TraceId;
        var trace2 = compaction.Requests[1].TraceId;
        Assert.IsNotNull(trace1);
        Assert.IsNotNull(trace2);
        Assert.IsTrue(
            Guid.TryParseExact(trace1, "N", out _),
            $"trace1 应为 Guid-N 根 Trace，实际为 '{trace1}'。");
        Assert.IsTrue(
            Guid.TryParseExact(trace2, "N", out _),
            $"trace2 应为 Guid-N 根 Trace，实际为 '{trace2}'。");
        Assert.AreNotEqual(
            trace1,
            trace2,
            "每次 /compact 调用必须在系统命令边界创建各自的根 Trace，不能复用上一次的 TraceId。");
        Assert.AreEqual(4, await db.ChatMessages.CountAsync());
        Assert.AreEqual(0, await db.ChatExecutionCommands.CountAsync());
        Assert.AreEqual(0, await db.ConversationTurns.CountAsync());
    }

    /// <summary>
    /// `/effort max` 必须写进该 Agent 的配置，而不是只回一句“已设置”。
    /// 这是飞书渠道能继承思考强度的唯一路径：外部消息带不上 metadata，
    /// 只能靠 Configured 层。
    /// </summary>
    [TestMethod]
    public async Task Effort_Command_WritesAgentLevelSetting_AndReportsIt()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<PlatformDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var db = new PlatformDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var effort = new StubAgentReasoningEffortStore();
        var handler = CreateHandler(db, new RuntimeControlService(), reasoningEffortStore: effort);

        var result = await handler.HandleAsync(
            new SystemCommandRequest(
                ConversationId: "conversation-1",
                WorkspaceId: "default",
                AgentId: "agent-1",
                UserId: "admin",
                ClientRequestId: "request-effort",
                ClientMessageId: "user-message-effort",
                ResponseMessageId: "system-message-effort",
                CommandText: "/effort max"));

        Assert.AreEqual(1, effort.SetCalls.Count);
        Assert.AreEqual("default", effort.SetCalls[0].WorkspaceId);
        Assert.AreEqual("agent-1", effort.SetCalls[0].AgentId);
        Assert.AreEqual("max", effort.SetCalls[0].Effort);
        StringAssert.Contains(result.Message, "max");

        // 系统命令不创建 Agent 执行：改的是持久配置，不是起一轮对话。
        Assert.AreEqual(0, await db.ChatExecutionCommands.CountAsync());
        Assert.AreEqual(0, await db.ConversationTurns.CountAsync());
        Assert.AreEqual(2, await db.ChatMessages.CountAsync());
        Assert.IsFalse(result.ForwardToAgent);
    }

    /// <summary>无参数只回显，不得写盘——回显命令产生副作用会让用户不敢乱看。</summary>
    [TestMethod]
    public async Task Effort_WithoutArgument_EchoesStoredValue_WithoutWriting()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<PlatformDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var db = new PlatformDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var effort = new StubAgentReasoningEffortStore { Current = "max" };
        var handler = CreateHandler(db, new RuntimeControlService(), reasoningEffortStore: effort);

        var result = await handler.HandleAsync(
            new SystemCommandRequest(
                ConversationId: "conversation-1",
                WorkspaceId: "default",
                AgentId: "agent-1",
                UserId: "admin",
                ClientRequestId: "request-effort-read",
                ClientMessageId: "user-message-effort-read",
                ResponseMessageId: "system-message-effort-read",
                CommandText: "/effort"));

        Assert.AreEqual(1, effort.GetCalls.Count);
        Assert.AreEqual(0, effort.SetCalls.Count);
        StringAssert.Contains(result.Message, "max");
    }

    /// <summary>`/effort auto` 清除显式档位，回到模型默认。</summary>
    [TestMethod]
    public async Task Effort_Auto_ClearsStoredValue()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<PlatformDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var db = new PlatformDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var effort = new StubAgentReasoningEffortStore { Current = "max" };
        var handler = CreateHandler(db, new RuntimeControlService(), reasoningEffortStore: effort);

        var result = await handler.HandleAsync(
            new SystemCommandRequest(
                ConversationId: "conversation-1",
                WorkspaceId: "default",
                AgentId: "agent-1",
                UserId: "admin",
                ClientRequestId: "request-effort-auto",
                ClientMessageId: "user-message-effort-auto",
                ResponseMessageId: "system-message-effort-auto",
                CommandText: "/effort auto"));

        Assert.AreEqual(1, effort.SetCalls.Count);
        Assert.AreEqual("auto", effort.SetCalls[0].Effort);
        Assert.IsNull(effort.Current);
        StringAssert.Contains(result.Message, "清除");
    }

    /// <summary>
    /// 存储层拒绝非法值时，命令必须把原因回给用户，而不是让整轮对话失败；
    /// 也不能静默报“已设置”。
    /// </summary>
    [TestMethod]
    public async Task Effort_StoreRejection_IsReportedInsteadOfThrown()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<PlatformDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var db = new PlatformDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var effort = new StubAgentReasoningEffortStore
        {
            SetError = new ArgumentException(
                "unsupported reasoning effort 'medium'; expected one of: high, max, auto",
                "effort"),
        };
        var handler = CreateHandler(db, new RuntimeControlService(), reasoningEffortStore: effort);

        var result = await handler.HandleAsync(
            new SystemCommandRequest(
                ConversationId: "conversation-1",
                WorkspaceId: "default",
                AgentId: "agent-1",
                UserId: "admin",
                ClientRequestId: "request-effort-bad",
                ClientMessageId: "user-message-effort-bad",
                ResponseMessageId: "system-message-effort-bad",
                CommandText: "/effort max"));

        StringAssert.Contains(result.Message, "unsupported reasoning effort");
        Assert.AreEqual(0, await db.ChatExecutionCommands.CountAsync());
        Assert.IsFalse(result.ForwardToAgent);
    }

    private static SystemCommandHandler CreateHandler(
        PlatformDbContext db,
        IRuntimeControlService runtime,
        IRequestCompactionHandler? compaction = null,
        ISystemStatusSnapshotProvider? statusSnapshotProvider = null,
        IGoalCommandService? goalCommandService = null,
        IToolAuthorizationService? toolAuthorizationService = null,
        IAgentAccessLevelService? accessLevels = null,
        IAgentReasoningEffortStore? reasoningEffortStore = null) =>
        new(
            db,
            runtime,
            accessLevels ?? new StubAgentAccessLevelService(),
            compaction ?? new UnexpectedRequestCompactionHandler(),
            statusSnapshotProvider ?? new UnexpectedSystemStatusSnapshotProvider(),
            goalCommandService ?? new UnexpectedGoalCommandService(),
            toolAuthorizationService ?? new UnexpectedToolAuthorizationService(),
            reasoningEffortStore ?? new StubAgentReasoningEffortStore(),
            NullLogger<SystemCommandHandler>.Instance);

    /// <summary>
    /// 记录型桩：/effort 用例需要断言「写了什么、跳过了哪些调用」，
    /// 所以记录而不是一律抛错。
    /// </summary>
    private sealed class StubAgentReasoningEffortStore : IAgentReasoningEffortStore
    {
        public List<(string WorkspaceId, string AgentId, string? Effort)> SetCalls { get; } = [];

        public List<(string WorkspaceId, string AgentId)> GetCalls { get; } = [];

        public string? Current { get; set; }

        public Exception? SetError { get; init; }

        public Task<string?> GetReasoningEffortAsync(
            string workspaceId, string agentId, CancellationToken ct = default)
        {
            GetCalls.Add((workspaceId, agentId));
            return Task.FromResult(Current);
        }

        public Task<string?> SetReasoningEffortAsync(
            string workspaceId, string agentId, string? effort, CancellationToken ct = default)
        {
            if (SetError is not null)
                throw SetError;

            SetCalls.Add((workspaceId, agentId, effort));
            // 与真实实现同一口径：auto / 空 清除为 null，其余落小写。
            Current = string.IsNullOrWhiteSpace(effort)
                || string.Equals(effort, ReasoningEffortPolicy.AutoSelection, StringComparison.OrdinalIgnoreCase)
                ? null
                : effort.Trim().ToLowerInvariant();
            return Task.FromResult(Current);
        }
    }

    /// <summary>
    /// 记录型桩：/yolo 现在写的是 Agent 级访问级别（不再是进程态运行时模式），
    /// 用例需要断言「写了什么」，因此记录调用而不是直接失败。
    /// </summary>
    private sealed class StubAgentAccessLevelService : IAgentAccessLevelService
    {
        public List<(string AgentId, AgentAccessLevel Level, TimeSpan? Ttl)> SetCalls { get; } = [];

        public AgentAccessLevelState Get(string? agentInstanceId) => AgentAccessLevelState.Default;

        public AgentAccessLevelState Set(
            string agentInstanceId,
            AgentAccessLevel level,
            TimeSpan? ttl = null,
            string? actor = null)
        {
            SetCalls.Add((agentInstanceId, level, ttl));
            var now = DateTimeOffset.UtcNow;
            return new AgentAccessLevelState(
                level,
                level == AgentAccessLevel.Full && ttl is { } duration ? now.Add(duration) : null,
                now,
                actor);
        }

        public RuntimeExecutionMode ResolveEffectiveMode(
            string? agentInstanceId,
            RuntimeExecutionMode globalMode) => globalMode;
    }

    private sealed class UnexpectedGoalCommandService : IGoalCommandService
    {
        public Task<GoalCommandResult> ExecuteAsync(
            GoalCommandRequest request,
            CancellationToken ct = default) =>
            throw new AssertFailedException("This test must not execute a goal command.");
    }

    private sealed class UnexpectedToolAuthorizationService : IToolAuthorizationService
    {
        public Task<ToolAuthorizationCommandResult> ApplyCommandAsync(
            ToolAuthorizationCommand command,
            ToolAuthorizationContext context,
            CancellationToken ct = default) =>
            throw new AssertFailedException(
                "This test must not apply a tool authorization command. Pass a real IToolAuthorizationService from the test.");

        public Task<ToolAuthorizationCheckResult> CheckAsync(
            ToolAuthorizationContext context,
            ToolDescriptor descriptor,
            CancellationToken ct = default) =>
            throw new AssertFailedException("This test must not check tool authorization.");

        public string BuildRequiredMessage(string toolId, ToolDescriptor? descriptor = null) =>
            throw new AssertFailedException("This test must not build an authorization message.");
    }

    private sealed class UnexpectedSystemStatusSnapshotProvider : ISystemStatusSnapshotProvider
    {
        public Task<SystemStatusSnapshot> GetAsync(
            SystemStatusSnapshotRequest request,
            CancellationToken ct = default) =>
            throw new AssertFailedException("This test must not request a status snapshot.");
    }

    private sealed class RecordingSystemStatusSnapshotProvider(
        SystemStatusSnapshot snapshot) : ISystemStatusSnapshotProvider
    {
        public List<SystemStatusSnapshotRequest> Requests { get; } = [];

        public Task<SystemStatusSnapshot> GetAsync(
            SystemStatusSnapshotRequest request,
            CancellationToken ct = default)
        {
            Requests.Add(request);
            return Task.FromResult(snapshot);
        }
    }

    private sealed class UnexpectedRequestCompactionHandler : IRequestCompactionHandler
    {
        public Task<CompactionResult> HandleAsync(
            RequestCompactionCommand command,
            CancellationToken ct) =>
            throw new AssertFailedException("This test must not request compaction.");
    }

    private sealed class RecordingRequestCompactionHandler : IRequestCompactionHandler
    {
        public List<RequestCompactionCommand> Requests { get; } = [];

        public Task<CompactionResult> HandleAsync(
            RequestCompactionCommand command,
            CancellationToken ct)
        {
            Requests.Add(command);
            return Task.FromResult(new CompactionResult(
                command.CompactionId,
                new ContextCompactionResult(
                    command.ConversationId,
                    "summary-message",
                    ContextCompactionMode.Manual,
                    ContextCompactionLevel.Full,
                    BeforeTokens: 1200,
                    AfterTokens: 240,
                    CompactedMessageCount: 8,
                    SummaryPreview: "summary preview",
                    SummaryMarkdown: "summary markdown"),
                "conversation-feishu-compact-next",
                "压缩 - Fake Command Conversation"));
        }
    }

    [TestMethod]
    public async Task Goal_Command_Delegates_To_Goal_Service_And_Writes_Transcript_Pair()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<PlatformDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var db = new PlatformDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var goalService = new RecordingGoalCommandService();
        var handler = CreateHandler(db, new RuntimeControlService(), goalCommandService: goalService);

        var result = await handler.HandleAsync(new SystemCommandRequest(
            "conversation-1",
            "default",
            "agent-1",
            "admin",
            "request-1",
            "user-message-1",
            "system-message-1",
            "/goal 修复全部失败测试 --rounds 32"));

        // 回执来自 Goal 服务；同时写入 user/agent transcript 对。
        Assert.AreEqual("Goal active · iteration 0/32", result.Message);
        Assert.AreEqual(1, goalService.Requests.Count);
        Assert.AreEqual("request-1", goalService.Requests[0].ClientRequestId);
        Assert.AreEqual("conversation-1", goalService.Requests[0].ConversationId);
        Assert.AreEqual(GoalCommandKind.Set, goalService.Requests[0].Command.Kind);
        Assert.AreEqual(32, goalService.Requests[0].Command.Rounds);
        Assert.AreEqual(2, await db.ChatMessages.CountAsync());
        // G1 出口：/goal 命令不创建 Agent Turn。
        Assert.AreEqual(0, await db.ChatExecutionCommands.CountAsync());
        Assert.AreEqual(0, await db.ConversationTurns.CountAsync());
    }

    private sealed class RecordingGoalCommandService : IGoalCommandService
    {
        public List<GoalCommandRequest> Requests { get; } = [];

        public Task<GoalCommandResult> ExecuteAsync(
            GoalCommandRequest request,
            CancellationToken ct = default)
        {
            Requests.Add(request);
            return Task.FromResult(GoalCommandResult.Ok(
                $"Goal active · iteration 0/{request.Command.Rounds ?? 256}",
                new GoalSnapshot
                {
                    GoalRunId = "goal-fake",
                    WorkspaceId = request.WorkspaceId,
                    ConversationId = request.ConversationId,
                    AgentInstanceId = request.AgentInstanceId,
                    Objective = request.Command.Objective ?? string.Empty,
                    ObjectiveVersion = 1,
                    Phase = GoalPhase.Active,
                    MaxIterations = request.Command.Rounds ?? 256,
                    IterationsStarted = 0,
                    IterationsSettled = 0,
                    ActivationEpoch = 1,
                    AggregateVersion = 1,
                    CreatedAtUtc = DateTimeOffset.UtcNow,
                    UpdatedAtUtc = DateTimeOffset.UtcNow,
                }));
        }
    }
}
