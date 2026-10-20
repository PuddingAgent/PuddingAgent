using System.Runtime.CompilerServices;
using System.Text.Json;
using PuddingCode.Abstractions;
using PuddingCode.Platform;
using PuddingCode.Runtime;
using PuddingRuntime.Services;
using PuddingRuntime.Services.Messaging;

namespace PuddingRuntimeTests.Services;

[TestClass]
public sealed class TurnExecutorAdapterTests
{
    [TestMethod]
    public async Task ExecuteAsync_CarriesOnlyPublicErrorDiagnosticIntoTerminalInfo()
    {
        var adapter = new TurnExecutorAdapter(new ErrorRuntimeDispatcher("""
            {"message":"upload failed","errorId":"llm-test","causeCode":"transport.request_upload_reset",
             "causeTitle":"模型服务连接中断","retryable":true,"reportText":"redacted report",
             "apiKey":"must-not-cross-boundary","unrelated":{"secret":"hidden"}}
            """), Microsoft.Extensions.Logging.Abstractions.NullLogger<TurnExecutorAdapter>.Instance);
        var events = new List<TurnExecutionEvent>();
        await foreach (var evt in adapter.ExecuteAsync(CreateContext(), CancellationToken.None))
            events.Add(evt);
        var diagnostic = events.Single().TerminalInfo!.Diagnostic!.Value;
        Assert.AreEqual("llm-test", diagnostic.GetProperty("errorId").GetString());
        Assert.IsTrue(diagnostic.GetProperty("retryable").GetBoolean());
        Assert.AreEqual("redacted report", diagnostic.GetProperty("reportText").GetString());
        Assert.IsFalse(diagnostic.TryGetProperty("apiKey", out _));
        Assert.IsFalse(diagnostic.TryGetProperty("unrelated", out _));
    }

    [TestMethod]
    [DataRow("{\"errorCode\":\"vision.route_missing\",\"message\":\"route missing\"}", "vision.route_missing")]
    [DataRow("{\"code\":\"budget_exhausted\",\"message\":\"route missing\"}", "budget_exhausted")]
    [DataRow("{\"message\":\"route missing\"}", "runtime_execution_failed")]
    public async Task ExecuteAsync_PreservesRuntimeFailureCode(string payload, string expectedCode)
    {
        var adapter = new TurnExecutorAdapter(new ErrorRuntimeDispatcher(payload),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<TurnExecutorAdapter>.Instance);
        var events = new List<TurnExecutionEvent>();
        await foreach (var evt in adapter.ExecuteAsync(CreateContext(), CancellationToken.None))
            events.Add(evt);
        Assert.HasCount(1, events);
        Assert.AreEqual(ConversationEventTypes.TurnFailed, events[0].Type);
        Assert.AreEqual(expectedCode, events[0].TerminalInfo?.ErrorCode);
        Assert.AreEqual("route missing", events[0].TerminalInfo?.ErrorMessage);
    }

