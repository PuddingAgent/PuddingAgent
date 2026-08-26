using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using PuddingCode.Goals;
using PuddingCode.Platform;
using PuddingPlatform.Data;
using PuddingPlatform.Data.Entities;
using PuddingPlatform.Services;
using PuddingPlatform.Services.Goals;

namespace PuddingPlatformTests.Services.Goals;

[TestClass]
public sealed class GoalContinuationTests
{
    private string _root = null!;
    private PlatformDbContextFactory _factory = null!;
    private GoalOutboxStore _outboxStore = null!;

    private sealed class NoopSignal : ICommittedEventSignal
    {
        public ValueTask WaitForChangeAsync(string conversationId, long knownHead, CancellationToken ct)
            => ValueTask.FromCanceled(ct);
        public void Signal(string conversationId, long committedSequence)
        {
        }
    }

    [TestInitialize]
    public async Task InitializeAsync()
    {
        _root = Path.Combine(
            Path.GetTempPath(),
            "PuddingAgent",
            "goal-continuation-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        var options = new DbContextOptionsBuilder<PlatformDbContext>()
            .UseSqlite($"Data Source={Path.Combine(_root, "platform.db")};Default Timeout=10")
            .Options;
        _factory = new PlatformDbContextFactory(options);
        await using var db = await _factory.CreateDbContextAsync();
        await db.Database.EnsureCreatedAsync();
        _outboxStore = new GoalOutboxStore(_factory);
    }

    [TestCleanup]
    public void Cleanup()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    [TestMethod]
    public async Task TrustedAcceptance_AtomicallyConsumesIterationAndCompletesOutbox()
    {
        var goal = await CreateGoalWithContinuationAsync();
        var lease = await ClaimAsync();
        var request = BuildContinuationRequest(goal, lease);

        AcceptanceResult accepted;
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var store = new ConversationAcceptanceStore(
                db,
                new NoopSignal(),
                NullLogger<ConversationAcceptanceStore>.Instance);
            accepted = await store.AcceptBatchAsync(
                request,
                goal.WorkspaceId,
                goal.CurrentConversationId,
                userId: null,
                CancellationToken.None);
        }

        await using var verify = await _factory.CreateDbContextAsync();
        var persistedGoal = await verify.GoalRuns.SingleAsync();
        var persistedOutbox = await verify.GoalOutbox.SingleAsync();
        var iteration = await verify.GoalIterations.SingleAsync();
        Assert.AreEqual(1, persistedGoal.IterationsStarted);
        Assert.AreEqual(0, persistedGoal.IterationsSettled);
        Assert.AreEqual(2, persistedGoal.AggregateVersion);
        Assert.AreEqual(GoalOutboxValues.Completed, persistedOutbox.Status);
        Assert.AreEqual("accepted", iteration.Status);
        Assert.AreEqual(accepted.TurnIds.Single(), iteration.TurnId);
        Assert.AreEqual(accepted.CommandIds.Single(), iteration.CommandId);
        Assert.AreEqual(1, await verify.ChatExecutionCommands.CountAsync());
        Assert.AreEqual(1, await verify.ConversationTurns.CountAsync());
        Assert.AreEqual(1, await verify.ConversationEvents.CountAsync(
            item => item.Type == GoalEventTypes.IterationAccepted));
        Assert.AreEqual(1, await verify.ConversationEvents.CountAsync(
            item => item.Type == GoalEventTypes.ContinuationDispatched));

        // Acceptance replay uses the stable outbox id and cannot consume a second budget unit.
        await using var replayDb = await _factory.CreateDbContextAsync();
        var replayStore = new ConversationAcceptanceStore(
            replayDb,
            new NoopSignal(),
            NullLogger<ConversationAcceptanceStore>.Instance);
        var replay = await replayStore.AcceptBatchAsync(
            request,
            goal.WorkspaceId,
            goal.CurrentConversationId,
            userId: null,
            CancellationToken.None);
        Assert.AreEqual(accepted.TurnIds.Single(), replay.TurnIds.Single());

        await using var replayVerify = await _factory.CreateDbContextAsync();
        Assert.AreEqual(1, (await replayVerify.GoalRuns.SingleAsync()).IterationsStarted);
        Assert.AreEqual(1, await replayVerify.GoalIterations.CountAsync());
    }

