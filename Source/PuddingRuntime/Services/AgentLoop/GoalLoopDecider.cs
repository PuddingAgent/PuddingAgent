namespace PuddingRuntime.Services.AgentLoop;

/// <summary>Loop 在"这一轮结束之后"应该做什么。</summary>
public enum GoalLoopActionKind
{
    /// <summary>继续下一轮（目标未完成、还有余量、上轮有推进）。</summary>
    Continue = 0,
    /// <summary>目标达成：**且**证据齐全，可以收尾。</summary>
    Complete = 1,
    /// <summary>让位：用户来消息了，先处理人。</summary>
    Yield = 2,
    /// <summary>停下（不含阻塞语义：取消、无进展、activation 未开启等）。</summary>
    Stop = 3,
    /// <summary>阻塞：需要人或外部条件才能继续，且要留下稳定原因码。</summary>
    Block = 4,
}

/// <summary>一次决策所需的全部输入（由调用方采集，本层不读任何外部状态）。</summary>
public sealed record GoalLoopInput
{
    /// <summary>当前目标状态；无目标时为 null。</summary>
    public GoalState? Goal { get; init; }

    public int RoundsStarted { get; init; }
    public int OpenItems { get; init; }
    public AgentLoopStopReason StopReason { get; init; } = AgentLoopStopReason.Done;

    public bool LastRoundMadeProgress { get; init; }
    public int ConsecutiveIdleRounds { get; init; }
    public bool UserMessagePending { get; init; }

    /// <summary>本轮是否检测到同一动作连续重复（由 <see cref="RepeatedActionGuard"/> 计算）。</summary>
    public bool LoopDetected { get; init; }

    /// <summary>模型是否在本轮宣称"已完成"。</summary>
    public bool ClaimsComplete { get; init; }

    /// <summary>宣称完成时随附的证据；为空表示"只是说了"。**不默认相信它。**</summary>
    public IReadOnlyList<GoalEvidence>? CompletionEvidence { get; init; }
}

/// <summary>决策结果：动作 + 稳定原因码 + 给人看的一句话。</summary>
public sealed record GoalLoopOutcome(GoalLoopActionKind Action, string ReasonCode, string Message)
{
    public bool IsTerminal => Action is GoalLoopActionKind.Complete or GoalLoopActionKind.Stop or GoalLoopActionKind.Block;
}

/// <summary>
/// goal 的**统一决策入口**：把四个判定（生命周期 / 轮次账本 / 续跑策略 / 空转检测）编排成一个动作。
/// <para>
/// 为什么要收口：四个组件各自正确，但"先问谁后问谁"散落在调用点会出事 ——
/// 例如先问"要不要继续"再问"用户来消息了吗"，就会在人说话时还自顾自续跑。
/// 顺序本身就是规则，所以必须固定在**一个可测的地方**。
/// </para>
/// </summary>
public static class GoalLoopDecider
{
    public const string ReasonUserPending = "user-message-pending";
    public const string ReasonAlreadyComplete = "goal-complete";
    public const string ReasonNotActive = "goal-not-active";
    public const string ReasonDisarmed = "goal-disarmed";
    public const string ReasonStopped = "stop-reason";
    public const string ReasonLoopDetected = "loop-detected";
    public const string ReasonRoundLimit = "round-limit";
    public const string ReasonEvidenceMissing = "completion-evidence-missing";
    public const string ReasonProgress = "progressing";
    public const string ReasonNoProgress = "no-progress";
    public const string ReasonNothingToDo = "no-open-items";

    public static GoalLoopOutcome Decide(GoalLoopInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        // ① 人在说话 → 永远让位。这条必须排在最前面：任何"自动推进"都不该压过用户。
        if (input.UserMessagePending)
            return new(GoalLoopActionKind.Yield, ReasonUserPending, "用户有新消息，先处理用户的请求。");

        var goal = input.Goal;
        if (goal is null)
            return new(GoalLoopActionKind.Stop, ReasonNotActive, "当前没有目标。");

        if (goal.Phase == GoalPhase.Complete)
            return new(GoalLoopActionKind.Stop, ReasonAlreadyComplete, "目标已完成，不再续跑。");

        // ② 模型宣称完成 —— 只有带可核验证据才认。
        if (input.ClaimsComplete)
        {
            var evidence = input.CompletionEvidence;
            if (evidence is null || evidence.Count == 0 || evidence.Any(e => !e.IsVerifiable))
                return new(GoalLoopActionKind.Block, ReasonEvidenceMissing,
                    "模型宣称已完成但未提供可核验证据；不能标完成，也不能继续当作在做。");

            return new(GoalLoopActionKind.Complete, ReasonAlreadyComplete, "目标完成，且证据可核验。");
        }

        if (goal.Phase != GoalPhase.Active)
            return new(GoalLoopActionKind.Stop,
                goal.Phase == GoalPhase.Blocked ? goal.BlockCode ?? ReasonNotActive : ReasonNotActive,
                $"目标处于 {goal.Phase} 阶段，不自动续跑。");

        // ③ activation 未开启（例如进程刚重启）→ 停，等人类显式 resume。
        if (goal.Activation != GoalActivation.Armed)
            return new(GoalLoopActionKind.Stop, ReasonDisarmed, "续跑未开启（重启或暂停后需要显式恢复）。");

        switch (input.StopReason)
        {
            case AgentLoopStopReason.Cancelled:
            case AgentLoopStopReason.Waiting:
            case AgentLoopStopReason.Failed:
                return new(GoalLoopActionKind.Stop, ReasonStopped, $"结束原因 {input.StopReason} 不自动续跑。");

            case AgentLoopStopReason.BudgetExhausted:
            case AgentLoopStopReason.MaxElapsedReached:
                return new(GoalLoopActionKind.Block, ReasonStopped, $"结束原因 {input.StopReason}：预算用尽。");
        }

        // ④ 空转：同一动作连续重复，继续跑只是烧 token。
        if (input.LoopDetected)
            return new(GoalLoopActionKind.Block, ReasonLoopDetected, "检测到同一动作连续重复，判定为空转，已停止。");

        // ⑤ 轮次额度。
        if (input.RoundsStarted >= goal.MaxRounds)
            return new(GoalLoopActionKind.Block, ReasonRoundLimit,
                $"已达到 {goal.MaxRounds} 轮上限。");

        // ⑥ 没有待办但也没声称完成 → 说明它还该去收尾，而不是停。
        if (input.OpenItems <= 0)
            return new(GoalLoopActionKind.Continue, ReasonNothingToDo,
                "清单已空但尚未验证完成；请收集证据后标记完成，或补充新的待办。");

        // ⑦ 续跑三条件：有待办 + 有推进 + 没连续空转。
        if (!input.LastRoundMadeProgress || input.ConsecutiveIdleRounds >= 2)
            return new(GoalLoopActionKind.Stop, ReasonNoProgress, "上一轮没有实质推进，停止以免空转。");

        return new(GoalLoopActionKind.Continue, ReasonProgress, "目标未完成且上轮有推进，继续下一轮。");
    }
}
