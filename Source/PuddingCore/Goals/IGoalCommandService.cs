namespace PuddingCode.Goals;

/// <summary>
/// ADR-074 §4: Goal 命令应用服务。slash 文本入口（SystemCommandHandler）与
/// 结构化 Control Plane API 共用同一实现、同一幂等语义，产生相同事件。
/// </summary>
public interface IGoalCommandService
{
    Task<GoalCommandResult> ExecuteAsync(GoalCommandRequest request, CancellationToken ct = default);
}

/// <summary>Goal 只读查询。任意入口看到的状态都来自同一服务端投影。</summary>
public interface IGoalQueryService
{
    /// <summary>会话当前非终态 Goal；无则返回 null。</summary>
    Task<GoalSnapshot?> GetActiveAsync(
        string workspaceId,
        string conversationId,
        CancellationToken ct = default);

    Task<GoalSnapshot?> GetAsync(string goalRunId, CancellationToken ct = default);

    /// <summary>会话最近一个 Goal（含终态），供 Banner 在无活动 Goal 时展示历史回执。</summary>
    Task<GoalSnapshot?> GetLatestAsync(
        string workspaceId,
        string conversationId,
        CancellationToken ct = default);

    Task<IReadOnlyList<GoalIterationSnapshot>> GetIterationsAsync(
        string goalRunId,
        CancellationToken ct = default);
}
