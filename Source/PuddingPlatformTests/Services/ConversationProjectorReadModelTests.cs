using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using PuddingCode.Platform;
using PuddingConversationReadModel;
using PuddingPlatform.Data;
using PuddingPlatform.Data.Entities;
using PuddingPlatform.Services;
using PuddingPlatform.Services.AgentChat;

namespace PuddingPlatformTests.Services;

/// <summary>
/// 阶段 C2b：ConversationProjector 事务改造端到端测试（真实 SQLite + fake event store）：
/// 消息物化、读模型摘要/read_state、checkpoint 同事务提交；失败回滚；重放幂等；子 run 隔离；旧库 partial 基线。
/// </summary>
[TestClass]
public sealed class ConversationProjectorReadModelTests
{
    [TestMethod]
    public async Task ProjectAsync_CommitsMessagesReadModelAndCheckpointAtomically()
    {
        await using var temp = TempProjectorDb.Create();
        await using var db = new PlatformDbContext(temp.Options);
        await db.Database.EnsureCreatedAsync();

        var store = FakeStore(
            Event(1, ConversationEventTypes.TurnStarted, runId: "run-1"),
            Event(2, ConversationEventTypes.MessageContentAppended, payloadJson: """{"delta":"hello "}"""),
            Event(3, ConversationEventTypes.TurnCompleted, messageId: "assistant-msg-1", payloadJson: """{"reply":"hi"}"""));

        var projector = NewProjector(temp, store);
        var result = await projector.ProjectAsync("conv-1");

        Assert.IsNull(result.Error);
        Assert.AreEqual(1, result.ProjectedCount);
        Assert.IsFalse(result.HasMore);

        await using var verify = new PlatformDbContext(temp.Options);
        var message = await verify.ChatMessages.SingleAsync();
        Assert.AreEqual("hi", message.Content);
        Assert.AreEqual("assistant-msg-1", message.MessageId);
        Assert.AreEqual("turn-1", message.TurnId);

        var turn = await verify.ConversationTurnSummaries.SingleAsync();
        Assert.AreEqual((int)TurnTerminalState.Completed, turn.TerminalState);
        Assert.IsTrue(turn.HasAssistantBody);
        Assert.AreEqual("assistant-msg-1", turn.AssistantMessageId);
        Assert.AreEqual("hello ", turn.ActivePreview);
        Assert.AreEqual(1, turn.ContentAppendCount);
        Assert.AreEqual(3L, turn.LastSequence);

        var readState = await verify.ConversationReadStates.SingleAsync();
        Assert.AreEqual((int)ReadModelInitializationState.Ready, readState.InitializationState);
        Assert.AreEqual("turn-1", readState.CurrentTurnId);
        Assert.IsTrue(readState.Revision > 0);

        var checkpoint = await verify.ConversationProjectionCheckpoints.SingleAsync();
        Assert.AreEqual(3L, checkpoint.ProjectedThrough);
    }

    [TestMethod]
    public async Task ProjectAsync_ReplayAfterCheckpoint_DoesNotDuplicate()
    {
        await using var temp = TempProjectorDb.Create();
        await using (var db = new PlatformDbContext(temp.Options))
        {
            await db.Database.EnsureCreatedAsync();
        }

        var store = FakeStore(
            Event(1, ConversationEventTypes.TurnStarted, runId: "run-1"),
            Event(2, ConversationEventTypes.TurnCompleted, messageId: "assistant-msg-1", payloadJson: """{"reply":"hi"}"""));
        var projector = NewProjector(temp, store);

        var first = await projector.ProjectAsync("conv-1");
        var second = await projector.ProjectAsync("conv-1");

        Assert.IsNull(first.Error);
        Assert.AreEqual(1, first.ProjectedCount);
        Assert.IsNull(second.Error);
        Assert.AreEqual(0, second.ProjectedCount);

        await using var verify = new PlatformDbContext(temp.Options);
        Assert.AreEqual(1, await verify.ChatMessages.CountAsync());
        Assert.AreEqual(1, await verify.ConversationTurnSummaries.CountAsync());
        Assert.AreEqual(2L, (await verify.ConversationProjectionCheckpoints.SingleAsync()).ProjectedThrough);
    }

    [TestMethod]
    public async Task ProjectAsync_CheckpointWriteFailure_RollsBackMessagesAndReadModel()
    {
        await using var temp = TempProjectorDb.Create();
        await using (var db = new PlatformDbContext(temp.Options))
        {
            await db.Database.EnsureCreatedAsync();
        }

        await CreateCheckpointFailureTriggerAsync(temp.Options);

        var store = FakeStore(
            Event(1, ConversationEventTypes.TurnStarted, runId: "run-1"),
            Event(2, ConversationEventTypes.TurnCompleted, messageId: "assistant-msg-1", payloadJson: """{"reply":"hi"}"""));
        var projector = NewProjector(temp, store);

        var result = await projector.ProjectAsync("conv-1");

        Assert.IsNotNull(result.Error, "injected checkpoint failure must surface as projection error");
        await using var verify = new PlatformDbContext(temp.Options);
        Assert.AreEqual(0, await verify.ChatMessages.CountAsync());
        Assert.AreEqual(0, await verify.ConversationTurnSummaries.CountAsync());
        Assert.AreEqual(0, await verify.ConversationReadStates.CountAsync());
        Assert.AreEqual(0, await verify.ConversationProjectionCheckpoints.CountAsync());
    }