    [TestMethod]
    public async Task EarlierUserAcceptance_DefersGoalWithoutConsumingBudget()
    {
        var goal = await CreateGoalWithContinuationAsync();

        await using (var userDb = await _factory.CreateDbContextAsync())
        {
            var userStore = new ConversationAcceptanceStore(
                userDb,
                new NoopSignal(),
                NullLogger<ConversationAcceptanceStore>.Instance);
            await userStore.AcceptBatchAsync(
                BuildUserRequest(),
                goal.WorkspaceId,
                goal.CurrentConversationId,
                "user-1",
                CancellationToken.None);
        }

        var lease = await ClaimAsync();
        await using var db = await _factory.CreateDbContextAsync();
        var store = new ConversationAcceptanceStore(
            db,
            new NoopSignal(),
            NullLogger<ConversationAcceptanceStore>.Instance);
        var error = await Assert.ThrowsAsync<GoalContinuationAcceptanceException>(() =>
            store.AcceptBatchAsync(
                BuildContinuationRequest(goal, lease),
                goal.WorkspaceId,
                goal.CurrentConversationId,
                userId: null,
                CancellationToken.None));

        Assert.IsTrue(error.Deferred);
        Assert.AreEqual(GoalContinuationAcceptanceErrorCodes.ConversationBusy, error.Code);
        await using var verify = await _factory.CreateDbContextAsync();
        Assert.AreEqual(0, (await verify.GoalRuns.SingleAsync()).IterationsStarted);
        Assert.AreEqual(0, await verify.GoalIterations.CountAsync());
    }

    [TestMethod]
    public async Task EpochChangeRejectsLateAcceptance()
    {
        var goal = await CreateGoalWithContinuationAsync();
        var lease = await ClaimAsync();

        await using (var mutateDb = await _factory.CreateDbContextAsync())
        {
            var goalStore = NewGoalStore(mutateDb);
            await goalStore.TryMutateAsync(
                goal.GoalRunId,
                0,
                current =>
                {
                    current.Status = GoalPhase.Paused;
                    current.ActivationEpoch++;
                    return true;
                },
                new GoalRunStore.GoalEventAppend(GoalEventTypes.Paused, new { reason = "test" }),
                "trace-pause",
                CancellationToken.None);
        }

        await using var db = await _factory.CreateDbContextAsync();
        var acceptance = new ConversationAcceptanceStore(
            db,
            new NoopSignal(),
            NullLogger<ConversationAcceptanceStore>.Instance);
        var error = await Assert.ThrowsAsync<GoalContinuationAcceptanceException>(() =>
            acceptance.AcceptBatchAsync(
                BuildContinuationRequest(goal, lease),
                goal.WorkspaceId,
                goal.CurrentConversationId,
                userId: null,
                CancellationToken.None));

        Assert.AreEqual(GoalContinuationAcceptanceErrorCodes.GoalInactive, error.Code);
        Assert.IsFalse(error.Deferred);
    }

    [TestMethod]
    public async Task ExpiredLeaseRecoveryIssuesNewFenceAndRejectsStaleOwner()
    {
        await CreateGoalWithContinuationAsync();
        var now = DateTimeOffset.Parse("2026-08-26T00:00:00Z");
        var first = await _outboxStore.TryClaimAsync(
            (await _outboxStore.PeekDueAsync(DateTimeOffset.MaxValue, 1)).Single().OutboxId,
            "worker-1",
            now,
            TimeSpan.FromSeconds(5));
        Assert.IsNotNull(first);

        Assert.AreEqual(1, await _outboxStore.RecoverExpiredLeasesAsync(now.AddSeconds(6)));
        var second = await _outboxStore.TryClaimAsync(
            first.OutboxId,
            "worker-2",
            now.AddSeconds(6),
            TimeSpan.FromSeconds(5));
        Assert.IsNotNull(second);
        Assert.IsGreaterThan(first.FencingToken, second.FencingToken);

        Assert.IsFalse(await _outboxStore.SuppressAsync(first, "stale"));
        Assert.IsTrue(await _outboxStore.SuppressAsync(second, "current"));
        Assert.AreEqual(
            GoalOutboxValues.Cancelled,
            (await _outboxStore.GetAsync(first.OutboxId))!.Status);
    }

