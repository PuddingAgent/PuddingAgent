using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PuddingCode.Platform;
using PuddingCode.Runtime;
using PuddingCode.Tasks;
using PuddingCode.Tools;
using PuddingPlatform.Data.Entities;
using PuddingRuntime.Services;
using PuddingRuntime.Services.AgentLoop;
using PuddingRuntime.Services.TaskTools;

namespace PuddingRuntimeTests.Services.TaskE2E;

/// <summary>
/// TB-08-C 四链（派发链/执行链/回写链/恢复链）E2E 测试。
/// <para>
/// 进程内集成式单测（TB-07 B2 延续）：真实四工具 + 真实 <see cref="TaskAgentCommandService"/>
/// + 真实 SQLite + 真实 <see cref="AgentExecutionService"/>。生产代码零改动。
/// </para>
/// <para>
/// 已实现 7 个新增测试：
///   T1 派发链  ActiveTask → ToolInvocationRequest
///   T4 执行链  task_claim → task_update(completed) 四表原子写回
///   T5 执行链  assignment.stale 守卫
///   T6 执行链  active_context_missing 守卫（反向）
///   T7 回写链  ToolInvocationService.ActiveTask → ToolExecutionContext 透传
///   T8 回写链  task_claim 以 Context 为准，冲突参数 state_conflict
///   T9 恢复链  WAIT → wakeup 恢复 ActiveTask → 真实工具写库
/// </para>
/// <para>
/// 沿用 B2（不重复实现）：
///   T2  CreateForWorkspaceAgentAsync_MetadataTaskKeys_BuildActiveTask
///       —— 见 AgentExecutionWakeupActiveTaskPreservationTests.Test5
///   T10 ExecuteWakeupAsync_WithoutAnchor_ReturnsFailed —— 见 B2 test3
///   T11 ResumeAnchor_ActiveTaskJsonRoundTrip —— 见 B2 test4
///   T3  DispatchOutbox → Envelope → Invocation.Metadata —— 依赖 TaskDispatcher 公开调用面，
///       不可轻量构造，按契约 §3 降级为「T2 + TaskInstructionEnvelopeTests 已覆盖」，不作为硬性交付。
/// </para>
/// </summary>
[TestClass]
public sealed class TaskActiveTaskFourChainE2ETests
{
    private const string WorkspaceId = "ws-1";
    private const string AgentId = "agent-1";
    private const string TaskId = "task-1";
    private const string AssignmentId = "assign-1";

    private TaskE2EHarness _harness = null!;

    [TestInitialize]
    public async Task InitializeAsync()
    {
        _harness = new TaskE2EHarness();
        await _harness.InitializeAsync();
    }

    [TestCleanup]
    public void Cleanup()
        => _harness.Dispose();

    // ── T1 派发链：ActiveTask → ToolInvocationRequest（active_context_missing 正向回归）──
    [TestMethod]
    public async Task ExecuteAsync_ActiveTaskInjected_PropagatesIntoToolInvocationRequest()
    {
        var activeTask = await _harness.SeedAssignedTaskAsync(
            WorkspaceId, TaskId, AssignmentId, AgentId);
        const string sessionId = "session-t1";

        _harness.Llm.Enqueue(
            ClaimJson(TaskId, AssignmentId, 1),
            DoneJson);

        var result = await _harness.ExecutionService.ExecuteAsync(
            _harness.CreateDispatchRequest(sessionId, activeTask));

        Assert.AreEqual(AgentExecutionState.Completed, result.ExecutionState,
            "task_claim 应成功执行并到达 DONE。");
        Assert.AreEqual(1, _harness.Tools.Captured.Count,
            "预期恰好一次 task_claim 工具调用。");
        AssertActiveTaskEqual(activeTask, _harness.Tools.Captured[0].ActiveTask);
    }

