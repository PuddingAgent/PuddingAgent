using Microsoft.Extensions.Logging.Abstractions;
using PuddingCode.Abstractions;
using PuddingCode.Platform;
using PuddingCode.Runtime;
using PuddingPlatform.Services.Conversation;

namespace PuddingPlatformTests.Services;

/// <summary>
/// P0-4f: RequestCompactionHandler TraceId 透传契约测试。
/// 覆盖：同次压缩 started/completed/failed/successor 与压缩服务调用共用同一 TraceId；
/// handler 只透传 command.TraceId，不生成、不 fallback（null 原样透传）；重复调用不重生成。
/// 幂等性由 CompactionId 承担（EventWriteCondition 业务键），TraceId 仅承担执行链追踪。
/// </summary>
[TestClass]
public sealed class RequestCompactionHandlerTraceTests
{
    [TestMethod]
    public async Task Cancellation_PersistsTerminalBeforeRethrowing()
    {
        var store = new RecordingConversationEventStore();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var handler = CreateHandler(store, new StubCompactionService
        {
            ThrowOnCompact = new OperationCanceledException(cts.Token),
        });
        await Assert.ThrowsAsync<OperationCanceledException>(() => handler.HandleAsync(
            new RequestCompactionCommand("conversation-1", "default", "agent-1",
                ContextCompactionLevel.Full, "manual", "cancelled-1", "admin"), cts.Token));
        Assert.AreEqual(1, store.Appended.Count(e => e.Event.Type == ConversationEventTypes.ContextCompactionFailed));
    }
    [TestMethod]
    public async Task HandleAsync_PreservesTraceId_AcrossStartedCompletedAndSuccessorEvents()
    {
        var store = new RecordingConversationEventStore();
        var compaction = new StubCompactionService();
        var handler = CreateHandler(store, compaction);

        var command = new RequestCompactionCommand(
            ConversationId: "conversation-1",
            WorkspaceId: "default",
            AgentId: "agent-1",
            Level: ContextCompactionLevel.Full,
            Reason: "manual compact",
            CompactionId: "compaction-1",
            UserId: "admin")
        {
            TraceId = "trace-abc-123",
        };

        var result = await handler.HandleAsync(command, CancellationToken.None);

        Assert.AreEqual("successor-conversation-1", result.NewConversationId);
        Assert.AreEqual("trace-abc-123", compaction.LastRequest!.TraceId);

        Assert.IsFalse(store.Appended.Any(item => item.Event.Type == ConversationEventTypes.ContextCompactionStarted));
        var completedSource = store.Appended.Single(
            item => item.Event.Type == ConversationEventTypes.ContextCompactionCompleted
                    && item.ConversationId == "conversation-1");
        var completedSuccessor = store.Appended.Single(
            item => item.Event.Type == ConversationEventTypes.ContextCompactionCompleted
                    && item.ConversationId == "successor-conversation-1");

        Assert.AreEqual("trace-abc-123", completedSource.Event.TraceId);
        Assert.AreEqual("trace-abc-123", completedSuccessor.Event.TraceId);

        CollectionAssert.AreEqual(
            new[] { "trace-abc-123" },
            store.Appended.Select(item => item.Event.TraceId).Distinct().ToArray(),
            "同一次压缩的全部生命周期事件必须共用同一个 TraceId。");
    }

    [TestMethod]
    public async Task HandleAsync_FailedEvent_ReusesSameTraceId()
    {
        var store = new RecordingConversationEventStore();
        var compaction = new StubCompactionService
        {
            ThrowOnCompact = new InvalidOperationException("synthetic compact failure"),
        };
        var handler = CreateHandler(store, compaction);

        var command = new RequestCompactionCommand(
            "conversation-1",
            "default",
            "agent-1",
            ContextCompactionLevel.Full,
            "manual",
            "compaction-fail",
            "admin")
        {
            TraceId = "trace-fail-1",
        };

        await Assert.ThrowsAsync<CompactionRequestException>(
            () => handler.HandleAsync(command, CancellationToken.None));

        Assert.IsFalse(store.Appended.Any(item => item.Event.Type == ConversationEventTypes.ContextCompactionStarted));
        var failed = store.Appended.Single(
            item => item.Event.Type == ConversationEventTypes.ContextCompactionFailed);

        Assert.AreEqual("trace-fail-1", failed.Event.TraceId);
        Assert.AreEqual(1, store.Appended.Count);
    }

