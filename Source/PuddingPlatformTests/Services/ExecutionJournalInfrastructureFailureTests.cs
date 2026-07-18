using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using PuddingCode.Platform;
using PuddingPlatform.Data;
using PuddingPlatform.Data.Entities;
using PuddingPlatform.Services.Execution;

namespace PuddingPlatformTests.Services;

[TestClass]
public sealed class ExecutionJournalInfrastructureFailureTests
{
    private SqliteConnection _connection = null!;
    private ServiceProvider _provider = null!;
    private RecordingCommittedEventSignal _signal = null!;
    private SqliteExecutionJournal _journal = null!;

    [TestInitialize]
    public async Task InitializeAsync()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        await _connection.OpenAsync();

        var services = new ServiceCollection();
        services.AddDbContextFactory<PlatformDbContext>(
            options => options.UseSqlite(_connection),
            ServiceLifetime.Singleton);
        _provider = services.BuildServiceProvider();

        await using var db = await _provider
            .GetRequiredService<IDbContextFactory<PlatformDbContext>>()
            .CreateDbContextAsync();
        await db.Database.EnsureCreatedAsync();

        _signal = new RecordingCommittedEventSignal();
        _journal = new SqliteExecutionJournal(
            _provider.GetRequiredService<IServiceScopeFactory>(),
            _signal,
            NullLogger<SqliteExecutionJournal>.Instance);
    }

    [TestCleanup]
    public async Task CleanupAsync()
    {
        await _provider.DisposeAsync();
        await _connection.DisposeAsync();
    }

    [TestMethod]
    public async Task TryCommitInfrastructureFailure_ClosesAllFactsAndPreservesPendingOutput()
    {
        var lease = await SeedLeasedExecutionAsync();
        var pending = new[]
        {
            NewEvent(
                lease,
                ConversationEventTypes.MessageContentAppended,
                new { delta = "partial" }),
        };

        var result = await _journal.TryCommitInfrastructureFailureAsync(
            lease,
            TurnTerminal.Failure("worker_escape", "escaped"),
            pending,
            CancellationToken.None);

        Assert.IsNotNull(result);
        Assert.AreEqual(2L, result.LastSequence);

        await using var db = await CreateDbAsync();
        var command = await db.ChatExecutionCommands.AsNoTracking().SingleAsync();
        var turn = await db.ConversationTurns.AsNoTracking().SingleAsync();
        var run = await db.ExecutionRuns.AsNoTracking().SingleAsync();
        var events = await db.ConversationEvents.AsNoTracking()
            .OrderBy(e => e.Sequence)
            .ToListAsync();

        Assert.AreEqual("failed", command.Status);
        Assert.AreEqual("failed", turn.Status);
        Assert.AreEqual("failed", run.Status);
        Assert.AreEqual(result.LastSequence, command.TerminalSequence);
        Assert.AreEqual(result.LastSequence, turn.TerminalSequence);
        Assert.AreEqual(result.LastSequence, run.TerminalSequence);
        CollectionAssert.AreEqual(
            new[]
            {
                ConversationEventTypes.MessageContentAppended,
                ConversationEventTypes.TurnFailed,
            },
            events.Select(e => e.Type).ToArray());
        Assert.AreEqual(
            "assistant-message-1",
            events.Single(e => e.Type == ConversationEventTypes.TurnFailed).MessageId);
        Assert.AreEqual((lease.ConversationId, 2L), _signal.LastSignal);
    }

    [TestMethod]
    public async Task TryCommitInfrastructureFailure_WithStaleFence_DoesNotOverwrite()
    {
        var lease = await SeedLeasedExecutionAsync();
        var staleLease = lease with { FencingToken = lease.FencingToken + 1 };

        var result = await _journal.TryCommitInfrastructureFailureAsync(
            staleLease,
            TurnTerminal.Failure("worker_escape", "escaped"),
            [],
            CancellationToken.None);

        Assert.IsNull(result);
        await using var db = await CreateDbAsync();
        Assert.AreEqual("leased",
            (await db.ChatExecutionCommands.AsNoTracking().SingleAsync()).Status);
        Assert.AreEqual("accepted",
            (await db.ConversationTurns.AsNoTracking().SingleAsync()).Status);
        Assert.AreEqual("leased",
            (await db.ExecutionRuns.AsNoTracking().SingleAsync()).Status);
        Assert.AreEqual(0, await db.ConversationEvents.CountAsync());
        Assert.IsNull(_signal.LastSignal);
    }

    private async Task<ExecutionLease> SeedLeasedExecutionAsync()
    {
        const string commandId = "command-1";
        const string conversationId = "conversation-1";
        const string turnId = "turn-1";
        const string runId = "run-1";
        const string workerId = "worker-1";
        var now = DateTimeOffset.UtcNow;
        var nowMs = now.ToUnixTimeMilliseconds();

        await using var db = await CreateDbAsync();
        db.ChatExecutionCommands.Add(new ChatExecutionCommandEntity
        {
            BatchId = "batch-1",
            CommandId = commandId,
            WorkspaceId = "default",
            SessionId = conversationId,
            MessageId = "assistant-message-1",
            UserMessageId = "user-message-1",
            TurnId = turnId,
            AgentInstanceId = "agent-1",
            Status = "leased",
            LeaseOwner = workerId,
            LeaseUntil = now.AddMinutes(2).ToUnixTimeMilliseconds(),
            CreatedAt = nowMs,
        });
        db.ConversationTurns.Add(new ConversationTurnEntity
        {
            ConversationId = conversationId,
            TurnId = turnId,
            CommandId = commandId,
            WorkspaceId = "default",
            Status = "accepted",
            AcceptedSequence = 0,
            CreatedAt = nowMs,
        });
        var run = new ExecutionRunEntity
        {
            RunId = runId,
            CommandId = commandId,
            ConversationId = conversationId,
            TurnId = turnId,
            Attempt = 1,
            WorkerId = workerId,
            Status = "leased",
            LeaseUntil = now.AddMinutes(2).ToUnixTimeMilliseconds(),
        };
        db.ExecutionRuns.Add(run);
        db.ConversationHeads.Add(new ConversationHeadEntity
        {
            ConversationId = conversationId,
            HeadSequence = 0,
        });
        await db.SaveChangesAsync();

        return new ExecutionLease(
            commandId,
            workerId,
            "default",
            conversationId,
            turnId,
            runId,
            run.FencingToken,
            now.AddMinutes(2));
    }

    private Task<PlatformDbContext> CreateDbAsync() =>
        _provider.GetRequiredService<IDbContextFactory<PlatformDbContext>>()
            .CreateDbContextAsync();

    private static NewConversationEvent NewEvent(
        ExecutionLease lease,
        string type,
        object payload) =>
        new(
            EventId: Guid.NewGuid().ToString("N"),
            Type: type,
            SchemaVersion: 1,
            WorkspaceId: lease.WorkspaceId,
            TurnId: lease.TurnId,
            CommandId: lease.CommandId,
            RunId: lease.RunId,
            MessageId: null,
            CorrelationId: lease.ConversationId,
            CausationId: lease.TurnId,
            ProducerEventId: null,
            Payload: JsonSerializer.SerializeToElement(payload));

    private sealed class RecordingCommittedEventSignal : ICommittedEventSignal
    {
        public (string ConversationId, long Sequence)? LastSignal { get; private set; }

        public ValueTask WaitForChangeAsync(
            string conversationId,
            long knownHead,
            CancellationToken ct) =>
            ValueTask.CompletedTask;

        public void Signal(string conversationId, long committedThroughSequence) =>
            LastSignal = (conversationId, committedThroughSequence);
    }
}
