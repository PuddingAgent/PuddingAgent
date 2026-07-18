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

[TestClass]
public sealed class SubAgentManagerMessageTests
{
    [TestMethod]
    public async Task SpawnAsync_SendsParentAgentMessage_WhenAsyncSubAgentCompletes()
    {
        var messageSystem = new RecordingMessageSystem();
        var dispatcher = new RecordingRuntimeAgentDispatcher();
        var services = new ServiceCollection()
            .AddSingleton<IMessageSystem>(messageSystem)
            .AddSingleton<IRuntimeAgentDispatcher>(dispatcher)
            .AddSingleton<IRuntimeExecutionConfigService>(new TestRuntimeExecutionConfigService())
            .BuildServiceProvider();

        var manager = new SubAgentManager(
            new RecordingSessionStateManager(),
            services,
            new RecordingInternalEventBus(),
            new RecordingSubAgentRunStore(),
            NullLogger<SubAgentManager>.Instance,
            new RecordingRuntimeActivitySink(),
            new RecordingRuntimeTraceAccessor(),
            services.GetRequiredService<IRuntimeExecutionConfigService>());

        var result = await manager.SpawnAsync(new SubAgentSpawnRequest
        {
            ParentSessionId = "parent-session",
            ParentAgentId = "agent-parent",
            WorkspaceId = "default",
            TaskDescription = "Return child result.",
            TemplateId = "workspace-task-agent",
            LlmConfig = CreateLlmConfig(),
            LlmProfile = CreateLlmProfile(),
        });

        Assert.IsTrue(result.Success);
        var envelope = await messageSystem.WaitForEnvelopeAsync();

        Assert.AreEqual(MessageEndpointKinds.Agent, envelope.From.Kind);
        Assert.AreEqual(result.SubSessionId, envelope.From.Id);
        Assert.AreEqual(MessageAudiences.Direct, envelope.Audience);
        Assert.AreEqual(MessageVisibilities.System, envelope.Visibility);
        Assert.AreEqual("parent-session", envelope.ConversationId);
        Assert.HasCount(1, envelope.To);
        Assert.AreEqual(MessageEndpointKinds.Agent, envelope.To[0].Kind);
        Assert.AreEqual("agent-parent", envelope.To[0].Id);
        Assert.AreEqual("subagent_result", envelope.Metadata["intent"]);
        Assert.AreEqual("true", envelope.Metadata["requires_response"]);
        Assert.AreEqual("subagent_result", envelope.Metadata["message_type"]);
        Assert.AreEqual("1", envelope.Metadata["pudding_message_version"]);
        Assert.AreEqual(result.SubSessionId, envelope.Metadata["sub_agent_id"]);
        Assert.AreEqual("completed", envelope.Metadata["subagent_status"], envelope.Content);
        StringAssert.Contains(envelope.Content, "\"schema\": \"pudding-message\"");
        StringAssert.Contains(envelope.Content, "\"message_type\": \"subagent_result\"");
        StringAssert.Contains(envelope.Content, "\"format\": \"text/markdown\"");
        StringAssert.Contains(envelope.Content, "child ok");
        Assert.IsNotNull(dispatcher.LastRequest);
        Assert.AreEqual("test-provider", dispatcher.LastRequest!.LlmProfile?.ProviderId);
        Assert.AreEqual("subagent.conscious", dispatcher.LastRequest.LlmProfile?.ProfileId);
        Assert.AreEqual("test-model", dispatcher.LastRequest.LlmProfile?.ModelId);
        Assert.AreEqual("test-model", dispatcher.LastRequest.LlmConfig?.ModelId);
    }