    [TestMethod]
    public async Task HandleAsync_NullTraceId_IsPassedThroughWithoutFallbackGeneration()
    {
        var store = new RecordingConversationEventStore();
        var compaction = new StubCompactionService();
        var handler = CreateHandler(store, compaction);

        // 未显式设置 TraceId：历史语义保持 null，Handler 禁止静默生成。
        var command = new RequestCompactionCommand(
            "conversation-1",
            "default",
            "agent-1",
            ContextCompactionLevel.Full,
            "manual",
            "compaction-null",
            "admin");

        await handler.HandleAsync(command, CancellationToken.None);

        Assert.IsNull(compaction.LastRequest!.TraceId);
        Assert.IsTrue(
            store.Appended.All(item => item.Event.TraceId is null),
            "Handler 不得在 command 未携带 trace 时自行生成/fallback trace。");
    }

    [TestMethod]
    public async Task HandleAsync_RepeatedInvocation_DoesNotRegenerateTraceId()
    {
        var store = new RecordingConversationEventStore();
        var compaction = new StubCompactionService();
        var handler = CreateHandler(store, compaction);

        var command = new RequestCompactionCommand(
            "conversation-1",
            "default",
            "agent-1",
            ContextCompactionLevel.Full,
            "manual",
            "compaction-1",
            "admin")
        {
            TraceId = "trace-stable-1",
        };

        await handler.HandleAsync(command, CancellationToken.None);
        await handler.HandleAsync(command, CancellationToken.None);

        Assert.AreEqual(4, store.Appended.Count);
        Assert.IsTrue(
            store.Appended.All(item => item.Event.TraceId == "trace-stable-1"),
            "TraceId 必须原样透传，Handler 不得在重复调用时重生成。");
        Assert.IsTrue(
            compaction.Requests.All(request => request.TraceId == "trace-stable-1"),
            "压缩服务调用必须收到与事件相同的 TraceId。");
    }

    [TestMethod]
    public async Task HandoffFailure_PreservesCommittedSummaryAndCorrelatesHttpWithEvent()
    {
        var store = new RecordingConversationEventStore();
        var compaction = new StubCompactionService();
        var inner = new HttpRequestException("private detail");
        inner.Data["upstream_error_id"] = "92f1268954f4";
        inner.Data["native_error_code"] = 1175;
        inner.Data["atomic_write_attempts"] = 7;
        var handler = CreateHandler(store, compaction, new ThrowingSuccessor(inner));
        var error = await Assert.ThrowsAsync<CompactionRequestException>(() => handler.HandleAsync(
            new RequestCompactionCommand("conversation-1", "default", "agent-1", ContextCompactionLevel.Full,
                "manual", "handoff-1", "admin") { TraceId = "trace-handoff" }, CancellationToken.None));
        Assert.IsTrue(error.Failure.SummaryCommitted);
        Assert.AreEqual("session_handoff", error.Failure.Phase);
        Assert.AreEqual(1175, error.Failure.NativeErrorCode);
        Assert.AreEqual(7, error.Failure.WriteAttempts);
        Assert.AreEqual("92f1268954f4", error.Failure.UpstreamErrorId);
        var failed = store.Appended.Single().Event.Payload.GetProperty("failure");
        Assert.AreEqual(error.Failure.ErrorId, failed.GetProperty("errorId").GetString());
        Assert.AreEqual("trace-handoff", failed.GetProperty("traceId").GetString());
        Assert.IsFalse(failed.GetRawText().Contains("private detail"));
        Assert.HasCount(1, compaction.Requests);
    }

