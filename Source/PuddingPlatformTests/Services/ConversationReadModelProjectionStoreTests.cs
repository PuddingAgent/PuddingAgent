using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using PuddingConversationReadModel;
using PuddingPlatform.Data;
using PuddingPlatform.Data.Entities;
using PuddingPlatform.Services.AgentChat;

namespace PuddingPlatformTests.Services;

/// <summary>
/// 阶段 C2：读模型持久化适配（load affected turns / persist 摘要 + read_state + checkpoint 同事务）。
/// 使用真实 SQLite 临时库验证往返、部分基线、upsert、回滚与损坏 JSON 的 fail-closed。
/// </summary>
[TestClass]
public sealed class ConversationReadModelProjectionStoreTests
{
    [TestMethod]
    public async Task LoadForBatch_NewConversation_IsReadyEmpty()
    {
        await using var temp = TempPlatformDb.Create();
        await using var db = new PlatformDbContext(temp.Options);
        await db.Database.EnsureCreatedAsync();

        var store = NewStore();
        var state = await store.LoadForBatchAsync(db, "conv-1", projectedThrough: 0, [], CancellationToken.None);

        Assert.AreEqual(ReadModelInitializationState.Ready, state.Initialization);
        Assert.AreEqual(0L, state.ProjectedThroughSequence);
        Assert.IsEmpty(state.Turns);
        Assert.IsNull(state.Compaction);
        Assert.IsEmpty(state.SubAgentRuns);
    }

    [TestMethod]
    public async Task LoadForBatch_LegacyCheckpointWithoutReadState_IsPartial()
    {
        await using var temp = TempPlatformDb.Create();
        await using var db = new PlatformDbContext(temp.Options);
        await db.Database.EnsureCreatedAsync();

        var store = NewStore();
        var state = await store.LoadForBatchAsync(db, "conv-1", projectedThrough: 42, [], CancellationToken.None);

        Assert.AreEqual(ReadModelInitializationState.Partial, state.Initialization);
        Assert.AreEqual(42L, state.ProjectedThroughSequence);
        Assert.IsEmpty(state.Turns);
    }

    [TestMethod]
    public async Task PersistAndLoad_RoundTripsTurnReadStateCompactionAndSubAgentRuns()
    {
        await using var temp = TempPlatformDb.Create();
        await using var db = new PlatformDbContext(temp.Options);
        await db.Database.EnsureCreatedAsync();

        var store = NewStore();
        var state = Reduce(
            new ConversationReadModelState { ConversationId = "conv-1" },
            Event(1, ConversationEventKind.TurnStarted, turnId: "turn-1", runId: "run-1"),
            Event(2, ConversationEventKind.MessageContentAppended, turnId: "turn-1", textDelta: "hello "),
            Event(3, ConversationEventKind.MessageCompleted, turnId: "turn-1", messageId: "assistant-msg-1"),
            Event(4, ConversationEventKind.SubAgentRunCreated, turnId: "turn-1", runId: "child-run-1", subAgentId: "sub-1"),
            Event(5, ConversationEventKind.TurnCompleted, turnId: "turn-1"),
            Event(6, ConversationEventKind.ContextCompactionStarted, runId: "compaction-run-1"));

        Assert.AreEqual(TurnTerminalState.Completed, state.Turns["turn-1"].TerminalState);
        Assert.IsTrue(state.Turns["turn-1"].HasAssistantBody);
        Assert.AreEqual("hello ", state.Turns["turn-1"].ActivePreview);

        var now = DateTimeOffset.Parse("2026-10-18T01:00:00Z");
        await using (var tx = await db.Database.BeginTransactionAsync())
        {
            await store.PersistAsync(db, state, ["turn-1"], checkpointSequence: 6, now, CancellationToken.None);
            await db.SaveChangesAsync();
            await tx.CommitAsync();
        }

        Assert.AreEqual(1, await db.ConversationReadStates.CountAsync());
        Assert.AreEqual(1, await db.ConversationTurnSummaries.CountAsync());
        var checkpoint = await db.ConversationProjectionCheckpoints.SingleAsync();
        Assert.AreEqual(6L, checkpoint.ProjectedThrough);

        var loaded = await store.LoadForBatchAsync(db, "conv-1", projectedThrough: 6, ["turn-1"], CancellationToken.None);

        Assert.AreEqual(ReadModelInitializationState.Ready, loaded.Initialization);
        Assert.AreEqual(6L, loaded.ProjectedThroughSequence);
        Assert.AreEqual("turn-1", loaded.CurrentTurnId);
        Assert.AreEqual("run-1", loaded.CurrentRootRunId);
        Assert.AreEqual(TurnTerminalState.Completed, loaded.CurrentRootTerminalState);
        Assert.IsTrue(loaded.Compaction?.IsRunning);
        Assert.AreEqual("compaction-run-1", loaded.Compaction?.RunId);
        Assert.HasCount(1, loaded.SubAgentRuns);
        Assert.AreEqual("child-run-1", loaded.SubAgentRuns[0].RunId);
        Assert.AreEqual("sub-1", loaded.SubAgentRuns[0].SubAgentId);

        var turn = loaded.Turns["turn-1"];
        Assert.AreEqual(TurnTerminalState.Completed, turn.TerminalState);
        Assert.IsTrue(turn.HasAssistantBody);
        Assert.AreEqual("assistant-msg-1", turn.AssistantMessageId);
        Assert.AreEqual("hello ", turn.ActivePreview);
        Assert.AreEqual(1, turn.Process.ContentAppendCount);
        Assert.AreEqual(1, turn.Process.SubAgentRunCount);
        Assert.AreEqual(5L, turn.LastSequence);
    }

