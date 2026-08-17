using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PuddingCode.Tasks;
using PuddingCode.Tools;
using PuddingRuntime.Services.TaskTools;

namespace PuddingRuntimeTests.Tools;

/// <summary>
/// TB-09 manage_tasks 工具 Runtime 单元测试。
/// <para>覆盖：13 个 action 路由、action/command 二选一归一化、feature flag、
/// 未知 action、get null → not_found、delete false → cannot_hard_delete、TaskStoreException 统一错误体、
/// ActorId 透传。</para>
/// <para>说明：本层只测 Runtime 工具行为（薄适配器）；CRUD/命令语义由 IWorkspaceTaskAdminService
/// （Platform 层）负责，此处用手写 fake 隔离。</para>
/// </summary>
[TestClass]
public sealed class ManageTasksToolTests
{
    private const string WorkspaceId = "ws-1";
    private const string AgentId = "agent-1";
    private const string SessionId = "session-1";

    // ─────────────────────────────────────────────────────────────
    // 构造与运行帮助
    // ─────────────────────────────────────────────────────────────

    private static ToolExecutionContext Context() => new()
    {
        WorkspaceId = WorkspaceId,
        SessionId = SessionId,
        AgentInstanceId = AgentId,
    };

    private static ManageTasksTool Tool(FakeWorkspaceTaskAdminService service, bool enabled = true) =>
        new(service, Options.Create(new WorkspaceTaskFeatureOptions { Enabled = enabled }), NullLogger<ManageTasksTool>.Instance);

    private static async Task<ToolExecutionResult> RunAsync(ManageTasksTool tool, string argsJson)
        => await tool.ExecuteAsync(new ToolExecutionRequest
        {
            ToolCallId = "call-" + Guid.NewGuid().ToString("N")[..8],
            ArgumentsJson = argsJson,
            Context = Context(),
        });

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

    private static TaskAdminGetResult GetResult(string title = "Write report", string taskId = "task-1") => new()
    {
        Task = new TaskAgentTaskDetail
        {
            TaskId = taskId,
            WorkspaceId = WorkspaceId,
            Title = title,
            Status = "Backlog",
            BoardColumn = "Backlog",
            Archived = false,
            Priority = "p3",
            ExecutionWindow = "inherit",
            SortOrder = 0,
            Version = 1,
            CreatedAtUtc = new DateTimeOffset(2026, 8, 17, 0, 0, 0, TimeSpan.Zero),
            UpdatedAtUtc = new DateTimeOffset(2026, 8, 17, 0, 0, 0, TimeSpan.Zero),
        },
        AllowedTransitions = ["Ready"],
        AllowedDispositions = [],
        ActiveAssignment = null,
        RecentEvents = [],
    };

    private static TaskAdminListResult ListResult() => new()
    {
        Total = 0,
        NextCursor = null,
        Items = [],
    };

    // ─────────────────────────────────────────────────────────────
    // 手写 fake（隔离 IWorkspaceTaskAdminService）
    // ─────────────────────────────────────────────────────────────

    private sealed class FakeWorkspaceTaskAdminService : IWorkspaceTaskAdminService
    {
        public string? LastAction { get; private set; }
        public TaskAdminCreateRequest? LastCreate { get; private set; }
        public TaskAdminListQuery? LastList { get; private set; }
        public (string WorkspaceId, string TaskId)? LastGet { get; private set; }
        public TaskAdminUpdateRequest? LastUpdate { get; private set; }
        public (string WorkspaceId, string TaskId)? LastDelete { get; private set; }
        public TaskAdminCommandRequest? LastCommand { get; private set; }

        public Func<TaskAdminCreateRequest, CancellationToken, Task<TaskAdminGetResult>>? OnCreate { get; set; }
        public Func<TaskAdminListQuery, CancellationToken, Task<TaskAdminListResult>>? OnList { get; set; }
        public Func<string, string, CancellationToken, Task<TaskAdminGetResult?>>? OnGet { get; set; }
        public Func<TaskAdminUpdateRequest, CancellationToken, Task<TaskAdminGetResult>>? OnUpdate { get; set; }
        public Func<string, string, CancellationToken, Task<bool>>? OnDelete { get; set; }
        public Func<TaskAdminCommandRequest, CancellationToken, Task<TaskAdminGetResult>>? OnCommand { get; set; }

