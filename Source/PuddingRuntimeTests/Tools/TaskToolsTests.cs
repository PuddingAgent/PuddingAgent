using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PuddingCode.Models;
using PuddingCode.Runtime;
using PuddingCode.Tasks;
using PuddingCode.Tools;
using PuddingRuntime.Services.TaskTools;
using PuddingRuntime.Services.Tools;

namespace PuddingRuntimeTests.Tools;

/// <summary>
/// TB-06 四工具（task_list / task_get / task_claim / task_update）Runtime 单元测试。
/// <para>
/// 覆盖契约 §11.3 全部 7 类用例：
///   ① 四工具注册（PuddingToolRegistry 出现 4 个 id、无重复、参数 schema 正确）；
///   ② TaskListTool（mine 过滤透传、status/board_column/priority/limit/cursor、空结果）；
///   ③ TaskGetTool（序列化详情、Cancelled/Archived board_column=null 不抛错、不存在统一 not_found）；
///   ④ TaskClaimTool（成功、无上下文 422、状态冲突 409、版本冲突 409、assignment.stale 409、
///      缺陷 2d5a2ebe 回归：注入快照过期 + worker 传新活版本 → 成功）；
///   ⑤ TaskUpdateTool（7 disposition 成功 + 全部 422 规则 + 迟到拒绝）；
///   ⑥ ActiveTask 注入（ToolExecutionContext.ActiveTask + ExecutionIdentity 关联透传）；
///   ⑦ Feature Flag（WorkspaceTasks.Enabled=false → capability.missing）。
/// </para>
/// <para>
/// 说明：本层只测 Runtime 工具行为（薄适配器）；mine 过滤、CAS、状态机推进等语义由
/// ITaskAgentCommandService（Platform 层）负责，此处用手写 fake 隔离。
/// </para>
/// </summary>
[TestClass]
public sealed class TaskToolsTests
{
    private const string WorkspaceId = "ws-1";
    private const string AgentId = "agent-1";
    private const string SessionId = "session-1";

    // ─────────────────────────────────────────────────────────────
    // 构造与运行帮助
    // ─────────────────────────────────────────────────────────────

    private static ToolExecutionContext Context(
        string? agentId = null,
        ActiveTaskRuntimeContext? activeTask = null,
        RuntimeExecutionIdentity? identity = null) => new()
    {
        WorkspaceId = WorkspaceId,
        SessionId = SessionId,
        AgentInstanceId = agentId ?? AgentId,
        ActiveTask = activeTask,
        ExecutionIdentity = identity,
    };

    private static ActiveTaskRuntimeContext ActiveTask(
        string taskId = "task-1",
        string assignmentId = "assign-1",
        int? expectedVersion = 1) => new()
    {
        WorkspaceId = WorkspaceId,
        TaskId = taskId,
        AssignmentId = assignmentId,
        AgentId = AgentId,
        Origin = "task.manual",
        Priority = "p1",
        ExecutionWindow = "anytime",
        ExpectedVersion = expectedVersion,
    };

    private static RuntimeExecutionIdentity Identity(string runId = "run-1", string? traceId = "trace-1") => new()
    {
        Kind = RuntimeExecutionKind.ConversationTurn,
        ConversationId = "conv-1",
        RunId = runId,
        TraceId = traceId,
    };

    private static TaskListTool ListTool(FakeTaskAgentCommandService service, bool enabled = true) =>
        new(service, Options.Create(new WorkspaceTaskFeatureOptions { Enabled = enabled }), NullLogger<TaskListTool>.Instance);

    private static TaskGetTool GetTool(FakeTaskAgentCommandService service, bool enabled = true) =>
        new(service, Options.Create(new WorkspaceTaskFeatureOptions { Enabled = enabled }), NullLogger<TaskGetTool>.Instance);

    private static TaskClaimTool ClaimTool(FakeTaskAgentCommandService service, bool enabled = true) =>
        new(service, Options.Create(new WorkspaceTaskFeatureOptions { Enabled = enabled }), NullLogger<TaskClaimTool>.Instance);

    private static TaskUpdateTool UpdateTool(FakeTaskAgentCommandService service, bool enabled = true) =>
        new(service, Options.Create(new WorkspaceTaskFeatureOptions { Enabled = enabled }), NullLogger<TaskUpdateTool>.Instance);

    private static async Task<ToolExecutionResult> RunAsync(IPuddingTool tool, string argsJson, ToolExecutionContext? context = null)
    {
        return await tool.ExecuteAsync(new ToolExecutionRequest
        {
            ToolCallId = "call-" + Guid.NewGuid().ToString("N")[..8],
            ArgumentsJson = argsJson,
            Context = context ?? Context(),
        });
    }

    private static JsonElement ParseOutput(ToolExecutionResult result)
    {
        Assert.IsTrue(result.Success, result.Error);
        return JsonDocument.Parse(result.Output!).RootElement;
    }

    private static JsonElement ParseError(ToolExecutionResult result)
    {
        Assert.IsFalse(result.Success, "expected failure but succeeded");
        Assert.IsNotNull(result.Error);
        return JsonDocument.Parse(result.Error!).RootElement.GetProperty("error");
    }

    private static void AssertErrorCode(ToolExecutionResult result, string expectedCode)
    {
        var error = ParseError(result);
        Assert.AreEqual(expectedCode, error.GetProperty("code").GetString());
    }

    private const string DisabledMessage = "Workspace task tools are disabled (WorkspaceTasks.Enabled=false).";

    // ─────────────────────────────────────────────────────────────
    // 手写 fake（隔离 ITaskAgentCommandService）
    // ─────────────────────────────────────────────────────────────

    private sealed class FakeTaskAgentCommandService : ITaskAgentCommandService
    {
        public Func<TaskAgentListQuery, CancellationToken, Task<TaskAgentListResult>>? ListMine { get; set; }
        public Func<string, string, string, int, CancellationToken, Task<TaskAgentGetResult?>>? Get { get; set; }
        public Func<TaskAgentClaimRequest, CancellationToken, Task<TaskAgentMutationResult>>? Claim { get; set; }
        public Func<TaskAgentUpdateRequest, CancellationToken, Task<TaskAgentMutationResult>>? ApplyDisposition { get; set; }

