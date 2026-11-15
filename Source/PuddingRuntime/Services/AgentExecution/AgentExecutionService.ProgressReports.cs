using PuddingCode.SubAgents;

namespace PuddingRuntime.Services;

/// <summary>
/// 子→主汇报的判定接线（partial）。
/// <para>
/// 这一层只回答「这一刻该不该向父代理汇报」，不负责传输：投递要走宿主消息系统，
/// 与终态通知（<c>SubAgentManager.NotifyParentAgentAsync</c>）不是同一条通道——
/// 终态通知按 <c>(runId, status, parentSession)</c> 做幂等 MessageId，中途进度不能复用，
/// 否则每个 status 只会投递一次。
/// </para>
/// <para>
/// 判定结果作为 <c>subagent.progress.planned</c> 事件落进 run 事件流，目的是让
/// 「某轮为什么没汇报」可事后解释；判定规则本身是纯函数
/// <see cref="SubAgentProgressReportPolicy"/>，已有独立测试覆盖。
/// </para>
/// </summary>
public sealed partial class AgentExecutionService
{
    /// <summary>
    /// 判定本轮结束是否应产生一条进度汇报，并在应投递时推进游标。
    /// </summary>
    /// <param name="subAgentRunId">run 目录句柄；非子代理运行为 null，此时直接返回原状态。</param>
    /// <param name="subSessionId">子会话 id，仅用于事件可读性。</param>
    /// <param name="round">已完成的轮次（1 基）。</param>
    /// <param name="toolCalls">累计工具调用数。</param>
    /// <param name="state">已投递游标。</param>
    /// <returns>推进后的游标；未判定为应投递时原样返回。</returns>
    private async Task<SubAgentProgressReportState> TryPlanSubAgentProgressReportAsync(
        string? subAgentRunId,
        string subSessionId,
        int round,
        int toolCalls,
        SubAgentProgressReportState state)
    {
        if (subAgentRunId is null)
            return state;

        var snapshot = new SubAgentProgressSnapshot(round, toolCalls, SubAgentProgressKind.Progress);
        var plan = SubAgentProgressReportPolicy.Plan(state, snapshot);

        await TryAppendSubAgentEventAsync(subAgentRunId, "subagent.progress.planned", new
        {
            sub_agent_id = subSessionId,
            round,
            tool_calls = toolCalls,
            decision = plan.Decision.ToString(),
            reason = plan.Reason,
            deliver = plan.ShouldDeliver,
        });

        return plan.ShouldDeliver
            ? SubAgentProgressReportPolicy.Record(state, snapshot)
            : state;
    }
}
