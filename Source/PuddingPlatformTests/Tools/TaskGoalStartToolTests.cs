using System.Text.Json;
using PuddingCode.Tasks;
using PuddingCode.Tools;
using PuddingRuntime.Services.TaskTools;

namespace PuddingPlatformTests.Tools;

/// <summary>
/// TGS-2：task_goal_start Runtime 工具单元测试（薄适配器；启动语义由 Platform 层
/// <c>TaskGoalLaunchService</c>（TGS-1）负责，此处用手写 fake 隔离）。
/// <para>
/// 选择 PuddingPlatformTests 的理由：该工程引用 PuddingRuntime（csproj:18）且当前可编译；
/// 最自然的 PuddingRuntimeTests 存在预存编译失败（ContextPipelineSkillLayerTests.cs:3 缺
/// Moq 引用，缺陷 b44b33c0 同类，按任务书不顺手修复），TGS-1 服务测试亦在本工程可对照。
/// </para>
/// 覆盖：① 缺 task_id ⇒ 参数校验失败；② iteration_budget 非法（0/负）⇒ 拒绝；
/// ③ 成功路径 ⇒ 断言注入服务的是上下文身份（workspace/agent/session）且结果正确映射；
/// ④ Started=false（task_held_by_other_agent）⇒ 结构化失败含原样 code 且不抛异常。
/// </summary>
[TestClass]
public sealed class TaskGoalStartToolTests
{
    private const string WorkspaceId = "ws-1";
    private const string AgentId = "agent-1";
    private const string SessionId = "session-1";

    private static ToolExecutionContext Context() => new()
    {
        WorkspaceId = WorkspaceId,
        SessionId = SessionId,
        AgentInstanceId = AgentId,
    };

    private static async Task<ToolExecutionResult> RunAsync(FakeTaskGoalLaunchService service, string argsJson)
        => await new TaskGoalStartTool(service).ExecuteAsync(new ToolExecutionRequest
        {
            ToolCallId = "call-" + Guid.NewGuid().ToString("N")[..8],
            ArgumentsJson = argsJson,
            Context = Context(),
        });

    private static JsonElement ParseError(ToolExecutionResult result)
    {
        Assert.IsFalse(result.Success, "expected failure but succeeded");
        Assert.IsNotNull(result.Error);
        return JsonDocument.Parse(result.Error!).RootElement.GetProperty("error");
    }

    // ── ① 缺 task_id ⇒ 参数校验失败（不触达服务） ──

    [TestMethod]
    public async Task Execute_MissingTaskId_FailsParameterValidation()
    {
        var service = new FakeTaskGoalLaunchService();

        var missing = await RunAsync(service, """{"expected_version": 3}""");
        Assert.IsFalse(missing.Success);
        Assert.IsNotNull(missing.Error);
        Assert.IsNull(service.LastRequest, "task_id 缺失时不得触达服务。");

        var nullValue = await RunAsync(service, """{"task_id": null}""");
        Assert.IsFalse(nullValue.Success);
        Assert.IsNotNull(nullValue.Error);
        Assert.IsTrue(nullValue.Error!.Contains("task_id", StringComparison.Ordinal));
        Assert.IsNull(service.LastRequest);
    }

    // ── ② iteration_budget 非法（0 / 负） ⇒ 拒绝 ──

    [TestMethod]
    public async Task Execute_InvalidIterationBudget_IsRejected()
    {
        foreach (var budget in new[] { 0, -1 })
        {
            var service = new FakeTaskGoalLaunchService();
            var result = await RunAsync(service, $$"""{"task_id":"task-1","iteration_budget":{{budget}}}""");

            Assert.IsFalse(result.Success, $"iteration_budget={budget} 应被拒绝。");
            Assert.IsNotNull(result.Error);
            Assert.IsTrue(result.Error!.Contains("iteration_budget", StringComparison.Ordinal));
            Assert.IsNull(service.LastRequest);
        }
    }

    // ── ③ 成功路径：上下文身份注入 + 结果映射 ──

    [TestMethod]
    public async Task Execute_Success_PassesContextIdentity_AndMapsResult()
    {
        var service = new FakeTaskGoalLaunchService
        {
            Result = new TaskGoalLaunchResult
            {
                Started = true,
                Code = TaskGoalLaunchCodes.Started,
                GoalRunId = "goal-1",
                AssignmentId = "assign-1",
                TaskVersion = 5,
                Message = "已启动 Goal 迭代。",
            },
        };

        var result = await RunAsync(service,
            """{"task_id":"task-1","expected_version":5,"iteration_budget":3,"reason":"自驱收口"}""");

        Assert.IsTrue(result.Success, result.Error);
        var request = service.LastRequest!;
        Assert.AreEqual(WorkspaceId, request.WorkspaceId, "身份取自 context.WorkspaceId。");
        Assert.AreEqual(AgentId, request.AgentId, "身份取自 context.AgentInstanceId。");
        Assert.AreEqual(SessionId, request.ConversationId, "身份取自 context.SessionId。");
        Assert.AreEqual("task-1", request.TaskId);
        Assert.AreEqual(5, request.ExpectedVersion);
        Assert.AreEqual(3, request.IterationBudget);
        Assert.AreEqual("自驱收口", request.Reason);

        var output = JsonDocument.Parse(result.Output!).RootElement;
        Assert.IsTrue(output.GetProperty("started").GetBoolean());
        Assert.AreEqual("started", output.GetProperty("code").GetString());
        Assert.AreEqual("goal-1", output.GetProperty("goal_run_id").GetString());
        Assert.AreEqual("assign-1", output.GetProperty("assignment_id").GetString());
        Assert.AreEqual(5, output.GetProperty("task_version").GetInt32());
        Assert.AreEqual("已启动 Goal 迭代。", output.GetProperty("message").GetString());
        Assert.IsFalse(output.TryGetProperty("reservation_id", out _), "启动路径不伪造 reservation_id。");
        Assert.IsFalse(output.TryGetProperty("task_plan_id", out _), "启动路径不伪造 task_plan_id。");
    }

    // ── ④ Started=false ⇒ 结构化失败（code 原样保留，不抛异常） ──

    [TestMethod]
    public async Task Execute_StartedFalse_ReturnsStructuredCode_WithoutThrowing()
    {
        var service = new FakeTaskGoalLaunchService
        {
            Result = new TaskGoalLaunchResult
            {
                Started = false,
                Code = TaskGoalLaunchCodes.HeldByOtherAgent,
                TaskVersion = 4,
                Message = "任务被其他 Agent 持有。",
            },
        };

        var result = await RunAsync(service, """{"task_id":"task-1"}""");

        Assert.IsFalse(result.Success);
        var error = ParseError(result);
        Assert.AreEqual("task_held_by_other_agent", error.GetProperty("code").GetString());
        Assert.AreEqual("任务被其他 Agent 持有。", error.GetProperty("message").GetString());
        Assert.AreEqual("task-1", error.GetProperty("task_id").GetString());
        Assert.AreEqual(4, error.GetProperty("current_version").GetInt32());
    }

    private sealed class FakeTaskGoalLaunchService : ITaskGoalLaunchService
    {
        public TaskGoalLaunchRequest? LastRequest { get; private set; }

        public TaskGoalLaunchResult Result { get; init; } = new()
        {
            Started = true,
            Code = TaskGoalLaunchCodes.Started,
            Message = "ok",
        };

        public Task<TaskGoalLaunchResult> LaunchAsync(TaskGoalLaunchRequest request, CancellationToken ct = default)
        {
            LastRequest = request;
            return Task.FromResult(Result);
        }
    }
}