        public TaskAgentListQuery? LastListQuery { get; private set; }
        public (string WorkspaceId, string TaskId, string AgentId, int EventsLimit)? LastGetArgs { get; private set; }
        public TaskAgentClaimRequest? LastClaimRequest { get; private set; }
        public TaskAgentUpdateRequest? LastUpdateRequest { get; private set; }

        public Task<TaskAgentListResult> ListMineAsync(TaskAgentListQuery query, CancellationToken ct = default)
        {
            LastListQuery = query;
            if (ListMine is not null)
                return ListMine(query, ct);

            return Task.FromResult(new TaskAgentListResult { Total = 0, Items = [] });
        }

        public Task<TaskAgentGetResult?> GetAsync(
            string workspaceId,
            string taskId,
            string agentId,
            int eventsLimit,
            CancellationToken ct = default)
        {
            LastGetArgs = (workspaceId, taskId, agentId, eventsLimit);
            if (Get is not null)
                return Get(workspaceId, taskId, agentId, eventsLimit, ct);

            return Task.FromResult<TaskAgentGetResult?>(null);
        }

        public Task<TaskAgentMutationResult> ClaimAsync(TaskAgentClaimRequest request, CancellationToken ct = default)
        {
            LastClaimRequest = request;
            if (Claim is not null)
                return Claim(request, ct);

            return Task.FromResult(Mutation(
                taskId: request.TaskId,
                disposition: "accept",
                status: "InProgress",
                version: request.ExpectedVersion + 1,
                assignmentId: request.AssignmentId,
                assignmentStatus: "InProgress",
                @event: "task.accepted",
                boardColumn: "InProgress"));
        }

        public Task<TaskAgentMutationResult> ApplyDispositionAsync(TaskAgentUpdateRequest request, CancellationToken ct = default)
        {
            LastUpdateRequest = request;
            if (ApplyDisposition is not null)
                return ApplyDisposition(request, ct);

            return Task.FromResult(Mutation(
                taskId: request.TaskId,
                disposition: request.Disposition,
                status: "InProgress",
                version: request.ExpectedVersion + 1,
                assignmentId: request.AssignmentId,
                assignmentStatus: "InProgress",
                @event: "task.progressed",
                boardColumn: "InProgress"));
        }

        private static TaskAgentMutationResult Mutation(
            string taskId,
            string disposition,
            string status,
            int version,
            string assignmentId,
            string assignmentStatus,
            string @event,
            string boardColumn) => new()
        {
            TaskId = taskId,
            Disposition = disposition,
            Status = status,
            Version = version,
            AssignmentId = assignmentId,
            AssignmentStatus = assignmentStatus,
            Event = @event,
            BoardColumn = boardColumn,
        };
    }

    private static TaskAgentGetResult GetResult(
        string taskId = "task-1",
        string? activeAssignmentId = "assign-1",
        string? boardColumn = "Todo",
        bool archived = false,
        string? status = null,
        int version = 1,
        string? assignmentAgentId = null) => new()
    {
        Task = new TaskAgentTaskDetail
        {
            TaskId = taskId,
            WorkspaceId = WorkspaceId,
            Title = "Write report",
            Status = status ?? (archived ? "Cancelled" : "Assigned"),
            BoardColumn = boardColumn,
            Archived = archived,
            Priority = "p1",
            ExecutionWindow = "anytime",
            ActiveAssignmentId = activeAssignmentId,
            SortOrder = 1,
            Version = version,
            CreatedAtUtc = new DateTimeOffset(2026, 8, 16, 0, 0, 0, TimeSpan.Zero),
            UpdatedAtUtc = new DateTimeOffset(2026, 8, 16, 1, 0, 0, TimeSpan.Zero),
        },
        AllowedTransitions = ["InProgress", "Blocked"],
        AllowedDispositions = ["accept", "progress", "todo", "blocked", "needs_approval", "rejected", "completed"],
        ActiveAssignment = new TaskAgentAssignmentSummary
        {
            AssignmentId = activeAssignmentId ?? "assign-1",
            AgentId = assignmentAgentId ?? AgentId,
            Status = "Assigned",
        },
        RecentEvents =
        [
            new TaskAgentEventSummary
            {
                EventId = "evt-1",
                Sequence = 1,
                EventType = "task.created",
                CreatedAtUtc = new DateTimeOffset(2026, 8, 16, 0, 0, 0, TimeSpan.Zero),
            },
        ],
    };

    // ─────────────────────────────────────────────────────────────
    // ① 四工具注册
    // ─────────────────────────────────────────────────────────────

    [TestMethod]
    public void FourTools_RegisterInRegistry_WithFourUniqueIds()
    {
        var service = new FakeTaskAgentCommandService();
        var registry = new PuddingToolRegistry(
        [
            ListTool(service),
            GetTool(service),
            ClaimTool(service),
            UpdateTool(service),
        ]);

        var ids = registry.ListDescriptors().Select(d => d.ToolId).ToArray();

        CollectionAssert.AreEquivalent(
            new[] { "task_list", "task_get", "task_claim", "task_update" },
            ids,
            "PuddingToolRegistry 应恰好出现 4 个无重复的 task_* id");
    }

    [TestMethod]
    public void FourTools_ParameterSchemas_ExposeRequiredFields()
    {
        var service = new FakeTaskAgentCommandService();

        var list = ListTool(service).Descriptor.Parameters.Required.ToArray();
        CollectionAssert.AreEquivalent(Array.Empty<string>(), list, "task_list 无可选参数之外的必填参数");

        var get = GetTool(service).Descriptor.Parameters.Required.ToArray();
        CollectionAssert.AreEquivalent(new[] { "task_id" }, get);

        var claim = ClaimTool(service).Descriptor.Parameters.Required.ToArray();
        CollectionAssert.AreEquivalent(new[] { "task_id", "assignment_id", "expected_version" }, claim);

        var update = UpdateTool(service).Descriptor.Parameters.Required.ToArray();
        CollectionAssert.AreEquivalent(
            new[] { "task_id", "assignment_id", "expected_version", "disposition" },
            update);
    }