    [TestMethod]
    public async Task LoadForBatch_OnlyLoadsAffectedTurns()
    {
        await using var temp = TempPlatformDb.Create();
        await using var db = new PlatformDbContext(temp.Options);
        await db.Database.EnsureCreatedAsync();

        var store = NewStore();
        var state = Reduce(
            new ConversationReadModelState { ConversationId = "conv-1" },
            Event(1, ConversationEventKind.TurnStarted, turnId: "turn-1", runId: "run-1"),
            Event(2, ConversationEventKind.TurnCompleted, turnId: "turn-1"),
            Event(3, ConversationEventKind.TurnStarted, turnId: "turn-2", runId: "run-2"),
            Event(4, ConversationEventKind.TurnCompleted, turnId: "turn-2"));

        await using (var tx = await db.Database.BeginTransactionAsync())
        {
            await store.PersistAsync(db, state, ["turn-1", "turn-2"], 4, DateTimeOffset.UtcNow, CancellationToken.None);
            await db.SaveChangesAsync();
            await tx.CommitAsync();
        }

        var loaded = await store.LoadForBatchAsync(db, "conv-1", 4, ["turn-2"], CancellationToken.None);

        Assert.HasCount(1, loaded.Turns);
        Assert.IsTrue(loaded.Turns.ContainsKey("turn-2"));
        Assert.IsFalse(loaded.Turns.ContainsKey("turn-1"));
    }

    [TestMethod]
    public async Task Persist_IsUpsertAndUpdatesExistingCheckpoint()
    {
        await using var temp = TempPlatformDb.Create();
        await using var db = new PlatformDbContext(temp.Options);
        await db.Database.EnsureCreatedAsync();

        db.ConversationProjectionCheckpoints.Add(new ConversationProjectionCheckpointEntity
        {
            ConversationId = "conv-1",
            ProjectedThrough = 5,
            UpdatedAt = DateTimeOffset.UtcNow.ToString("O"),
        });
        await db.SaveChangesAsync();

        var store = NewStore();
        var first = Reduce(
            new ConversationReadModelState { ConversationId = "conv-1" },
            Event(1, ConversationEventKind.TurnStarted, turnId: "turn-1", runId: "run-1"));
        await PersistAsync(store, db, first, ["turn-1"], 6);

        var second = Reduce(
            first,
            Event(2, ConversationEventKind.MessageContentAppended, turnId: "turn-1", textDelta: "more"));
        await PersistAsync(store, db, second, ["turn-1"], 7);

        Assert.AreEqual(1, await db.ConversationTurnSummaries.CountAsync());
        Assert.AreEqual(1, await db.ConversationReadStates.CountAsync());
        var checkpoint = await db.ConversationProjectionCheckpoints.SingleAsync();
        Assert.AreEqual(7L, checkpoint.ProjectedThrough);

        var loaded = await store.LoadForBatchAsync(db, "conv-1", 7, ["turn-1"], CancellationToken.None);
        Assert.AreEqual("more", loaded.Turns["turn-1"].ActivePreview);
    }

    [TestMethod]
    public async Task Persist_InsideRolledBackTransaction_WritesNothing()
    {
        await using var temp = TempPlatformDb.Create();
        await using var db = new PlatformDbContext(temp.Options);
        await db.Database.EnsureCreatedAsync();

        var store = NewStore();
        var state = Reduce(
            new ConversationReadModelState { ConversationId = "conv-1" },
            Event(1, ConversationEventKind.TurnStarted, turnId: "turn-1", runId: "run-1"));

        await using (var tx = await db.Database.BeginTransactionAsync())
        {
            await store.PersistAsync(db, state, ["turn-1"], 1, DateTimeOffset.UtcNow, CancellationToken.None);
            await db.SaveChangesAsync();
            await tx.RollbackAsync();
        }

        db.ChangeTracker.Clear();
        Assert.AreEqual(0, await db.ConversationReadStates.CountAsync());
        Assert.AreEqual(0, await db.ConversationTurnSummaries.CountAsync());
        Assert.AreEqual(0, await db.ConversationProjectionCheckpoints.CountAsync());
    }

