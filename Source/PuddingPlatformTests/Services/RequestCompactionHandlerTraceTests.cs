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

        var started = store.Appended.Single(
            item => item.Event.Type == ConversationEventTypes.ContextCompactionStarted);
        var completedSource = store.Appended.Single(
            item => item.Event.Type == ConversationEventTypes.ContextCompactionCompleted
                    && item.ConversationId == "conversation-1");
        var completedSuccessor = store.Appended.Single(
            item => item.Event.Type == ConversationEventTypes.ContextCompactionCompleted
                    && item.ConversationId == "successor-conversation-1");

        Assert.AreEqual("trace-abc-123", started.Event.TraceId);
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

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => handler.HandleAsync(command, CancellationToken.None));

        var started = store.Appended.Single(
            item => item.Event.Type == ConversationEventTypes.ContextCompactionStarted);
        var failed = store.Appended.Single(
            item => item.Event.Type == ConversationEventTypes.ContextCompactionFailed);

        Assert.AreEqual("trace-fail-1", started.Event.TraceId);
        Assert.AreEqual("trace-fail-1", failed.Event.TraceId);
        Assert.AreEqual(2, store.Appended.Count);
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

        Assert.AreEqual(6, store.Appended.Count);
        Assert.IsTrue(
            store.Appended.All(item => item.Event.TraceId == "trace-stable-1"),
            "TraceId 必须原样透传，Handler 不得在重复调用时重生成。");
        Assert.IsTrue(
            compaction.Requests.All(request => request.TraceId == "trace-stable-1"),
            "压缩服务调用必须收到与事件相同的 TraceId。");
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
                SummaryMarkdown: "markdown"));
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
