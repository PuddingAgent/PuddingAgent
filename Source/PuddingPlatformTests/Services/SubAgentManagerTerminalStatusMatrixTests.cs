using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using PuddingCode.Abstractions;
using PuddingCode.Models;
using PuddingCode.Observability;
using PuddingCode.Platform;
using PuddingCode.Runtime;
using PuddingCode.SubAgents;
using PuddingPlatform.Services;

namespace PuddingPlatformTests.Services;

/// <summary>
/// P0 子代理终态事务 Phase 2：Manager 侧终态仲裁矩阵（SubAgentManager.ResolveRuntimeTerminalStatus）。
///
/// 通过公共 SpawnAsync 缝隙注入 RuntimeDispatchResult 变体，断言三层投影一致：
///   ① canonical 终态事件类型（InternalEvent）；
///   ② 父代理回执消息 metadata["subagent_status"]（NotifyParentAgentAsync）；
///   ③ SessionStateManager 捕获的 SubAgentResult.Status。
/// 语义（SubAgentManager.cs）：IsSuccess → completed（优先级最高）；
/// BudgetExhausted（state 或 StopReason）→ budget_exhausted；MaxElapsedReached → timed_out；
/// Cancelled → cancelled；其余 → failed。BudgetExhausted 路径在此矩阵中完整可表达。
/// </summary>
[TestClass]
public sealed class SubAgentManagerTerminalStatusMatrixTests
{
    [DataTestMethod]
    [DataRow(true, "Completed", "", "completed", ConversationEventTypes.SubAgentRunCompleted)]
    [DataRow(true, "WaitingEvent", "", "completed", ConversationEventTypes.SubAgentRunCompleted)]
    [DataRow(true, "Completed", "BudgetExhausted", "completed", ConversationEventTypes.SubAgentRunCompleted)]
    [DataRow(false, "BudgetExhausted", "", "budget_exhausted", ConversationEventTypes.SubAgentRunBudgetExhausted)]
    [DataRow(false, "Running", "BudgetExhausted", "budget_exhausted", ConversationEventTypes.SubAgentRunBudgetExhausted)]
    [DataRow(false, "Running", "MaxElapsedReached", "timed_out", ConversationEventTypes.SubAgentRunTimedOut)]
    [DataRow(false, "Cancelled", "", "cancelled", ConversationEventTypes.SubAgentRunCancelled)]
    [DataRow(false, "Failed", "", "failed", ConversationEventTypes.SubAgentRunFailed)]
    [DataRow(false, "Running", "", "failed", ConversationEventTypes.SubAgentRunFailed)]
    [DataRow(false, "Completed", "", "failed", ConversationEventTypes.SubAgentRunFailed)]
    public async Task SpawnAsync_TerminalStatusMatrix_ProjectsConsistentTerminalArbitration(
        bool isSuccess,
        string executionState,
        string stopReason,
        string expectedStatus,
        string expectedEventType)
    {
        var messageSystem = new RecordingMessageSystem();
        var eventBus = new CapturingInternalEventBus();
        var ssm = new RecordingSessionStateManager();
        var dispatcher = new RecordingRuntimeAgentDispatcher(new RuntimeDispatchResult
        {
            SessionId = "sub-session-matrix",
            AgentInstanceId = "sub-agent-matrix",
            IsSuccess = isSuccess,
            ReplyText = "matrix child reply",
            ErrorMessage = isSuccess ? null : "matrix child error",
            ExecutionState = Enum.Parse<AgentExecutionState>(executionState),
            StopReason = string.IsNullOrEmpty(stopReason) ? null : stopReason,
            ToolFailureCount = isSuccess ? 0 : 1,
            ToolFailureSummary = isSuccess ? null : "file_read rejected by policy",
        });

        var manager = CreateManager(dispatcher, messageSystem, eventBus, ssm);

        var result = await manager.SpawnAsync(CreateSpawnRequest());
        Assert.IsTrue(result.Success);

        var envelope = await messageSystem.WaitForEnvelopeAsync();

        // ① canonical 终态事件：恰好一条且类型与矩阵一致。
        var terminalEvents = eventBus.Published
            .Where(evt => IsTerminalRunEvent(evt.Type))
            .ToList();
        Assert.HasCount(1, terminalEvents, string.Join(", ", eventBus.Published.Select(evt => evt.Type)));
        Assert.AreEqual(expectedEventType, terminalEvents[0].Type);

        // ② 父代理回执：状态 + resumable 语义（仅 budget_exhausted 可续跑）。
        Assert.AreEqual(expectedStatus, envelope.Metadata["subagent_status"], envelope.Content);
        Assert.AreEqual(
            expectedStatus == "budget_exhausted" ? "true" : "false",
            envelope.Metadata["resumable"]);

        // ③ Manager 对 SessionStateManager 的终态投影与 IsSuccess 同源。
        var tracked = Assert.IsInstanceOfType<SubAgentResult>(await ssm.WaitForResultAsync());
        Assert.AreEqual(expectedStatus, tracked.Status);
        Assert.AreEqual(isSuccess, tracked.Success);
    }

