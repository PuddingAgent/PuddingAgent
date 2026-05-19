namespace PuddingCode.Agents;

/// <summary>
/// Agent 工作空间权限守卫 — 基于 glob 规则的路径和工具权限检查。
/// 由 AgentWorkspaceGuard 实现，接入 FileTool/ShellTool 执行前拦截。
/// 关联 ADR：Docs/07架构/21子代理工作空间与运行归档ADR.md
/// </summary>
public interface IAgentWorkspaceGuard
{
    /// <summary>检查 agent 是否可以读取指定路径。</summary>
    WorkspaceGuardDecision CanRead(string agentInstanceId, string workspaceRoot, string path);

    /// <summary>检查 agent 是否可以写入指定路径。</summary>
    WorkspaceGuardDecision CanWrite(string agentInstanceId, string workspaceRoot, string path);

    /// <summary>检查 agent 是否可以执行指定工具。</summary>
    WorkspaceGuardDecision CanExecuteTool(string agentInstanceId, string toolId);
}

/// <summary>
/// 工作空间权限检查结果。
/// </summary>
public sealed record WorkspaceGuardDecision
{
    public bool Allowed { get; init; }
    public string? Reason { get; init; }
    public string? MatchedRule { get; init; }

    public static WorkspaceGuardDecision Allow() => new() { Allowed = true };
    public static WorkspaceGuardDecision Deny(string reason, string? matchedRule = null) => new()
    {
        Allowed = false,
        Reason = reason,
        MatchedRule = matchedRule,
    };
}