    // ─────────────────────────────────────────────────────────────
    // ② TaskListTool
    // ─────────────────────────────────────────────────────────────

    [TestMethod]
    public async Task List_ReturnsSerializedResult_AndUsesContextIdentity()
    {
        var service = new FakeTaskAgentCommandService
        {
            ListMine = (_, _) => Task.FromResult(new TaskAgentListResult
            {
                Total = 1,
                Items =
                [
                    new TaskAgentListItem
                    {
                        TaskId = "task-1",
                        Title = "Write report",
                        Status = "Assigned",
                        BoardColumn = "Todo",
                        Priority = "p1",
                        ExecutionWindow = "anytime",
                        ActiveAssignmentId = "assign-1",
                        ProgressPercent = 25,
                        UpdatedAtUtc = new DateTimeOffset(2026, 8, 16, 1, 0, 0, TimeSpan.Zero),
                        Version = 3,
                    },
                ],
            }),
        };

        var result = await RunAsync(ListTool(service), "{}");

        var root = ParseOutput(result);
        Assert.AreEqual(1, root.GetProperty("total").GetInt32());
        var items = root.GetProperty("items");
        Assert.AreEqual(1, items.GetArrayLength());
        Assert.AreEqual("task-1", items[0].GetProperty("task_id").GetString());
        Assert.AreEqual("Assigned", items[0].GetProperty("status").GetString());
        Assert.AreEqual("Todo", items[0].GetProperty("board_column").GetString());
        Assert.AreEqual(25, items[0].GetProperty("progress_percent").GetInt32());

        // 身份恒为 context 注入，不接受 Agent 指定
        Assert.AreEqual(WorkspaceId, service.LastListQuery!.WorkspaceId);
        Assert.AreEqual(AgentId, service.LastListQuery!.AgentId);
    }

    [TestMethod]
    public async Task List_PassesFilters_AndDefaultsLimit()
    {
        var service = new FakeTaskAgentCommandService();

        var result = await RunAsync(
            ListTool(service),
            """{"status":"InProgress","board_column":"InProgress","priority":"p0","limit":10,"cursor":"1|task-2"}""");

        Assert.IsTrue(result.Success, result.Error);
        var query = service.LastListQuery!;
        Assert.AreEqual("InProgress", query.Status);
        Assert.AreEqual("InProgress", query.BoardColumn);
        Assert.AreEqual("p0", query.Priority);
        Assert.AreEqual(10, query.Limit);
        Assert.AreEqual("1|task-2", query.Cursor);
    }

    [TestMethod]
    public async Task List_DefaultsLimitTo50_WhenNotProvided()
    {
        var service = new FakeTaskAgentCommandService();

        var result = await RunAsync(ListTool(service), "{}");

        Assert.IsTrue(result.Success, result.Error);
        Assert.AreEqual(50, service.LastListQuery!.Limit);
    }

    [TestMethod]
    public async Task List_EmptyResult_Ok()
    {
        var service = new FakeTaskAgentCommandService();

        var root = ParseOutput(await RunAsync(ListTool(service), "{}"));

        Assert.AreEqual(0, root.GetProperty("total").GetInt32());
        Assert.AreEqual(0, root.GetProperty("items").GetArrayLength());
    }

    [TestMethod]
    public async Task List_ServiceThrows_ReturnsErrorJson()
    {
        var service = new FakeTaskAgentCommandService
        {
            ListMine = (_, _) => Task.FromException<TaskAgentListResult>(
                new TaskStoreException(TaskErrorCode.CapabilityMissing, "store down")),
        };

        var result = await RunAsync(ListTool(service), "{}");

        AssertErrorCode(result, "capability.missing");
    }

    [TestMethod]
    public async Task List_Disabled_ReturnsCapabilityMissing()
    {
        var result = await RunAsync(ListTool(new FakeTaskAgentCommandService(), enabled: false), "{}");

        var error = ParseError(result);
        Assert.AreEqual("capability.missing", error.GetProperty("code").GetString());
        Assert.AreEqual(DisabledMessage, error.GetProperty("message").GetString());
    }

    // ─────────────────────────────────────────────────────────────
    // ③ TaskGetTool
    // ─────────────────────────────────────────────────────────────

    [TestMethod]
    public async Task Get_ReturnsSerializedDetail_AndDefaultsEventsLimit()
    {
        var service = new FakeTaskAgentCommandService
        {
            Get = (_, _, _, _, _) => Task.FromResult<TaskAgentGetResult?>(GetResult()),
        };

        var root = ParseOutput(await RunAsync(GetTool(service), """{"task_id":"task-1"}"""));

        Assert.AreEqual("task-1", root.GetProperty("task").GetProperty("task_id").GetString());
        Assert.AreEqual("Todo", root.GetProperty("task").GetProperty("board_column").GetString());
        CollectionAssert.AreEquivalent(
            new[] { "InProgress", "Blocked" },
            root.GetProperty("allowed_transitions").EnumerateArray().Select(e => e.GetString()).ToArray());
        CollectionAssert.AreEquivalent(
            new[] { "accept", "progress", "todo", "blocked", "needs_approval", "rejected", "completed" },
            root.GetProperty("allowed_dispositions").EnumerateArray().Select(e => e.GetString()).ToArray());
        Assert.AreEqual(1, root.GetProperty("recent_events").GetArrayLength());
        Assert.AreEqual("task.created", root.GetProperty("recent_events")[0].GetProperty("event_type").GetString());

        // events_limit 未提供时默认 20，且身份恒为 context 注入
        var getArgs = service.LastGetArgs!.Value;
        Assert.AreEqual("ws-1", getArgs.WorkspaceId);
        Assert.AreEqual("task-1", getArgs.TaskId);
        Assert.AreEqual("agent-1", getArgs.AgentId);
        Assert.AreEqual(20, getArgs.EventsLimit);
    }