    // ─────────────────────────────── 构造帮助 ───────────────────────────────

    private static SubAgentSpawnRequest CreateSpawnRequest() => new()
    {
        ParentSessionId = "parent-session",
        ParentAgentId = "agent-parent",
        WorkspaceId = "default",
        TaskDescription = "Terminal status matrix probe.",
        TemplateId = "workspace-task-agent",
        LlmConfig = CreateLlmConfig(),
        LlmProfile = CreateLlmProfile(),
    };

    private static SubAgentManager CreateManager(
        RecordingRuntimeAgentDispatcher dispatcher,
        RecordingMessageSystem messageSystem,
        CapturingInternalEventBus eventBus,
        RecordingSessionStateManager ssm)
    {
        var services = new ServiceCollection()
            .AddSingleton<IMessageSystem>(messageSystem)
            .AddSingleton<IRuntimeAgentDispatcher>(dispatcher)
            .AddSingleton<IRuntimeExecutionConfigService>(new TestRuntimeExecutionConfigService())
            .BuildServiceProvider();

        return new SubAgentManager(
            ssm,
            services,
            eventBus,
            new RecordingSubAgentRunStore(),
            NullLogger<SubAgentManager>.Instance,
            new RecordingRuntimeActivitySink(),
            new RecordingRuntimeTraceAccessor(),
            services.GetRequiredService<IRuntimeExecutionConfigService>());
    }

    private static bool IsTerminalRunEvent(string type) =>
        type is ConversationEventTypes.SubAgentRunCompleted
            or ConversationEventTypes.SubAgentRunFailed
            or ConversationEventTypes.SubAgentRunCancelled
            or ConversationEventTypes.SubAgentRunTimedOut
            or ConversationEventTypes.SubAgentRunBudgetExhausted
            or ConversationEventTypes.SubAgentRunInterrupted;

    private static LlmConfig CreateLlmConfig() => new()
    {
        Endpoint = "https://example.invalid/v1",
        ModelId = "test-model",
    };

    private static LlmInvocationProfile CreateLlmProfile() => new()
    {
        ProviderId = "test-provider",
        ProfileId = "subagent.conscious",
        ModelId = "test-model",
    };

    private sealed class TestRuntimeExecutionConfigService : IRuntimeExecutionConfigService
    {
        public RuntimeExecutionOptions GetOptions() => new();
    }

    // ─────────────────────────────── 测试替身 ───────────────────────────────

    private sealed class RecordingMessageSystem : IMessageSystem
    {
        private readonly TaskCompletionSource<MessageEnvelope> _sent =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<MessageSendResult> SendAsync(MessageEnvelope envelope, CancellationToken ct = default)
        {
            _sent.TrySetResult(envelope);
            return Task.FromResult(new MessageSendResult
            {
                MessageId = envelope.MessageId,
                RoomId = envelope.RoomId,
                DeliveryIds = ["delivery-1"],
            });
        }

        public async Task<MessageEnvelope> WaitForEnvelopeAsync()
            => await _sent.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    private sealed class CapturingInternalEventBus : IInternalEventBus
    {
        public List<InternalEvent> Published { get; } = [];

        public Task PublishAsync(InternalEvent evt, CancellationToken ct = default)
        {
            Published.Add(evt);
            return Task.CompletedTask;
        }

        public Task<IEventSubscriptionHandle> SubscribeAsync(
            string eventTypePattern,
            Func<InternalEvent, Task> handler,
            CancellationToken ct = default) =>
            Task.FromResult<IEventSubscriptionHandle>(new RecordingSubscriptionHandle(eventTypePattern));

        public Task UnsubscribeAsync(IEventSubscriptionHandle handle) =>
            Task.CompletedTask;
    }

    private sealed class RecordingSubscriptionHandle(string eventTypePattern) : IEventSubscriptionHandle
    {
        public string SubscriptionId { get; } = "sub-1";
        public string EventTypePattern { get; } = eventTypePattern;
        public bool IsActive { get; private set; } = true;
        public void Dispose() => IsActive = false;
    }

    private sealed class RecordingRuntimeAgentDispatcher(RuntimeDispatchResult result) : IRuntimeAgentDispatcher
    {
        public RuntimeDispatchRequest? LastRequest { get; private set; }