    [TestMethod]
    public async Task Persist_RoundTripsBoundedSubAgentRunsAndOmittedCount()
    {
        await using var temp = TempPlatformDb.Create();
        await using var db = new PlatformDbContext(temp.Options);
        await db.Database.EnsureCreatedAsync();

        var store = NewStore();
        var initial = Reduce(
            new ConversationReadModelState { ConversationId = "conv-1" },
            Event(1, ConversationEventKind.TurnStarted, turnId: "turn-1", runId: "run-1"),
            Event(2, ConversationEventKind.SubAgentRunCreated, turnId: "turn-1", runId: "child-1", subAgentId: "sub-1"),
            Event(3, ConversationEventKind.SubAgentRunStarted, turnId: "turn-1", runId: "child-1", subAgentId: "sub-1"));

        // 直接构造 omitted 计数，验证持久化行不会丢字段
        var state = initial with { SubAgentRunsOmitted = 5 };
        await PersistAsync(store, db, state, ["turn-1"], 3);

        var loaded = await store.LoadForBatchAsync(db, "conv-1", 3, ["turn-1"], CancellationToken.None);
        Assert.HasCount(1, loaded.SubAgentRuns);
        Assert.AreEqual(5, loaded.SubAgentRunsOmitted);
        Assert.AreEqual(SubAgentRunLifecycleState.Running, loaded.SubAgentRuns[0].State);
    }

    [TestMethod]
    public async Task LoadForBatch_CorruptedSubAgentJson_FailsClosed()
    {
        await using var temp = TempPlatformDb.Create();
        await using var db = new PlatformDbContext(temp.Options);
        await db.Database.EnsureCreatedAsync();

        db.ConversationReadStates.Add(new ConversationReadStateEntity
        {
            ConversationId = "conv-1",
            InitializationState = (int)ReadModelInitializationState.Ready,
            SubAgentRunsJson = "{not-json",
            ReadModelVersion = ConversationReadModelLimits.CurrentReadModelVersion,
            UpdatedAt = DateTimeOffset.UtcNow.ToString("O"),
        });
        await db.SaveChangesAsync();

        var store = NewStore();
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            store.LoadForBatchAsync(db, "conv-1", 1, [], CancellationToken.None));
    }

    // ── helpers ──────────────────────────────────────────────────────────

    private static ConversationReadModelProjectionStore NewStore()
        => new(NullLogger<ConversationReadModelProjectionStore>.Instance);

    private static async Task PersistAsync(
        ConversationReadModelProjectionStore store,
        PlatformDbContext db,
        ConversationReadModelState state,
        IReadOnlyCollection<string> touchedTurnIds,
        long checkpointSequence)
    {
        await using var tx = await db.Database.BeginTransactionAsync();
        await store.PersistAsync(db, state, touchedTurnIds, checkpointSequence, DateTimeOffset.UtcNow, CancellationToken.None);
        await db.SaveChangesAsync();
        await tx.CommitAsync();
    }

    private static ConversationReadModelState Reduce(
        ConversationReadModelState state,
        params ConversationEventInput[] events)
    {
        var normalized = state.ConversationId == "conv-1"
            ? state
            : state with { ConversationId = "conv-1" };
        if (normalized.Turns.Count == 0
            && normalized.ProjectedThroughSequence == 0
            && normalized.Revision == 0)
        {
            normalized = ConversationReadModelReducer.CreateEmpty(normalized.ConversationId);
        }

        return ConversationReadModelReducer.Apply(normalized, events).State;
    }

    private static ConversationEventInput Event(
        long sequence,
        ConversationEventKind kind,
        string? turnId = null,
        string? runId = null,
        string? messageId = null,
        string? subAgentId = null,
        string? textDelta = null)
        => new()
        {
            ConversationId = "conv-1",
            Sequence = sequence,
            EventId = $"evt-{sequence}",
            Kind = kind,
            TurnId = turnId,
            RunId = runId,
            MessageId = messageId,
            SubAgentId = subAgentId,
            TextDelta = textDelta,
        };

    private sealed class TempPlatformDb : IAsyncDisposable
    {
        private TempPlatformDb(string rootPath, string dbPath)
        {
            RootPath = rootPath;
            Options = new DbContextOptionsBuilder<PlatformDbContext>()
                .UseSqlite($"Data Source={dbPath}")
                .Options;
        }

        public string RootPath { get; }
        public DbContextOptions<PlatformDbContext> Options { get; }

        public static TempPlatformDb Create()
        {
            var root = Path.Combine(Path.GetTempPath(), "pudding-rm-proj-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            return new TempPlatformDb(root, Path.Combine(root, "platform.db"));
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
                // best-effort cleanup of test temp data outside the repo
            }

            return ValueTask.CompletedTask;
        }
    }
}