    [TestMethod]
    public async Task CompletedTurnWithoutIndependentCompletionFact_SettlesAndQueuesNextIteration()
    {
        var goal = await CreateGoalWithContinuationAsync();
        var lease = await ClaimAsync();
        var accepted = await AcceptContinuationAsync(goal, lease);
        await CommitSyntheticTerminalAsync(accepted.TurnIds.Single(), "completed");

        var settlement = NewSettlementStore();
        var candidate = (await settlement.GetCandidatesAsync(8)).Single();
        var verifier = new ConservativeGoalIterationVerifier();
        var decision = await verifier.VerifyAsync(candidate.ToCapsule());
        Assert.AreEqual(GoalVerificationVerdict.Continue, decision.Verdict);
        Assert.IsTrue(await settlement.ApplyAsync(candidate, decision));

        await using var verify = await _factory.CreateDbContextAsync();
        var persistedGoal = await verify.GoalRuns.SingleAsync();
        Assert.AreEqual(GoalPhase.Active, persistedGoal.Status);
        Assert.AreEqual(1, persistedGoal.IterationsStarted);
        Assert.AreEqual(1, persistedGoal.IterationsSettled);
        Assert.AreEqual("settled", (await verify.GoalIterations.SingleAsync()).Status);
        Assert.AreEqual("continue", (await verify.GoalVerifications.SingleAsync()).Verdict);
        Assert.AreEqual(1, await verify.GoalOutbox.CountAsync(
            item => item.Status == GoalOutboxValues.Pending));
        Assert.AreEqual(2, await verify.GoalOutbox.CountAsync());

        // Settlement is idempotent: the same terminal fact cannot create another continuation.
        Assert.IsFalse(await settlement.ApplyAsync(candidate, decision));
        Assert.AreEqual(2, await verify.GoalOutbox.CountAsync());
    }

    [TestMethod]
    public async Task FailedTurn_FailClosedBlocksGoalAndCreatesNoContinuation()
    {
        var goal = await CreateGoalWithContinuationAsync();
        var lease = await ClaimAsync();
        var accepted = await AcceptContinuationAsync(goal, lease);
        await CommitSyntheticTerminalAsync(accepted.TurnIds.Single(), "failed");

        var settlement = NewSettlementStore();
        var candidate = (await settlement.GetCandidatesAsync(8)).Single();
        var decision = await new ConservativeGoalIterationVerifier().VerifyAsync(candidate.ToCapsule());
        Assert.AreEqual(GoalVerificationVerdict.Blocked, decision.Verdict);
        Assert.IsTrue(await settlement.ApplyAsync(candidate, decision));

        await using var verify = await _factory.CreateDbContextAsync();
        var persistedGoal = await verify.GoalRuns.SingleAsync();
        Assert.AreEqual(GoalPhase.Blocked, persistedGoal.Status);
        Assert.AreEqual("iteration_failed", persistedGoal.BlockedCode);
        Assert.AreEqual(0, await verify.GoalOutbox.CountAsync(
            item => item.Status == GoalOutboxValues.Pending));
    }

