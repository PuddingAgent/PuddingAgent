using System.Runtime.CompilerServices;
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