    // ── T4 执行链：task_claim → task_update(completed) 四表原子写回 ──────────
    [TestMethod]
    public async Task ClaimThenCompleted_RealTools_AtomicallyWritesTaskAttemptEventBinding()
    {
        // 种子：Assigned v1。
        var ctxV1 = await _harness.SeedAssignedTaskAsync(
            WorkspaceId, TaskId, AssignmentId, AgentId, version: 1);

        const string runId = "run-t4";
        const string traceId = "trace-t4";

        // ── 第一链段：task_claim（Assigned v1 → InProgress v2）──
        _harness.Llm.Enqueue(
            ClaimJson(TaskId, AssignmentId, 1),
            DoneJson);

        var claimDispatch = _harness.CreateDispatchRequest("session-t4-claim", ctxV1) with
        {
            ExecutionIdentity = CreateIdentity(runId, traceId),
        };
        var claimResult = await _harness.ExecutionService.ExecuteAsync(claimDispatch);
        Assert.AreEqual(AgentExecutionState.Completed, claimResult.ExecutionState);

        var afterClaim = await _harness.Probe.GetTaskAsync(WorkspaceId, TaskId);
        Assert.IsNotNull(afterClaim);
        Assert.AreEqual(WorkspaceTaskStatus.InProgress, afterClaim!.Status);
        Assert.AreEqual(2, afterClaim.Version);

        // ── 第二链段：task_update(completed)（InProgress v2 → Completed v3）──
        // 生产代码的 ActiveTaskRuntimeContext.ExpectedVersion 是「派发时刻快照」，不会随 Agent
        // 自身 mutation 前进；因此第二段需以 claim 后的真实 version 重新注入（见契约 §7 RISKS）。
        var ctxV2 = ctxV1 with { ExpectedVersion = afterClaim.Version };
        _harness.Llm.Enqueue(
            UpdateCompletedJson(TaskId, AssignmentId, afterClaim.Version),
            DoneJson);

        var completeResult = await _harness.ExecutionService.ExecuteAsync(
            _harness.CreateDispatchRequest("session-t4-complete", ctxV2));
        Assert.AreEqual(AgentExecutionState.Completed, completeResult.ExecutionState);

        // ── 四表原子一致性断言（独立 DbContext 只读探测）──
        var task = await _harness.Probe.GetTaskAsync(WorkspaceId, TaskId);
        Assert.IsNotNull(task);
        Assert.AreEqual(WorkspaceTaskStatus.Completed, task!.Status, "Task 应 Completed。");
        Assert.AreEqual(3, task.Version, "Version 应 1→2→3。");
        Assert.IsNotNull(task.CompletedAtUtc, "completed_at_utc 应非空。");

        var attempt = await _harness.Probe.GetAttemptAsync(AssignmentId);
        Assert.IsNotNull(attempt);
        Assert.AreEqual(AssignmentAttemptStatus.Completed, attempt!.Status, "Attempt 应 Completed。");

        var events = await _harness.Probe.GetEventsAsync(TaskId);
        Assert.AreEqual(3, events.Count, "应有 task.assigned + task.accepted + task.completed 三条事件。");
        Assert.AreEqual(TaskEventType.TaskAccepted, events[1].EventType);
        Assert.AreEqual(TaskEventType.TaskCompleted, events[2].EventType);
        Assert.AreEqual(2, events[1].Sequence, "task.accepted 的 Sequence 应对应 version 2。");
        Assert.AreEqual(3, events[2].Sequence, "task.completed 的 Sequence 应对应 version 3。");

        var bindings = await _harness.Probe.GetBindingsAsync(TaskId);
        Assert.AreEqual(1, bindings.Count, "应存在一行 task_execution_bindings。");
        Assert.AreEqual(runId, bindings[0].ExecutionId, "Binding.ExecutionId 应与注入 RunId 一致。");
        Assert.AreEqual("session-t4-claim", bindings[0].SessionId,
            "Binding.SessionId 应与首次回填的 SessionId 一致。");
    }

    // ── T5 执行链守卫：assignment.stale（迟到调用）───────────────────────────
    [TestMethod]
    public async Task ClaimAsync_StaleAssignment_ReturnsAssignmentStale()
    {
        var seeded = await _harness.SeedAssignedTaskAsync(
            WorkspaceId, TaskId, AssignmentId, AgentId);

        // 迟到守卫：把请求 AssignmentId 换成非当前值（等价「Attempt 已被重派」）。
        var staleContext = seeded with { AssignmentId = "assign-stale" };
        const string sessionId = "session-t5";

        _harness.Llm.Enqueue(
            ClaimJson(TaskId, "assign-stale", 1),
            DoneJson);

        var result = await _harness.ExecutionService.ExecuteAsync(
            _harness.CreateDispatchRequest(sessionId, staleContext));

        Assert.AreEqual(AgentExecutionState.Completed, result.ExecutionState);
        Assert.AreEqual(1, _harness.Tools.Captured.Count);
        AssertToolErrorCode(_harness.Tools.Results[0], "assignment.stale");

        // DB 无任何状态推进。
        var task = await _harness.Probe.GetTaskAsync(WorkspaceId, TaskId);
        Assert.IsNotNull(task);
        Assert.AreEqual(WorkspaceTaskStatus.Assigned, task!.Status);
        Assert.AreEqual(1, task.Version);
    }