    [TestMethod]
    public async Task BoundTaskCompleted_IsRequiredAndSufficientForConservativeGoalCompletion()
    {
        var goal = await CreateGoalWithContinuationAsync();
        await using (var bindingDb = await _factory.CreateDbContextAsync())
        {
            var now = DateTimeOffset.UtcNow;
            bindingDb.WorkspaceTasks.Add(new WorkspaceTaskEntity
            {
                TaskId = "task-1",
                WorkspaceId = goal.WorkspaceId,
                Title = "Task goal",
                AcceptanceCriteria = "tests pass",
                Status = PuddingCode.Tasks.WorkspaceTaskStatus.InProgress,
                Priority = PuddingCode.Tasks.TaskPriority.P1,
                ExecutionWindow = PuddingCode.Tasks.TaskExecutionWindow.Anytime,
                Version = 4,
                SortOrder = 0,
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
                ActiveAssignmentId = "assignment-1",
            });
            bindingDb.TaskAssignmentAttempts.Add(new TaskAssignmentAttemptEntity
            {
                AttemptId = "assignment-1",
                TaskId = "task-1",
                WorkspaceId = goal.WorkspaceId,
                AgentId = goal.AgentInstanceId,
                AttemptNumber = 1,
                Status = AssignmentAttemptStatus.InProgress,
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
                ActiveAtUtc = now,
            });
            var reservation = new AgentExecutionReservationEntity
            {
                ReservationId = "reservation-1",
                WorkspaceId = goal.WorkspaceId,
                AgentId = goal.AgentInstanceId,
                TaskId = "task-1",
                GoalRunId = goal.GoalRunId,
                OwnerId = "coordinator",
                Status = "active",
                LeaseUntilUtc = now.AddHours(1),
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
            };
            bindingDb.AgentExecutionReservations.Add(reservation);
            await bindingDb.SaveChangesAsync();
            bindingDb.TaskGoalBindings.Add(new TaskGoalBindingEntity
            {
                BindingId = "binding-1",
                WorkspaceId = goal.WorkspaceId,
                TaskId = "task-1",
                AssignmentId = "assignment-1",
                ExpectedTaskVersion = 4,
                GoalRunId = goal.GoalRunId,
                AgentInstanceId = goal.AgentInstanceId,
                ReservationId = reservation.ReservationId,
                ReservationFencingToken = reservation.FencingToken,
                Status = "active",
                CreatedAtUtc = now,
            });
            await bindingDb.SaveChangesAsync();
        }

        var lease = await ClaimAsync();
        var accepted = await AcceptContinuationAsync(goal, lease);
        await using (var completeDb = await _factory.CreateDbContextAsync())
        {
            var task = await completeDb.WorkspaceTasks.SingleAsync();
            task.Status = PuddingCode.Tasks.WorkspaceTaskStatus.Completed;
            task.Version++;
            task.CompletedAtUtc = DateTimeOffset.UtcNow;
            task.UpdatedAtUtc = DateTimeOffset.UtcNow;
            await completeDb.SaveChangesAsync();
        }
        await CommitSyntheticTerminalAsync(accepted.TurnIds.Single(), "completed");
        var settlement = NewSettlementStore();
        var candidate = (await settlement.GetCandidatesAsync(8)).Single();
        var decision = await new ConservativeGoalIterationVerifier().VerifyAsync(candidate.ToCapsule());
        Assert.AreEqual(GoalVerificationVerdict.Complete, decision.Verdict);
        Assert.IsTrue(await settlement.ApplyAsync(candidate, decision));

        await using var verify = await _factory.CreateDbContextAsync();
        Assert.AreEqual(GoalPhase.Completed, (await verify.GoalRuns.SingleAsync()).Status);
        Assert.AreEqual("terminal", (await verify.TaskGoalBindings.SingleAsync()).Status);
        Assert.AreEqual(1, await verify.ConversationEvents.CountAsync(
            item => item.Type == GoalEventTypes.TaskGoalCompleted));
        Assert.AreEqual(0, await verify.GoalOutbox.CountAsync(
            item => item.Status == GoalOutboxValues.Pending));
    }

    private async Task<GoalRunEntity> CreateGoalWithContinuationAsync()
    {
        await using var db = await _factory.CreateDbContextAsync();
        var store = NewGoalStore(db);
        return await store.CreateAsync(
            new GoalRunEntity
            {
                GoalRunId = Guid.NewGuid().ToString("N"),
                WorkspaceId = "ws",
                CurrentConversationId = "conv-1",
                AgentInstanceId = "agent-1",
                Objective = "完成一项有证据的测试工作",
                ObjectiveVersion = 1,
                Status = GoalPhase.Active,
                MaxIterations = 8,
                ActivationEpoch = 1,
                AggregateVersion = 1,
                SourceCommandId = Guid.NewGuid().ToString("N"),
                CreatedAtUtc = DateTimeOffset.UtcNow,
                UpdatedAtUtc = DateTimeOffset.UtcNow,
            },
            "trace-create",
            CancellationToken.None,
            enqueueContinuation: true);
    }

    private async Task<GoalOutboxEntity> ClaimAsync()
    {
        var due = (await _outboxStore.PeekDueAsync(DateTimeOffset.MaxValue, 1)).Single();
        return (await _outboxStore.TryClaimAsync(
            due.OutboxId,
            "worker-1",
            DateTimeOffset.UtcNow,
            TimeSpan.FromMinutes(2)))!;
    }