        public Task<TaskAdminGetResult> CreateTaskAsync(TaskAdminCreateRequest request, CancellationToken ct = default)
        {
            LastAction = "create";
            LastCreate = request;
            return OnCreate is not null ? OnCreate(request, ct) : Task.FromResult(GetResult(request.Title));
        }

        public Task<TaskAdminListResult> ListTasksAsync(TaskAdminListQuery query, CancellationToken ct = default)
        {
            LastAction = "list";
            LastList = query;
            return OnList is not null ? OnList(query, ct) : Task.FromResult(ListResult());
        }

        public Task<TaskAdminGetResult?> GetTaskAsync(string workspaceId, string taskId, CancellationToken ct = default)
        {
            LastAction = "get";
            LastGet = (workspaceId, taskId);
            return OnGet is not null ? OnGet(workspaceId, taskId, ct) : Task.FromResult<TaskAdminGetResult?>(GetResult(taskId: taskId));
        }

        public Task<TaskAdminGetResult> UpdateTaskAsync(TaskAdminUpdateRequest request, CancellationToken ct = default)
        {
            LastAction = "update";
            LastUpdate = request;
            return OnUpdate is not null ? OnUpdate(request, ct) : Task.FromResult(GetResult(taskId: request.TaskId));
        }

        public Task<bool> DeleteTaskAsync(string workspaceId, string taskId, CancellationToken ct = default)
        {
            LastAction = "delete";
            LastDelete = (workspaceId, taskId);
            return OnDelete is not null ? OnDelete(workspaceId, taskId, ct) : Task.FromResult(true);
        }

        public Task<TaskAdminGetResult> ApplyCommandAsync(TaskAdminCommandRequest request, CancellationToken ct = default)
        {
            LastAction = request.Command;
            LastCommand = request;
            return OnCommand is not null ? OnCommand(request, ct) : Task.FromResult(GetResult(taskId: request.TaskId));
        }
    }

    // ─────────────────────────────────────────────────────────────
    // CRUD action 路由
    // ─────────────────────────────────────────────────────────────

    [TestMethod]
    public async Task List_RoutesToListTasksAsync_AndPassesFilters()
    {
        var svc = new FakeWorkspaceTaskAdminService();

        var result = await RunAsync(
            Tool(svc),
            """{"action":"list","status":"Backlog","board_column":"Todo","agent_id":"agent-9","priority":"p0","limit":10,"cursor":"1|task-2"}""");

        var root = ParseOutput(result);
        Assert.AreEqual(0, root.GetProperty("total").GetInt32());
        Assert.AreEqual("list", svc.LastAction);
        var query = svc.LastList!;
        Assert.AreEqual(WorkspaceId, query.WorkspaceId);
        Assert.AreEqual("Backlog", query.Status);
        Assert.AreEqual("Todo", query.BoardColumn);
        Assert.AreEqual("agent-9", query.AgentId);
        Assert.AreEqual("p0", query.Priority);
        Assert.AreEqual(10, query.Limit);
        Assert.AreEqual("1|task-2", query.Cursor);
    }

    [TestMethod]
    public async Task List_DefaultsLimitTo50()
    {
        var svc = new FakeWorkspaceTaskAdminService();

        await RunAsync(Tool(svc), """{"action":"list"}""");

        Assert.AreEqual(50, svc.LastList!.Limit);
    }

    [TestMethod]
    public async Task Create_RoutesToCreateTaskAsync_AndPassesFields()
    {
        var svc = new FakeWorkspaceTaskAdminService();

        var result = await RunAsync(
            Tool(svc),
            """{"action":"create","title":"T","description":"D","acceptance_criteria":"A","priority":"p1","execution_window":"anytime","preferred_agent_id":"agent-9","not_before_utc":"2026-08-18T00:00:00Z","due_at_utc":"2026-08-19T00:00:00Z","sort_order":7}""");

        Assert.IsTrue(result.Success, result.Error);
        Assert.AreEqual("create", svc.LastAction);
        var req = svc.LastCreate!;
        Assert.AreEqual(WorkspaceId, req.WorkspaceId);
        Assert.AreEqual("T", req.Title);
        Assert.AreEqual("D", req.Description);
        Assert.AreEqual("A", req.AcceptanceCriteria);
        Assert.AreEqual("p1", req.Priority);
        Assert.AreEqual("anytime", req.ExecutionWindow);
        Assert.AreEqual("agent-9", req.PreferredAgentId);
        Assert.AreEqual(new DateTimeOffset(2026, 8, 18, 0, 0, 0, TimeSpan.Zero), req.NotBeforeUtc!.Value);
        Assert.AreEqual(new DateTimeOffset(2026, 8, 19, 0, 0, 0, TimeSpan.Zero), req.DueAtUtc!.Value);
        Assert.AreEqual(7L, req.SortOrder!.Value);
    }

