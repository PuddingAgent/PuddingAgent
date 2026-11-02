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
                // 未完成待办来自真实拆解表（ITodoStore）；未注入读取器时为 0。
                // 注意：openItems 为 0 会让决策落到「清单空 → 继续收尾」，
                // 而不是「无推进 → 停」—— 前者安全，后者会误杀还有活的会话。
                OpenItems = await CountOpenItemsAsync(context, ct).ConfigureAwait(false),
                LastRoundMadeProgress = ToolCallsFor(context.SessionId) > 0,
                // 恒 0：Decider 的「清单空 → 继续收尾」(⑥) 排在「连续空转 → 停」(⑦) 之前，
                // 所以在 openItems 接入真实拆解表之前，本字段根本走不到。
                // 接上 todo 后必须改成真实累计，否则 ⑦ 的 Idle>=2 判据永远不成立。
                ConsecutiveIdleRounds = 0,
                // 恒 false：本 hook 拿不到「该会话是否有待处理的用户消息」（AgentLoopContext 没有该字段）。
                // 但这**不影响安全**，因为用户中途插话由 AgentExecutionService 的 steering 机制
                // 在本 hook/gate **之前**消费（TryInjectSteeringMessageAsync，有 steering 就直接
                // continue，根本走不到 goal 续跑分支）。
                // ⚠️ 顺序依赖：若将来把 goal 续跑挪到 steering 检查之前，用户插话就会被续跑压过。
                UserMessagePending = false,
                LoopDetected = IsLooping(context.SessionId),
                // 恒 false —— 这是**刻意**的保守，不是漏接（2026-11-02 复核）。
                // 目前没有显式的「标记完成」信号源；若按 stopReason==Done 之类去推，
                // 第一轮结束就会被判 Complete，目标模式从此永不续跑 —— 比现在更糟。
                // 接上显式完成信号（工具或协议字段）之后再改这里。
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
        finally
        {
            // 成功与失败都要清：否则异常路径留下的调用计数会放大下一轮的"有推进"判断。
            ResetRoundSignals(context.SessionId);
        }
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
