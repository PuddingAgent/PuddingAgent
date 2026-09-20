using System.Text.Json;
using PuddingCode.Goals;
using PuddingCode.Tools;
using PuddingRuntime.Services.TaskTools;

namespace PuddingPlatformTests.Tools;

/// <summary>
/// GL-2：goal_start / goal_pause / goal_cancel Runtime 工具单元测试（薄适配器；创建/暂停/取消
/// 语义与归属过滤由 Platform 层 <c>GoalLifecycleService</c>（GL-1）与 canonical
/// <c>GoalCommandService</c> 负责，此处用手写 fake 隔离）。
/// <para>
/// 覆盖：① 三个工具的身份注入（workspace/session/agent 取自 context）与参数透传；
/// ② 伪造身份参数不生效（身份只能来自 context）；③ 成功码映射
/// goal.started / goal.paused / goal.cancelled；④ 拒绝码原样透传（goal_conflict /
/// goal_not_found）且不抛异常；⑤ phase/预算快照字段映射。
/// </para>
/// </summary>
[TestClass]
public sealed class GoalLifecycleToolsTests
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

    private static async Task<ToolExecutionResult> RunStartAsync(
        FakeGoalLifecycleService service,
        string argsJson)
        => await new GoalStartTool(service).ExecuteAsync(new ToolExecutionRequest
        {
            ToolCallId = "call-" + Guid.NewGuid().ToString("N")[..8],
            ArgumentsJson = argsJson,
            Context = Context(),
        });

    private static async Task<ToolExecutionResult> RunPauseAsync(
        FakeGoalLifecycleService service,
        string argsJson)
        => await new GoalPauseTool(service).ExecuteAsync(new ToolExecutionRequest
        {
            ToolCallId = "call-" + Guid.NewGuid().ToString("N")[..8],
            ArgumentsJson = argsJson,
            Context = Context(),
        });

    private static async Task<ToolExecutionResult> RunCancelAsync(
        FakeGoalLifecycleService service,
        string argsJson)
        => await new GoalCancelTool(service).ExecuteAsync(new ToolExecutionRequest
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

    // ── ① goal_start：上下文身份 + objective/rounds 透传 + 成功码映射 ──

    [TestMethod]
    public async Task Start_Success_PassesContextIdentity_AndMapsStarted()
    {
        var service = new FakeGoalLifecycleService
        {
            Result = new GoalLifecycleResult
            {
                Success = true,
                Code = GoalLifecycleCodes.Started,
                Message = "已开始 Goal 迭代。",
                GoalRunId = "goal-9",
                Objective = "把 schedule_skip 降噪落地",
                Phase = GoalPhase.Active,
                MaxIterations = 12,
                IterationsStarted = 0,
                IterationsSettled = 0,
                ActivationEpoch = 1,
                AggregateVersion = 1,
            },
        };

        var result = await RunStartAsync(service,
            """{"objective":"把 schedule_skip 降噪落地","rounds":12}""");

        Assert.IsTrue(result.Success, result.Error);
        var request = service.LastRequest!;
        Assert.AreEqual(GoalLifecycleAction.Start, request.Action);
        Assert.AreEqual(WorkspaceId, request.WorkspaceId, "身份取自 context.WorkspaceId。");
        Assert.AreEqual(SessionId, request.ConversationId, "身份取自 context.SessionId。");
        Assert.AreEqual(AgentId, request.AgentInstanceId, "身份取自 context.AgentInstanceId。");
        Assert.AreEqual(AgentId, request.UserId, "Agent 自主发起时 UserId 填 Agent 身份。");
        Assert.AreEqual("把 schedule_skip 降噪落地", request.Objective);
        Assert.AreEqual(12, request.Rounds);
        Assert.AreEqual(GoalLifecycleCodes.AgentToolSourceChannel, request.SourceChannel);

        var output = JsonDocument.Parse(result.Output!).RootElement;
        Assert.IsTrue(output.GetProperty("success").GetBoolean());
        Assert.AreEqual("goal.started", output.GetProperty("code").GetString());
        Assert.AreEqual("goal-9", output.GetProperty("goal_run_id").GetString());
        Assert.AreEqual("Active", output.GetProperty("phase").GetString());
        Assert.AreEqual(12, output.GetProperty("max_iterations").GetInt32());
        Assert.AreEqual("已开始 Goal 迭代。", output.GetProperty("message").GetString());
    }

    // ── ② 身份只能来自 context：参数侧伪造必须无效 ──

    [TestMethod]
    public async Task Start_ForgedIdentityArgs_AreIgnored_ContextWins()
    {
        var service = new FakeGoalLifecycleService();

        await RunStartAsync(service,
            """{"objective":"x","workspace_id":"ws-evil","agent_instance_id":"agent-evil","conversation_id":"session-evil"}""");

        var request = service.LastRequest!;
        Assert.AreEqual(WorkspaceId, request.WorkspaceId, "workspace 不可由参数伪造。");
        Assert.AreEqual(AgentId, request.AgentInstanceId, "agent 身份不可由参数伪造。");
        Assert.AreEqual(SessionId, request.ConversationId, "conversation 不可由参数伪造。");
    }

    // ── ③ 已有非终态 Goal：goal_conflict 原样透传（结构化失败、不抛异常） ──

    [TestMethod]
    public async Task Start_GoalConflict_IsPassedThrough_WithoutThrowing()
    {
        var service = new FakeGoalLifecycleService
        {
            Result = new GoalLifecycleResult
            {
                Success = false,
                Code = GoalErrorCodes.GoalConflict,
                Message = "当前会话已有一个非终态 Goal。",
                GoalRunId = "goal-9",
                Phase = GoalPhase.Active,
            },
        };

        var result = await RunStartAsync(service, """{"objective":"新目标"}""");

        var error = ParseError(result);
        Assert.AreEqual("goal_conflict", error.GetProperty("code").GetString());
        Assert.AreEqual("goal-9", error.GetProperty("goal_run_id").GetString());
        Assert.AreEqual("Active", error.GetProperty("phase").GetString());
    }

    // ── ④ goal_pause：Action=Pause + reason 透传 + goal.paused ──

    [TestMethod]
    public async Task Pause_Success_PassesReason_AndMapsPaused()
    {
        var service = new FakeGoalLifecycleService
        {
            Result = new GoalLifecycleResult
            {
                Success = true,
                Code = GoalLifecycleCodes.Paused,
                Message = "Goal 已暂停。",
                GoalRunId = "goal-9",
                Phase = GoalPhase.Paused,
            },
        };

        var result = await RunPauseAsync(service, """{"reason":"等待人工裁决判据缺陷"}""");

        Assert.IsTrue(result.Success, result.Error);
        var request = service.LastRequest!;
        Assert.AreEqual(GoalLifecycleAction.Pause, request.Action);
        Assert.AreEqual("等待人工裁决判据缺陷", request.Reason);
        Assert.IsNull(request.Objective, "pause 不携带 objective。");

        var output = JsonDocument.Parse(result.Output!).RootElement;
        Assert.AreEqual("goal.paused", output.GetProperty("code").GetString());
        Assert.AreEqual("Paused", output.GetProperty("phase").GetString());
    }

    // ── ⑤ goal_cancel：Action=Cancel；无活动 Goal 时 goal_not_found 透传 ──

    [TestMethod]
    public async Task Cancel_NoActiveGoal_PassesGoalNotFoundThrough()
    {
        var service = new FakeGoalLifecycleService
        {
            Result = new GoalLifecycleResult
            {
                Success = false,
                Code = GoalErrorCodes.GoalNotFound,
                Message = "当前会话没有活动 Goal。",
            },
        };

        var result = await RunCancelAsync(service, """{"reason":"目标作废"}""");

        Assert.AreEqual(GoalLifecycleAction.Cancel, service.LastRequest!.Action);
        Assert.AreEqual("目标作废", service.LastRequest!.Reason);
        var error = ParseError(result);
        Assert.AreEqual("goal_not_found", error.GetProperty("code").GetString());
    }

    [TestMethod]
    public async Task Cancel_Success_MapsCancelled()
    {
        var service = new FakeGoalLifecycleService
        {
            Result = new GoalLifecycleResult
            {
                Success = true,
                Code = GoalLifecycleCodes.Cancelled,
                Message = "Goal 已取消。",
                GoalRunId = "goal-9",
                Phase = GoalPhase.Cancelled,
            },
        };

        var result = await RunCancelAsync(service, "{}");

        Assert.IsTrue(result.Success, result.Error);
        var output = JsonDocument.Parse(result.Output!).RootElement;
        Assert.AreEqual("goal.cancelled", output.GetProperty("code").GetString());
        Assert.AreEqual("Cancelled", output.GetProperty("phase").GetString());
    }

    private sealed class FakeGoalLifecycleService : IGoalLifecycleService
    {
        public GoalLifecycleRequest? LastRequest { get; private set; }

        public GoalLifecycleResult Result { get; init; } = new()
        {
            Success = true,
            Code = GoalLifecycleCodes.Started,
            Message = "ok",
        };

        public Task<GoalLifecycleResult> ExecuteAsync(
            GoalLifecycleRequest request,
            CancellationToken ct = default)
        {
            LastRequest = request;
            return Task.FromResult(Result);
        }
    }
}