    /// <summary>
    /// 复核 P1（前台租约接线）：真实 Turn 入口必须持有**同键 session 前台租约**，
    /// 且该租约在整个 Turn 期间有效 —— 包括**超过消息入口 5 秒限时预留**之后。
    /// 否则长 Turn 跨过预留窗口时，终态后可选维护会在 Turn 中途开工（抢占/提交）。
    /// </summary>
    [TestMethod]
    public async Task ExecuteAsync_HoldsSessionForegroundLease_BeyondReservationWindow()
    {
        var coordinator = new AgentExecutionAdmissionCoordinator();
        var runtime = new SlowRuntimeDispatcher(TimeSpan.FromSeconds(6));
        var adapter = new TurnExecutorAdapter(
            runtime,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<TurnExecutorAdapter>.Instance,
            coordinator);

        // 维护按 session 注册后台工作（用调度器的同一注册面）。
        using var maintenanceCts = new CancellationTokenSource();
        using var background = coordinator.TryRegisterSessionBackground(
            "default", "conversation-1", maintenanceCts);
        Assert.IsNotNull(background, "前提：Turn 开始前应能注册同会话后台工作");

        var sawDemandDuringTurn = false;
        var sawDemandAfterReservationWindow = false;
        var expectation = TimeSpan.FromSeconds(5.5);
        var startedAt = DateTimeOffset.UtcNow;

        await foreach (var evt in adapter.ExecuteAsync(CreateContext(), CancellationToken.None))
        {
            if (evt.Type != ConversationEventTypes.MessageContentAppended)
                continue;

            sawDemandDuringTurn = coordinator.HasSessionForegroundDemand("default", "conversation-1");
            // 等到超过消息入口的默认 5 秒预留窗口再复查一次。
            while (DateTimeOffset.UtcNow - startedAt < expectation)
                await Task.Delay(100);

            sawDemandAfterReservationWindow = coordinator.HasSessionForegroundDemand("default", "conversation-1");
        }

        Assert.IsTrue(sawDemandDuringTurn, "Turn 运行期间必须有 session 前台需求（租约）");
        Assert.IsTrue(
            sawDemandAfterReservationWindow,
            "超过 5 秒预留窗口后仍必须由**租约**维持前台需求（限时预留会过期）");
        Assert.IsTrue(maintenanceCts.IsCancellationRequested, "Turn 入口必须提前取消同会话后台工作");
        Assert.IsFalse(
            coordinator.HasSessionForegroundDemand("default", "conversation-1"),
            "Turn 结束后（租约释放）不应再有前台需求");
    }

    /// <summary>先给一帧正文，再慢一段时间，最后给终态（模拟跨过预留窗口的长 Turn）。</summary>
    private sealed class SlowRuntimeDispatcher(TimeSpan workDuration) : IRuntimeAgentDispatcher
    {
        public Task<RuntimeDispatchResult> DispatchAsync(RuntimeDispatchRequest request,
            CancellationToken ct = default) => throw new NotSupportedException();

        public async IAsyncEnumerable<ServerSentEventFrame> DispatchStreamAsync(
            RuntimeDispatchRequest request, [EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.Yield();
            yield return new ServerSentEventFrame("delta", "{\"text\":\"working\"}");
            await Task.Delay(workDuration, ct);
            yield return new ServerSentEventFrame("done", "{\"reply\":\"ok\"}");
        }
    }

    private sealed class ErrorRuntimeDispatcher(string payload) : IRuntimeAgentDispatcher
    {
        public Task<RuntimeDispatchResult> DispatchAsync(RuntimeDispatchRequest request,
            CancellationToken ct = default) => throw new NotSupportedException();

        public async IAsyncEnumerable<ServerSentEventFrame> DispatchStreamAsync(
            RuntimeDispatchRequest request, [EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.Yield();
            yield return new ServerSentEventFrame("error", payload);
        }
    }

    [TestMethod]
    public async Task ExecuteAsync_AcquiresForegroundAndPreemptsBackgroundDelivery()
    {
        var coordinator = new AgentExecutionAdmissionCoordinator();
        using var backgroundCts = new CancellationTokenSource();
        using var background = coordinator.TryRegisterBackground(
            "default",
            "agent-1",
            backgroundCts);
        var runtime = new BusyThenSuccessRuntimeDispatcher();
        var adapter = new TurnExecutorAdapter(
            runtime,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<TurnExecutorAdapter>.Instance,
            coordinator);

        await foreach (var _ in adapter.ExecuteAsync(CreateContext(), CancellationToken.None))
        {
        }

        Assert.IsTrue(backgroundCts.IsCancellationRequested);
        Assert.IsTrue(background!.WasPreempted);
        Assert.IsFalse(coordinator.HasForegroundDemand("default", "agent-1"));
    }

    [TestMethod]
    public async Task ExecuteAsync_WaitsForSharedRuntimeBusyStateThenCompletes()
    {
        var runtime = new BusyThenSuccessRuntimeDispatcher();
        var adapter = new TurnExecutorAdapter(
            runtime,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<TurnExecutorAdapter>.Instance);
        var events = new List<TurnExecutionEvent>();

        await foreach (var evt in adapter.ExecuteAsync(CreateContext(), CancellationToken.None))
            events.Add(evt);

        Assert.AreEqual(2, runtime.DispatchCount);
        Assert.IsFalse(events.Any(evt => evt.Type == ConversationEventTypes.TurnFailed));
        Assert.HasCount(2, events);
        Assert.AreEqual(ConversationEventTypes.MessageContentAppended, events[0].Type);
        Assert.AreEqual(ConversationEventTypes.TurnCompleted, events[1].Type);
        Assert.IsTrue(events[1].IsTerminal);
        Assert.AreEqual(TurnTerminalKind.Completed, events[1].TerminalInfo?.Kind);
        Assert.AreEqual("ok", events[1].TerminalInfo?.Reply);
    }

