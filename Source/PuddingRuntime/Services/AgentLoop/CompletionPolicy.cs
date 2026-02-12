namespace PuddingRuntime.Services.AgentLoop;

/// <summary>CompletionPolicy 对本轮状态的裁决结果。</summary>
public enum CompletionVerdict
{
    /// <summary>继续下一轮。</summary>
    Continue,
    /// <summary>任务完成，可以收口。</summary>
    Completed,
    /// <summary>进入等待态（等待外部事件、审批或恢复条件）。</summary>
    Waiting,
    /// <summary>任务失败。</summary>
    Failed,
    /// <summary>执行被取消或冻结。</summary>
    Cancelled,
}

/// <summary>
/// 完成策略——Runtime 对 Agent 发出的 DONE / WAIT / FAILED 信号进行二次裁决。
///
/// 原则：Agent 只能"申请"完成，Runtime 才能"批准"完成。
///
/// MVP 版本执行基础优先级检查：
///   取消/冻结 > FAILED > WAIT > DONE > CONTINUE
/// 后续应接入 sub_agent 追踪、审批链路和副作用检查。
/// </summary>
public sealed class CompletionPolicy
{
    /// <summary>
    /// 根据 LLM 响应、执行日志及当前控制状态，裁决本轮应采取的行动。
    /// </summary>
    public CompletionVerdict Evaluate(
        AgentLoopContext context,
        AgentLoopResponse response,
        IReadOnlyList<TurnRecord> turns,
        bool isCancelled,
        bool isFrozen)
    {
        // 1. 最高优先级：外部取消 / 冻结
        if (isCancelled || isFrozen)
            return CompletionVerdict.Cancelled;

        // 2. Agent 明确声明失败
        if (response.IsFailed)
            return CompletionVerdict.Failed;

        // 3. Agent 申请进入等待态
        if (response.IsWaiting)
            return CompletionVerdict.Waiting;

        // 4. Agent 申请完成——MVP 直接接受
        //    TODO: 此处后续应检查 pending sub_agents / approvals / side-effects
        if (response.IsDone)
            return CompletionVerdict.Completed;

        return CompletionVerdict.Continue;
    }
}
