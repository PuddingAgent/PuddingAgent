namespace PuddingRuntime.Services.AgentLoop;

/// <summary>
/// 目标创建入口（**仅开发验证用**，Codex 2026-11-01 21:55 裁定的方案 3）。
/// <para>
/// 定位说明：这不是产品入口。它存在的唯一目的是把"决策链"在真实会话上跑通，
/// 让 <see cref="GoalLoopDecisionHook"/> 不再永远返回 no-goal。
/// 产品入口要等进入方案 2（下挂长期目标），判据见
/// `Docs/12_features/2026-11-01-Super-agent架构与goal模式差距对照.md` §8。
/// </para>
/// <para>
/// 刻意做成**纯函数 + 注入读取器**：不直接读 Environment，便于单测；
/// 也不抛异常 —— 配置缺失只是"没有目标"，不该让启动失败。
/// </para>
/// </summary>
public static class GoalBootstrap
{
    /// <summary>未指定 maxRounds 时的默认轮次上限。</summary>
    public const int DefaultMaxRounds = 8;

    /// <summary>轮次上限的合理区间；越界会被夹取而不是报错。</summary>
    public const int MinMaxRounds = 1;

    public const int MaxMaxRounds = 64;

    /// <summary>从环境变量读取目标与轮次上限（产品路径不使用）。</summary>
    public static GoalState? FromEnvironment(GoalRegistry registry, string? sessionId) =>
        CreateIfConfigured(
            registry,
            sessionId,
            () => Environment.GetEnvironmentVariable("PUDDING_GOAL_OBJECTIVE"),
            () => Environment.GetEnvironmentVariable("PUDDING_GOAL_MAX_ROUNDS"));

    /// <summary>
    /// 若已配置目标则创建；未配置、会话无效、或该会话已有目标时返回现有/空。
    /// **幂等**：重复调用不会抛错。
    /// </summary>
    public static GoalState? CreateIfConfigured(
        GoalRegistry registry,
        string? sessionId,
        Func<string?> readObjective,
        Func<string?>? readMaxRounds = null)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(readObjective);

        if (string.IsNullOrWhiteSpace(sessionId))
            return null;

        var existing = registry.Get(sessionId);
        if (existing is not null)
            return existing;

        var objective = readObjective();
        if (string.IsNullOrWhiteSpace(objective))
            return null;

        return registry.Create(sessionId, objective.Trim(), ParseMaxRounds(readMaxRounds?.Invoke()));
    }

    /// <summary>解析轮次上限：空/非法回落到默认值，越界夹取。</summary>
    public static int ParseMaxRounds(string? raw)
    {
        if (!int.TryParse(raw?.Trim(), out var value))
            return DefaultMaxRounds;

        return Math.Clamp(value, MinMaxRounds, MaxMaxRounds);
    }
}