    [TestMethod]
    public async Task Get_RoutesToGetTaskAsync_AndSerializesDetail()
    {
        var svc = new FakeWorkspaceTaskAdminService();

        var root = ParseOutput(await RunAsync(Tool(svc), """{"action":"get","task_id":"task-1"}"""));

        Assert.AreEqual("get", svc.LastAction);
        var get = svc.LastGet!.Value;
        Assert.AreEqual(WorkspaceId, get.WorkspaceId);
        Assert.AreEqual("task-1", get.TaskId);
        Assert.AreEqual("task-1", root.GetProperty("task").GetProperty("task_id").GetString());
    }

    [TestMethod]
    public async Task Update_RoutesToUpdateTaskAsync_AndPassesFields()
    {
        var svc = new FakeWorkspaceTaskAdminService();

        var result = await RunAsync(
            Tool(svc),
            """{"action":"update","task_id":"task-1","expected_version":3,"title":"T2","priority":"p2","status":"Ready"}""");

        Assert.IsTrue(result.Success, result.Error);
        Assert.AreEqual("update", svc.LastAction);
        var req = svc.LastUpdate!;
        Assert.AreEqual(WorkspaceId, req.WorkspaceId);
        Assert.AreEqual("task-1", req.TaskId);
        Assert.AreEqual(3, req.ExpectedVersion!.Value);
        Assert.AreEqual("T2", req.Title);
        Assert.AreEqual("p2", req.Priority);
        Assert.AreEqual("Ready", req.Status);
    }

    [TestMethod]
    public async Task Delete_RoutesToDeleteTaskAsync_AndSerializesTrue()
    {
        var svc = new FakeWorkspaceTaskAdminService();

        var root = ParseOutput(await RunAsync(Tool(svc), """{"action":"delete","task_id":"task-1"}"""));

        Assert.AreEqual("delete", svc.LastAction);
        var del = svc.LastDelete!.Value;
        Assert.AreEqual(WorkspaceId, del.WorkspaceId);
        Assert.AreEqual("task-1", del.TaskId);
        Assert.IsTrue(root.GetBoolean());
    }

    // ─────────────────────────────────────────────────────────────
    // 命令 action 路由
    // ─────────────────────────────────────────────────────────────

    [TestMethod]
    public async Task CommandActions_RouteToApplyCommandAsync()
    {
        var commands = new[] { "assign", "run_now", "cancel", "reopen", "archive", "mark_failed", "resume", "requeue" };

        foreach (var cmd in commands)
        {
            var svc = new FakeWorkspaceTaskAdminService();
            var extra = cmd switch
            {
                "assign" => ",\"agent_id\":\"agent-2\"",
                "run_now" => ",\"agent_id\":\"agent-2\",\"window_decision\":\"anytime\"",
                _ => ",\"reason\":\"because\"",
            };
            var argsJson = $$"""{"action":"{{cmd}}","task_id":"task-1"{{extra}}}""";

            var result = await RunAsync(Tool(svc), argsJson);

            Assert.IsTrue(result.Success, result.Error);
            Assert.AreEqual(cmd, svc.LastAction, $"action '{cmd}' 应路由到 ApplyCommandAsync");
            var req = svc.LastCommand!;
            Assert.AreEqual(WorkspaceId, req.WorkspaceId);
            Assert.AreEqual("task-1", req.TaskId);
            Assert.AreEqual(cmd, req.Command);
        }
    }

    [TestMethod]
    public async Task Command_PassesAgentWindowDecisionAndReason()
    {
        var svc = new FakeWorkspaceTaskAdminService();

        await RunAsync(Tool(svc), """{"action":"run_now","task_id":"task-1","agent_id":"agent-2","window_decision":"anytime","reason":"why"}""");

        var req = svc.LastCommand!;
        Assert.AreEqual("run_now", req.Command);
        Assert.AreEqual("agent-2", req.AgentId);
        Assert.AreEqual("anytime", req.WindowDecision);
        Assert.AreEqual("why", req.Reason);
    }

