namespace PuddingCode.SubAgents;

/// <summary>向运行中的子代理投递 steering 消息的结果分类。</summary>
public enum SubAgentSteerOutcome
{
    /// <summary>已入队；子代理会在下一次 LLM 调用前消费它。</summary>
    Queued,
    /// <summary>目标不在本进程运行（已终态、不存在，或运行发生在别的进程）。没有 loop 会消费它。</summary>
    NotRunning,
    /// <summary>运行 manifest 缺少定位所需字段，已按 fail closed 拒绝。</summary>
    TargetUnresolved,
    /// <summary>入参不合法（空 subSessionId / 空消息）。</summary>
    Invalid
}

/// <summary>steering 决策结果。Outcome=Queued 时 Target 非空。</summary>
public sealed record SubAgentSteerPlan(
    SubAgentSteerOutcome Outcome,
    SubAgentSteeringTarget? Target,
    string? Detail);

/// <summary>面向调用方（Agent 工具）的结果：只暴露结果分类与 steering id。</summary>
public sealed record SubAgentSteerResult(
    SubAgentSteerOutcome Outcome,
    string? SteeringId = null,
    string? Detail = null);

/// <summary>
/// 把「这条 steering 消息该不该投、投到哪个键」这件事抽成纯函数，便于逐分支断言，
/// 而不必为一次判定去搭整套 SubAgentManager 依赖。
/// </summary>
public static class SubAgentSteerPlanner
{
    /// <summary>
    /// 判定一次 steering 请求。
    /// <para>
    /// 只对**本进程正在运行**的子代理有意义：已终态的运行没有 loop 会消费消息，
    /// 入队只会让它躺到 TTL 过期，而调用方从回执上看不出区别。因此这里明确返回
    /// <see cref="SubAgentSteerOutcome.NotRunning"/> 而不是静默成功。
    /// </para>
    /// </summary>
    /// <param name="subSessionId">目标子代理会话 id。</param>
    /// <param name="message">要注入的正文。</param>
    /// <param name="isRunning">该运行当前是否登记在本进程的运行注册表中。</param>
    /// <param name="manifest">运行 manifest；目标不在运行时调用方可传 null。</param>
    public static SubAgentSteerPlan Plan(
        string? subSessionId,
        string? message,
        bool isRunning,
        SubAgentRunManifest? manifest)
    {
        if (string.IsNullOrWhiteSpace(subSessionId))
            return new SubAgentSteerPlan(
                SubAgentSteerOutcome.Invalid, null, "sub_agent_id is required");

        if (string.IsNullOrWhiteSpace(message))
            return new SubAgentSteerPlan(
                SubAgentSteerOutcome.Invalid, null, "message is required");

        if (!isRunning)
            return new SubAgentSteerPlan(
                SubAgentSteerOutcome.NotRunning, null,
                "sub-agent is not running in this process; the message would never be consumed");

        var target = SubAgentSteeringTarget.FromManifest(manifest);
        if (target is null)
            return new SubAgentSteerPlan(
                SubAgentSteerOutcome.TargetUnresolved, null,
                "run manifest lacks workspace / sub-session / parent turn");

        return new SubAgentSteerPlan(SubAgentSteerOutcome.Queued, target, null);
    }
}
