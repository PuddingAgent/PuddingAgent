using PuddingCode.Platform;

namespace PuddingRuntime.Services.Improvement.Rsi;

/// <summary>
/// 把 <see cref="RsiTurnSlice"/>（按 turn 切开的事件行）装配成带结局标注的 <see cref="RsiTrajectory"/>
/// 的<b>纯函数</b>（规格 S3 §2.3 / §2.4 冻结规则，实现不得另发明）。
/// <para>
/// 装配规则（冻结）：
/// <list type="bullet">
/// <item>输入 null / 空 ⇒ 空列表，不抛异常；</item>
/// <item>只有 <c>tool.call.completed</c> 与 <c>tool.call.failed</c> 生成 step，其余事件一律忽略；</item>
/// <item>结局字段全部复用 <see cref="RsiToolOutcomeDeriver.Derive"/> 解析（不重写判定逻辑）；
///       但 <c>tool.call.failed</c> 语义上就是失败 ⇒ Outcome 强制 <see cref="RsiToolOutcome.Failed"/>；</item>
/// <item>ToolName 空/空白 ⇒ 常量 <see cref="UnknownToolName"/>（required string 不得留 null）；</item>
/// <item>steps 按 Sequence 升序排序（Sequence 是唯一合法排序键，不得依赖输入顺序）；</item>
/// <item>HasOutcomeAnomaly = 任一步 Outcome != Completed；</item>
/// <item>⛔ 失败步不得被丢弃，⛔ 不得模仿 ADR-064「Any(Failed) ⇒ 整条作废」（反面教材）；</item>
/// <item>Steps 为空的 turn 不产出轨迹（零工具调用不携带信号）；</item>
/// <item>输出顺序与输入 slices 顺序一致（稳定）。</item>
/// </list>
/// </para>
/// </summary>
public static class RsiTrajectoryAssembler
{
    /// <summary>ToolName 缺失时的占位常量（规格冻结：不得留 null）。</summary>
    public const string UnknownToolName = "(unknown)";

    /// <summary>装配轨迹。任何输入都不抛异常；无有效工具步的 turn 被跳过。</summary>
    public static IReadOnlyList<RsiTrajectory> Assemble(IReadOnlyList<RsiTurnSlice>? slices)
    {
        if (slices is null || slices.Count == 0)
            return [];

        var trajectories = new List<RsiTrajectory>(slices.Count);
        foreach (var slice in slices)
        {
            if (slice is null)
                continue;   // 防御 required 契约的显式 null：不抛异常是硬约束

            var steps = BuildSteps(slice);
            if (steps.Count == 0)
                continue;   // 零工具调用的 turn 不携带信号（规格 §2.3 冻结）

            trajectories.Add(new RsiTrajectory
            {
                WorkspaceId = slice.WorkspaceId,
                AgentInstanceId = slice.AgentInstanceId,
                SessionId = slice.SessionId,
                TurnId = slice.TurnId,
                Steps = steps,
                HasOutcomeAnomaly = steps.Any(step => step.Outcome != RsiToolOutcome.Completed),
            });
        }

        return trajectories;
    }

    /// <summary>从一个 turn 的事件行生成按 Sequence 升序的 steps；只有 completed / failed 两类事件参与。</summary>
    private static List<RsiToolStep> BuildSteps(RsiTurnSlice slice)
    {
        var steps = new List<RsiToolStep>();
        if (slice.Events is null)
            return steps;

        foreach (var row in slice.Events)
        {
            if (row is null)
                continue;

            switch (row.Type)
            {
                case ConversationEventTypes.ToolCallCompleted:
                    AddStep(steps, slice.TurnId, row, RsiToolOutcomeDeriver.Derive(row.Payload), forceFailed: false);
                    break;

                case ConversationEventTypes.ToolCallFailed:
                    // failed 事件语义上就是失败：Derive 可能因 payload 无信号给出 Unknown，但结局强制 Failed。
                    AddStep(steps, slice.TurnId, row, RsiToolOutcomeDeriver.Derive(row.Payload), forceFailed: true);
                    break;
            }
        }

        if (steps.Count > 1)
            steps.Sort(static (left, right) => left.Sequence.CompareTo(right.Sequence));

        return steps;
    }

    private static void AddStep(List<RsiToolStep> steps, string turnId, RsiEventRow row, RsiToolOutcomeResult derived, bool forceFailed)
    {
        var toolName = derived.ToolName;
        steps.Add(new RsiToolStep
        {
            TurnId = turnId,
            ToolName = string.IsNullOrWhiteSpace(toolName) ? UnknownToolName : toolName,
            Sequence = row.Sequence,
            Outcome = forceFailed ? RsiToolOutcome.Failed : derived.Outcome,
            ExitCode = derived.ExitCode,
            Error = derived.Error,
            Output = derived.Output,
            OccurredAtUtc = row.OccurredAtUtc,
        });
    }
}
