using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using PuddingCode.Goals;
using PuddingCode.Platform;
using PuddingPlatform.Data;
using PuddingPlatform.Data.Entities;
using PuddingPlatform.Services.Execution;

namespace PuddingPlatformTests.Services;

/// <summary>
/// A1（G92-1 S1-c 片6 段2）：turn.completed payload 中 goal_contract_proposal
/// 必须独立成键持久化，reply 原文不被污染；无 proposal 时写 null 保持向后兼容。
/// </summary>
[TestClass]
public sealed class ExecutionJournalGoalContractProposalPayloadTests
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
    public async Task CommitTerminal_WithProposal_WritesStandaloneKeyAndKeepsReplyUntouched()
    {
        var lease = await SeedRunningTurnAsync();
        var proposal = new GoalContractProposal
        {
            SchemaVersion = GoalContractProposal.CurrentSchemaVersion,
            Kind = GoalContractProposal.RefineAcceptanceContractKind,
            ExpectedContractVersion = 2,
            Criteria =
            [
                new GoalContractProposalCriterion
                {
                    Requirement = "最终回复严格等于 READY",
                    RequirementRefs = ["objective:line-1"],
                    Verification = new GoalContractProposalVerification
                    {
                        Kind = "text-assertion",
                        DefinitionRef = "checks/text-assertion.md#equals",
                        InputRefs = ["reply"],
                        ExpectedText = "READY",
                    },
                },
            ],
        };

        var result = await _journal.CommitTerminalAsync(
            lease,
            TurnTerminal.Success("plain final reply", null, proposal),
            [],
            CancellationToken.None);

        Assert.AreEqual(1L, result.LastSequence);
        await using var db = await CreateDbAsync();
        var terminalEvent = await db.ConversationEvents.AsNoTracking()
            .SingleAsync(e => e.Type == ConversationEventTypes.TurnCompleted);
        using var payload = JsonDocument.Parse(terminalEvent.Payload!);
        var root = payload.RootElement;

        // proposal 独立成键（camelCase，与 parser 输入形状一致）
        var persisted = root.GetProperty("goal_contract_proposal");
        Assert.AreEqual(1, persisted.GetProperty("schemaVersion").GetInt32());
        Assert.AreEqual(
            GoalContractProposal.RefineAcceptanceContractKind,
            persisted.GetProperty("kind").GetString());
        Assert.AreEqual(2, persisted.GetProperty("expectedContractVersion").GetInt32());
        Assert.AreEqual(1, persisted.GetProperty("criteria").GetArrayLength());
        Assert.AreEqual(
            "READY",
            persisted.GetProperty("criteria")[0]
                .GetProperty("verification").GetProperty("expectedText").GetString());

        // reply 原文不被污染
        Assert.AreEqual("plain final reply", root.GetProperty("reply").GetString());
        Assert.AreEqual("Completed", root.GetProperty("kind").GetString());
    }

    [TestMethod]
    public async Task CommitTerminal_WithoutProposal_WritesNullProposalKeyBackwardCompat()
    {
        var lease = await SeedRunningTurnAsync();

        var result = await _journal.CommitTerminalAsync(
            lease,
            TurnTerminal.Success("hello", null),
            [],
            CancellationToken.None);

        Assert.AreEqual(1L, result.LastSequence);
        await using var db = await CreateDbAsync();
        var terminalEvent = await db.ConversationEvents.AsNoTracking()
            .SingleAsync(e => e.Type == ConversationEventTypes.TurnCompleted);
        using var payload = JsonDocument.Parse(terminalEvent.Payload!);
        var root = payload.RootElement;

        Assert.AreEqual(JsonValueKind.Null, root.GetProperty("goal_contract_proposal").ValueKind);
        Assert.AreEqual("hello", root.GetProperty("reply").GetString());
        Assert.AreEqual("Completed", root.GetProperty("kind").GetString());
    }

    private async Task<ExecutionLease> SeedRunningTurnAsync()
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
            Status = "running",
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
            Status = "running",
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
            Status = "running",
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
            now.AddMinutes(2))
        {
            TraceId = null,
        };
    }

    private Task<PlatformDbContext> CreateDbAsync() =>
        _provider.GetRequiredService<IDbContextFactory<PlatformDbContext>>()
            .CreateDbContextAsync();

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