    // ── T6 执行链守卫（反向）：无 ActiveContext → 服务端反查 + 状态门槛（缺陷 3f8df399）──────
    [TestMethod]
    public async Task UpdateCompleted_WithoutActiveContext_RebuildChecksState_ReturnsStateConflict()
    {
        // 种子后验证 DB 无变化。
        await _harness.SeedAssignedTaskAsync(WorkspaceId, TaskId, AssignmentId, AgentId);

        var tool = new TaskUpdateTool(
            _harness.CommandService,
            Options.Create(new WorkspaceTaskFeatureOptions { Enabled = true }),
            NullLogger<TaskUpdateTool>.Instance);

        var context = new ToolExecutionContext
        {
            WorkspaceId = WorkspaceId,
            SessionId = "session-t6",
            AgentInstanceId = AgentId,
            ActiveTask = null,
        };

        var result = await tool.ExecuteAsync(new ToolExecutionRequest
        {
            ToolCallId = "call-t6",
            ArgumentsJson =
                """{"task_id":"task-1","assignment_id":"assign-1","expected_version":1,"disposition":"completed","result_summary":"done"}""",
            Context = context,
        });

        // 缺陷 3f8df399：无 ActiveContext 时经服务端反查归属（mine 过滤 + assignment 匹配 + 版本 CAS 均通过），
        // 状态门槛要求 InProgress，种子任务为 Assigned → 精确返回 task.state_conflict（原为笼统 active_context_missing）。
        AssertToolExecutionErrorCode(result, "task.state_conflict");

        var task = await _harness.Probe.GetTaskAsync(WorkspaceId, TaskId);
        Assert.IsNotNull(task);
        Assert.AreEqual(WorkspaceTaskStatus.Assigned, task!.Status, "状态门槛拒绝时 DB 不得变化。");
        Assert.AreEqual(1, task.Version);
    }

    // ── T7 回写链：真实 ToolInvocationService 透传 ActiveTask ─────────────────
    [TestMethod]
    public async Task ToolInvocationService_ActiveTask_PassthroughToToolExecutionContext()
    {
        var recorder = new RecordingToolExecutionService();
        var service = new ToolInvocationService(recorder);
        var activeTask = CreateActiveTask();

        var request = new ToolInvocationRequest
        {
            WorkspaceId = WorkspaceId,
            SessionId = "session-t7",
            AgentInstanceId = AgentId,
            ToolCallId = "call-t7",
            ToolName = "task_claim",
            ArgumentsJson = "{}",
            ActiveTask = activeTask,
        };

        var result = await service.InvokeAsync(request);

        Assert.IsTrue(result.Success, result.Error);
        Assert.IsNotNull(recorder.Context, "真实 ToolInvocationService 应把请求透传给执行服务。");
        AssertActiveTaskEqual(activeTask, recorder.Context!.ActiveTask);
    }