    [TestMethod]
    public async Task Get_EventsLimit_IsPassedThrough()
    {
        var service = new FakeTaskAgentCommandService
        {
            Get = (_, _, _, _, _) => Task.FromResult<TaskAgentGetResult?>(GetResult()),
        };

        await RunAsync(GetTool(service), """{"task_id":"task-1","events_limit":5}""");

        Assert.AreEqual(5, service.LastGetArgs!.Value.EventsLimit);
    }

    [TestMethod]
    public async Task Get_ServiceReturnsNull_ReturnsNotFound()
    {
        var service = new FakeTaskAgentCommandService(); // 默认 GetAsync 返回 null

        var result = await RunAsync(GetTool(service), """{"task_id":"task-1"}""");

        var error = ParseError(result);
        Assert.AreEqual("task.not_found", error.GetProperty("code").GetString());
        Assert.AreEqual("task-1", error.GetProperty("task_id").GetString());
    }

    [TestMethod]
    public async Task Get_AssignmentMismatch_ReturnsAssignmentStale()
    {
        var service = new FakeTaskAgentCommandService
        {
            Get = (_, _, _, _, _) => Task.FromResult<TaskAgentGetResult?>(GetResult(activeAssignmentId: "assign-2")),
        };

        var result = await RunAsync(GetTool(service), """{"task_id":"task-1","assignment_id":"assign-1"}""");

        AssertErrorCode(result, "assignment.stale");
    }

    [TestMethod]
    public async Task Get_AssignmentMatch_Ok()
    {
        var service = new FakeTaskAgentCommandService
        {
            Get = (_, _, _, _, _) => Task.FromResult<TaskAgentGetResult?>(GetResult(activeAssignmentId: "assign-1")),
        };

        var result = await RunAsync(GetTool(service), """{"task_id":"task-1","assignment_id":"assign-1"}""");

        Assert.IsTrue(result.Success, result.Error);
    }

    [TestMethod]
    public async Task Get_CancelledOrArchived_BoardColumnNull_NoThrow()
    {
        var service = new FakeTaskAgentCommandService
        {
            Get = (_, _, _, _, _) => Task.FromResult<TaskAgentGetResult?>(
                GetResult(boardColumn: null, archived: true)),
        };

        var result = await RunAsync(GetTool(service), """{"task_id":"task-1"}""");

        var root = ParseOutput(result);
        var task = root.GetProperty("task");
        Assert.IsTrue(task.GetProperty("archived").GetBoolean());
        Assert.IsFalse(task.TryGetProperty("board_column", out _), "Cancelled/Archived 的 board_column 应为 null 且被忽略");
    }

    [TestMethod]
    public async Task Get_ServiceThrows_ReturnsErrorJson()
    {
        var service = new FakeTaskAgentCommandService
        {
            Get = (_, _, _, _, _) => Task.FromException<TaskAgentGetResult?>(
                new TaskStoreException(TaskErrorCode.TaskNotFound, "gone", "task-1")),
        };

        var result = await RunAsync(GetTool(service), """{"task_id":"task-1"}""");

        AssertErrorCode(result, "task.not_found");
    }

    [TestMethod]
    public async Task Get_Disabled_ReturnsCapabilityMissing()
    {
        var result = await RunAsync(GetTool(new FakeTaskAgentCommandService(), enabled: false), """{"task_id":"task-1"}""");

        AssertErrorCode(result, "capability.missing");
    }

    // ─────────────────────────────────────────────────────────────
    // ④ TaskClaimTool
    // ─────────────────────────────────────────────────────────────

    [TestMethod]
    public async Task Claim_Success_ReturnsClaimResult()
    {
        var service = new FakeTaskAgentCommandService();

        var root = ParseOutput(await RunAsync(
            ClaimTool(service),
            """{"task_id":"task-1","assignment_id":"assign-1","expected_version":1}""",
            Context(activeTask: ActiveTask())));

        Assert.AreEqual("task-1", root.GetProperty("task_id").GetString());
        Assert.AreEqual("InProgress", root.GetProperty("status").GetString());
        Assert.AreEqual(2, root.GetProperty("version").GetInt32());
        Assert.AreEqual("assign-1", root.GetProperty("assignment_id").GetString());
        Assert.AreEqual("InProgress", root.GetProperty("assignment_status").GetString());
        Assert.AreEqual("task.accepted", root.GetProperty("event").GetString());
        Assert.AreEqual("InProgress", root.GetProperty("board_column").GetString());
    }

    [TestMethod]
    public async Task Claim_NoActiveTask_ReturnsActiveContextMissing()
    {
        var service = new FakeTaskAgentCommandService();

        var result = await RunAsync(
            ClaimTool(service),
            """{"task_id":"task-1","assignment_id":"assign-1","expected_version":1}""",
            Context());

        AssertErrorCode(result, "task.active_context_missing");
    }

    [TestMethod]
    public async Task Claim_TaskIdMismatch_ReturnsStateConflict()
    {
        var service = new FakeTaskAgentCommandService();

        var result = await RunAsync(
            ClaimTool(service),
            """{"task_id":"task-2","assignment_id":"assign-1","expected_version":1}""",
            Context(activeTask: ActiveTask(taskId: "task-1")));

        AssertErrorCode(result, "task.state_conflict");
    }

    [TestMethod]
    public async Task Claim_AssignmentIdMismatch_ReturnsStateConflict()
    {
        var service = new FakeTaskAgentCommandService();

        var result = await RunAsync(
            ClaimTool(service),
            """{"task_id":"task-1","assignment_id":"assign-2","expected_version":1}""",
            Context(activeTask: ActiveTask(assignmentId: "assign-1")));

        AssertErrorCode(result, "task.state_conflict");
    }