    [TestMethod]
    public async Task SpawnAsync_PropagatesToolFailureDiagnostics_ToParentAgentMessage()
    {
        var messageSystem = new RecordingMessageSystem();
        var services = new ServiceCollection()
            .AddSingleton<IMessageSystem>(messageSystem)
            .AddSingleton<IRuntimeAgentDispatcher>(new RecordingRuntimeAgentDispatcher(new RuntimeDispatchResult
            {
                SessionId = "will-be-overwritten-by-dispatcher",
                AgentInstanceId = "workspace-task-agent",
                IsSuccess = false,
                ErrorMessage = "shell: Command timed out after 30 seconds.",
                ExecutionState = AgentExecutionState.Failed,
                ToolFailureCount = 1,
                ToolOutputTruncatedCount = 1,
                ToolOutputChars = 100_088,
                ToolFailureSummary = "shell: Command timed out after 30 seconds.",
            }))
            .AddSingleton<IRuntimeExecutionConfigService>(new TestRuntimeExecutionConfigService())
            .BuildServiceProvider();

        var manager = new SubAgentManager(
            new RecordingSessionStateManager(),
            services,
            new RecordingInternalEventBus(),
            new RecordingSubAgentRunStore(),
            NullLogger<SubAgentManager>.Instance,
            new RecordingRuntimeActivitySink(),
            new RecordingRuntimeTraceAccessor(),
            services.GetRequiredService<IRuntimeExecutionConfigService>());

        var result = await manager.SpawnAsync(new SubAgentSpawnRequest
        {
            ParentSessionId = "parent-session",
            ParentAgentId = "agent-parent",
            WorkspaceId = "default",
            TaskDescription = "Return child result.",
            TemplateId = "workspace-task-agent",
            LlmConfig = CreateLlmConfig(),
            LlmProfile = CreateLlmProfile(),
        });

        Assert.IsTrue(result.Success);
        var envelope = await messageSystem.WaitForEnvelopeAsync();

        Assert.AreEqual("subagent_result", envelope.Metadata["intent"]);
        Assert.AreEqual("failed", envelope.Metadata["subagent_status"]);
        Assert.AreEqual("1", envelope.Metadata["tool_failure_count"], envelope.Content);
        Assert.AreEqual("1", envelope.Metadata["tool_output_truncated_count"]);
        Assert.AreEqual("100088", envelope.Metadata["tool_output_chars"]);
        Assert.AreEqual("shell: Command timed out after 30 seconds.", envelope.Metadata["tool_failure_summary"]);
        StringAssert.Contains(envelope.Content, "\"subagent_status\": \"failed\"");
        StringAssert.Contains(envelope.Content, "\"tool_failure_count\": \"1\"");
        StringAssert.Contains(envelope.Content, "Command timed out after 30 seconds.");
    }

    [TestMethod]
    public async Task ExecuteSyncAsync_RejectsMismatchedLlmRoute_BeforeDispatch()
    {
        var dispatcher = new RecordingRuntimeAgentDispatcher();
        var manager = CreateManager(dispatcher);

        var error = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            manager.ExecuteSyncAsync(new SubAgentSpawnRequest
            {
                ParentSessionId = "parent-session",
                ParentAgentId = "agent-parent",
                WorkspaceId = "default",
                TaskDescription = "Must not dispatch.",
                TemplateId = "workspace-task-agent",
                LlmConfig = CreateLlmConfig() with { ModelId = "other-model" },
                LlmProfile = CreateLlmProfile(),
            }));