    [TestMethod]
    public async Task ProjectAsync_ChildRunContentDoesNotPolluteParentSummary()
    {
        await using var temp = TempProjectorDb.Create();
        await using (var db = new PlatformDbContext(temp.Options))
        {
            await db.Database.EnsureCreatedAsync();
        }

        var store = FakeStore(
            Event(1, ConversationEventTypes.TurnStarted, runId: "run-1"),
            Event(2, ConversationEventTypes.SubAgentRunCreated,
                runId: "child-run-1",
                producerComponent: "subagent.runtime",
                payloadJson: """{"sub_agent_id":"sub-1"}"""),
            Event(3, ConversationEventTypes.MessageContentAppended,
                runId: "child-run-1",
                producerComponent: "subagent.runtime",
                payloadJson: """{"delta":"child text"}"""),
            Event(4, ConversationEventTypes.TurnCompleted, messageId: "assistant-msg-1", payloadJson: """{"reply":"parent"}"""));
        var projector = NewProjector(temp, store);

        var result = await projector.ProjectAsync("conv-1");

        Assert.IsNull(result.Error);
        await using var verify = new PlatformDbContext(temp.Options);
        var turn = await verify.ConversationTurnSummaries.SingleAsync();
        Assert.AreEqual(0, turn.ContentAppendCount, "child-run content must not be counted on the parent turn");
        Assert.AreEqual(string.Empty, turn.ActivePreview);
        Assert.AreEqual(1, turn.SubAgentRunCount);
        Assert.IsTrue(turn.HasAssistantBody);
        Assert.AreEqual("parent", (await verify.ChatMessages.SingleAsync()).Content);
    }

    [TestMethod]
    public async Task ProjectAsync_FailedTurn_DoesNotMaterializeAssistantMessage()
    {
        await using var temp = TempProjectorDb.Create();
        await using (var db = new PlatformDbContext(temp.Options))
        {
            await db.Database.EnsureCreatedAsync();
        }

        var store = FakeStore(
            Event(1, ConversationEventTypes.TurnStarted, runId: "run-1"),
            Event(2, ConversationEventTypes.TurnFailed, payloadJson: """{"errorCode":"runtime_execution_failed"}"""));
        var projector = NewProjector(temp, store);

        var result = await projector.ProjectAsync("conv-1");

        Assert.IsNull(result.Error);
        Assert.AreEqual(0, result.ProjectedCount);
        await using var verify = new PlatformDbContext(temp.Options);
        Assert.AreEqual(0, await verify.ChatMessages.CountAsync());
        var turn = await verify.ConversationTurnSummaries.SingleAsync();
        Assert.AreEqual((int)TurnTerminalState.Failed, turn.TerminalState);
        Assert.IsFalse(turn.HasAssistantBody);
        Assert.IsNull(turn.AssistantMessageId);
        Assert.AreEqual(2L, (await verify.ConversationProjectionCheckpoints.SingleAsync()).ProjectedThrough);
    }

    [TestMethod]
    public async Task ProjectAsync_LegacyCheckpointWithoutReadState_StartsWithPartialBaseline()
    {
        await using var temp = TempProjectorDb.Create();
        await using (var db = new PlatformDbContext(temp.Options))
        {
            await db.Database.EnsureCreatedAsync();
            db.ConversationProjectionCheckpoints.Add(new ConversationProjectionCheckpointEntity
            {
                ConversationId = "conv-1",
                ProjectedThrough = 100,
                UpdatedAt = DateTimeOffset.UtcNow.ToString("O"),
            });
            await db.SaveChangesAsync();
        }

        var store = FakeStore(
            Event(101, ConversationEventTypes.TurnStarted, runId: "run-1"),
            Event(102, ConversationEventTypes.TurnCompleted, messageId: "assistant-msg-1", payloadJson: """{"reply":"hi"}"""));
        var projector = NewProjector(temp, store);

        var result = await projector.ProjectAsync("conv-1");

        Assert.IsNull(result.Error);
        Assert.AreEqual(1, result.ProjectedCount);
        await using var verify = new PlatformDbContext(temp.Options);
        var readState = await verify.ConversationReadStates.SingleAsync();
        Assert.AreEqual((int)ReadModelInitializationState.Partial, readState.InitializationState);
        Assert.AreEqual(102L, (await verify.ConversationProjectionCheckpoints.SingleAsync()).ProjectedThrough);
        Assert.AreEqual(1, await verify.ChatMessages.CountAsync());
    }