    [TestMethod]
    public async Task CommandFallback_UsesCommandWhenActionMissing()
    {
        var svc = new FakeWorkspaceTaskAdminService();

        var result = await RunAsync(Tool(svc), """{"command":"assign","task_id":"task-1","agent_id":"agent-2"}""");

        Assert.IsTrue(result.Success, result.Error);
        Assert.AreEqual("assign", svc.LastAction);
        Assert.IsNotNull(svc.LastCommand);
    }

    // ─────────────────────────────────────────────────────────────
    // ActorId 透传
    // ─────────────────────────────────────────────────────────────

    [TestMethod]
    public async Task CreateUpdateCommand_PassActorIdFromContextAgentInstanceId()
    {
        var create = new FakeWorkspaceTaskAdminService();
        await RunAsync(Tool(create), """{"action":"create","title":"T"}""");
        Assert.AreEqual(AgentId, create.LastCreate!.ActorId);

        var update = new FakeWorkspaceTaskAdminService();
        await RunAsync(Tool(update), """{"action":"update","task_id":"task-1"}""");
        Assert.AreEqual(AgentId, update.LastUpdate!.ActorId);

        var command = new FakeWorkspaceTaskAdminService();
        await RunAsync(Tool(command), """{"action":"cancel","task_id":"task-1"}""");
        Assert.AreEqual(AgentId, command.LastCommand!.ActorId);
    }

    // ─────────────────────────────────────────────────────────────
    // 错误分支
    // ─────────────────────────────────────────────────────────────

    [TestMethod]
    public async Task Disabled_ReturnsCapabilityMissing()
    {
        var result = await RunAsync(Tool(new FakeWorkspaceTaskAdminService(), enabled: false), """{"action":"list"}""");

        var error = ParseError(result);
        Assert.AreEqual("capability.missing", error.GetProperty("code").GetString());
        Assert.AreEqual("Workspace task tools are disabled (WorkspaceTasks.Enabled=false).", error.GetProperty("message").GetString());
    }

    [TestMethod]
    public async Task UnknownAction_ReturnsInvalidTransition()
    {
        var svc = new FakeWorkspaceTaskAdminService();

        var result = await RunAsync(Tool(svc), """{"action":"bogus"}""");

        var error = ParseError(result);
        Assert.AreEqual("task.invalid_transition", error.GetProperty("code").GetString());
        Assert.IsNull(svc.LastAction);
    }

    [TestMethod]
    public async Task MissingActionAndCommand_ReturnsInvalidTransition()
    {
        var svc = new FakeWorkspaceTaskAdminService();

        var result = await RunAsync(Tool(svc), "{}");

        AssertErrorCode(result, "task.invalid_transition");
    }

    [TestMethod]
    public async Task Get_ServiceReturnsNull_ReturnsNotFound()
    {
        var svc = new FakeWorkspaceTaskAdminService
        {
            OnGet = (_, _, _) => Task.FromResult<TaskAdminGetResult?>(null),
        };

        var result = await RunAsync(Tool(svc), """{"action":"get","task_id":"task-1"}""");

        var error = ParseError(result);
        Assert.AreEqual("task.not_found", error.GetProperty("code").GetString());
        Assert.AreEqual("task-1", error.GetProperty("task_id").GetString());
    }

    [TestMethod]
    public async Task Delete_ServiceReturnsFalse_ReturnsCannotHardDelete()
    {
        var svc = new FakeWorkspaceTaskAdminService
        {
            OnDelete = (_, _, _) => Task.FromResult(false),
        };

        var result = await RunAsync(Tool(svc), """{"action":"delete","task_id":"task-1"}""");

        var error = ParseError(result);
        Assert.AreEqual("task.cannot_hard_delete", error.GetProperty("code").GetString());
        Assert.AreEqual("task-1", error.GetProperty("task_id").GetString());
    }

    [TestMethod]
    public async Task ServiceThrows_ReturnsUnifiedErrorJson()
    {
        var svc = new FakeWorkspaceTaskAdminService
        {
            OnList = (_, _) => Task.FromException<TaskAdminListResult>(
                new TaskStoreException(TaskErrorCode.TaskVersionConflict, "version conflict", "task-1", actualVersion: 5)),
        };

        var result = await RunAsync(Tool(svc), """{"action":"list"}""");

        var error = ParseError(result);
        Assert.AreEqual("task.version_conflict", error.GetProperty("code").GetString());
        Assert.AreEqual(5, error.GetProperty("current_version").GetInt32());
    }
}