    // ── T8 回写链：以 Context 为准，冲突参数被拒（state_conflict）────────────
    [TestMethod]
    public async Task TaskClaimTool_ConsumesActiveTask_ConflictingArgsRejected()
    {
        var activeTask = await _harness.SeedAssignedTaskAsync(
            WorkspaceId, TaskId, AssignmentId, AgentId);
        const string sessionId = "session-t8";

        _harness.Llm.Enqueue(
            ClaimJson("task-wrong", AssignmentId, 1),   // 冲突 task_id → state_conflict
            ClaimJson(TaskId, AssignmentId, 1),          // 正确 → 认领成功
            DoneJson);

        var result = await _harness.ExecutionService.ExecuteAsync(
            _harness.CreateDispatchRequest(sessionId, activeTask));

        Assert.AreEqual(AgentExecutionState.Completed, result.ExecutionState);
        Assert.AreEqual(2, _harness.Tools.Captured.Count,
            "应有两次 task_claim（一次冲突 + 一次正确）。");

        Assert.IsFalse(_harness.Tools.Results[0].Success, "冲突参数应返回失败。");
        AssertToolErrorCode(_harness.Tools.Results[0], "task.state_conflict");
        Assert.IsTrue(_harness.Tools.Results[1].Success, "正确参数应认领成功。");

        // 冲突那次不落库：事件仍为 task.assigned + task.accepted（正确那次）两条。
        var task = await _harness.Probe.GetTaskAsync(WorkspaceId, TaskId);
        Assert.IsNotNull(task);
        Assert.AreEqual(WorkspaceTaskStatus.InProgress, task!.Status);
        Assert.AreEqual(2, task.Version);

        var events = await _harness.Probe.GetEventsAsync(TaskId);
        Assert.AreEqual(2, events.Count, "冲突调用不得追加事件。");
    }

    // ── T9 恢复链：WAIT → wakeup 恢复 ActiveTask → 真实工具写库 ─────────────
    [TestMethod]
    public async Task WaitThenWakeup_ActiveTaskRestored_RealTools_CompletesTask()
    {
        var activeTask = await _harness.SeedAssignedTaskAsync(
            WorkspaceId, TaskId, AssignmentId, AgentId);
        const string sessionId = "session-t9";

        _harness.Llm.Enqueue(WaitJson);
        var first = await _harness.ExecutionService.ExecuteAsync(
            _harness.CreateDispatchRequest(sessionId, activeTask));

        // ① WAIT 落锚。
        Assert.AreEqual(AgentExecutionState.WaitingEvent, first.ExecutionState);
        Assert.IsFalse(string.IsNullOrWhiteSpace(first.ResumeAnchorId));

        // ② 锚点含 ActiveTask 快照。
        var anchor = _harness.Journal.GetAnchor(sessionId);
        Assert.IsNotNull(anchor);
        AssertActiveTaskEqual(activeTask, anchor!.ActiveTask);

        // ③ wakeup：恢复 ActiveTask → 真实 task_claim 写库。
        _harness.Llm.Enqueue(
            ClaimJson(TaskId, AssignmentId, 1),
            DoneJson);
        var wakeup = await _harness.ExecutionService.ExecuteWakeupAsync(
            _harness.CreateWakeupRequest(sessionId));

        Assert.AreEqual(AgentExecutionState.Completed, wakeup.ExecutionState);
        Assert.AreEqual(1, _harness.Tools.Captured.Count,
            "wakeup 后应恰好一次 task_claim。");
        AssertActiveTaskEqual(activeTask, _harness.Tools.Captured[0].ActiveTask);

        // ④ 真实工具 + 真实库闭环：task 已认领（Assigned → InProgress）。
        //    （契约 T9 原文标注 DB Task=Completed；因生产代码的 ExpectedVersion 为派发快照、
        //     无法在同一恢复上下文内推进 claim→completed 两段，故此处按「claim 真写库」断言
        //     恢复链闭环，完整 claim→completed 由 T4 覆盖。见交付 RISKS。）
        var task = await _harness.Probe.GetTaskAsync(WorkspaceId, TaskId);
        Assert.IsNotNull(task);
        Assert.AreEqual(WorkspaceTaskStatus.InProgress, task!.Status);
        Assert.AreEqual(2, task.Version);
    }

    // ── Fixture 帮助 ────────────────────────────────────────────────────────

    private static RuntimeExecutionIdentity CreateIdentity(string runId, string traceId) => new()
    {
        Kind = RuntimeExecutionKind.ConversationTurn,
        ConversationId = "conv-1",
        RunId = runId,
        TraceId = traceId,
    };

    private static ActiveTaskRuntimeContext CreateActiveTask() => new()
    {
        WorkspaceId = WorkspaceId,
        TaskId = TaskId,
        AssignmentId = AssignmentId,
        AgentId = AgentId,
        Origin = "task.manual",
        Priority = "p1",
        ExecutionWindow = "anytime",
        ExpectedVersion = 1,
        PolicyVersion = "v1",
        DispatchIdempotencyKey = "idem-1",
    };

    private static string ClaimJson(string taskId, string assignmentId, int expectedVersion)
        => ScriptJson("task_claim", new
        {
            task_id = taskId,
            assignment_id = assignmentId,
            expected_version = expectedVersion,
        });

