using System.Text.RegularExpressions;

namespace PuddingRuntime.Services.AgentLoop;

/// <summary>持久生命周期阶段（会落盘）。</summary>
public enum GoalPhase
{
    Active = 0,
    Paused = 1,
    Blocked = 2,
    Complete = 3,
}

/// <summary>
/// 进程本地的续跑开关（**永不落盘**）。
/// <para>
/// 与 <see cref="GoalPhase"/> 分离是刻意的：重启后 phase 仍是 Active，
/// 但 activation 回到 Disarmed —— 于是「恢复后不自动乱跑」是默认成立的，
/// 而不是靠某处 if 判断兜住。
/// </para>
/// </summary>
public enum GoalActivation
{
    Disarmed = 0,
    Armed = 1,
}

/// <summary>目标状态。revision 每次变更 +1，用作乐观并发的 CAS 身份。</summary>
public sealed record GoalState(
    string GoalId,
    long Revision,
    string Objective,
    int MaxRounds,
    GoalPhase Phase,
    GoalActivation Activation,
    string? BlockCode = null,
    string? BlockMessage = null,
    /// <summary>完成时冻结的证据。非 Complete 阶段为空。</summary>
    IReadOnlyList<GoalEvidence> Evidence = null!)
{
    public GoalRef Ref => new(GoalId, Revision);
}

/// <summary>CAS 身份。</summary>
public sealed record GoalRef(string GoalId, long Revision);

/// <summary>非法状态迁移。带上稳定错误码，便于上层区分而不是解析文本。</summary>
public sealed class GoalTransitionException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

/// <summary>
/// goal 生命周期状态机（纯函数）。
/// <para>
/// 语义对齐 DeepSeek Harness `GoalService`（实读源码）：
/// <list type="bullet">
/// <item>create → Active + **Armed**（模型建的目标默认就自动续跑）</item>
/// <item>pause → Paused + Disarmed</item>
/// <item>resume → Active + Armed</item>
/// <item>complete / block → 对应阶段 + Disarmed</item>
/// <item>**进程重启 → 保留 phase，强制 Disarmed**</item>
/// </list>
/// </para>
/// </summary>
public static class GoalLifecycle
{
    /// <summary>阻塞码必须是 lower-kebab-case（对齐上游校验）。</summary>
    private static readonly Regex BlockCodePattern = new("^[a-z][a-z0-9]*(?:-[a-z0-9]+)*$", RegexOptions.CultureInvariant);

    public const string InvalidTransition = "GOAL_INVALID_TRANSITION";
    public const string InvalidBlockReason = "GOAL_INVALID_BLOCK_REASON";
    public const string CompletionEvidenceRequired = "GOAL_COMPLETION_EVIDENCE_REQUIRED";

    public static GoalState Create(string goalId, string objective, int maxRounds)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(goalId);
        ArgumentException.ThrowIfNullOrWhiteSpace(objective);
        if (maxRounds <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxRounds), "maxRounds must be positive.");

        return new GoalState(goalId, Revision: 1, objective, maxRounds, GoalPhase.Active, GoalActivation.Armed);
    }

    public static GoalState Pause(GoalState state)
    {
        Require(state, "pause", GoalPhase.Active);
        return state with
        {
            Revision = state.Revision + 1,
            Phase = GoalPhase.Paused,
            Activation = GoalActivation.Disarmed,
        };
    }

    /// <summary>恢复：从 paused 或 blocked 回到 active 并**重新武装**。</summary>
    public static GoalState Resume(GoalState state)
    {
        if (state.Phase is not (GoalPhase.Paused or GoalPhase.Blocked))
            throw Transition(state, "resume", GoalPhase.Paused, GoalPhase.Blocked);

        return state with
        {
            Revision = state.Revision + 1,
            Phase = GoalPhase.Active,
            Activation = GoalActivation.Armed,
            BlockCode = null,
            BlockMessage = null,
        };
    }

    /// <summary>
    /// 完成：**必须带可核验证据**。
    /// <para>
    /// 为什么不提供无参重载：`OpenItems == 0` 只是"清单空了"，模型最容易在这里
    /// 宣布自己做完了。留下一个不带证据的入口，等于给假完成开绿灯。
    /// </para>
    /// </summary>
    public static GoalState Complete(GoalState state, IReadOnlyList<GoalEvidence> evidence)
    {
        Require(state, "complete", GoalPhase.Active);

        if (evidence is not { Count: > 0 })
            throw new GoalTransitionException(
                CompletionEvidenceRequired,
                "completing a goal requires at least one verifiable evidence item");

        var bad = evidence.FirstOrDefault(e => !e.IsVerifiable);
        if (bad is not null)
            throw new GoalTransitionException(
                CompletionEvidenceRequired,
                $"evidence of kind {bad.Kind} must carry a non-empty reference and summary");

        return state with
        {
            Revision = state.Revision + 1,
            Phase = GoalPhase.Complete,
            Activation = GoalActivation.Disarmed,
            Evidence = evidence.ToArray(),
        };
    }

    /// <summary>阻塞：必须给出 lower-kebab 的稳定码与非空说明。</summary>
    public static GoalState Block(GoalState state, string code, string message)
    {
        Require(state, "block", GoalPhase.Active);

        if (string.IsNullOrWhiteSpace(code) || !BlockCodePattern.IsMatch(code))
            throw new GoalTransitionException(InvalidBlockReason, "block reason code must be lower-kebab-case.");
        if (string.IsNullOrWhiteSpace(message))
            throw new GoalTransitionException(InvalidBlockReason, "block reason message must be non-empty.");

        return state with
        {
            Revision = state.Revision + 1,
            Phase = GoalPhase.Blocked,
            Activation = GoalActivation.Disarmed,
            BlockCode = code,
            BlockMessage = message.Trim(),
        };
    }

    /// <summary>
    /// 进程重启后的状态：**phase 原样保留，activation 强制 Disarmed**。
    /// 这就是「恢复后不自动跑」的全部实现 —— 不需要额外判断。
    /// </summary>
    public static GoalState AfterRestart(GoalState state) => state with { Activation = GoalActivation.Disarmed };

    /// <summary>是否可以自动续跑。两个条件缺一不可。</summary>
    public static bool CanAutoContinue(GoalState state) =>
        state.Phase == GoalPhase.Active && state.Activation == GoalActivation.Armed;

    private static void Require(GoalState state, string operation, params GoalPhase[] allowed)
    {
        if (!allowed.Contains(state.Phase))
            throw Transition(state, operation, allowed);
    }

    private static GoalTransitionException Transition(GoalState state, string operation, params GoalPhase[] allowed) =>
        new(InvalidTransition,
            $"cannot {operation} goal \"{state.GoalId}\" from phase \"{state.Phase}\"; expected {string.Join(" or ", allowed)}");
}
