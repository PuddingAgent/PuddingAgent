using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
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
    public void BuildPrompt_InjectsFourPhaseLoopEnvelope_WithPlanProgress()
    {
        var steps = new List<TaskNodeEntity>
        {
            new()
            {
                TaskNodeId = "node-1",
                PlanId = "plan-1",
                Depth = 1,
                SequenceNo = 10,
                WorkUnitKind = "Explore",
                Title = "Step one",
                Status = "Completed",
            },
            new()
            {
                TaskNodeId = "node-2",
                PlanId = "plan-1",
                Depth = 1,
                SequenceNo = 20,
                WorkUnitKind = "Change",
                Title = "Step two",
                Status = "InProgress",
            },
            new()
            {
                TaskNodeId = "node-3",
                PlanId = "plan-1",
                Depth = 1,
                SequenceNo = 30,
                WorkUnitKind = "Test",
                Title = "Step three",
                Status = "Draft",
            },
        };

        var prompt = GoalContinuationWorker.BuildPrompt(
            new GoalRunEntity
            {
                GoalRunId = "goal-loop",
                Objective = "loop envelope",
                ObjectiveVersion = 1,
                MaxIterations = 8,
                IterationsStarted = 0,
            },
            binding: new TaskGoalBindingEntity { TaskId = "task-1", TaskPlanId = "plan-1" },
            task: new WorkspaceTaskEntity
            {
                TaskId = "task-1",
                Status = PuddingCode.Tasks.WorkspaceTaskStatus.InProgress,
            },
            workUnit: steps[1],
            iterationNo: 2,
            planSteps: steps);

        StringAssert.Contains(prompt, "four phases");
        StringAssert.Contains(prompt, "Observe:");
        StringAssert.Contains(prompt, "Plan:");
        StringAssert.Contains(prompt, "Act:");
        StringAssert.Contains(prompt, "Verify:");

        using var doc = ParseGoalPayload(prompt);
        var root = doc.RootElement;
        var loop = root.GetProperty("loop");
        Assert.AreEqual(4, loop.GetProperty("phases").GetArrayLength());
        Assert.AreEqual("observe", loop.GetProperty("phases")[0].GetString());
        Assert.AreEqual("plan", loop.GetProperty("phases")[1].GetString());
        Assert.AreEqual("act", loop.GetProperty("phases")[2].GetString());
        Assert.AreEqual("verify", loop.GetProperty("phases")[3].GetString());
        Assert.AreEqual("observe", loop.GetProperty("current").GetString());
        Assert.AreEqual(2, loop.GetProperty("stepIndex").GetInt32());
        Assert.AreEqual(3, loop.GetProperty("stepTotal").GetInt32());

        var currentStep = root.GetProperty("currentStep");
        Assert.AreEqual("node-2", currentStep.GetProperty("nodeId").GetString());
        Assert.AreEqual("Change", currentStep.GetProperty("kind").GetString());
        Assert.AreEqual("Step two", currentStep.GetProperty("title").GetString());
        Assert.AreEqual(20, currentStep.GetProperty("sequenceNo").GetInt32());
        Assert.AreEqual("InProgress", currentStep.GetProperty("status").GetString());

        var progress = root.GetProperty("progress");
        Assert.AreEqual(1, progress.GetProperty("stepsPassed").GetInt32());
        Assert.AreEqual(3, progress.GetProperty("stepsTotal").GetInt32());
    }

    [TestMethod]
    public void BuildPrompt_WithoutFrozenPlan_CurrentStepIsNull_AndProgressZeroed()
    {
        var prompt = GoalContinuationWorker.BuildPrompt(
            new GoalRunEntity
            {
                GoalRunId = "goal-loop-empty",
                Objective = "loop envelope empty",
                ObjectiveVersion = 1,
                MaxIterations = 8,
                IterationsStarted = 0,
            },
            binding: null,
            task: null,
            workUnit: null,
            iterationNo: 1);

        StringAssert.Contains(prompt, "four phases");

        using var doc = ParseGoalPayload(prompt);
        var root = doc.RootElement;
        var loop = root.GetProperty("loop");
        Assert.AreEqual(4, loop.GetProperty("phases").GetArrayLength());
        Assert.AreEqual("observe", loop.GetProperty("current").GetString());
        Assert.AreEqual(0, loop.GetProperty("stepIndex").GetInt32());
        Assert.AreEqual(0, loop.GetProperty("stepTotal").GetInt32());
        Assert.AreEqual(JsonValueKind.Null, root.GetProperty("currentStep").ValueKind);
        Assert.AreEqual(0, root.GetProperty("progress").GetProperty("stepsPassed").GetInt32());
        Assert.AreEqual(0, root.GetProperty("progress").GetProperty("stepsTotal").GetInt32());
    }

    [TestMethod]
    public void BuildPrompt_FirstIteration_LastVerdictIsNull()
    {
        var prompt = GoalContinuationWorker.BuildPrompt(
            new GoalRunEntity
            {
                GoalRunId = "goal-first",
                Objective = "first iteration",
                ObjectiveVersion = 1,
                MaxIterations = 8,
                IterationsStarted = 0,
            },
            binding: null,
            task: null,
            workUnit: null,
            iterationNo: 1);

        using var doc = ParseGoalPayload(prompt);
        var root = doc.RootElement;
        // 纯增量：既有字段不变，acceptanceContract 是最后一个字段（合同缺位时为 null，不伪造空对象）。
        Assert.AreEqual("goal-first", root.GetProperty("goalRunId").GetString());
        Assert.AreEqual("acceptanceContract", root.EnumerateObject().Last().Name);
        Assert.AreEqual(JsonValueKind.Null, root.GetProperty("lastVerdict").ValueKind);
    }

    [TestMethod]
    public void BuildPrompt_WithLatestVerdict_MapsRecord_WithSummaryTruncation()
    {
        var criteria = new List<string>();
        for (var i = 0; i < 12; i++)
            criteria.Add($"unmet-{i}-" + new string('x', 250));
        var completedAt = new DateTimeOffset(2026, 9, 16, 3, 30, 0, TimeSpan.Zero);
        var verification = new GoalVerificationEntity
        {
            VerificationId = "gv-goal-verdict-1-2",
            GoalRunId = "goal-verdict",
            ActivationEpoch = 1,
            IterationNo = 2,
            Status = "succeeded",
            Verdict = "blocked",
            BlockerCode = "acceptance_not_verified",
            UnmetCriteriaJson = JsonSerializer.Serialize(
                criteria, new JsonSerializerOptions(JsonSerializerDefaults.Web)),
            CompletedAtUtc = completedAt,
        };

        var prompt = GoalContinuationWorker.BuildPrompt(
            new GoalRunEntity
            {
                GoalRunId = "goal-verdict",
                Objective = "verdict echo",
                ObjectiveVersion = 1,
                MaxIterations = 8,
                IterationsStarted = 2,
            },
            binding: null,
            task: null,
            workUnit: null,
            iterationNo: 3,
            lastVerification: verification);

        using var doc = ParseGoalPayload(prompt);
        var root = doc.RootElement;
        var lastVerdict = root.GetProperty("lastVerdict");
        Assert.AreEqual("blocked", lastVerdict.GetProperty("outcome").GetString());
        Assert.AreEqual("acceptance_not_verified", lastVerdict.GetProperty("blockerCode").GetString());
        var unmet = lastVerdict.GetProperty("unmetCriteria");
        Assert.AreEqual(10, unmet.GetArrayLength());
        foreach (var item in unmet.EnumerateArray())
        {
            Assert.IsTrue(item.GetString()!.Length <= 200, "each unmetCriteria item must be <=200 chars");
            Assert.IsTrue(item.GetString()!.StartsWith("unmet-", StringComparison.Ordinal));
        }
        var roundTripped = DateTimeOffset.Parse(
            lastVerdict.GetProperty("completedAtUtc").GetString()!,
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.RoundtripKind);
        Assert.AreEqual(completedAt, roundTripped);
        // 既有字段不受影响。
        Assert.AreEqual(3, root.GetProperty("iteration").GetInt32());
        StringAssert.Contains(prompt, "lastVerdict is present");
    }

    [TestMethod]
    public void BuildPrompt_WithCorruptVerdictRecord_FailsSoftWithoutThrowing()
    {
        var verification = new GoalVerificationEntity
        {
            VerificationId = "gv-goal-corrupt-1-1",
            GoalRunId = "goal-corrupt",
            ActivationEpoch = 1,
            IterationNo = 1,
            Status = "succeeded",
            Verdict = "continue",
            BlockerCode = null,
            UnmetCriteriaJson = "{not-valid-json",
        };

        var prompt = GoalContinuationWorker.BuildPrompt(
            new GoalRunEntity
            {
                GoalRunId = "goal-corrupt",
                Objective = "corrupt verdict",
                ObjectiveVersion = 1,
                MaxIterations = 8,
                IterationsStarted = 1,
            },
            binding: null,
            task: null,
            workUnit: null,
            iterationNo: 2,
            lastVerification: verification);

        using var doc = ParseGoalPayload(prompt);
        var lastVerdict = doc.RootElement.GetProperty("lastVerdict");
        // fail-soft：不抛异常；真实列保留，损坏列表降级为空。
        Assert.AreEqual("continue", lastVerdict.GetProperty("outcome").GetString());
        Assert.AreEqual(JsonValueKind.Null, lastVerdict.GetProperty("blockerCode").ValueKind);
        Assert.AreEqual(0, lastVerdict.GetProperty("unmetCriteria").GetArrayLength());
    }

    [TestMethod]
    public void BuildPrompt_WithAcceptanceContract_ProjectsSummary_AndProposalHint()
    {
        var prompt = GoalContinuationWorker.BuildPrompt(
            new GoalRunEntity
            {
                GoalRunId = "goal-contract",
                Objective = "wire contract",
                ObjectiveVersion = 2,
                MaxIterations = 8,
                IterationsStarted = 0,
            },
            binding: null,
            task: null,
            workUnit: null,
            iterationNo: 1,
            acceptanceContract: new GoalAcceptanceContractEntity
            {
                ContractId = "gc-goal-contract-1-2",
                GoalRunId = "goal-contract",
                ActivationEpoch = 1,
                ObjectiveVersion = 2,
                ContractVersion = 3,
                CriteriaJson = "[{\"id\":\"c1\",\"requirement\":\"build passes\"},{\"id\":\"c2\",\"requirement\":\"tests green\"}]",
                Source = "bounded_planning",
            });

        StringAssert.Contains(prompt, "meta.goal_contract_proposal");
        StringAssert.Contains(prompt, "refine_acceptance_contract");

        using var doc = ParseGoalPayload(prompt);
        var contract = doc.RootElement.GetProperty("acceptanceContract");
        Assert.AreEqual("bounded_planning", contract.GetProperty("source").GetString());
        Assert.AreEqual(3, contract.GetProperty("contractVersion").GetInt32());
        Assert.AreEqual(2, contract.GetProperty("criteriaCount").GetInt32());
    }

    [TestMethod]
    public void BuildPrompt_WithoutAcceptanceContract_PayloadSummaryIsNull()
    {
        var prompt = GoalContinuationWorker.BuildPrompt(
            new GoalRunEntity
            {
                GoalRunId = "goal-no-contract",
                Objective = "no contract yet",
                ObjectiveVersion = 1,
                MaxIterations = 8,
                IterationsStarted = 0,
            },
            binding: null,
            task: null,
            workUnit: null,
            iterationNo: 1);

        using var doc = ParseGoalPayload(prompt);
        Assert.AreEqual(
            JsonValueKind.Null,
            doc.RootElement.GetProperty("acceptanceContract").ValueKind);
    }

    [TestMethod]
    public async Task FindLatestVerificationAsync_NoSettledIteration_ReturnsNull()
    {
        await using var db = await _factory.CreateDbContextAsync();
        var store = NewGoalStore(db);

        Assert.IsNull(await store.FindLatestVerificationAsync("goal-no-verdict", CancellationToken.None));
    }

    [TestMethod]
    public async Task FindLatestVerificationAsync_PicksHighestEpochThenIteration()
    {
        await using var db = await _factory.CreateDbContextAsync();
        db.GoalVerifications.AddRange(
            new GoalVerificationEntity
            {
                VerificationId = "gv-goal-order-1-1",
                GoalRunId = "goal-order",
                ActivationEpoch = 1,
                IterationNo = 1,
                Status = "succeeded",
                Verdict = "blocked",
                BlockerCode = "one",
            },
            new GoalVerificationEntity
            {
                VerificationId = "gv-goal-order-1-2",
                GoalRunId = "goal-order",
                ActivationEpoch = 1,
                IterationNo = 2,
                Status = "succeeded",
                Verdict = "continue",
            },
            new GoalVerificationEntity
            {
                VerificationId = "gv-goal-order-2-1",
                GoalRunId = "goal-order",
                ActivationEpoch = 2,
                IterationNo = 1,
                Status = "succeeded",
                Verdict = "needs_user",
                BlockerCode = "awaiting_user",
            });
        await db.SaveChangesAsync();
        var store = NewGoalStore(db);

        var latest = await store.FindLatestVerificationAsync("goal-order", CancellationToken.None);

        Assert.IsNotNull(latest);
        Assert.AreEqual(2, latest.ActivationEpoch);
        Assert.AreEqual(1, latest.IterationNo);
        Assert.AreEqual("needs_user", latest.Verdict);
    }

    private static JsonDocument ParseGoalPayload(string prompt)
    {
        const string open = "<goal_payload>";
        const string close = "</goal_payload>";
        var start = prompt.IndexOf(open, StringComparison.Ordinal) + open.Length;
        var end = prompt.LastIndexOf(close, StringComparison.Ordinal);
        return JsonDocument.Parse(prompt[start..end]);
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

        // 卡 2b60040d：镜像 GoalContinuationWorker 的 ADR-072 §9.1 ActiveTask metadata 注入，
        // 验证 ExecutionCommandReader → ActiveTaskMetadata.TryBuild 的全键映射。
        var baseRequest = BuildContinuationRequest(goal, lease, binding, workUnit);
        var activeTaskMetadata = new Dictionary<string, string>(baseRequest.Metadata!)
        {
            ["origin"] = "task.auto",
            ["task_id"] = binding.TaskId,
            ["assignment_id"] = binding.AssignmentId,
            ["dispatch_idempotency_key"] = binding.IdempotencyKey ?? binding.BindingId,
        };
        if (binding.ReservationFencingToken.HasValue)
        {
            activeTaskMetadata["reservation_fencing_token"] =
                binding.ReservationFencingToken.Value.ToString();
        }

        await using (var taskDb = await _factory.CreateDbContextAsync())
        {
            var task = await taskDb.WorkspaceTasks.AsNoTracking().SingleAsync(
                item => item.TaskId == binding.TaskId);
            activeTaskMetadata["expected_version"] =
                binding.ExpectedTaskVersion?.ToString() ?? task.Version.ToString();
            activeTaskMetadata["priority"] = task.Priority.ToString().ToLowerInvariant();
            activeTaskMetadata["execution_window"] = task.ExecutionWindow switch
            {
                PuddingCode.Tasks.TaskExecutionWindow.Anytime => "anytime",
                PuddingCode.Tasks.TaskExecutionWindow.OffPeakOnly => "off_peak_only",
                _ => "inherit",
            };
        }

        var request = baseRequest with { Metadata = activeTaskMetadata };

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

        var reader = new ExecutionCommandReader(_factory);
        var command = await reader.GetAsync(accepted.CommandIds.Single());
        Assert.IsNotNull(command?.WorkUnit);
        Assert.AreEqual(binding.TaskPlanId, command.WorkUnit.PlanId);
        Assert.AreEqual(workUnit.TaskNodeId, command.WorkUnit.TaskNodeId);
        Assert.AreEqual(25, command.WorkUnit.MaxRounds);
        Assert.AreEqual(60, command.WorkUnit.MaxToolCallsTotal);
        Assert.AreEqual(1800, command.WorkUnit.MaxDurationSeconds);
        Assert.AreEqual(150_000, command.WorkUnit.MaxInputTokens);

        // 卡 2b60040d：canonical 命令路径必须把 metadata 键集解析为 ActiveTask（与投递路径共用唯一映射器）。
        Assert.IsNotNull(command.ActiveTask);
        Assert.AreEqual(goal.WorkspaceId, command.ActiveTask.WorkspaceId);
        Assert.AreEqual("task-planned", command.ActiveTask.TaskId);
        Assert.AreEqual("assignment-planned", command.ActiveTask.AssignmentId);
        Assert.AreEqual(goal.AgentInstanceId, command.ActiveTask.AgentId);
        Assert.AreEqual("task.auto", command.ActiveTask.Origin);
        Assert.AreEqual("p0", command.ActiveTask.Priority);
        Assert.AreEqual("anytime", command.ActiveTask.ExecutionWindow);
        Assert.AreEqual(3, command.ActiveTask.ExpectedVersion);
        Assert.AreEqual("binding-planned", command.ActiveTask.DispatchIdempotencyKey);
        Assert.AreEqual(binding.ReservationFencingToken.Value.ToString(), command.ActiveTask.ReservationFencingToken);
        Assert.IsNull(command.ActiveTask.PolicyVersion);
        Assert.IsNull(command.ActiveTask.DeliveryId);

        await using var verify = await _factory.CreateDbContextAsync();
        var persistedNode = await verify.TaskNodes.SingleAsync(
            item => item.TaskNodeId == workUnit.TaskNodeId);
        Assert.AreEqual(PuddingCode.Models.TaskNodeStatuses.Running.ToString(), persistedNode.Status);
        Assert.IsNotNull(persistedNode.StartedAt);

        // A frozen v1 plan must not silently acquire the new input-capacity semantics.
        var oldPlan = await verify.TaskPlanRuns.SingleAsync();
        oldPlan.PlanVersion = 1;
        await verify.SaveChangesAsync();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => reader.GetAsync(accepted.CommandIds.Single()));
        StringAssert.Contains(error.Message, "task_execution_plan_version_unsupported");
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
        // ADR-092 §4 + §13.6：本夹具从不播种 GoalAcceptanceContracts，capsule.Criteria 恒为空，
        // verifier 必须 fail-closed 落 acceptance_contract_missing（Repair），不得 vacuous pass。
        Assert.AreEqual(GoalVerificationVerdict.Blocked, decision.Verdict);
        Assert.AreEqual("acceptance_contract_missing", decision.BlockerCode);
        Assert.AreEqual(GoalSettlementDispositions.Repair,
            GoalSettlementDecisionCalculator.ComputeDisposition(decision));
        Assert.IsTrue(await settlement.ApplyAsync(candidate, decision));

        await using (var verify = await _factory.CreateDbContextAsync())
        {
            var nodes = await verify.TaskNodes.Where(item => item.Depth == 1)
                .OrderBy(item => item.SequenceNo)
                .ToListAsync();
            // 当前（Running）单元没有验收证据 ⇒ 不得被结算完成，保持原状。
            Assert.AreEqual(PuddingCode.Models.TaskNodeStatuses.Running.ToString(), nodes[0].Status);
            // 下一单元不得被认领。
            Assert.AreEqual(PuddingCode.Models.TaskNodeStatuses.Planned.ToString(), nodes[1].Status);
            Assert.AreEqual(PuddingCode.Models.TaskPlanStatuses.Active.ToString(),
                (await verify.TaskPlanRuns.SingleAsync()).Status);
            // 结算冻结的是本次迭代后的实时 Task 版本（3 → 4）。
            Assert.AreEqual(4, (await verify.TaskGoalBindings.SingleAsync()).ExpectedTaskVersion);
            var settledGoal = await verify.GoalRuns.SingleAsync();
            Assert.AreEqual(GoalPhase.Active, settledGoal.Status);
            Assert.IsNull(settledGoal.TerminalAtUtc);
            Assert.AreEqual("acceptance_contract_missing", settledGoal.BlockedCode);
            // Repair 恰好投递一轮续行。
            Assert.AreEqual(1, await verify.GoalOutbox.CountAsync(
                item => item.Status == GoalOutboxValues.Pending));
            Assert.AreEqual(2, await verify.GoalOutbox.CountAsync());
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
        // 当前单元仍未验证完成（保持 Running），下一单元因此在准入层就被拒绝认领。
        var rejection = await Assert.ThrowsAsync<GoalContinuationAcceptanceException>(() =>
            nextStore.AcceptBatchAsync(
                BuildContinuationRequest(currentGoal, nextLease, currentBinding, nextWorkUnit),
                currentGoal.WorkspaceId,
                currentGoal.CurrentConversationId,
                userId: null,
                CancellationToken.None));
        Assert.AreEqual(GoalContinuationAcceptanceErrorCodes.TaskPlanChanged, rejection.Code);
        Assert.IsFalse(rejection.Deferred);
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
        // ADR-092 §4 + ADR-092「对旧设计的修订」：空验收合同不得 vacuous pass，
        // 且“最后一个 WorkUnit 缺 Task 完成事实”不再终止 Goal，而是本单元内的有界修复（Repair）。
        Assert.AreEqual(GoalVerificationVerdict.Blocked, proposed.Verdict);
        Assert.AreEqual("acceptance_contract_missing", proposed.BlockerCode);
        Assert.AreEqual(GoalSettlementDispositions.Repair,
            GoalSettlementDecisionCalculator.ComputeDisposition(proposed));
        Assert.IsTrue(await settlement.ApplyAsync(candidate, proposed));

        await using var verify = await _factory.CreateDbContextAsync();
        var settledGoal = await verify.GoalRuns.SingleAsync();
        // Repair：Goal 保持非终态，仅归档阻塞事实（ADR-092 决策 6：保留 Goal 与逻辑绑定）。
        Assert.AreEqual(GoalPhase.Active, settledGoal.Status);
        Assert.IsNull(settledGoal.TerminalAtUtc);
        Assert.AreEqual("acceptance_contract_missing", settledGoal.BlockedCode);
        // 保留逻辑归属：Task / attempt / binding / reservation 均不得被释放。
        var settledTask = await verify.WorkspaceTasks.SingleAsync();
        Assert.AreEqual(PuddingCode.Tasks.WorkspaceTaskStatus.Assigned, settledTask.Status);
        Assert.AreEqual("assignment-planned", settledTask.ActiveAssignmentId);
        Assert.IsNull(settledTask.BlockerKind);
        var settledBinding = await verify.TaskGoalBindings.SingleAsync();
        Assert.AreEqual("active", settledBinding.Status);
        Assert.IsNull(settledBinding.ReleasedAtUtc);
        var settledAttempt = await verify.TaskAssignmentAttempts.SingleAsync();
        Assert.AreEqual(AssignmentAttemptStatus.Assigned, settledAttempt.Status);
        Assert.IsNull(settledAttempt.ReleasedAtUtc);
        Assert.AreEqual("active", (await verify.AgentExecutionReservations.SingleAsync()).Status);
        // 未验证完成：当前单元保持 Running，计划保持 Active。
        Assert.AreEqual(PuddingCode.Models.TaskNodeStatuses.Running.ToString(),
            (await verify.TaskNodes.SingleAsync(item => item.Depth == 1)).Status);
        Assert.AreEqual(PuddingCode.Models.TaskPlanStatuses.Active.ToString(),
            (await verify.TaskPlanRuns.SingleAsync()).Status);
        // 恰好投递一轮 continuation（Repair 必须给出下一步，不得停在原地）。
        Assert.AreEqual(1, await verify.GoalOutbox.CountAsync(
            item => item.Status == GoalOutboxValues.Pending));
        Assert.AreEqual(2, await verify.GoalOutbox.CountAsync());
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
        // ADR-092「对旧设计的修订」：非 completed Turn 不再终止整个目标（取舍表明确不采用
        // “测试失败直接 Goal Failed，再新建 Goal 重试”）；iteration_failed 属可修复族 ⇒ Repair。
        Assert.AreEqual(GoalSettlementDispositions.Repair,
            GoalSettlementDecisionCalculator.ComputeDisposition(decision));
        Assert.IsTrue(await settlement.ApplyAsync(candidate, decision));

        await using var verify = await _factory.CreateDbContextAsync();
        var settledGoal = await verify.GoalRuns.SingleAsync();
        // Goal 保持 Active（非终态），仅归档阻塞事实。
        Assert.AreEqual(GoalPhase.Active, settledGoal.Status);
        Assert.IsNull(settledGoal.TerminalAtUtc);
        Assert.AreEqual("iteration_failed", settledGoal.BlockedCode);
        // ADR-092 决策 6：保留逻辑归属（Goal / Task binding / assignment）；执行租约只续不释放，
        // 恢复时由新的 admission/预约取得新 fence——因此如实断言“仍归属”而不是“已释放”。
        var settledTask = await verify.WorkspaceTasks.SingleAsync();
        Assert.AreEqual(PuddingCode.Tasks.WorkspaceTaskStatus.Assigned, settledTask.Status);
        Assert.AreEqual("assignment-planned", settledTask.ActiveAssignmentId);
        var settledBinding = await verify.TaskGoalBindings.SingleAsync();
        Assert.AreEqual("active", settledBinding.Status);
        Assert.IsNull(settledBinding.ReleasedAtUtc);
        var settledAttempt = await verify.TaskAssignmentAttempts.SingleAsync();
        Assert.AreEqual(AssignmentAttemptStatus.Assigned, settledAttempt.Status);
        Assert.IsNull(settledAttempt.ReleasedAtUtc);
        Assert.AreEqual("active", (await verify.AgentExecutionReservations.SingleAsync()).Status);
        // 未验证完成：计划/单元保持非终态与执行身份。
        Assert.AreEqual(PuddingCode.Models.TaskNodeStatuses.Running.ToString(),
            (await verify.TaskNodes.SingleAsync(item => item.Depth == 1)).Status);
        Assert.AreEqual(PuddingCode.Models.TaskPlanStatuses.Active.ToString(),
            (await verify.TaskPlanRuns.SingleAsync()).Status);
        // Repair 必须给出下一轮 continuation。
        Assert.AreEqual(1, await verify.GoalOutbox.CountAsync(
            item => item.Status == GoalOutboxValues.Pending));
        Assert.AreEqual(2, await verify.GoalOutbox.CountAsync());
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
        // ADR-092：回合失败是当前单元可修复的未通过，Goal 保持非终态，但必须归档真实 errorCode。
        Assert.AreEqual(GoalPhase.Active, archivedGoal.Status);
        Assert.IsNull(archivedGoal.TerminalAtUtc);
        Assert.AreEqual("work_unit_budget_exhausted", archivedGoal.BlockedCode);
        Assert.AreEqual(
            "WorkUnit input Token budget exhausted (input 150000 tokens).",
            archivedGoal.BlockedMessage);
        // Repair 不释放逻辑归属：Task 保持可续行，真实 errorCode 不得被错误改写成 Task blocker。
        var archivedTask = await verify.WorkspaceTasks.SingleAsync();
        Assert.AreEqual(PuddingCode.Tasks.WorkspaceTaskStatus.Assigned, archivedTask.Status);
        Assert.AreEqual("assignment-planned", archivedTask.ActiveAssignmentId);
        Assert.IsNull(archivedTask.BlockerKind);
        Assert.IsNull(archivedTask.BlockerReason);
        // goal_iterations.error_id must archive the real errorCode from the
        // turn.failed payload, not stay null.
        var archivedIteration = await verify.GoalIterations.SingleAsync();
        Assert.AreEqual("work_unit_budget_exhausted", archivedIteration.ErrorId);
        Assert.AreEqual("failed", archivedIteration.Status);
        // 且下一轮 continuation 已投递。
        Assert.AreEqual(1, await verify.GoalOutbox.CountAsync(
            item => item.Status == GoalOutboxValues.Pending));
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
        // ADR-092 §4：Turn 结束、Task Completed 都不是完成证明；空合同 ⇒ Blocked + Repair。
        Assert.AreEqual(GoalVerificationVerdict.Blocked, decision.Verdict);
        Assert.AreEqual("acceptance_contract_missing", decision.BlockerCode);
        Assert.AreEqual(GoalSettlementDispositions.Repair,
            GoalSettlementDecisionCalculator.ComputeDisposition(decision));
        Assert.IsTrue(await settlement.ApplyAsync(candidate, decision));

        await using var verify = await _factory.CreateDbContextAsync();
        var persistedGoal = await verify.GoalRuns.SingleAsync();
        Assert.AreEqual(GoalPhase.Active, persistedGoal.Status);
        Assert.IsNull(persistedGoal.TerminalAtUtc);
        Assert.AreEqual("acceptance_contract_missing", persistedGoal.BlockedCode);
        Assert.AreEqual(1, persistedGoal.IterationsStarted);
        Assert.AreEqual(1, persistedGoal.IterationsSettled);
        Assert.AreEqual("settled", (await verify.GoalIterations.SingleAsync()).Status);
        Assert.AreEqual("blocked", (await verify.GoalVerifications.SingleAsync()).Verdict);
        // 保留原续行断言：Repair 仍恰好投递一轮下一 iteration（目标保持续行）。
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
        // 与 verdict 门禁解耦：本测试只关心“128 条以上流事件之后 Turn 终态证据仍完整”，
        // 因此证据断言保持不变，verdict 按 ADR-092 空合同语义重定基线。
        Assert.AreEqual(GoalVerificationVerdict.Blocked, decision.Verdict);
        Assert.AreEqual("acceptance_contract_missing", decision.BlockerCode);
        Assert.AreEqual(GoalSettlementDispositions.Repair,
            GoalSettlementDecisionCalculator.ComputeDisposition(decision));
        Assert.IsTrue(await settlement.ApplyAsync(candidate, decision));

        await using var verify = await _factory.CreateDbContextAsync();
        var persistedGoal = await verify.GoalRuns.SingleAsync();
        var persistedIteration = await verify.GoalIterations.SingleAsync();
        // 终态证据与目标终态解耦：证据完整不意味着 Goal 可以完成。
        Assert.AreEqual(GoalPhase.Active, persistedGoal.Status);
        Assert.IsNull(persistedGoal.TerminalAtUtc);
        Assert.AreEqual("acceptance_contract_missing", persistedGoal.BlockedCode);
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

    // ADR-092「对旧设计的修订」逐字修订了旧 ADR-074 的“非 completed Turn 一律终止整个目标”，
    // 取舍表也明确不采用“回合失败直接 Goal Failed、再新建 Goal 重试”（丢失目标身份、归属与累计预算）。
    // 因此回合失败 = 本单元内可修复的未通过（Repair）：Goal 保持 Active，并投递下一轮 continuation。
    [TestMethod]
    public async Task FailedTurn_IsRepairableAndQueuesNextIteration()
    {
        var goal = await CreateGoalWithContinuationAsync();
        var lease = await ClaimAsync();
        var accepted = await AcceptContinuationAsync(goal, lease);
        await CommitSyntheticTerminalAsync(accepted.TurnIds.Single(), "failed");

        var settlement = NewSettlementStore();
        var candidate = (await settlement.GetCandidatesAsync(8)).Single();
        var decision = await new ConservativeGoalIterationVerifier().VerifyAsync(candidate.ToCapsule());
        Assert.AreEqual(GoalVerificationVerdict.Blocked, decision.Verdict);
        Assert.AreEqual("iteration_failed", decision.BlockerCode);
        Assert.AreEqual(GoalSettlementDispositions.Repair,
            GoalSettlementDecisionCalculator.ComputeDisposition(decision));
        Assert.IsTrue(await settlement.ApplyAsync(candidate, decision));

        await using var verify = await _factory.CreateDbContextAsync();
        var persistedGoal = await verify.GoalRuns.SingleAsync();
        // 不再 fail-closed 终结：Goal 仍可续行，仅归档阻塞事实。
        Assert.AreEqual(GoalPhase.Active, persistedGoal.Status);
        Assert.IsNull(persistedGoal.TerminalAtUtc);
        Assert.AreEqual("iteration_failed", persistedGoal.BlockedCode);
        // 恰好投递一轮下一 iteration 的 continuation。
        Assert.AreEqual(1, await verify.GoalOutbox.CountAsync(
            item => item.Status == GoalOutboxValues.Pending));
        Assert.AreEqual(2, await verify.GoalOutbox.CountAsync());
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
        // ADR-092 §5/§13.3：Task Completed 只是 completion proposal，既不充分（没有验收合同/
        // goal 级同版本 passed 证据就不得 Complete）也非必要；本夹具下只能落 Blocked + acceptance_contract_missing。
        Assert.AreEqual(GoalVerificationVerdict.Blocked, decision.Verdict);
        Assert.AreEqual("acceptance_contract_missing", decision.BlockerCode);
        Assert.AreEqual(GoalSettlementDispositions.Repair,
            GoalSettlementDecisionCalculator.ComputeDisposition(decision));
        Assert.IsTrue(await settlement.ApplyAsync(candidate, decision));

        await using var verify = await _factory.CreateDbContextAsync();
        var settledGoal = await verify.GoalRuns.SingleAsync();
        Assert.AreEqual(GoalPhase.Active, settledGoal.Status);
        Assert.IsNull(settledGoal.TerminalAtUtc);
        Assert.AreEqual("acceptance_contract_missing", settledGoal.BlockedCode);
        // Repair 不释放逻辑归属。
        var settledBinding = await verify.TaskGoalBindings.SingleAsync();
        Assert.AreEqual("active", settledBinding.Status);
        Assert.IsNull(settledBinding.ReleasedAtUtc);
        // 未完成 ⇒ 不得产生 canonical TaskGoalCompleted 事件。
        Assert.AreEqual(0, await verify.ConversationEvents.CountAsync(
            item => item.Type == GoalEventTypes.TaskGoalCompleted));
        // Repair 投递下一轮 continuation。
        Assert.AreEqual(1, await verify.GoalOutbox.CountAsync(
            item => item.Status == GoalOutboxValues.Pending));
        Assert.AreEqual(2, await verify.GoalOutbox.CountAsync());
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
            PlanVersion = PuddingCode.Scheduling.TaskExecutionPlanSnapshot.CurrentPlanVersion,
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

    // ── P0-4：迭代预算 wrap-up steering（受理成功锚点投递） ──

    private static (GoalContinuationWorker Worker, SessionSteeringService Steering) NewWrapUpWorker(
        GoalOutboxStore outboxStore,
        PlatformDbContextFactory factory,
        GoalRunOptions options)
    {
        var steering = new SessionSteeringService(factory, NullLogger<SessionSteeringService>.Instance);
        var services = new ServiceCollection();
        services.AddScoped<PlatformDbContext>(sp => factory.CreateDbContext());
        services.AddScoped<GoalRunStore>(sp => new GoalRunStore(
            sp.GetRequiredService<PlatformDbContext>(),
            new NoopSignal(),
            NullLogger<GoalRunStore>.Instance));
        services.AddScoped<IConversationAcceptanceStore>(sp => new ConversationAcceptanceStore(
            sp.GetRequiredService<PlatformDbContext>(),
            new NoopSignal(),
            NullLogger<ConversationAcceptanceStore>.Instance));
        // 卡 353ece3b：DispatchOneAsync 经 DI 解析验收合同仓库，测试容器同步补注册。
        services.AddSingleton<IDbContextFactory<PlatformDbContext>>(factory);
        services.AddScoped<GoalAcceptanceContractStore>();
        var provider = services.BuildServiceProvider();
        var worker = new GoalContinuationWorker(
            provider.GetRequiredService<IServiceScopeFactory>(),
            outboxStore,
            new GoalOutboxSignal(),
            Options.Create(options),
            TimeProvider.System,
            NullLogger<GoalContinuationWorker>.Instance,
            steering);
        return (worker, steering);
    }

    private async Task SetBudgetProgressAsync(string goalRunId, int iterations, int? maxIterations = null)
    {
        await using var db = await _factory.CreateDbContextAsync();
        var goal = await db.GoalRuns.SingleAsync(item => item.GoalRunId == goalRunId);
        goal.IterationsStarted = iterations;
        goal.IterationsSettled = iterations;
        if (maxIterations is { } max)
            goal.MaxIterations = max;
        await db.SaveChangesAsync();
    }

    private async Task RequeueOutboxForReplayAsync()
    {
        await using var db = await _factory.CreateDbContextAsync();
        var outbox = await db.GoalOutbox.SingleAsync();
        var goal = await db.GoalRuns.SingleAsync();
        outbox.Status = GoalOutboxValues.Pending;
        outbox.LeaseOwner = null;
        outbox.LeaseUntilUtc = null;
        outbox.CompletedAtUtc = null;
        outbox.AttemptCount = 0;
        outbox.AggregateVersion = goal.AggregateVersion;
        goal.IterationsSettled = goal.IterationsStarted; // 重放夹具：结算轴对齐，避免 IterationConflict
        await db.SaveChangesAsync();
    }

    [TestMethod]
    public async Task BudgetWrapUp_AtThreshold_DispatchesSteeringToNewTurn()
    {
        var goal = await CreateGoalWithContinuationAsync();
        await SetBudgetProgressAsync(goal.GoalRunId, iterations: 7); // 快照 7/8 = 0.875 ≥ 0.8
        var (worker, _) = NewWrapUpWorker(
            _outboxStore,
            _factory,
            new GoalRunOptions { Enabled = true, ContinuationEnabled = true });

        var processed = await worker.ProcessOnceAsync();

        Assert.AreEqual(1, processed);
        await using var db = await _factory.CreateDbContextAsync();
        var steerings = await db.SessionSteeringMessages.ToListAsync();
        Assert.AreEqual(1, steerings.Count);
        var turn = await db.ConversationTurns.SingleAsync();
        Assert.AreEqual(turn.TurnId, steerings[0].TargetTurnId);
        Assert.AreEqual(goal.WorkspaceId, steerings[0].WorkspaceId);
        Assert.AreEqual(goal.CurrentConversationId, steerings[0].SessionId);
        Assert.AreEqual(goal.AgentInstanceId, steerings[0].AgentId);
        Assert.AreEqual("goal-continuation-worker", steerings[0].CreatedBy);
        var outbox = await db.GoalOutbox.SingleAsync();
        Assert.AreEqual($"budget-wrapup-{outbox.OutboxId}", steerings[0].SourceQueueItemId);
        StringAssert.Contains(steerings[0].MessageText, "迭代预算");
        StringAssert.Contains(steerings[0].MessageText, "收尾");
        Assert.AreEqual(GoalOutboxValues.Completed, outbox.Status);
    }

    [TestMethod]
    public async Task BudgetWrapUp_SameOutboxReplay_DoesNotDuplicateSteering()
    {
        var goal = await CreateGoalWithContinuationAsync();
        await SetBudgetProgressAsync(goal.GoalRunId, iterations: 13, maxIterations: 16); // 13/16 = 0.8125 ≥ 0.8
        var (worker, _) = NewWrapUpWorker(
            _outboxStore,
            _factory,
            new GoalRunOptions { Enabled = true, ContinuationEnabled = true });

        Assert.AreEqual(1, await worker.ProcessOnceAsync());
        await RequeueOutboxForReplayAsync();
        Assert.AreEqual(1, await worker.ProcessOnceAsync());

        await using var verify = await _factory.CreateDbContextAsync();
        Assert.AreEqual(1, await verify.SessionSteeringMessages.CountAsync());
        // acceptance 幂等重放不得重复消费预算或重复建轮
        Assert.AreEqual(1, await verify.GoalIterations.CountAsync());
        Assert.AreEqual(1, await verify.ConversationTurns.CountAsync());
        Assert.AreEqual(14, (await verify.GoalRuns.SingleAsync()).IterationsStarted);
    }

    [TestMethod]
    public async Task BudgetWrapUp_BelowThreshold_DoesNotDispatch()
    {
        var goal = await CreateGoalWithContinuationAsync();
        await SetBudgetProgressAsync(goal.GoalRunId, iterations: 2); // 2/8 = 0.25 < 0.8
        var (worker, _) = NewWrapUpWorker(
            _outboxStore,
            _factory,
            new GoalRunOptions { Enabled = true, ContinuationEnabled = true });

        Assert.AreEqual(1, await worker.ProcessOnceAsync());

        await using var db = await _factory.CreateDbContextAsync();
        Assert.AreEqual(0, await db.SessionSteeringMessages.CountAsync());
        Assert.AreEqual(GoalOutboxValues.Completed, (await db.GoalOutbox.SingleAsync()).Status);
    }

    [TestMethod]
    public async Task BudgetWrapUp_ThresholdConfiguration_IsHonored_AndValidated()
    {
        var goal = await CreateGoalWithContinuationAsync();
        await SetBudgetProgressAsync(goal.GoalRunId, iterations: 7); // 7/8 = 0.875 < 0.95
        var (worker, _) = NewWrapUpWorker(
            _outboxStore,
            _factory,
            new GoalRunOptions { Enabled = true, ContinuationEnabled = true, BudgetWrapUpThreshold = 0.95 });

        Assert.AreEqual(1, await worker.ProcessOnceAsync());
        await using (var db = await _factory.CreateDbContextAsync())
        {
            Assert.AreEqual(0, await db.SessionSteeringMessages.CountAsync());
        }

        // 同水位 + 更敏感阈值 → 投递（证明阈值配置真实传导到判定）
        await RequeueOutboxForReplayAsync();
        await SetBudgetProgressAsync(goal.GoalRunId, iterations: 7); // 拉回快照 7/8，避免重放被 BudgetExhausted 拦截
        var (sensitive, _) = NewWrapUpWorker(
            _outboxStore,
            _factory,
            new GoalRunOptions { Enabled = true, ContinuationEnabled = true, BudgetWrapUpThreshold = 0.5 });
        Assert.AreEqual(1, await sensitive.ProcessOnceAsync());
        await using var verify = await _factory.CreateDbContextAsync();
        Assert.AreEqual(1, await verify.SessionSteeringMessages.CountAsync());

        // Validate 边界：0.5..1.0 合法，越界报错
        Assert.AreEqual(0, GoalRunOptions.Validate(new GoalRunOptions { BudgetWrapUpThreshold = 0.5 }).Count);
        Assert.AreEqual(0, GoalRunOptions.Validate(new GoalRunOptions { BudgetWrapUpThreshold = 1.0 }).Count);
        StringAssert.Contains(
            GoalRunOptions.Validate(new GoalRunOptions { BudgetWrapUpThreshold = 0.4 }).Single(),
            "BudgetWrapUpThreshold");
        StringAssert.Contains(
            GoalRunOptions.Validate(new GoalRunOptions { BudgetWrapUpThreshold = 1.1 }).Single(),
            "BudgetWrapUpThreshold");
    }
}
