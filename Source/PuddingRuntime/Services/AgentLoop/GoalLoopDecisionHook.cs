using Microsoft.Extensions.Logging;

namespace PuddingRuntime.Services.AgentLoop;

/// <summary>
/// 把 <see cref="GoalLoopDecider"/> 挂到真实循环结束点上的钩子。
/// <para>
/// 挂载点就是既有的 `OnLoopCompleteAsync`（`AgentExecutionService` 已经在那里分发所有 Hook），
/// 因此**不需要改动执行服务的任何一行**。
/// </para>
/// <para>
/// 第一期（本类当前行为）只做**观测**：算出决策、记日志，**不改变控制流**。
/// 理由：改变控制流等于让 agent 自己决定要不要继续跑，这是最容易失控的一步；
/// 先让决策在真实负载上跑一段，确认它说的和实际该做的对得上，再谈生效。
/// </para>
/// </summary>
public sealed class GoalLoopDecisionHook : IAgentLoopHook
{
    private readonly GoalRegistry _registry;
    private readonly ILogger<GoalLoopDecisionHook>? _logger;
    private readonly Func<bool> _isEnabled;

    public GoalLoopDecisionHook(
        GoalRegistry registry,
        ILogger<GoalLoopDecisionHook>? logger = null,
        Func<bool>? isEnabled = null)
    {
        _registry = registry;
        _logger = logger;
        _isEnabled = isEnabled ?? (() => DefaultEnabled);
    }

    /// <summary>默认关闭：<c>PUDDING_GOAL_DECIDER=1</c> 才计算。关闭时零开销、零行为。</summary>
    public static bool DefaultEnabled { get; } =
        string.Equals(Environment.GetEnvironmentVariable("PUDDING_GOAL_DECIDER"), "1", StringComparison.Ordinal);

    /// <summary>供测试与诊断观察：本实例算过多少次决策。</summary>
    public int DecisionCount { get; private set; }

    /// <summary>最近一次决策结果。</summary>
    public GoalLoopOutcome? LastOutcome { get; private set; }

    public Task OnLoopCompleteAsync(
        AgentLoopContext context,
        string finalMessage,
        AgentLoopStopReason stopReason,
        CancellationToken ct = default)
    {
        if (!_isEnabled() || context is null)
            return Task.CompletedTask;

        try
        {
            var goal = _registry.Get(context.SessionId);
            var ledger = _registry.GetLedger(context.SessionId);

            var input = new GoalLoopInput
            {
                Goal = goal,
                RoundsStarted = ledger?.RoundsStarted ?? 0,
                StopReason = stopReason,
                // 观测期不臆造进展信号：没有真实数据就按"没有推进"上报，
                // 这样决策会给出 Stop(no-progress)，不会假装在推进。
                OpenItems = 0,
                LastRoundMadeProgress = false,
                ConsecutiveIdleRounds = 0,
                UserMessagePending = false,
                LoopDetected = false,
                ClaimsComplete = false,
            };

            var outcome = GoalLoopDecider.Decide(input);
            DecisionCount++;
            LastOutcome = outcome;

            _logger?.LogInformation(
                "[GoalLoop] session={Session} action={Action} reason={Reason} detail={Detail}",
                context.SessionId, outcome.Action, outcome.ReasonCode, outcome.Message);
        }
        catch (Exception ex)
        {
            // 决策是观测，不能因为它把一次正常结束变成故障。
            _logger?.LogWarning(ex, "[GoalLoop] 决策失败，已跳过 session={Session}", context.SessionId);
        }

        return Task.CompletedTask;
    }
}