    [TestMethod]
    public async Task Claim_InjectedSnapshotStale_WorkerPassesLiveVersion_Succeeds()
    {
        // 缺陷 2d5a2ebe 回归：注入快照 v1（派发时刻）vs 服务端活版本 v2；worker 传 expected_version=2
        // → 不再被注入快照第一重 CAS 拦死，请求 ExpectedVersion==2 且成功（旧语义此处返回 state_conflict）。
        var service = new FakeTaskAgentCommandService
        {
            Get = (_, _, _, _, _) => Task.FromResult<TaskAgentGetResult?>(GetResult(status: "Assigned", version: 2)),
        };

        var root = ParseOutput(await RunAsync(
            ClaimTool(service),
            """{"task_id":"task-1","assignment_id":"assign-1","expected_version":2}""",
            Context(activeTask: ActiveTask(expectedVersion: 1))));

        Assert.AreEqual("task-1", root.GetProperty("task_id").GetString());
        Assert.AreEqual("InProgress", root.GetProperty("status").GetString());
        Assert.AreEqual(2, service.LastClaimRequest!.ExpectedVersion);
    }

    [TestMethod]
    public async Task Claim_ServiceThrowsVersionConflict_ReturnsVersionConflict()
    {
        var service = new FakeTaskAgentCommandService
        {
            Claim = (_, _) => Task.FromException<TaskAgentMutationResult>(
                new TaskStoreException(TaskErrorCode.TaskVersionConflict, "version conflict", "task-1", actualVersion: 5)),
        };

        var result = await RunAsync(
            ClaimTool(service),
            """{"task_id":"task-1","assignment_id":"assign-1","expected_version":1}""",
            Context(activeTask: ActiveTask()));

        var error = ParseError(result);
        Assert.AreEqual("task.version_conflict", error.GetProperty("code").GetString());
        Assert.AreEqual(5, error.GetProperty("current_version").GetInt32());
    }

    [TestMethod]
    public async Task Claim_ServiceThrowsAssignmentStale_ReturnsAssignmentStale()
    {
        var service = new FakeTaskAgentCommandService
        {
            Claim = (_, _) => Task.FromException<TaskAgentMutationResult>(
                new TaskStoreException(TaskErrorCode.AssignmentStale, "stale assignment", "task-1")),
        };

        var result = await RunAsync(
            ClaimTool(service),
            """{"task_id":"task-1","assignment_id":"assign-1","expected_version":1}""",
            Context(activeTask: ActiveTask()));

        AssertErrorCode(result, "assignment.stale");
    }

    [TestMethod]
    public async Task Claim_UsesActiveTaskAndExecutionIdentity()
    {
        var service = new FakeTaskAgentCommandService();

        await RunAsync(
            ClaimTool(service),
            """{"task_id":"task-1","assignment_id":"assign-1","expected_version":1}""",
            Context(activeTask: ActiveTask(), identity: Identity(runId: "run-9", traceId: "trace-9")));

        var request = service.LastClaimRequest!;
        Assert.AreEqual(WorkspaceId, request.WorkspaceId);
        Assert.AreEqual("task-1", request.TaskId);
        Assert.AreEqual("assign-1", request.AssignmentId);
        Assert.AreEqual(1, request.ExpectedVersion);
        Assert.AreEqual(AgentId, request.AgentId);
        Assert.AreEqual("run-9", request.ExecutionId);
        Assert.AreEqual(SessionId, request.SessionId);
        Assert.AreEqual("trace-9", request.TraceId);
    }

    [TestMethod]
    public async Task Claim_Disabled_ReturnsCapabilityMissing()
    {
        var result = await RunAsync(
            ClaimTool(new FakeTaskAgentCommandService(), enabled: false),
            """{"task_id":"task-1","assignment_id":"assign-1","expected_version":1}""",
            Context(activeTask: ActiveTask()));

        AssertErrorCode(result, "capability.missing");
    }

    // ─────────────────────────────────────────────────────────────
    // ⑤ TaskUpdateTool
    // ─────────────────────────────────────────────────────────────

    [TestMethod]
    public async Task Update_AllSevenDispositions_Succeed()
    {
        var cases = new (string Disposition, string Extra)[]
        {
            ("accept", ""),
            ("progress", ",\u0022progress_summary\u0022:\u0022doing\u0022"),
            ("todo", ""),
            ("blocked", ",\u0022reason\u0022:\u0022blocked by X\u0022"),
            ("needs_approval", ",\u0022reason\u0022:\u0022await approval\u0022"),
            ("rejected", ",\u0022reason\u0022:\u0022wrong scope\u0022"),
            ("completed", ",\u0022result_summary\u0022:\u0022done\u0022"),
        };

        foreach (var (disposition, extra) in cases)
        {
            var service = new FakeTaskAgentCommandService();
            var argsJson = $$"""{"task_id":"task-1","assignment_id":"assign-1","expected_version":1,"disposition":"{{disposition}}"{{extra}}}""";

            var result = await RunAsync(UpdateTool(service), argsJson, Context(activeTask: ActiveTask()));

            var root = ParseOutput(result);
            Assert.AreEqual(disposition, root.GetProperty("disposition").GetString(), $"disposition '{disposition}' 应成功");
            Assert.IsTrue(root.GetProperty("version").GetInt32() >= 1);
        }
    }

    [TestMethod]
    public async Task Update_Progress_MergesSummaryAndNextAction()
    {
        var service = new FakeTaskAgentCommandService();

        await RunAsync(
            UpdateTool(service),
            """{"task_id":"task-1","assignment_id":"assign-1","expected_version":1,"disposition":"progress","progress_summary":"doing X","next_action":"then Y","progress_percent":42}""",
            Context(activeTask: ActiveTask()));

        var request = service.LastUpdateRequest!;
        Assert.AreEqual("doing X\nthen Y", request.ProgressSummary);
        Assert.AreEqual(42, request.ProgressPercent);
    }

    [TestMethod]
    public async Task Update_UnknownDisposition_ReturnsInvalidDisposition()
    {
        var service = new FakeTaskAgentCommandService();

        var result = await RunAsync(
            UpdateTool(service),
            """{"task_id":"task-1","assignment_id":"assign-1","expected_version":1,"disposition":"bogus"}""",
            Context(activeTask: ActiveTask()));

        AssertErrorCode(result, "task.invalid_disposition");
    }