    private static string UpdateCompletedJson(string taskId, string assignmentId, int expectedVersion)
        => ScriptJson("task_update", new
        {
            task_id = taskId,
            assignment_id = assignmentId,
            expected_version = expectedVersion,
            disposition = "completed",
            result_summary = "done",
        });

    private static string ScriptJson(string toolName, object args)
        => JsonSerializer.Serialize(new
        {
            status = "CONTINUE",
            message = "invoking " + toolName,
            tool = new { name = toolName, args },
        });

    private const string DoneJson = "{\"status\":\"DONE\",\"message\":\"finished\"}";
    private const string WaitJson =
        "{\"status\":\"WAIT\",\"message\":\"awaiting external event\",\"meta\":{\"reason\":\"file.ready\"}}";

    private static void AssertActiveTaskEqual(
        ActiveTaskRuntimeContext? expected,
        ActiveTaskRuntimeContext? actual)
    {
        Assert.IsNotNull(expected);
        Assert.IsNotNull(actual);
        if (expected is null || actual is null)
            return;

        Assert.AreEqual(expected.WorkspaceId, actual.WorkspaceId, nameof(ActiveTaskRuntimeContext.WorkspaceId));
        Assert.AreEqual(expected.TaskId, actual.TaskId, nameof(ActiveTaskRuntimeContext.TaskId));
        Assert.AreEqual(expected.AssignmentId, actual.AssignmentId, nameof(ActiveTaskRuntimeContext.AssignmentId));
        Assert.AreEqual(expected.AgentId, actual.AgentId, nameof(ActiveTaskRuntimeContext.AgentId));
        Assert.AreEqual(expected.Origin, actual.Origin, nameof(ActiveTaskRuntimeContext.Origin));
        Assert.AreEqual(expected.Priority, actual.Priority, nameof(ActiveTaskRuntimeContext.Priority));
        Assert.AreEqual(expected.ExecutionWindow, actual.ExecutionWindow, nameof(ActiveTaskRuntimeContext.ExecutionWindow));
        Assert.AreEqual(expected.ExpectedVersion, actual.ExpectedVersion, nameof(ActiveTaskRuntimeContext.ExpectedVersion));
        Assert.AreEqual(expected.PolicyVersion, actual.PolicyVersion, nameof(ActiveTaskRuntimeContext.PolicyVersion));
        Assert.AreEqual(expected.DispatchIdempotencyKey, actual.DispatchIdempotencyKey, nameof(ActiveTaskRuntimeContext.DispatchIdempotencyKey));
        Assert.AreEqual(expected.DeliveryId, actual.DeliveryId, nameof(ActiveTaskRuntimeContext.DeliveryId));
        Assert.AreEqual(expected.ReservationFencingToken, actual.ReservationFencingToken, nameof(ActiveTaskRuntimeContext.ReservationFencingToken));
    }

    private static void AssertToolErrorCode(ToolInvocationResult result, string expectedCode)
    {
        Assert.IsFalse(result.Success, $"expected tool failure but succeeded (output={result.Output})");
        Assert.IsNotNull(result.Error, "expected error JSON but got null");
        using var doc = JsonDocument.Parse(result.Error!);
        Assert.AreEqual(expectedCode, doc.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    private static void AssertToolExecutionErrorCode(ToolExecutionResult result, string expectedCode)
    {
        Assert.IsFalse(result.Success, $"expected tool failure but succeeded (output={result.Output})");
        Assert.IsNotNull(result.Error, "expected error JSON but got null");
        using var doc = JsonDocument.Parse(result.Error!);
        Assert.AreEqual(expectedCode, doc.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    /// <summary>真实 ToolInvocationService 的桩执行服务（T7 专用）：捕获收到的 ToolExecutionContext。</summary>
    private sealed class RecordingToolExecutionService : IPuddingToolExecutionService
    {
        public ToolExecutionContext? Context { get; private set; }

        public Task<ToolExecutionResult> ExecuteAsync(
            string toolId,
            string argumentsJson,
            ToolExecutionContext context,
            CapabilityPolicy? policy,
            CancellationToken ct = default)
        {
            Context = context;
            return Task.FromResult(ToolExecutionResult.Ok("ok"));
        }
    }
}
