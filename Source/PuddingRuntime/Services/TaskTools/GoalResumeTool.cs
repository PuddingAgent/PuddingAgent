using PuddingCode.Goals;
using PuddingCode.Models;
using PuddingCode.Tools;

namespace PuddingRuntime.Services.TaskTools;

/// <summary>
/// goal_resume — Agent 自主恢复 Goal（paused/blocked → active；自动续跑出口，复用 canonical
/// resume 链，见 PuddingCore 契约 <see cref="IGoalResumeService"/>）。
/// <para>
/// 薄适配器：身份（workspace/agent/session）一律取自运行时上下文，绝不作为工具参数暴露；
/// 拒绝路径透传 <see cref="GoalResumeCodes"/> 稳定 wire code（结构化失败、不抛异常）；
/// 三道闸门（归属 goal.held_by_other_agent / 熔断证据 goal.circuit_evidence_required /
/// epoch 配额 goal.resume_epoch_limit）全部由服务层 fail-closed 裁决，工具层不复制判断。
/// </para>
/// </summary>
[Tool(
    id: "goal_resume",
    name: "恢复 Goal 执行",
    description: "恢复当前会话本人的 Goal（paused/blocked → active，复用 canonical resume 链）。【何时用】Goal 因人工暂停或无进展熔断而阻塞、阻塞已解除需要自动续跑时使用。【怎么用】goal_run_id 可选，缺省定位当前会话本人的当前 Goal；expected_version 可选 CAS（不符返回 goal.version_conflict）；reason 可选，写入结果与审计；evidence_refs 为字符串数组——Goal 处于熔断阻塞（blocked_code=no_progress_circuit_open）时必填非空，否则返回 goal.circuit_evidence_required。【坑】workspace/agent/session 身份由运行时上下文注入、不作为参数；同一 activation epoch 内自动恢复上限 1 次（goal.resume_epoch_limit），任何后续状态转换（pause/blocked/人工介入/重启换发 fence）都会推进 epoch 并重置配额；已 active 时幂等返回 goal.already_active（resumed=false，无任何写入）；拒绝（goal.not_found / goal.not_resumable / goal.held_by_other_agent 等）返回结构化失败 code 而非异常。",
    category: ToolCategory.Orchestration,
    permission: ToolPermissionLevel.Low)]
public sealed class GoalResumeTool(IGoalResumeService service) : PuddingToolBase<GoalResumeArgs>
{
    protected override async Task<ToolExecutionResult> ExecuteCoreAsync(
        GoalResumeArgs args,
        ToolExecutionContext context,
        CancellationToken ct)
    {
        var result = await service.ResumeAsync(new GoalResumeRequest
        {
            WorkspaceId = context.WorkspaceId,
            ConversationId = context.SessionId,
            AgentInstanceId = context.AgentInstanceId,
            GoalRunId = args.GoalRunId,
            ExpectedVersion = args.ExpectedVersion,
            Reason = args.Reason,
            EvidenceRefs = args.EvidenceRefs,
        }, ct);

        if (!result.Success)
        {
            // 结构化失败（非异常）：wire code 原样保留，便于 Agent 与面板识别（契约 §4）。
            return ToolExecutionResult.Fail(TaskToolJson.Serialize(new
            {
                error = new
                {
                    code = result.Code,
                    message = result.Message,
                    goal_run_id = result.GoalRunId,
                    phase = result.Phase?.ToString(),
                    blocked_code = result.BlockedCode,
                    activation_epoch = result.ActivationEpoch,
                    aggregate_version = result.AggregateVersion,
                },
            }));
        }

        return ToolExecutionResult.Ok(TaskToolJson.Serialize(new GoalResumeToolResult
        {
            Success = result.Success,
            Resumed = result.Resumed,
            Code = result.Code,
            Message = result.Message,
            GoalRunId = result.GoalRunId,
            Phase = result.Phase?.ToString(),
            BlockedCode = result.BlockedCode,
            ActivationEpoch = result.ActivationEpoch,
            AggregateVersion = result.AggregateVersion,
            IterationsStarted = result.IterationsStarted,
            IterationsSettled = result.IterationsSettled,
            MaxIterations = result.MaxIterations,
        }));
    }
}

/// <summary>goal_resume 参数（身份字段一律由运行时上下文注入，见契约 §4）。</summary>
public sealed record GoalResumeArgs
{
    [ToolParam("可选目标 GoalRunId；缺省定位当前会话本人的当前 Goal。")]
    public string? GoalRunId { get; init; }

    [ToolParam("可选 CAS：期望版本，不符返回 goal.version_conflict。")]
    public int? ExpectedVersion { get; init; }

    [ToolParam("可选恢复原因（进入结果 message 与审计日志）。")]
    public string? Reason { get; init; }

    [ToolParam("熔断恢复证据引用数组：Goal 处于熔断阻塞（no_progress_circuit_open）时必填非空，否则返回 goal.circuit_evidence_required。")]
    public IReadOnlyList<string>? EvidenceRefs { get; init; }
}

/// <summary>goal_resume 成功结果（resumed=true 为本次完成实际转换；false 为幂等 already_active）。</summary>
public sealed record GoalResumeToolResult
{
    public required bool Success { get; init; }

    public required bool Resumed { get; init; }

    public required string Code { get; init; }

    public string? Message { get; init; }

    public string? GoalRunId { get; init; }

    /// <summary>GoalPhase wire 字符串（如 Active/Paused/Blocked），来自服务端状态快照。</summary>
    public string? Phase { get; init; }

    public string? BlockedCode { get; init; }

    public int? ActivationEpoch { get; init; }

    public int? AggregateVersion { get; init; }

    public int? IterationsStarted { get; init; }

    public int? IterationsSettled { get; init; }

    public int? MaxIterations { get; init; }
}