    [TestMethod]
    public async Task Update_BlockedRejectedNeedsApproval_MissingReason_ReturnsReasonRequired()
    {
        foreach (var disposition in new[] { "blocked", "rejected", "needs_approval" })
        {
            var service = new FakeTaskAgentCommandService();
            var argsJson = $$"""{"task_id":"task-1","assignment_id":"assign-1","expected_version":1,"disposition":"{{disposition}}"}""";

            var result = await RunAsync(UpdateTool(service), argsJson, Context(activeTask: ActiveTask()));

            AssertErrorCode(result, "task.reason_required");
            Assert.IsNull(service.LastUpdateRequest, $"disposition '{disposition}' 缺 reason 时不得调用服务");
        }
    }

    [TestMethod]
    public async Task Update_Progress_MissingSummaryAndNextAction_ReturnsReasonRequired()
    {
        var service = new FakeTaskAgentCommandService();

        var result = await RunAsync(
            UpdateTool(service),
            """{"task_id":"task-1","assignment_id":"assign-1","expected_version":1,"disposition":"progress"}""",
            Context(activeTask: ActiveTask()));

        AssertErrorCode(result, "task.reason_required");
    }

    [TestMethod]
    public async Task Update_Completed_MissingResultSummary_ReturnsResultRequired()
    {
        var service = new FakeTaskAgentCommandService();

        var result = await RunAsync(
            UpdateTool(service),
            """{"task_id":"task-1","assignment_id":"assign-1","expected_version":1,"disposition":"completed"}""",
            Context(activeTask: ActiveTask()));

        AssertErrorCode(result, "task.result_required");
    }

    [TestMethod]
    public async Task Update_ProgressPercentOutOfRange_ReturnsPlainText()
    {
        foreach (var percent in new[] { -1, 101 })
        {
            var service = new FakeTaskAgentCommandService();
            var argsJson = $$"""{"task_id":"task-1","assignment_id":"assign-1","expected_version":1,"disposition":"progress","progress_summary":"doing","progress_percent":{{percent}}}""";

            var result = await RunAsync(UpdateTool(service), argsJson, Context(activeTask: ActiveTask()));

            Assert.IsFalse(result.Success);
            Assert.AreEqual("progress_percent must be between 0 and 100.", result.Error);
            Assert.IsFalse(result.Error!.StartsWith('{'), "越界应返回纯文本而非错误 JSON");
            Assert.IsNull(service.LastUpdateRequest);
        }
    }

    [TestMethod]
    public async Task Update_ArtifactsEmptyString_ReturnsPlainText()
    {
        var service = new FakeTaskAgentCommandService();

        var result = await RunAsync(
            UpdateTool(service),
            """{"task_id":"task-1","assignment_id":"assign-1","expected_version":1,"disposition":"completed","result_summary":"done","artifacts":["a",""]}""",
            Context(activeTask: ActiveTask()));

        Assert.IsFalse(result.Success);
        Assert.AreEqual("artifacts must contain only non-empty strings.", result.Error);
        Assert.IsFalse(result.Error!.StartsWith('{'));
        Assert.IsNull(service.LastUpdateRequest);
    }

    [TestMethod]
    public async Task Update_NoActiveTask_ReturnsActiveContextMissing()
    {
        var service = new FakeTaskAgentCommandService();

        var result = await RunAsync(
            UpdateTool(service),
            """{"task_id":"task-1","assignment_id":"assign-1","expected_version":1,"disposition":"accept"}""",
            Context());

        AssertErrorCode(result, "task.active_context_missing");
    }

    [TestMethod]
    public async Task Update_TaskIdMismatch_ReturnsStateConflict()
    {
        var service = new FakeTaskAgentCommandService();

        var result = await RunAsync(
            UpdateTool(service),
            """{"task_id":"task-2","assignment_id":"assign-1","expected_version":1,"disposition":"accept"}""",
            Context(activeTask: ActiveTask(taskId: "task-1")));

        AssertErrorCode(result, "task.state_conflict");
    }

    [TestMethod]
    public async Task Update_AssignmentIdMismatch_ReturnsStateConflict()
    {
        var service = new FakeTaskAgentCommandService();

        var result = await RunAsync(
            UpdateTool(service),
            """{"task_id":"task-1","assignment_id":"assign-2","expected_version":1,"disposition":"accept"}""",
            Context(activeTask: ActiveTask(assignmentId: "assign-1")));

        AssertErrorCode(result, "task.state_conflict");
    }

    [TestMethod]
    public async Task Update_InjectedSnapshotStale_WorkerPassesLiveVersion_Succeeds()
    {
        // 缺陷 2d5a2ebe 回归：注入快照 v1 vs 服务端活版本 v2；worker 传 expected_version=2
        // → 请求 ExpectedVersion==2 且成功（旧语义第一重校验会拦死该路径）。
        var service = new FakeTaskAgentCommandService
        {
            Get = (_, _, _, _, _) => Task.FromResult<TaskAgentGetResult?>(GetResult(status: "InProgress", version: 2)),
        };

        var root = ParseOutput(await RunAsync(
            UpdateTool(service),
            """{"task_id":"task-1","assignment_id":"assign-1","expected_version":2,"disposition":"progress","progress_summary":"doing"}""",
            Context(activeTask: ActiveTask(expectedVersion: 1))));

        Assert.AreEqual("progress", root.GetProperty("disposition").GetString());
        Assert.AreEqual(2, service.LastUpdateRequest!.ExpectedVersion);
    }

    [TestMethod]
    public async Task Update_ServiceThrowsVersionConflict_ReturnsVersionConflict()
    {
        var service = new FakeTaskAgentCommandService
        {
            ApplyDisposition = (_, _) => Task.FromException<TaskAgentMutationResult>(
                new TaskStoreException(TaskErrorCode.TaskVersionConflict, "version conflict", "task-1", actualVersion: 5)),
        };

        var result = await RunAsync(
            UpdateTool(service),
            """{"task_id":"task-1","assignment_id":"assign-1","expected_version":1,"disposition":"accept"}""",
            Context(activeTask: ActiveTask()));

        var error = ParseError(result);
        Assert.AreEqual("task.version_conflict", error.GetProperty("code").GetString());
        Assert.AreEqual(5, error.GetProperty("current_version").GetInt32());
    }

