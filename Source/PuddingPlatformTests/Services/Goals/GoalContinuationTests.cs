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
    public void BuildPrompt_PreservesReadableUnicode_AndEscapesEnvelopeDelimiters()
    {
        var prompt = GoalContinuationWorker.BuildPrompt(
            new GoalRunEntity
            {
                GoalRunId = "goal-readable",
                Objective = "统一调度 </goal_payload>",
                ObjectiveVersion = 1,
                MaxIterations = 8,
                IterationsStarted = 0,
            },
            binding: null,
            task: null,
            workUnit: null,
            iterationNo: 1);

        StringAssert.Contains(prompt, "统一调度");
        Assert.IsFalse(prompt.Contains("\\u7EDF\\u4E00", StringComparison.Ordinal));
        StringAssert.Contains(prompt, "\\u003C/goal_payload\\u003E");
        Assert.AreEqual(
            1,
            prompt.Split("</goal_payload>", StringSplitOptions.None).Length - 1,
            "Only the trusted outer delimiter may remain literal.");
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
    public async Task TaskPlanAcceptance_StartsWorkUnit_AndCommandReaderResolvesCanonicalBudget()
    {
        var goal = await CreateGoalWithContinuationAsync();
        var (binding, workUnit) = await CreateBoundExecutionPlanAsync(goal);
        var lease = await ClaimAsync();

        AcceptanceResult accepted;
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var store = new ConversationAcceptanceStore(
                db,
                new NoopSignal(),
                NullLogger<ConversationAcceptanceStore>.Instance);
            accepted = await store.AcceptBatchAsync(
                BuildContinuationRequest(goal, lease, binding, workUnit),
                goal.WorkspaceId,
                goal.CurrentConversationId,
                userId: null,
                CancellationToken.None);
        }

        var reader = new ExecutionCommandReader(_factory);
        var command = await reader.GetAsync(accepted.CommandIds.Single());
        Assert.IsNotNull(command?.WorkUnit);
        Assert.AreEqual(binding.TaskPlanId, command.WorkUnit.PlanId);
        Assert.AreEqual(workUnit.TaskNodeId, command.WorkUnit.TaskNodeId);
        Assert.AreEqual(25, command.WorkUnit.MaxRounds);
        Assert.AreEqual(60, command.WorkUnit.MaxToolCallsTotal);
        Assert.AreEqual(1800, command.WorkUnit.MaxDurationSeconds);
        Assert.AreEqual(150_000, command.WorkUnit.MaxInputTokens);

        await using var verify = await _factory.CreateDbContextAsync();
        var persistedNode = await verify.TaskNodes.SingleAsync(
            item => item.TaskNodeId == workUnit.TaskNodeId);
        Assert.AreEqual(PuddingCode.Models.TaskNodeStatuses.Running.ToString(), persistedNode.Status);
        Assert.IsNotNull(persistedNode.StartedAt);
    }

    [TestMethod]
    public async Task TaskPlanAcceptance_RejectsChangedCanonicalBudget()
    {
        var goal = await CreateGoalWithContinuationAsync();
        var (binding, workUnit) = await CreateBoundExecutionPlanAsync(goal);
        var lease = await ClaimAsync();
        var request = BuildContinuationRequest(goal, lease, binding, workUnit);

        await using (var mutateDb = await _factory.CreateDbContextAsync())
        {
            var node = await mutateDb.TaskNodes.SingleAsync(
                item => item.TaskNodeId == workUnit.TaskNodeId);
            node.MaxRounds = 0;
            await mutateDb.SaveChangesAsync();
        }

        await using var db = await _factory.CreateDbContextAsync();
        var store = new ConversationAcceptanceStore(
            db,
            new NoopSignal(),
            NullLogger<ConversationAcceptanceStore>.Instance);
        var error = await Assert.ThrowsAsync<GoalContinuationAcceptanceException>(() =>
            store.AcceptBatchAsync(
                request,
                goal.WorkspaceId,
                goal.CurrentConversationId,
                userId: null,
                CancellationToken.None));

        Assert.AreEqual(GoalContinuationAcceptanceErrorCodes.TaskPlanChanged, error.Code);
        Assert.IsFalse(error.Deferred);
    }

    [TestMethod]
    public async Task TaskPlanSettlement_CompletesCurrentWorkUnit_AndAdmitsNextAfterTaskVersionAdvances()
    {
        var goal = await CreateGoalWithContinuationAsync();
        var (binding, firstWorkUnit) = await CreateBoundExecutionPlanAsync(goal, includeNext: true);
        var lease = await ClaimAsync();
        AcceptanceResult accepted;
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var store = new ConversationAcceptanceStore(
                db,
                new NoopSignal(),
                NullLogger<ConversationAcceptanceStore>.Instance);
            accepted = await store.AcceptBatchAsync(
                BuildContinuationRequest(goal, lease, binding, firstWorkUnit),
                goal.WorkspaceId,
                goal.CurrentConversationId,
                userId: null,
                CancellationToken.None);
        }

        // Normal task_claim/update advances the live Task revision. It must not invalidate
        // the immutable compile-time plan fingerprint for the next WorkUnit.
        await using (var updateDb = await _factory.CreateDbContextAsync())
        {
            var task = await updateDb.WorkspaceTasks.SingleAsync();
            task.Status = PuddingCode.Tasks.WorkspaceTaskStatus.InProgress;
            task.Version++;
            task.UpdatedAtUtc = DateTimeOffset.UtcNow;
            var attempt = await updateDb.TaskAssignmentAttempts.SingleAsync();
            attempt.Status = AssignmentAttemptStatus.InProgress;
            attempt.UpdatedAtUtc = DateTimeOffset.UtcNow;
            await updateDb.SaveChangesAsync();
        }

        await CommitSyntheticTerminalAsync(accepted.TurnIds.Single(), "completed");
        var settlement = NewSettlementStore();
        var candidate = (await settlement.GetCandidatesAsync(8)).Single();
        var decision = await new ConservativeGoalIterationVerifier().VerifyAsync(candidate.ToCapsule());
        Assert.IsTrue(await settlement.ApplyAsync(candidate, decision));

        await using (var verify = await _factory.CreateDbContextAsync())
        {
            var nodes = await verify.TaskNodes.Where(item => item.Depth == 1)
                .OrderBy(item => item.SequenceNo)
                .ToListAsync();
            Assert.AreEqual(PuddingCode.Models.TaskNodeStatuses.Completed.ToString(), nodes[0].Status);
            Assert.AreEqual(PuddingCode.Models.TaskNodeStatuses.Planned.ToString(), nodes[1].Status);
            Assert.AreEqual(PuddingCode.Models.TaskPlanStatuses.Active.ToString(),
                (await verify.TaskPlanRuns.SingleAsync()).Status);
            Assert.AreEqual(4, (await verify.TaskGoalBindings.SingleAsync()).ExpectedTaskVersion);
        }

        var nextLease = await ClaimAsync();
        await using var nextDb = await _factory.CreateDbContextAsync();
        var currentGoal = await nextDb.GoalRuns.AsNoTracking().SingleAsync();
        var currentBinding = await nextDb.TaskGoalBindings.AsNoTracking().SingleAsync();
        var nextWorkUnit = await nextDb.TaskNodes.AsNoTracking().SingleAsync(
            item => item.Depth == 1 && item.SequenceNo == 2);
        var nextStore = new ConversationAcceptanceStore(
            nextDb,
            new NoopSignal(),
            NullLogger<ConversationAcceptanceStore>.Instance);
        var nextAccepted = await nextStore.AcceptBatchAsync(
            BuildContinuationRequest(currentGoal, nextLease, currentBinding, nextWorkUnit),
            currentGoal.WorkspaceId,
            currentGoal.CurrentConversationId,
            userId: null,
            CancellationToken.None);
        var command = await new ExecutionCommandReader(_factory).GetAsync(nextAccepted.CommandIds.Single());
        Assert.AreEqual(nextWorkUnit.TaskNodeId, command?.WorkUnit?.TaskNodeId);
    }

    [TestMethod]
    public async Task TaskPlanSettlement_FinalWorkUnitWithoutTaskCompletion_StopsForReview()
    {
        var goal = await CreateGoalWithContinuationAsync();
        var (binding, workUnit) = await CreateBoundExecutionPlanAsync(goal);
        var lease = await ClaimAsync();
        AcceptanceResult accepted;
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var store = new ConversationAcceptanceStore(
                db,
                new NoopSignal(),
                NullLogger<ConversationAcceptanceStore>.Instance);
            accepted = await store.AcceptBatchAsync(
                BuildContinuationRequest(goal, lease, binding, workUnit),
                goal.WorkspaceId,
                goal.CurrentConversationId,
                userId: null,
                CancellationToken.None);
        }

        await CommitSyntheticTerminalAsync(accepted.TurnIds.Single(), "completed");
        var settlement = NewSettlementStore();
        var candidate = (await settlement.GetCandidatesAsync(8)).Single();
        var proposed = await new ConservativeGoalIterationVerifier().VerifyAsync(candidate.ToCapsule());
        Assert.AreEqual(GoalVerificationVerdict.Continue, proposed.Verdict);
        Assert.IsTrue(await settlement.ApplyAsync(candidate, proposed));

        await using var verify = await _factory.CreateDbContextAsync();
        Assert.AreEqual(GoalPhase.Failed, (await verify.GoalRuns.SingleAsync()).Status);
        Assert.IsNotNull((await verify.GoalRuns.SingleAsync()).TerminalAtUtc);
        Assert.AreEqual("task_completion_fact_missing", (await verify.GoalRuns.SingleAsync()).BlockedCode);
        Assert.AreEqual(PuddingCode.Tasks.WorkspaceTaskStatus.NeedsReview,
            (await verify.WorkspaceTasks.SingleAsync()).Status);
        Assert.IsNull((await verify.WorkspaceTasks.SingleAsync()).ActiveAssignmentId);
        Assert.AreEqual("terminal", (await verify.TaskGoalBindings.SingleAsync()).Status);
        Assert.IsNotNull((await verify.TaskAssignmentAttempts.SingleAsync()).ReleasedAtUtc);
        Assert.AreEqual(PuddingCode.Models.TaskNodeStatuses.Completed.ToString(),
            (await verify.TaskNodes.SingleAsync(item => item.Depth == 1)).Status);
        Assert.AreEqual(PuddingCode.Models.TaskPlanStatuses.Completed.ToString(),
            (await verify.TaskPlanRuns.SingleAsync()).Status);
        Assert.AreEqual(0, await verify.GoalOutbox.CountAsync(
            item => item.Status == GoalOutboxValues.Pending));
    }

    [TestMethod]
    public async Task TaskPlanSettlement_FailedTurn_ReleasesAssignmentAndAgentOwnership()
    {
        var goal = await CreateGoalWithContinuationAsync();
        var (binding, workUnit) = await CreateBoundExecutionPlanAsync(goal);
        var lease = await ClaimAsync();
        AcceptanceResult accepted;
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var store = new ConversationAcceptanceStore(
                db,
                new NoopSignal(),
                NullLogger<ConversationAcceptanceStore>.Instance);
            accepted = await store.AcceptBatchAsync(
                BuildContinuationRequest(goal, lease, binding, workUnit),
                goal.WorkspaceId,
                goal.CurrentConversationId,
                userId: null,
                CancellationToken.None);
        }

        await CommitSyntheticTerminalAsync(accepted.TurnIds.Single(), "failed");
        var settlement = NewSettlementStore();
        var candidate = (await settlement.GetCandidatesAsync(8)).Single();
        var decision = await new ConservativeGoalIterationVerifier().VerifyAsync(candidate.ToCapsule());
        Assert.AreEqual(GoalVerificationVerdict.Blocked, decision.Verdict);
        Assert.AreEqual("iteration_failed", decision.BlockerCode);
        Assert.IsTrue(await settlement.ApplyAsync(candidate, decision));

        await using var verify = await _factory.CreateDbContextAsync();
        Assert.AreEqual(GoalPhase.Failed, (await verify.GoalRuns.SingleAsync()).Status);
        Assert.IsNotNull((await verify.GoalRuns.SingleAsync()).TerminalAtUtc);
        Assert.AreEqual(PuddingCode.Tasks.WorkspaceTaskStatus.Blocked,
            (await verify.WorkspaceTasks.SingleAsync()).Status);
        Assert.IsNull((await verify.WorkspaceTasks.SingleAsync()).ActiveAssignmentId);
        Assert.AreEqual("terminal", (await verify.TaskGoalBindings.SingleAsync()).Status);
        Assert.AreEqual(AssignmentAttemptStatus.Failed,
            (await verify.TaskAssignmentAttempts.SingleAsync()).Status);
        Assert.AreEqual("released", (await verify.AgentExecutionReservations.SingleAsync()).Status);
        Assert.AreEqual(PuddingCode.Models.TaskPlanStatuses.Failed.ToString(),
            (await verify.TaskPlanRuns.SingleAsync()).Status);
        Assert.AreEqual(0, await verify.GoalOutbox.CountAsync(
            item => item.Status == GoalOutboxValues.Pending));
    }

    [TestMethod]
    public async Task TaskPlanSettlement_FailedTurn_ArchivesRealErrorCodeAndBlockerReason()
    {
        var goal = await CreateGoalWithContinuationAsync();
        var (binding, workUnit) = await CreateBoundExecutionPlanAsync(goal);
        var lease = await ClaimAsync();
        AcceptanceResult accepted;
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var store = new ConversationAcceptanceStore(
                db,
                new NoopSignal(),
                NullLogger<ConversationAcceptanceStore>.Instance);
            accepted = await store.AcceptBatchAsync(
                BuildContinuationRequest(goal, lease, binding, workUnit),
                goal.WorkspaceId,
                goal.CurrentConversationId,
                userId: null,
                CancellationToken.None);
        }

        // The journal persists terminal payloads with camelCase errorCode/errorMessage
        // (see SqliteExecutionJournal.BuildTerminalPayload) — GoalSettlementStore must
        // surface those real values on the archived Goal and bound Task.
        const string failedPayload =
            "{\"kind\":\"failed\",\"errorCode\":\"work_unit_budget_exhausted\"," +
            "\"errorMessage\":\"WorkUnit input Token budget exhausted (input 150000 tokens).\"}";
        await CommitSyntheticTerminalAsync(accepted.TurnIds.Single(), "failed", failedPayload);
        var settlement = NewSettlementStore();
        var candidate = (await settlement.GetCandidatesAsync(8)).Single();
        Assert.AreEqual("work_unit_budget_exhausted", candidate.ErrorCode);
        Assert.AreEqual(
            "WorkUnit input Token budget exhausted (input 150000 tokens).",
            candidate.ErrorMessage);
        var decision = await new ConservativeGoalIterationVerifier().VerifyAsync(candidate.ToCapsule());
        Assert.IsTrue(await settlement.ApplyAsync(candidate, decision));

        await using var verify = await _factory.CreateDbContextAsync();
        var archivedGoal = await verify.GoalRuns.SingleAsync();
        Assert.AreEqual(GoalPhase.Failed, archivedGoal.Status);
        Assert.AreEqual("work_unit_budget_exhausted", archivedGoal.BlockedCode);
        Assert.AreEqual(
            "WorkUnit input Token budget exhausted (input 150000 tokens).",
            archivedGoal.BlockedMessage);
        var archivedTask = await verify.WorkspaceTasks.SingleAsync();
        Assert.AreEqual(PuddingCode.Tasks.WorkspaceTaskStatus.Blocked, archivedTask.Status);
        Assert.AreEqual("work_unit_budget_exhausted", archivedTask.BlockerKind);
        Assert.AreEqual(
            "WorkUnit input Token budget exhausted (input 150000 tokens).",
            archivedTask.BlockerReason);
        // goal_iterations.error_id must archive the real errorCode from the
        // turn.failed payload, not stay null.
        var archivedIteration = await verify.GoalIterations.SingleAsync();
        Assert.AreEqual("work_unit_budget_exhausted", archivedIteration.ErrorId);
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
    public async Task Settlement_RetainsTerminalEvidenceAfterMoreThan128StreamEvents()
    {
        var goal = await CreateGoalWithContinuationAsync();
        var lease = await ClaimAsync();
        var accepted = await AcceptContinuationAsync(goal, lease);
        var turnId = accepted.TurnIds.Single();

        await using (var db = await _factory.CreateDbContextAsync())
        {
            var head = await db.ConversationHeads.SingleAsync(
                item => item.ConversationId == goal.CurrentConversationId);
            for (var i = 0; i < 150; i++)
            {
                head.HeadSequence++;
                db.ConversationEvents.Add(new ConversationEventEntity
                {
                    ConversationId = goal.CurrentConversationId,
                    Sequence = head.HeadSequence,
                    EventId = $"thinking-{turnId}-{i}",
                    WorkspaceId = goal.WorkspaceId,
                    TurnId = turnId,
                    Type = ConversationEventTypes.MessageThinkingSummaryAppended,
                    SchemaVersion = 1,
                    Payload = "{\"delta\":\"x\"}",
                    OccurredAt = DateTimeOffset.UtcNow.ToString("O"),
                    CommittedAt = DateTimeOffset.UtcNow.ToString("O"),
                    CorrelationId = goal.CurrentConversationId,
                    SourceKind = "agent",
                });
            }
            head.HeadSequence++;
            db.ConversationEvents.Add(new ConversationEventEntity
            {
                ConversationId = goal.CurrentConversationId,
                Sequence = head.HeadSequence,
                EventId = $"usage-{turnId}",
                WorkspaceId = goal.WorkspaceId,
                TurnId = turnId,
                Type = ConversationEventTypes.UsageRecorded,
                SchemaVersion = 2,
                Payload = "{\"usage\":{\"promptTokens\":1200,\"completionTokens\":300}}",
                OccurredAt = DateTimeOffset.UtcNow.ToString("O"),
                CommittedAt = DateTimeOffset.UtcNow.ToString("O"),
                CorrelationId = goal.CurrentConversationId,
                SourceKind = "agent",
            });
            head.HeadSequence++;
            db.ConversationEvents.Add(new ConversationEventEntity
            {
                ConversationId = goal.CurrentConversationId,
                Sequence = head.HeadSequence,
                EventId = $"tool-{turnId}",
                WorkspaceId = goal.WorkspaceId,
                TurnId = turnId,
                Type = ConversationEventTypes.ToolCallRequested,
                SchemaVersion = 1,
                Payload = "{}",
                OccurredAt = DateTimeOffset.UtcNow.ToString("O"),
                CommittedAt = DateTimeOffset.UtcNow.ToString("O"),
                CorrelationId = goal.CurrentConversationId,
                SourceKind = "agent",
            });
            await db.SaveChangesAsync();
        }

        await CommitSyntheticTerminalAsync(turnId, "completed");
        var settlement = NewSettlementStore();
        var candidate = (await settlement.GetCandidatesAsync(8)).Single();

        Assert.IsTrue(candidate.EvidenceComplete);
        Assert.IsTrue(candidate.EvidenceRefs.Any(item => item.Contains($"terminal-{turnId}")));
        Assert.AreEqual(1, candidate.LlmRounds);
        Assert.AreEqual(1, candidate.ToolCalls);
        Assert.AreEqual(1200L, candidate.InputTokens);
        Assert.AreEqual(300L, candidate.OutputTokens);
        var decision = await new ConservativeGoalIterationVerifier().VerifyAsync(candidate.ToCapsule());
        Assert.AreEqual(GoalVerificationVerdict.Continue, decision.Verdict);
        Assert.IsTrue(await settlement.ApplyAsync(candidate, decision));

        await using var verify = await _factory.CreateDbContextAsync();
        var persistedGoal = await verify.GoalRuns.SingleAsync();
        var persistedIteration = await verify.GoalIterations.SingleAsync();
        Assert.AreEqual(1, persistedIteration.LlmRounds);
        Assert.AreEqual(1, persistedIteration.ToolCalls);
        Assert.AreEqual(1200L, persistedGoal.InputTokens);
        Assert.AreEqual(300L, persistedGoal.OutputTokens);
    }

    [TestMethod]
    public async Task Settlement_AddsRecursiveDelegatedUsageWithinCurrentTurnWindow()
    {
        var goal = await CreateGoalWithContinuationAsync();
        var lease = await ClaimAsync();
        var accepted = await AcceptContinuationAsync(goal, lease);
        var turnId = accepted.TurnIds.Single();
        var childSessionId = $"{goal.CurrentConversationId}-sub-child";
        var grandchildSessionId = $"{childSessionId}-sub-grandchild";

        await using (var db = await _factory.CreateDbContextAsync())
        {
            var turn = await db.ConversationTurns.SingleAsync(item => item.TurnId == turnId);
            var occurredAt = DateTimeOffset.UtcNow;
            var head = await db.ConversationHeads.SingleAsync(
                item => item.ConversationId == goal.CurrentConversationId);
            head.HeadSequence++;
            db.ConversationEvents.Add(new ConversationEventEntity
            {
                ConversationId = goal.CurrentConversationId,
                Sequence = head.HeadSequence,
                EventId = $"usage-{turnId}",
                WorkspaceId = goal.WorkspaceId,
                TurnId = turnId,
                Type = ConversationEventTypes.UsageRecorded,
                SchemaVersion = 2,
                Payload = "{\"usage\":{\"promptTokens\":100,\"completionTokens\":10}}",
                OccurredAt = occurredAt.ToString("O"),
                CommittedAt = occurredAt.ToString("O"),
                CorrelationId = goal.CurrentConversationId,
                SourceKind = "agent",
            });
            db.SessionSubAgents.AddRange(
                new SessionSubAgentEntity
                {
                    ParentSessionId = goal.CurrentConversationId,
                    SubSessionId = childSessionId,
                    Status = "completed",
                    TaskSummary = "child",
                    SpawnedAt = occurredAt.ToString("O"),
                },
                new SessionSubAgentEntity
                {
                    ParentSessionId = childSessionId,
                    SubSessionId = grandchildSessionId,
                    Status = "completed",
                    TaskSummary = "grandchild",
                    SpawnedAt = occurredAt.ToString("O"),
                });
            db.TokenUsageEvents.AddRange(
                new TokenUsageEventEntity
                {
                    SourceType = "runtime_activity",
                    SourceId = "child-current",
                    WorkspaceId = goal.WorkspaceId,
                    SessionId = childSessionId,
                    ParentSessionId = goal.CurrentConversationId,
                    OccurredAtUtc = occurredAt,
                    YearMonth = occurredAt.ToString("yyyy-MM"),
                    PromptTokens = 200,
                    CompletionTokens = 20,
                    TotalTokens = 220,
                },
                new TokenUsageEventEntity
                {
                    SourceType = "runtime_activity",
                    SourceId = "grandchild-current",
                    WorkspaceId = goal.WorkspaceId,
                    SessionId = grandchildSessionId,
                    ParentSessionId = childSessionId,
                    OccurredAtUtc = occurredAt,
                    YearMonth = occurredAt.ToString("yyyy-MM"),
                    PromptTokens = 300,
                    CompletionTokens = 30,
                    TotalTokens = 330,
                },
                new TokenUsageEventEntity
                {
                    SourceType = "runtime_activity",
                    SourceId = "child-previous-turn",
                    WorkspaceId = goal.WorkspaceId,
                    SessionId = childSessionId,
                    ParentSessionId = goal.CurrentConversationId,
                    OccurredAtUtc = DateTimeOffset.FromUnixTimeMilliseconds(turn.CreatedAt).AddSeconds(-1),
                    YearMonth = occurredAt.ToString("yyyy-MM"),
                    PromptTokens = 9_999,
                    CompletionTokens = 999,
                    TotalTokens = 10_998,
                });
            await db.SaveChangesAsync();
        }

        await CommitSyntheticTerminalAsync(turnId, "completed");
        var candidate = (await NewSettlementStore().GetCandidatesAsync(8)).Single();

        Assert.AreEqual(3, candidate.LlmRounds);
        Assert.AreEqual(600L, candidate.InputTokens);
        Assert.AreEqual(60L, candidate.OutputTokens);
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

    private async Task<(TaskGoalBindingEntity Binding, TaskNodeEntity WorkUnit)>
        CreateBoundExecutionPlanAsync(GoalRunEntity goal, bool includeNext = false)
    {
        await using var db = await _factory.CreateDbContextAsync();
        var now = DateTimeOffset.UtcNow;
        const string taskId = "task-planned";
        const string assignmentId = "assignment-planned";
        const string reservationId = "reservation-planned";
        const string planId = "plan-planned";
        const string rootNodeId = "node-root";
        const string workUnitId = "node-explore";
        var fingerprint = new string('a', 64);
        db.WorkspaceTasks.Add(new WorkspaceTaskEntity
        {
            TaskId = taskId,
            WorkspaceId = goal.WorkspaceId,
            Title = "Planned task",
            AcceptanceCriteria = "focused tests pass",
            Status = PuddingCode.Tasks.WorkspaceTaskStatus.Assigned,
            Priority = PuddingCode.Tasks.TaskPriority.P0,
            ExecutionWindow = PuddingCode.Tasks.TaskExecutionWindow.Anytime,
            Version = 3,
            SortOrder = 0,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            ActiveAssignmentId = assignmentId,
        });
        db.TaskAssignmentAttempts.Add(new TaskAssignmentAttemptEntity
        {
            AttemptId = assignmentId,
            TaskId = taskId,
            WorkspaceId = goal.WorkspaceId,
            AgentId = goal.AgentInstanceId,
            AttemptNumber = 1,
            Status = AssignmentAttemptStatus.Assigned,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            ActiveAtUtc = now,
        });
        var reservation = new AgentExecutionReservationEntity
        {
            ReservationId = reservationId,
            WorkspaceId = goal.WorkspaceId,
            AgentId = goal.AgentInstanceId,
            TaskId = taskId,
            GoalRunId = goal.GoalRunId,
            OwnerId = "scheduler",
            Status = "active",
            LeaseUntilUtc = now.AddHours(1),
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
        db.AgentExecutionReservations.Add(reservation);
        db.TaskPlanRuns.Add(new TaskPlanRunEntity
        {
            PlanId = planId,
            WorkspaceId = goal.WorkspaceId,
            WorkspaceTaskId = taskId,
            WorkspaceTaskVersion = 3,
            PlanVersion = 1,
            SchemaVersion = 1,
            PlanKind = "workspace-task-v1",
            PlanFingerprint = fingerprint,
            RootSessionId = goal.CurrentConversationId,
            LeaderAgentId = goal.AgentInstanceId,
            Objective = "Execute planned task",
            Status = PuddingCode.Models.TaskPlanStatuses.Active.ToString(),
            CreatedAt = now.ToUnixTimeMilliseconds(),
            UpdatedAt = now.ToUnixTimeMilliseconds(),
        });
        db.TaskNodes.Add(new TaskNodeEntity
        {
            TaskNodeId = rootNodeId,
            PlanId = planId,
            Depth = 0,
            SequenceNo = 0,
            Objective = "Execute plan",
            AssignedToKind = "Leader",
            AssignedToId = goal.AgentInstanceId,
            Status = PuddingCode.Models.TaskNodeStatuses.Running.ToString(),
            CreatedAt = now.ToUnixTimeMilliseconds(),
            UpdatedAt = now.ToUnixTimeMilliseconds(),
        });
        var workUnit = new TaskNodeEntity
        {
            TaskNodeId = workUnitId,
            PlanId = planId,
            ParentTaskNodeId = rootNodeId,
            Depth = 1,
            SequenceNo = 1,
            WorkUnitKind = "Explore",
            Objective = "Collect canonical evidence",
            MaxRounds = 25,
            MaxToolCalls = 60,
            MaxDurationSeconds = 1800,
            MaxInputTokens = 150_000,
            MaxOutputTokens = 20_000,
            MaxCost = 1m,
            AssignedToKind = "Leader",
            AssignedToId = goal.AgentInstanceId,
            Status = PuddingCode.Models.TaskNodeStatuses.Planned.ToString(),
            CreatedAt = now.ToUnixTimeMilliseconds(),
            UpdatedAt = now.ToUnixTimeMilliseconds(),
        };
        db.TaskNodes.Add(workUnit);
        if (includeNext)
        {
            db.TaskNodes.Add(new TaskNodeEntity
            {
                TaskNodeId = "node-change",
                PlanId = planId,
                ParentTaskNodeId = rootNodeId,
                Depth = 1,
                SequenceNo = 2,
                WorkUnitKind = "Change",
                Objective = "Apply bounded changes",
                MaxRounds = 40,
                MaxToolCalls = 120,
                MaxDurationSeconds = 3600,
                MaxInputTokens = 250_000,
                MaxOutputTokens = 40_000,
                MaxCost = 2.5m,
                AssignedToKind = "Leader",
                AssignedToId = goal.AgentInstanceId,
                Status = PuddingCode.Models.TaskNodeStatuses.Planned.ToString(),
                CreatedAt = now.ToUnixTimeMilliseconds(),
                UpdatedAt = now.ToUnixTimeMilliseconds(),
            });
        }
        await db.SaveChangesAsync();
        var binding = new TaskGoalBindingEntity
        {
            BindingId = "binding-planned",
            WorkspaceId = goal.WorkspaceId,
            TaskId = taskId,
            AssignmentId = assignmentId,
            ExpectedTaskVersion = 3,
            GoalRunId = goal.GoalRunId,
            AgentInstanceId = goal.AgentInstanceId,
            ReservationId = reservationId,
            ReservationFencingToken = reservation.FencingToken,
            TaskPlanId = planId,
            PlanFingerprint = fingerprint,
            Status = "active",
            CreatedAtUtc = now,
        };
        db.TaskGoalBindings.Add(binding);
        await db.SaveChangesAsync();
        return (binding, workUnit);
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

    private async Task CommitSyntheticTerminalAsync(string turnId, string kind, string payload = "{}")
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
        var command = await db.ChatExecutionCommands.SingleAsync(item => item.TurnId == turnId);
        command.Status = kind == "completed" ? "completed" : kind;
        command.TerminalSequence = sequence;
        command.CompletedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        command.LeaseOwner = null;
        command.LeaseUntil = null;
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
            Payload = payload,
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
        TaskGoalBindingEntity? binding = null,
        TaskNodeEntity? workUnit = null)
    {
        var iterationNo = goal.IterationsStarted + 1;
        var metadata = new Dictionary<string, string>
        {
            [GoalContinuationMetadata.Managed] = "true",
        };
        if (!string.IsNullOrWhiteSpace(binding?.TaskPlanId))
            metadata[GoalContinuationMetadata.TaskPlanId] = binding.TaskPlanId;
        if (!string.IsNullOrWhiteSpace(binding?.PlanFingerprint))
            metadata[GoalContinuationMetadata.TaskPlanFingerprint] = binding.PlanFingerprint;
        if (workUnit is not null)
        {
            metadata[GoalContinuationMetadata.TaskNodeId] = workUnit.TaskNodeId;
            if (!string.IsNullOrWhiteSpace(workUnit.ParentTaskNodeId))
                metadata[GoalContinuationMetadata.ParentTaskNodeId] = workUnit.ParentTaskNodeId;
        }

        return new SubmitTurnRequest
        {
            ClientRequestId = lease.OutboxId,
            ClientMessageId = $"gm-{goal.GoalRunId}-{goal.ActivationEpoch}-{iterationNo}",
            Recipients = new RecipientRequest { Type = "agent", AgentIds = [goal.AgentInstanceId] },
            Content = [new ContentPart { Type = "text", Text = "continue" }],
            Metadata = metadata,
            GoalContinuation = new GoalContinuationAcceptanceContext
            {
                OutboxId = lease.OutboxId,
                GoalRunId = goal.GoalRunId,
                ActivationEpoch = goal.ActivationEpoch,
                AggregateVersion = goal.AggregateVersion,
                IterationNo = iterationNo,
                LeaseOwner = lease.LeaseOwner!,
                FencingToken = lease.FencingToken,
                TaskId = binding?.TaskId,
                ExpectedTaskVersion = binding?.ExpectedTaskVersion,
                ReservationFencingToken = binding?.ReservationFencingToken,
                TaskPlanId = binding?.TaskPlanId,
                TaskPlanFingerprint = binding?.PlanFingerprint,
                TaskNodeId = workUnit?.TaskNodeId,
                ParentTaskNodeId = workUnit?.ParentTaskNodeId,
            },
        };
    }

    private static SubmitTurnRequest BuildUserRequest() => new()
    {
        ClientRequestId = "user-request-1",
        ClientMessageId = "user-message-1",
        Recipients = new RecipientRequest { Type = "agent", AgentIds = ["agent-1"] },
        Content = [new ContentPart { Type = "text", Text = "用户消息优先" }],
    };
}
