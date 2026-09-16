using System.Text.Json;
using PuddingCode.Goals;
using PuddingCode.Tools;
using PuddingRuntime.Services.TaskTools;

namespace PuddingPlatformTests.Tools;

/// <summary>
/// GR-2：goal_resume Runtime 工具单元测试（薄适配器；恢复语义与三道 fail-closed 闸门由
/// Platform 层 <c>GoalResumeService</c>（GR-1）负责，此处用手写 fake 隔离）。
/// <para>
/// 与 TaskGoalStartToolTests 同目录同模式：该工程引用 PuddingRuntime（csproj:18）且可编译。
/// 覆盖：① 成功路径 ⇒ 断言注入服务的是上下文身份（workspace/agent/session）且结果正确映射；
/// ② 身份只能来自 context ⇒ 参数侧伪造 workspace_id/agent_instance_id/conversation_id 不生效；
/// ③ 熔断态缺证据 ⇒ 拒绝码 goal.circuit_evidence_required 原样透传（结构化失败、不抛异常）；
/// ④ 其余拒绝码原样透传；⑤ evidence_refs 数组透传；⑥ 幂等 already_active（resumed=false）。
/// </para>
/// </summary>
[TestClass]
public sealed class GoalResumeToolTests
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

    private static async Task<ToolExecutionResult> RunAsync(FakeGoalResumeService service, string argsJson)
        => await new GoalResumeTool(service).ExecuteAsync(new ToolExecutionRequest
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

    // ── ① 成功路径：上下文身份注入 + 结果映射 ──

    [TestMethod]
    public async Task Execute_Success_PassesContextIdentity_AndMapsResult()
    {
        var service = new FakeGoalResumeService
        {
            Result = new GoalResumeResult
            {
                Success = true,
                Resumed = true,
                Code = GoalResumeCodes.Resumed,
                Message = "已恢复 Goal 迭代。",
                GoalRunId = "goal-1",
                Phase = GoalPhase.Active,
                ActivationEpoch = 3,
                AggregateVersion = 9,
                IterationsStarted = 4,
                IterationsSettled = 4,
                MaxIterations = 8,
            },
        };

        var result = await RunAsync(service,
            """{"goal_run_id":"goal-1","expected_version":8,"reason":"熔断已解除"}""");

        Assert.IsTrue(result.Success, result.Error);
        var request = service.LastRequest!;
        Assert.AreEqual(WorkspaceId, request.WorkspaceId, "身份取自 context.WorkspaceId。");
        Assert.AreEqual(AgentId, request.AgentInstanceId, "身份取自 context.AgentInstanceId。");
        Assert.AreEqual(SessionId, request.ConversationId, "身份取自 context.SessionId。");
        Assert.AreEqual("goal-1", request.GoalRunId);
        Assert.AreEqual(8, request.ExpectedVersion);
        Assert.AreEqual("熔断已解除", request.Reason);

        var output = JsonDocument.Parse(result.Output!).RootElement;
        Assert.IsTrue(output.GetProperty("success").GetBoolean());
        Assert.IsTrue(output.GetProperty("resumed").GetBoolean());
        Assert.AreEqual("goal.resumed", output.GetProperty("code").GetString());
        Assert.AreEqual("goal-1", output.GetProperty("goal_run_id").GetString());
        Assert.AreEqual("Active", output.GetProperty("phase").GetString());
        Assert.AreEqual(3, output.GetProperty("activation_epoch").GetInt32());
        Assert.AreEqual(9, output.GetProperty("aggregate_version").GetInt32());
        Assert.AreEqual(8, output.GetProperty("max_iterations").GetInt32());
        Assert.AreEqual("已恢复 Goal 迭代。", output.GetProperty("message").GetString());
    }

    // ── ② 身份只能来自 context：参数侧伪造身份字段不得生效 ──

    [TestMethod]
    public async Task Execute_ForgedIdentityArgs_AreIgnored_ContextWins()
    {
        var service = new FakeGoalResumeService();

        await RunAsync(service,
            """{"workspace_id":"ws-evil","agent_instance_id":"agent-evil","conversation_id":"session-evil"}""");

        var request = service.LastRequest!;
        Assert.AreEqual(WorkspaceId, request.WorkspaceId, "workspace 不可由参数伪造。");
        Assert.AreEqual(AgentId, request.AgentInstanceId, "agent 身份不可由参数伪造。");
        Assert.AreEqual(SessionId, request.ConversationId, "conversation 不可由参数伪造。");
    }

    // ── ③ 熔断态：拒绝码 circuit_evidence_required 原样透传（结构化失败） ──

    [TestMethod]
    public async Task Execute_CircuitEvidenceRequired_IsPassedThrough_WithoutThrowing()
    {
        var service = new FakeGoalResumeService
        {
            Result = new GoalResumeResult
            {
                Success = false,
                Resumed = false,
                Code = GoalResumeCodes.CircuitEvidenceRequired,
                Message = "熔断恢复必须携带证据引用。",
                GoalRunId = "goal-1",
                Phase = GoalPhase.Blocked,
                BlockedCode = GoalResumeCodes.CircuitBlockerCode,
                ActivationEpoch = 2,
            },
        };

        var result = await RunAsync(service, """{"goal_run_id":"goal-1"}""");

        Assert.IsFalse(result.Success);
        var error = ParseError(result);
        Assert.AreEqual("goal.circuit_evidence_required", error.GetProperty("code").GetString());
        Assert.AreEqual("熔断恢复必须携带证据引用。", error.GetProperty("message").GetString());
        Assert.AreEqual("goal-1", error.GetProperty("goal_run_id").GetString());
        Assert.AreEqual("Blocked", error.GetProperty("phase").GetString());
        Assert.AreEqual("no_progress_circuit_open", error.GetProperty("blocked_code").GetString());
        Assert.AreEqual(2, error.GetProperty("activation_epoch").GetInt32());
    }

    // ── ④ 其余拒绝码原样透传（不重命名、不抛异常） ──

    [TestMethod]
    public async Task Execute_RejectionCodes_PassThroughVerbatim()
    {
        var rejectionCodes = new[]
        {
            GoalResumeCodes.NotFound,
            GoalResumeCodes.NotResumable,
            GoalResumeCodes.HeldByOtherAgent,
            GoalResumeCodes.VersionConflict,
            GoalResumeCodes.ResumeEpochLimit,
        };

        foreach (var code in rejectionCodes)
        {
            var service = new FakeGoalResumeService
            {
                Result = new GoalResumeResult
                {
                    Success = false,
                    Resumed = false,
                    Code = code,
                    Message = "rejected: " + code,
                },
            };

            var result = await RunAsync(service, "{}");

            Assert.IsFalse(result.Success, code);
            var error = ParseError(result);
            Assert.AreEqual(code, error.GetProperty("code").GetString(), code);
            Assert.AreEqual("rejected: " + code, error.GetProperty("message").GetString(), code);
        }
    }

    // ── ⑤ evidence_refs 数组透传给服务 ──

    [TestMethod]
    public async Task Execute_EvidenceRefs_PassedThrough()
    {
        var service = new FakeGoalResumeService();

        var result = await RunAsync(service,
            """{"goal_run_id":"goal-1","evidence_refs":["evt-42","artifact://a1"]}""");

        Assert.IsTrue(result.Success, result.Error);
        var refs = service.LastRequest!.EvidenceRefs;
        Assert.IsNotNull(refs);
        Assert.AreEqual(2, refs.Count);
        Assert.AreEqual("evt-42", refs[0]);
        Assert.AreEqual("artifact://a1", refs[1]);
    }

    // ── ⑥ 幂等 already_active：success=true 且 resumed=false ──

    [TestMethod]
    public async Task Execute_AlreadyActive_IsIdempotentSuccess()
    {
        var service = new FakeGoalResumeService
        {
            Result = new GoalResumeResult
            {
                Success = true,
                Resumed = false,
                Code = GoalResumeCodes.AlreadyActive,
                Message = "Goal 已处于 active。",
                GoalRunId = "goal-1",
                Phase = GoalPhase.Active,
            },
        };

        var result = await RunAsync(service, "{}");

        Assert.IsTrue(result.Success, result.Error);
        var output = JsonDocument.Parse(result.Output!).RootElement;
        Assert.IsTrue(output.GetProperty("success").GetBoolean());
        Assert.IsFalse(output.GetProperty("resumed").GetBoolean(), "幂等命中不得虚报实际转换。");
        Assert.AreEqual("goal.already_active", output.GetProperty("code").GetString());
    }

    private sealed class FakeGoalResumeService : IGoalResumeService
    {
        public GoalResumeRequest? LastRequest { get; private set; }

        public GoalResumeResult Result { get; init; } = new()
        {
            Success = true,
            Resumed = true,
            Code = GoalResumeCodes.Resumed,
            Message = "ok",
        };

        public Task<GoalResumeResult> ResumeAsync(GoalResumeRequest request, CancellationToken ct = default)
        {
            LastRequest = request;
            return Task.FromResult(Result);
        }
    }
}