        public Task<RuntimeDispatchResult> DispatchAsync(RuntimeDispatchRequest request, CancellationToken ct = default)
        {
            LastRequest = request;
            return Task.FromResult(result with
            {
                SessionId = request.SessionId,
                AgentInstanceId = request.AgentTemplateId,
            });
        }

        public async IAsyncEnumerable<ServerSentEventFrame> DispatchStreamAsync(
            RuntimeDispatchRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            yield return ServerSentEventFrame.Json("done", new { ok = true });
            await Task.CompletedTask;
        }
    }

    private sealed class RecordingSessionStateManager : ISessionStateManager
    {
        private readonly TaskCompletionSource<SubAgentResult> _completed =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<SubAgentResult> WaitForResultAsync()
            => await _completed.Task.WaitAsync(TimeSpan.FromSeconds(5));

        public Task<long> AppendAsync(
            string sessionId,
            string workspaceId,
            ServerSentEventFrame frame,
            CancellationToken ct = default,
            RuntimeTraceContext? trace = null,
            string? component = null,
            string? operation = null) => Task.FromResult(1L);

        public ChannelReader<ServerSentEventFrame>? Subscribe(string sessionId) => null;
        public void Unsubscribe(string sessionId, ChannelReader<ServerSentEventFrame> reader) { }
        public ChannelReader<SessionNotification> SubscribeWorkspace(string workspaceId) =>
            Channel.CreateUnbounded<SessionNotification>().Reader;
        public void UnsubscribeWorkspace(string workspaceId, ChannelReader<SessionNotification> reader) { }
        public Task<SessionState> GetSessionStateAsync(string sessionId, CancellationToken ct = default) =>
            Task.FromResult(SessionState.Completed);

        public Task TrackSubAgentStartAsync(string parentSessionId, SubAgentSpawnInfo info, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task TrackSubAgentCompleteAsync(string subSessionId, SubAgentResult result, CancellationToken ct = default)
        {
            _completed.TrySetResult(result);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<SubAgentStatus>> GetSubAgentsAsync(string sessionId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<SubAgentStatus>>([]);
        public Task<int> GetRunningSubAgentCountAsync(string parentSessionId, CancellationToken ct = default) =>
            Task.FromResult(0);
        public Task MarkStreamCompleteAsync(string sessionId, CancellationToken ct = default) => Task.CompletedTask;
        public Task MarkSessionClosedAsync(string sessionId, CancellationToken ct = default) => Task.CompletedTask;

        public Task<SessionTraceReport> GetTraceReportAsync(string sessionId, bool includeSubAgents = false, CancellationToken ct = default) =>
            Task.FromResult(new SessionTraceReport
            {
                SessionId = sessionId,
                TraceIds = [],
                ComponentTimeline = [],
                LlmCalls = [],
                ToolCalls = [],
                SubAgents = [],
            });

        public void Restore(string sessionId, SessionState state) { }
    }

    private sealed class RecordingSubAgentRunStore : ISubAgentRunStore
    {
        public Task<SubAgentRunHandle> CreateRunAsync(SubAgentRunCreateRequest request, CancellationToken ct = default) =>
            Task.FromResult(new SubAgentRunHandle
            {
                RunId = "run-1",
                ArchivePath = "archive",
            });

        public Task AppendEventAsync(string runId, string eventType, object payload, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task AppendToolAuditAsync(string runId, SubAgentToolAuditEntry entry, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task<SubAgentRunTerminalWriteResult> CompleteRunAsync(
            string runId,
            SubAgentRunCompletion completion,
            CancellationToken ct = default) =>
            Task.FromResult(SubAgentRunTerminalWriteResult.Applied);

        public Task<SubAgentRunArchive?> GetRunArchiveAsync(string runId, CancellationToken ct = default) =>
            Task.FromResult<SubAgentRunArchive?>(null);

        public Task<int> RecoverInterruptedRunsAsync(
            DateTimeOffset startedBeforeUtc,
            int maxRuns,
            CancellationToken ct = default) =>
            Task.FromResult(0);

        public Task<int> ReplayPendingConversationEventsAsync(
            int maxRuns,
            CancellationToken ct = default) =>
            Task.FromResult(0);

        public Task<bool> DeleteRunAsync(string runId, CancellationToken ct = default) =>
            Task.FromResult(false);
    }

    private sealed class RecordingRuntimeActivitySink : IRuntimeActivitySink
    {
        public Task RecordAsync(RuntimeActivity activity, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<RuntimeActivity>> QueryAsync(RuntimeActivityQuery query, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<RuntimeActivity>>([]);
    }

    private sealed class RecordingRuntimeTraceAccessor : IRuntimeTraceAccessor
    {
        public RuntimeTraceContext? Current { get; set; }
    }
}
