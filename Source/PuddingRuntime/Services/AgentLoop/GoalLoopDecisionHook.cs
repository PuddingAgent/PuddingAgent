using Microsoft.Extensions.Logging;
using PuddingRuntime.Services.Skills;

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

    /// <summary>每会话一个空转守卫：签名来自真实的工具名 + 参数。</summary>
    private readonly Dictionary<string, RepeatedActionGuard> _guards = new(StringComparer.Ordinal);

    /// <summary>本轮以来每会话的工具调用次数 —— 这是"上轮是否有推进"的唯一真实信号。</summary>
    private readonly Dictionary<string, int> _toolCalls = new(StringComparer.Ordinal);

    private readonly GoalOpenItemsReader? _openItemsReader;

    public GoalLoopDecisionHook(
        GoalRegistry registry,
        ILogger<GoalLoopDecisionHook>? logger = null,
        Func<bool>? isEnabled = null,
        GoalOpenItemsReader? openItemsReader = null)
    {
        _registry = registry;
        _logger = logger;
        _isEnabled = isEnabled ?? (() => DefaultEnabled);
        _openItemsReader = openItemsReader;
    }

    /// <summary>默认关闭：<c>PUDDING_GOAL_DECIDER=1</c> 才计算。关闭时零开销、零行为。</summary>
    public static bool DefaultEnabled { get; } =
        string.Equals(Environment.GetEnvironmentVariable("PUDDING_GOAL_DECIDER"), "1", StringComparison.Ordinal);

    /// <summary>供测试与诊断观察：本实例算过多少次决策。</summary>
    public int DecisionCount { get; private set; }

    /// <summary>最近一次决策结果。</summary>
    public GoalLoopOutcome? LastOutcome { get; private set; }

    /// <summary>
    /// 观测工具结果：把「工具名 + 参数」当作动作签名喂给空转守卫，并累计调用次数。
    /// <para>
    /// 为什么必须在这里采集：`OnLoopCompleteAsync` 只给 (context, finalMessage, stopReason)，
    /// 拿不到"这一轮到底动了没有"。没有这个信号，决策器只能靠猜 —— 而靠猜的续跑是最危险的。
    /// </para>
    /// </summary>
    public Task<SkillResult> OnToolResultTransformingAsync(
        AgentLoopContext context,
        int round,
        string toolName,
        string argsJson,
        SkillResult result,
        CancellationToken ct = default)
    {
        if (_isEnabled() && context is not null && !string.IsNullOrWhiteSpace(context.SessionId))
        {
            var key = context.SessionId;
            lock (_guards)
            {
                if (!_guards.TryGetValue(key, out var guard))
                    _guards[key] = guard = new RepeatedActionGuard();

                _toolCalls[key] = _toolCalls.GetValueOrDefault(key) + 1;
                guard.Record($"{toolName}|{argsJson}");
            }
        }

        // 语义上不改工具结果：本 hook 只观测。
        return Task.FromResult(result);
    }

    public async Task OnLoopCompleteAsync(
        AgentLoopContext context,
        string finalMessage,
        AgentLoopStopReason stopReason,
        CancellationToken ct = default)
    {
        if (!_isEnabled() || context is null)
            return;

        try
        {
            // 方案 3 临时入口：配置了 PUDDING_GOAL_OBJECTIVE 时，首次调用即建立目标，
            // 让决策链不再永远返回 no-goal。未配置时这一步是空操作。
            GoalBootstrap.FromEnvironment(_registry, context.SessionId);

            var goal = _registry.Get(context.SessionId);
            var ledger = _registry.GetLedger(context.SessionId);

            var input = new GoalLoopInput
            {
                Goal = goal,
                RoundsStarted = ledger?.RoundsStarted ?? 0,
                StopReason = stopReason,
                // 观测期不臆造进展信号：没有真实数据就按"没有推进"上报，
                // 这样决策会给出 Stop(no-progress)，不会假装在推进。
                OpenItems = await CountOpenItemsAsync(context, ct).ConfigureAwait(false),
                LastRoundMadeProgress = ToolCallsFor(context.SessionId) > 0,
                ConsecutiveIdleRounds = 0,
                UserMessagePending = false,
                LoopDetected = IsLooping(context.SessionId),
                ClaimsComplete = false,
            };

            var outcome = GoalLoopDecider.Decide(input);
            DecisionCount++;
            LastOutcome = outcome;
            ResetRoundSignals(context.SessionId);

            _logger?.LogInformation(
                "[GoalLoop] session={Session} action={Action} reason={Reason} detail={Detail}",
                context.SessionId, outcome.Action, outcome.ReasonCode, outcome.Message);
        }
        catch (Exception ex)
        {
            // 决策是观测，不能因为它把一次正常结束变成故障。
            _logger?.LogWarning(ex, "[GoalLoop] 决策失败，已跳过 session={Session}", context.SessionId);
        }

        return;
    }

    private int ToolCallsFor(string sessionId)
    {
        lock (_guards)
            return _toolCalls.GetValueOrDefault(sessionId);
    }

    private bool IsLooping(string sessionId)
    {
        lock (_guards)
            return _guards.TryGetValue(sessionId, out var guard) && guard.Streak >= 3;
    }

    /// <summary>一次 Loop 结束后重置本轮计数；守卫保留历史，否则跨轮的空转就看不出来了。</summary>
    private void ResetRoundSignals(string sessionId)
    {
        lock (_guards)
            _toolCalls.Remove(sessionId);
    }

    /// <summary>
    /// 读取当前会话未完成待办数；未注入读取器或读取失败都按 0 处理。
    /// <para>
    /// 读取失败**不能**让决策变成"没事可做" —— 所以这里记 warning 后返回 0，
    /// 让决策落到「清单空 → 继续收尾」而不是「无推进 → 停」，两种误判里后者更糟。
    /// </para>
    /// </summary>
    private async Task<int> CountOpenItemsAsync(AgentLoopContext context, CancellationToken ct)
    {
        if (_openItemsReader is null)
            return 0;

        try
        {
            return await _openItemsReader
                .CountOpenAsync(context.AgentInstanceId, context.SessionId, ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "[GoalLoop] 读取待办失败，按 0 处理 session={Session}", context.SessionId);
            return 0;
        }
    }
}