    [TestMethod]
    public async Task Update_ServiceThrowsAssignmentStale_ReturnsAssignmentStale()
    {
        var service = new FakeTaskAgentCommandService
        {
            ApplyDisposition = (_, _) => Task.FromException<TaskAgentMutationResult>(
                new TaskStoreException(TaskErrorCode.AssignmentStale, "stale assignment", "task-1")),
        };

        var result = await RunAsync(
            UpdateTool(service),
            """{"task_id":"task-1","assignment_id":"assign-1","expected_version":1,"disposition":"accept"}""",
            Context(activeTask: ActiveTask()));

        AssertErrorCode(result, "assignment.stale");
    }

    [TestMethod]
    public async Task Update_UsesActiveTaskAndExecutionIdentity()
    {
        var service = new FakeTaskAgentCommandService();

        await RunAsync(
            UpdateTool(service),
            """{"task_id":"task-1","assignment_id":"assign-1","expected_version":1,"disposition":"blocked","reason":"blocked by Y"}""",
            Context(activeTask: ActiveTask(), identity: Identity(runId: "run-9", traceId: "trace-9")));

        var request = service.LastUpdateRequest!;
        Assert.AreEqual(WorkspaceId, request.WorkspaceId);
        Assert.AreEqual("task-1", request.TaskId);
        Assert.AreEqual("assign-1", request.AssignmentId);
        Assert.AreEqual(1, request.ExpectedVersion);
        Assert.AreEqual("blocked", request.Disposition);
        Assert.AreEqual("blocked by Y", request.Reason);
        Assert.AreEqual(AgentId, request.AgentId);
        Assert.AreEqual("run-9", request.ExecutionId);
        Assert.AreEqual(SessionId, request.SessionId);
        Assert.AreEqual("trace-9", request.TraceId);
    }

    [TestMethod]
    public async Task Update_Disabled_ReturnsCapabilityMissing()
    {
        var result = await RunAsync(
            UpdateTool(new FakeTaskAgentCommandService(), enabled: false),
            """{"task_id":"task-1","assignment_id":"assign-1","expected_version":1,"disposition":"accept"}""",
            Context(activeTask: ActiveTask()));

        AssertErrorCode(result, "capability.missing");
    }

    // ─────────────────────────────────────────────────────────────
    // ⑥ ActiveTask 丢失 → 服务端反查重建（缺陷 3f8df399）
    // ─────────────────────────────────────────────────────────────

    [TestMethod]
    public async Task Claim_ActiveTaskLost_LegalOwnership_RebuildsContextAndSucceeds()
    {
        var service = new FakeTaskAgentCommandService
        {
            Get = (_, _, _, _, _) => Task.FromResult<TaskAgentGetResult?>(GetResult(status: "Assigned", version: 1)),
        };

        var root = ParseOutput(await RunAsync(
            ClaimTool(service),
            """{"task_id":"task-1","assignment_id":"assign-1","expected_version":1}""",
            Context())); // 宿主重启后的新 run：无 ActiveTask

        Assert.AreEqual("task-1", root.GetProperty("task_id").GetString());
        Assert.AreEqual("InProgress", root.GetProperty("status").GetString());
        var request = service.LastClaimRequest!;
        Assert.AreEqual(WorkspaceId, request.WorkspaceId);
        Assert.AreEqual("task-1", request.TaskId);
        Assert.AreEqual("assign-1", request.AssignmentId);
        Assert.AreEqual(1, request.ExpectedVersion);
        Assert.AreEqual(AgentId, request.AgentId);

        // 反查使用当前 Agent 身份（mine 过滤），eventsLimit 收敛为 1。
        var getArgs = service.LastGetArgs!.Value;
        Assert.AreEqual(WorkspaceId, getArgs.WorkspaceId);
        Assert.AreEqual("task-1", getArgs.TaskId);
        Assert.AreEqual(AgentId, getArgs.AgentId);
        Assert.AreEqual(1, getArgs.EventsLimit);
    }

    [TestMethod]
    public async Task Claim_ActiveTaskLost_NotMineOrMissing_StaysRejected()
    {
        // GetAsync 未配置 → 返回 null（mine 信息隐藏：不存在与归属他人统一 null）。
        var service = new FakeTaskAgentCommandService();

        var result = await RunAsync(
            ClaimTool(service),
            """{"task_id":"task-1","assignment_id":"assign-1","expected_version":1}""",
            Context());

        AssertErrorCode(result, "task.active_context_missing");
        Assert.IsNull(service.LastClaimRequest, "重建失败时不得调用 ClaimAsync");
    }

    [TestMethod]
    public async Task Claim_ActiveTaskLost_AssignmentAgentMismatch_StaysRejected()
    {
        var service = new FakeTaskAgentCommandService
        {
            Get = (_, _, _, _, _) => Task.FromResult<TaskAgentGetResult?>(
                GetResult(status: "Assigned", assignmentAgentId: "agent-2")),
        };

        var result = await RunAsync(
            ClaimTool(service),
            """{"task_id":"task-1","assignment_id":"assign-1","expected_version":1}""",
            Context());

        AssertErrorCode(result, "task.active_context_missing");
        Assert.IsNull(service.LastClaimRequest, "归属防御校验失败时不得调用 ClaimAsync");
    }

    [TestMethod]
    public async Task Claim_ActiveTaskLost_VersionMismatch_ReturnsVersionConflict()
    {
        var service = new FakeTaskAgentCommandService
        {
            Get = (_, _, _, _, _) => Task.FromResult<TaskAgentGetResult?>(GetResult(status: "Assigned", version: 5)),
        };

        var result = await RunAsync(
            ClaimTool(service),
            """{"task_id":"task-1","assignment_id":"assign-1","expected_version":1}""",
            Context());

        var error = ParseError(result);
        Assert.AreEqual("task.version_conflict", error.GetProperty("code").GetString());
        Assert.AreEqual(5, error.GetProperty("current_version").GetInt32());
        Assert.IsNull(service.LastClaimRequest);
    }