        StringAssert.Contains(error.Message, "does not match config model");
        Assert.IsNull(dispatcher.LastRequest);
    }

    [TestMethod]
    public async Task ExecuteSyncAsync_RejectsMissingLlmProfile_BeforeDispatch()
    {
        var dispatcher = new RecordingRuntimeAgentDispatcher();
        var manager = CreateManager(dispatcher);

        var error = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            manager.ExecuteSyncAsync(new SubAgentSpawnRequest
            {
                ParentSessionId = "parent-session",
                ParentAgentId = "agent-parent",
                WorkspaceId = "default",
                TaskDescription = "Must not dispatch.",
                TemplateId = "workspace-task-agent",
                LlmConfig = CreateLlmConfig(),
                LlmProfile = null!,
            }));

        StringAssert.Contains(error.Message, "missing its LLM profile snapshot");
        Assert.IsNull(dispatcher.LastRequest);
    }

    private static SubAgentManager CreateManager(RecordingRuntimeAgentDispatcher dispatcher)
    {
        var services = new ServiceCollection()
            .AddSingleton<IRuntimeAgentDispatcher>(dispatcher)
            .BuildServiceProvider();
        return new SubAgentManager(
            new RecordingSessionStateManager(),
            services,
            new RecordingInternalEventBus(),
            new RecordingSubAgentRunStore(),
            NullLogger<SubAgentManager>.Instance,
            new RecordingRuntimeActivitySink(),
            new RecordingRuntimeTraceAccessor(),
            new TestRuntimeExecutionConfigService());
    }

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

    private sealed class RecordingRuntimeAgentDispatcher : IRuntimeAgentDispatcher
    {
        private readonly RuntimeDispatchResult? _result;
        public RuntimeDispatchRequest? LastRequest { get; private set; }

        public RecordingRuntimeAgentDispatcher(RuntimeDispatchResult? result = null)
        {
            _result = result;
        }

        public Task<RuntimeDispatchResult> DispatchAsync(RuntimeDispatchRequest request, CancellationToken ct = default)
        {
            LastRequest = request;
            var result = _result ?? new RuntimeDispatchResult
            {
                SessionId = request.SessionId,
                AgentInstanceId = request.AgentTemplateId,
                IsSuccess = true,
                ReplyText = "child ok",
                ExecutionState = AgentExecutionState.Completed,
            };

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
        public Task<long> AppendAsync(
            string sessionId,
            string workspaceId,
            ServerSentEventFrame frame,
            CancellationToken ct = default,
            RuntimeTraceContext? trace = null,
            string? component = null,
            string? operation = null) => Task.FromResult(1L);

        public Task<SessionEventPage> GetEventsAsync(
            string sessionId,
            long? fromSequence = null,
            int limit = 50,
            CancellationToken ct = default) =>
            Task.FromResult(new SessionEventPage
            {
                Events = [],
                HasMore = false,
                MinSequence = 0,
                MaxSequence = 0,
                TotalCount = 0,
            });

        public Task<long> GetEventCountAfterAsync(string sessionId, long afterSequence, CancellationToken ct = default) =>
            Task.FromResult(0L);

        public ChannelReader<ServerSentEventFrame>? Subscribe(string sessionId) => null;
        public void Unsubscribe(string sessionId, ChannelReader<ServerSentEventFrame> reader) { }
        public ChannelReader<SessionNotification> SubscribeWorkspace(string workspaceId) =>
            Channel.CreateUnbounded<SessionNotification>().Reader;
        public void UnsubscribeWorkspace(string workspaceId, ChannelReader<SessionNotification> reader) { }
        public Task<SessionState> GetSessionStateAsync(string sessionId, CancellationToken ct = default) =>
            Task.FromResult(SessionState.Completed);
        public Task TrackSubAgentStartAsync(string parentSessionId, SubAgentSpawnInfo info, CancellationToken ct = default) =>
            Task.CompletedTask;
        public Task TrackSubAgentCompleteAsync(string subSessionId, SubAgentResult result, CancellationToken ct = default) =>
            Task.CompletedTask;
        public Task<IReadOnlyList<SubAgentStatus>> GetSubAgentsAsync(string sessionId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<SubAgentStatus>>([]);
        public Task<int> GetRunningSubAgentCountAsync(string parentSessionId, CancellationToken ct = default) =>
            Task.FromResult(0);
        public Task<SessionReplayResult> ReplaySessionAsync(
            string sessionId,
            long? fromSequenceNum = null,
            int limit = 200,
            CancellationToken ct = default) =>
            Task.FromResult(new SessionReplayResult
            {
                SessionId = sessionId,
                CurrentState = "completed",
                Events = [],
                TotalEventCount = 0,
                HasMore = false,
                SubAgents = [],
            });
        public Task MarkStreamCompleteAsync(string sessionId, CancellationToken ct = default) => Task.CompletedTask;
        public Task MarkSessionClosedAsync(string sessionId, CancellationToken ct = default) => Task.CompletedTask;
        public Task<SessionConsistencyReport> CheckConsistencyAsync(string sessionId, CancellationToken ct = default) =>
            Task.FromResult(new SessionConsistencyReport
            {
                SessionId = sessionId,
                IsConsistent = true,
            });
        public Task<SessionTraceReport> GetTraceReportAsync(string sessionId, CancellationToken ct = default) =>
            Task.FromResult(new SessionTraceReport
            {
                SessionId = sessionId,
                TraceIds = [],
                ComponentTimeline = [],
                LlmCalls = [],
                ToolCalls = [],
                SubAgents = [],
            });
        public Task<long> GetLatestSequenceNumAsync(string sessionId, CancellationToken ct = default) =>
            Task.FromResult(0L);
        public void Restore(string sessionId, SessionState state) { }
    }

    private sealed class RecordingInternalEventBus : IInternalEventBus
    {
        public Task PublishAsync(InternalEvent evt, CancellationToken ct = default) => Task.CompletedTask;

        public Task<IEventSubscriptionHandle> SubscribeAsync(
            string eventTypePattern,
            Func<InternalEvent, Task> handler,
            CancellationToken ct = default) =>
            Task.FromResult<IEventSubscriptionHandle>(new RecordingSubscriptionHandle(eventTypePattern));

        public Task UnsubscribeAsync(IEventSubscriptionHandle handle) => Task.CompletedTask;
    }

    private sealed class RecordingSubscriptionHandle(string eventTypePattern) : IEventSubscriptionHandle
    {
        public string SubscriptionId { get; } = "sub-1";
        public string EventTypePattern { get; } = eventTypePattern;
        public bool IsActive { get; private set; } = true;
        public void Dispose() => IsActive = false;
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