    [TestMethod]
    public async Task ExecuteAsync_PropagatesCanonicalTaskPlanIdentity()
    {
        var runtime = new BusyThenSuccessRuntimeDispatcher();
        var adapter = new TurnExecutorAdapter(
            runtime,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<TurnExecutorAdapter>.Instance);
        var context = CreateContext() with
        {
            TaskPlanId = "plan-1",
            TaskNodeId = "node-1",
            ParentTaskNodeId = "root-1",
            UsageBudget = new ExecutionUsageBudget
            {
                MaxInputTokens = 1000,
                MaxOutputTokens = 100,
                MaxCost = 0.5m,
                PricingKnown = true,
                InputPricePer1MTokens = 1m,
                OutputPricePer1MTokens = 2m,
                CacheHitPricePer1MTokens = 0.1m,
            },
        };

        await foreach (var _ in adapter.ExecuteAsync(context, CancellationToken.None))
        {
        }

        Assert.IsNotNull(runtime.LastRequest);
        Assert.AreEqual("plan-1", runtime.LastRequest.TaskPlanId);
        Assert.AreEqual("node-1", runtime.LastRequest.TaskNodeId);
        Assert.AreEqual("root-1", runtime.LastRequest.ParentTaskNodeId);
        Assert.AreEqual(1000L, runtime.LastRequest.UsageBudget?.MaxInputTokens);
        Assert.AreEqual(0.5m, runtime.LastRequest.UsageBudget?.MaxCost);
    }

    // ── A1（G92-1 S1-c 片6 段2）：done frame envelope reply → TurnTerminalInfo proposal 传播 ──

    [TestMethod]
    public async Task ExecuteAsync_DoneFrameWithEnvelopeReply_PropagatesTypedProposalAndPreservesReply()
    {
        const string proposalJson = """
            {"schemaVersion":1,"kind":"refine_acceptance_contract","expectedContractVersion":2,
             "criteria":[{"requirement":"只输出 READY","requirementRefs":["objective:line-1"],
               "verification":{"kind":"text-assertion","definitionRef":"checks/text-assertion.md#equals",
                 "inputRefs":["reply"],"expectedText":"READY"}}]}
            """;
        var envelope = JsonSerializer.Serialize(new
        {
            status = "DONE",
            message = "READY",
            meta = new { goal_contract_proposal = JsonDocument.Parse(proposalJson).RootElement.Clone() },
        });
        var runtime = new StaticFramesRuntimeDispatcher(
            ServerSentEventFrame.Json("done", new { reply = envelope }));
        var adapter = new TurnExecutorAdapter(
            runtime,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<TurnExecutorAdapter>.Instance);

        var events = new List<TurnExecutionEvent>();
        await foreach (var evt in adapter.ExecuteAsync(CreateContext(), CancellationToken.None))
            events.Add(evt);

        var terminal = events.Single(e => e.IsTerminal).TerminalInfo;
        Assert.AreEqual(TurnTerminalKind.Completed, terminal!.Kind);
        Assert.AreEqual(envelope, terminal.Reply);
        Assert.IsNotNull(terminal.GoalContractProposal);
        Assert.AreEqual(2, terminal.GoalContractProposal.ExpectedContractVersion);
        Assert.AreEqual("READY", terminal.GoalContractProposal.Criteria[0].Verification.ExpectedText);
    }