    private async Task<AcceptanceResult> AcceptContinuationAsync(
        GoalRunEntity goal,
        GoalOutboxEntity lease)
    {
        await using var db = await _factory.CreateDbContextAsync();
        var binding = await db.TaskGoalBindings.AsNoTracking()
            .SingleOrDefaultAsync(item => item.GoalRunId == goal.GoalRunId);
        var store = new ConversationAcceptanceStore(
            db,
            new NoopSignal(),
            NullLogger<ConversationAcceptanceStore>.Instance);
        return await store.AcceptBatchAsync(
            BuildContinuationRequest(goal, lease, binding),
            goal.WorkspaceId,
            goal.CurrentConversationId,
            userId: null,
            CancellationToken.None);
    }

    private async Task CommitSyntheticTerminalAsync(string turnId, string kind)
    {
        await using var db = await _factory.CreateDbContextAsync();
        var turn = await db.ConversationTurns.SingleAsync(item => item.TurnId == turnId);
        var iteration = await db.GoalIterations.SingleAsync(item => item.TurnId == turnId);
        var goal = await db.GoalRuns.SingleAsync(item => item.GoalRunId == iteration.GoalRunId);
        var head = await db.ConversationHeads.SingleAsync(
            item => item.ConversationId == goal.CurrentConversationId);
        var sequence = head.HeadSequence + 1;
        var eventType = kind switch
        {
            "completed" => ConversationEventTypes.TurnCompleted,
            "failed" => ConversationEventTypes.TurnFailed,
            _ => ConversationEventTypes.TurnCancelled,
        };
        turn.Status = kind;
        turn.TerminalKind = kind;
        turn.TerminalSequence = sequence;
        turn.CompletedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        head.HeadSequence = sequence;
        db.ConversationEvents.Add(new ConversationEventEntity
        {
            ConversationId = goal.CurrentConversationId,
            Sequence = sequence,
            EventId = $"terminal-{turnId}",
            WorkspaceId = goal.WorkspaceId,
            TurnId = turnId,
            CommandId = iteration.CommandId,
            Type = eventType,
            SchemaVersion = 1,
            Payload = "{}",
            OccurredAt = DateTimeOffset.UtcNow.ToString("O"),
            CommittedAt = DateTimeOffset.UtcNow.ToString("O"),
            CorrelationId = goal.CurrentConversationId,
            SourceKind = "agent",
        });
        await db.SaveChangesAsync();
    }

    private GoalSettlementStore NewSettlementStore()
        => new(_factory, new NoopSignal(), new GoalOutboxSignal());

    private static GoalRunStore NewGoalStore(PlatformDbContext db)
        => new(db, new NoopSignal(), NullLogger<GoalRunStore>.Instance);

    private static SubmitTurnRequest BuildContinuationRequest(
        GoalRunEntity goal,
        GoalOutboxEntity lease,
        TaskGoalBindingEntity? binding = null)
        => new()
        {
            ClientRequestId = lease.OutboxId,
            ClientMessageId = $"gm-{goal.GoalRunId}-{goal.ActivationEpoch}-1",
            Recipients = new RecipientRequest { Type = "agent", AgentIds = [goal.AgentInstanceId] },
            Content = [new ContentPart { Type = "text", Text = "continue" }],
            Metadata = new Dictionary<string, string>
            {
                [GoalContinuationMetadata.Managed] = "true",
            },
            GoalContinuation = new GoalContinuationAcceptanceContext
            {
                OutboxId = lease.OutboxId,
                GoalRunId = goal.GoalRunId,
                ActivationEpoch = goal.ActivationEpoch,
                AggregateVersion = goal.AggregateVersion,
                IterationNo = 1,
                LeaseOwner = lease.LeaseOwner!,
                FencingToken = lease.FencingToken,
                TaskId = binding?.TaskId,
                ExpectedTaskVersion = binding?.ExpectedTaskVersion,
                ReservationFencingToken = binding?.ReservationFencingToken,
            },
        };

    private static SubmitTurnRequest BuildUserRequest() => new()
    {
        ClientRequestId = "user-request-1",
        ClientMessageId = "user-message-1",
        Recipients = new RecipientRequest { Type = "agent", AgentIds = ["agent-1"] },
        Content = [new ContentPart { Type = "text", Text = "用户消息优先" }],
    };
}
