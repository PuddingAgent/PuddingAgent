namespace PuddingRuntime.Services.GoalMode;

/// <summary>
/// Goal 模式：连续自主任务循环（参考 pi follow-up 注入模式）。
/// 回合终态后调用；若开关开启且队列非空，把下一个目标注入为系统信封，
/// 复用消息系统的持久 inbox / 去重 / busy 防抖 / 重试机制。
/// </summary>
public interface IGoalModeService
{
    /// <summary>
    /// 尝试注入下一个目标。返回是否发生了注入。
    /// 任何失败都不得抛出（调用方为主投递路径，注入属于新事务）。
    /// </summary>
    Task<bool> TryInjectNextGoalAsync(string workspaceId, string agentId, CancellationToken ct);
}