    [TestMethod]
    public async Task ExecuteAsync_DoneFrameWithPlainReply_LeavesProposalNull()
    {
        var runtime = new StaticFramesRuntimeDispatcher(
            ServerSentEventFrame.Json("done", new { reply = "plain answer" }));
        var adapter = new TurnExecutorAdapter(
            runtime,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<TurnExecutorAdapter>.Instance);

        var events = new List<TurnExecutionEvent>();
        await foreach (var evt in adapter.ExecuteAsync(CreateContext(), CancellationToken.None))
            events.Add(evt);

        var terminal = events.Single(e => e.IsTerminal).TerminalInfo;
        Assert.AreEqual(TurnTerminalKind.Completed, terminal!.Kind);
        Assert.AreEqual("plain answer", terminal.Reply);
        Assert.IsNull(terminal.GoalContractProposal);
    }

    [TestMethod]
    public async Task ExecuteAsync_DoneFrameWithMalformedEnvelope_FailsClosedWithoutBreakingTurn()
    {
        const string malformed = """{"status":"DONE","message":"m","meta":{"goal_contract_proposal":{"schemaVersion":1,"rogue":true}}}""";
        var runtime = new StaticFramesRuntimeDispatcher(
            ServerSentEventFrame.Json("done", new { reply = malformed }));
        var adapter = new TurnExecutorAdapter(
            runtime,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<TurnExecutorAdapter>.Instance);

        var events = new List<TurnExecutionEvent>();
        await foreach (var evt in adapter.ExecuteAsync(CreateContext(), CancellationToken.None))
            events.Add(evt);

        var terminal = events.Single(e => e.IsTerminal).TerminalInfo;
        Assert.AreEqual(TurnTerminalKind.Completed, terminal!.Kind);
        Assert.AreEqual(malformed, terminal.Reply);
        Assert.IsNull(terminal.GoalContractProposal);
    }

    private sealed class StaticFramesRuntimeDispatcher(params ServerSentEventFrame[] frames)
        : IRuntimeAgentDispatcher
    {
        public Task<RuntimeDispatchResult> DispatchAsync(
            RuntimeDispatchRequest request,
            CancellationToken ct = default) => throw new NotSupportedException();

        public async IAsyncEnumerable<ServerSentEventFrame> DispatchStreamAsync(
            RuntimeDispatchRequest request,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.Yield();
            foreach (var frame in frames)
                yield return frame;
        }
    }

    private static TurnExecutionContext CreateContext() => new(
        ConversationId: "conversation-1",
        WorkspaceId: "default",
        TurnId: "turn-1",
        CommandId: "command-1",
        RunId: "run-1",
        AgentInstanceId: "agent-1",
        AgentTemplateId: "global:general-assistant",
        MessageText: "continue",
        UserId: "user-1",
        CapabilityPolicy: null,
        ToolDefinitions: null,
        SkillPackages: null,
        LlmProfile: new LlmInvocationProfile
        {
            ProviderId = "test",
            ProfileId = "conscious.default",
            ModelId = "test-model",
        },
        LlmConfig: null,
        MaxRounds: 10,
        MaxElapsedSeconds: 120,
        MaxToolCallsTotal: 20,
        ChannelId: null,
        UserExternalId: null,
        RunCancellation: new RunCancellation(CancellationToken.None),
        VisualArtifactIds: null,
        AudioArtifactIds: null)
    {
        InboundMessageId = "message-1",
        TraceId = null,
    };

    private sealed class BusyThenSuccessRuntimeDispatcher : IRuntimeAgentDispatcher
    {
        public int DispatchCount { get; private set; }
        public RuntimeDispatchRequest? LastRequest { get; private set; }

        public Task<RuntimeDispatchResult> DispatchAsync(
            RuntimeDispatchRequest request,
            CancellationToken ct = default) =>
            throw new NotSupportedException();

        public async IAsyncEnumerable<ServerSentEventFrame> DispatchStreamAsync(
            RuntimeDispatchRequest request,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            LastRequest = request;
            DispatchCount++;
            if (DispatchCount == 1)
            {
                yield return ServerSentEventFrame.Json("error", new
                {
                    error = "Agent is busy.",
                    executionState = "Busy",
                });
                yield break;
            }

            yield return ServerSentEventFrame.Json("delta", new { text = "ok" });
            await Task.Yield();
            yield return ServerSentEventFrame.Json("done", new { reply = "ok" });
        }
    }
}