    [TestMethod]
    public async Task NoOp_DoesNotCreateOrRebindAnotherSession()
    {
        var store = new RecordingConversationEventStore();
        var handler = CreateHandler(store, new StubCompactionService { Outcome = ContextCompactionOutcome.SkippedNoCandidate },
            new ThrowingSuccessor(new InvalidOperationException("must not run")));
        var result = await handler.HandleAsync(new RequestCompactionCommand("conversation-1", "default", "agent-1",
            ContextCompactionLevel.Full, "manual", "skip-1", "admin"), CancellationToken.None);
        Assert.AreEqual("conversation-1", result.NewConversationId);
        Assert.AreEqual(ConversationEventTypes.ContextCompactionSkipped, store.Appended.Single().Event.Type);
    }

    private sealed class ThrowingSuccessor(Exception error) : ICompactionSessionSuccessor
    {
        public Task<CompactionSuccessor> CreateAsync(CreateCompactionSuccessorCommand command, CancellationToken ct)
            => throw error;
    }

    private static RequestCompactionHandler CreateHandler(
        RecordingConversationEventStore store,
        StubCompactionService compaction,
        ICompactionSessionSuccessor? successor = null) =>
        new(
            new FixedProfileResolver(),
            compaction,
            successor ?? new FixedSuccessor(),
            store,
            NullLogger<RequestCompactionHandler>.Instance);

    private sealed class FixedProfileResolver : IAgentRuntimeProfileResolver
    {
        public Task<AgentRuntimeProfile> ResolveAsync(
            string workspaceId,
            string agentId,
            CancellationToken ct = default) =>
            Task.FromResult(new AgentRuntimeProfile
            {
                WorkspaceId = workspaceId,
                AgentId = agentId,
                DisplayName = "Test Agent",
                SourceTemplateId = "test-template",
            });
    }

    private sealed class StubCompactionService : IContextCompactionService
    {
        public List<ContextCompactionRequest> Requests { get; } = [];

        public ContextCompactionRequest? LastRequest { get; private set; }

        public Exception? ThrowOnCompact { get; set; }
        public ContextCompactionOutcome Outcome { get; set; } = ContextCompactionOutcome.Applied;

        public Task<ContextCompactionResult> CompactAsync(
            ContextCompactionRequest request,
            CancellationToken ct = default)
        {
            Requests.Add(request);
            LastRequest = request;
            if (ThrowOnCompact is not null)
                throw ThrowOnCompact;

            return Task.FromResult(new ContextCompactionResult(
                request.SessionId,
                SummaryMessageId: "summary-1",
                request.Mode,
                request.Level,
                BeforeTokens: 1200,
                AfterTokens: 240,
                CompactedMessageCount: 8,
                SummaryPreview: "preview",
                SummaryMarkdown: "markdown") { Outcome = Outcome });
        }

        public Task<ContextHealthSnapshot> GetHealthAsync(
            string sessionId,
            CancellationToken ct = default,
            int? contextWindowTokens = null,
            int? maxOutputTokens = null,
            int? maxInputTokens = null,
            int toolCount = 0) =>
            throw new NotSupportedException("This test never calls GetHealthAsync.");
    }

    private sealed class FixedSuccessor : ICompactionSessionSuccessor
    {
        public Task<CompactionSuccessor> CreateAsync(
            CreateCompactionSuccessorCommand command,
            CancellationToken ct) =>
            Task.FromResult(new CompactionSuccessor(
                $"successor-{command.PreviousConversationId}",
                "Compacted successor"));
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
                Appended.Add((conversationId, item));
            return Task.FromResult(new AppendResult(
                Appended.Count,
                Appended.Count,
                events.Count));
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
}