    // ── helpers ──────────────────────────────────────────────────────────

    private static ConversationProjector NewProjector(TempProjectorDb temp, IConversationEventStore store)
    {
        var services = new ServiceCollection().BuildServiceProvider();
        var scopeFactory = services.GetRequiredService<IServiceScopeFactory>();
        var transcriptWriter = new ChatTranscriptWriter(
            scopeFactory,
            NullLogger<ChatTranscriptWriter>.Instance);
        var readModelStore = new ConversationReadModelProjectionStore(
            NullLogger<ConversationReadModelProjectionStore>.Instance);
        return new ConversationProjector(
            scopeFactory,
            temp.DbFactory,
            store,
            transcriptWriter,
            readModelStore,
            NullLogger<ConversationProjector>.Instance);
    }

    private static FakeEventStore FakeStore(params ConversationEvent[] events) => new(events);

    private static ConversationEvent Event(
        long sequence,
        string type,
        string turnId = "turn-1",
        string? runId = "run-1",
        string? messageId = null,
        string? producerComponent = null,
        string payloadJson = "{}")
        => new()
        {
            EventId = $"evt-{sequence}",
            ConversationId = "conv-1",
            Sequence = sequence,
            WorkspaceId = "ws",
            TurnId = turnId,
            RunId = runId,
            MessageId = messageId,
            Type = type,
            SchemaVersion = 1,
            OccurredAt = DateTimeOffset.Parse("2026-10-18T00:00:00Z").AddSeconds(sequence),
            CommittedAt = DateTimeOffset.Parse("2026-10-18T00:00:00Z").AddSeconds(sequence),
            Payload = JsonDocument.Parse(payloadJson).RootElement.Clone(),
            ProducerComponent = producerComponent,
        };

    private static async Task CreateCheckpointFailureTriggerAsync(DbContextOptions<PlatformDbContext> options)
    {
        await using var db = new PlatformDbContext(options);
        var connection = db.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open)
            await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            CREATE TRIGGER fail_checkpoint_insert
            BEFORE INSERT ON conversation_projection_checkpoints
            BEGIN
                SELECT RAISE(ABORT, 'injected checkpoint failure');
            END;
            """;
        await command.ExecuteNonQueryAsync();
    }

    private sealed class FakeEventStore(params ConversationEvent[] events) : IConversationEventStore
    {
        private readonly ConversationEvent[] _events = events
            .OrderBy(item => item.Sequence)
            .ToArray();

        public Task<EventPage> ReadForwardAsync(
            string conversationId,
            long afterExclusive,
            long? throughInclusive,
            int limit,
            CancellationToken ct)
        {
            var candidates = _events
                .Where(item => item.ConversationId == conversationId
                               && item.Sequence > afterExclusive
                               && (throughInclusive is null || item.Sequence <= throughInclusive))
                .ToArray();
            var page = candidates.Take(limit).ToArray();
            var hasMore = candidates.Length > page.Length;
            var nextCursor = page.Length == 0 ? (long?)null : page[^1].Sequence;
            return Task.FromResult(new EventPage(page, nextCursor, hasMore));
        }

        public Task<AppendResult> AppendAsync(
            string conversationId,
            long expectedVersion,
            IReadOnlyList<NewConversationEvent> events,
            EventWriteCondition condition,
            CancellationToken ct)
            => throw new NotSupportedException();

        public Task<EventPage> ReadBackwardAsync(
            string conversationId,
            long beforeExclusive,
            int limit,
            CancellationToken ct)
            => throw new NotSupportedException();

        public Task<EventPage> ReadByTypePrefixBackwardAsync(
            string conversationId,
            string typePrefix,
            long beforeExclusive,
            int limit,
            CancellationToken ct)
            => throw new NotSupportedException();

        public Task<EventBounds> GetBoundsAsync(string conversationId, CancellationToken ct)
            => Task.FromResult(new EventBounds(null, null));

        public Task EnsureTablesAsync(CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class TempProjectorDb : IAsyncDisposable
    {
        private TempProjectorDb(string rootPath)
        {
            RootPath = rootPath;
            Options = new DbContextOptionsBuilder<PlatformDbContext>()
                .UseSqlite($"Data Source={System.IO.Path.Combine(rootPath, "platform.db")}")
                .Options;
            DbFactory = new PlatformDbContextFactory(Options);
        }

        public string RootPath { get; }
        public DbContextOptions<PlatformDbContext> Options { get; }
        public PlatformDbContextFactory DbFactory { get; }

        public static TempProjectorDb Create()
        {
            var root = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "pudding-projection-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            return new TempProjectorDb(root);
        }

        public ValueTask DisposeAsync()
        {
            SqliteConnection.ClearAllPools();
            try
            {
                if (Directory.Exists(RootPath))
                    Directory.Delete(RootPath, recursive: true);
            }
            catch (IOException)
            {
                // best-effort cleanup of temp test data outside the repo
            }

            return ValueTask.CompletedTask;
        }
    }
}