    [TestMethod]
    public async Task Claim_ActiveTaskLost_TerminalState_ReturnsStateConflict()
    {
        var service = new FakeTaskAgentCommandService
        {
            Get = (_, _, _, _, _) => Task.FromResult<TaskAgentGetResult?>(GetResult(status: "Completed", version: 7)),
        };

        var result = await RunAsync(
            ClaimTool(service),
            """{"task_id":"task-1","assignment_id":"assign-1","expected_version":7}""",
            Context());

        var error = ParseError(result);
        Assert.AreEqual("task.state_conflict", error.GetProperty("code").GetString());
        Assert.AreEqual("Completed", error.GetProperty("current_status").GetString());
        Assert.IsNull(service.LastClaimRequest);
    }

    [TestMethod]
    public async Task Claim_ActiveTaskLost_AssignmentNotActive_ReturnsAssignmentStale()
    {
        var service = new FakeTaskAgentCommandService
        {
            Get = (_, _, _, _, _) => Task.FromResult<TaskAgentGetResult?>(
                GetResult(status: "InProgress", activeAssignmentId: "assign-2")),
        };

        var result = await RunAsync(
            ClaimTool(service),
            """{"task_id":"task-1","assignment_id":"assign-1","expected_version":1}""",
            Context());

        AssertErrorCode(result, "assignment.stale");
        Assert.IsNull(service.LastClaimRequest);
    }

    [TestMethod]
    public async Task Update_ActiveTaskLost_InProgress_RebuildsContextAndSucceeds()
    {
        var service = new FakeTaskAgentCommandService
        {
            Get = (_, _, _, _, _) => Task.FromResult<TaskAgentGetResult?>(GetResult(status: "InProgress", version: 3)),
        };

        var root = ParseOutput(await RunAsync(
            UpdateTool(service),
            """{"task_id":"task-1","assignment_id":"assign-1","expected_version":3,"disposition":"completed","result_summary":"done"}""",
            Context()));

        Assert.AreEqual("completed", root.GetProperty("disposition").GetString());
        var request = service.LastUpdateRequest!;
        Assert.AreEqual(WorkspaceId, request.WorkspaceId);
        Assert.AreEqual("task-1", request.TaskId);
        Assert.AreEqual("assign-1", request.AssignmentId);
        Assert.AreEqual(3, request.ExpectedVersion);
        Assert.AreEqual(AgentId, request.AgentId);
    }

    [TestMethod]
    public async Task Update_ActiveTaskLost_AssignedState_Rejected()
    {
        // update 场景要求 InProgress；Assigned（未认领）不得绕过状态机。
        var service = new FakeTaskAgentCommandService
        {
            Get = (_, _, _, _, _) => Task.FromResult<TaskAgentGetResult?>(GetResult(status: "Assigned", version: 1)),
        };

        var result = await RunAsync(
            UpdateTool(service),
            """{"task_id":"task-1","assignment_id":"assign-1","expected_version":1,"disposition":"accept"}""",
            Context());

        var error = ParseError(result);
        Assert.AreEqual("task.state_conflict", error.GetProperty("code").GetString());
        Assert.AreEqual("Assigned", error.GetProperty("current_status").GetString());
        Assert.IsNull(service.LastUpdateRequest);
    }

    [TestMethod]
    public async Task Update_ActiveTaskLost_VersionMismatch_ReturnsVersionConflict()
    {
        var service = new FakeTaskAgentCommandService
        {
            Get = (_, _, _, _, _) => Task.FromResult<TaskAgentGetResult?>(GetResult(status: "InProgress", version: 9)),
        };

        var result = await RunAsync(
            UpdateTool(service),
            """{"task_id":"task-1","assignment_id":"assign-1","expected_version":2,"disposition":"progress","progress_summary":"doing"}""",
            Context());

        var error = ParseError(result);
        Assert.AreEqual("task.version_conflict", error.GetProperty("code").GetString());
        Assert.AreEqual(9, error.GetProperty("current_version").GetInt32());
        Assert.IsNull(service.LastUpdateRequest);
    }

    [TestMethod]
    public async Task Claim_ActiveTaskPresent_Mismatch_DoesNotRebuild()
    {
        // 注入上下文存在时的参数不匹配仍是真实错误：不得触发反查。
        var service = new FakeTaskAgentCommandService
        {
            Get = (_, _, _, _, _) => throw new InvalidOperationException("rebuild must not run when ActiveTask is present"),
        };

        var result = await RunAsync(
            ClaimTool(service),
            """{"task_id":"task-2","assignment_id":"assign-1","expected_version":1}""",
            Context(activeTask: ActiveTask(taskId: "task-1")));

        AssertErrorCode(result, "task.state_conflict");
    }

    [TestMethod]
    public void TaskTools_Are_AutoAllowed_After_Low_Reclassification()
    {
        // 2026-08-28 裁定：task 看板元数据，非用户数据直接损坏/泄露，由 Medium 降为 Low ⇒ AutoAllowed 免审直通。
        var policy = new PuddingRuntime.Services.Tools.ToolPermissionPolicyService();
        IPuddingTool[] tools =
        [
            ListTool(new FakeTaskAgentCommandService()),
            GetTool(new FakeTaskAgentCommandService()),
            ClaimTool(new FakeTaskAgentCommandService()),
            UpdateTool(new FakeTaskAgentCommandService()),
        ];

        foreach (var tool in tools)
        {
            var descriptor = tool.Descriptor;
            var decision = policy.Classify(descriptor);

            Assert.AreEqual(ToolPermissionLevel.Low, descriptor.PermissionLevel, descriptor.ToolId);
            Assert.AreEqual(ToolPermissionTier.AutoAllowed, decision.Tier, descriptor.ToolId);
            Assert.IsFalse(decision.RequiresRuntimeAuthorization, descriptor.ToolId);
        }
    }
}
