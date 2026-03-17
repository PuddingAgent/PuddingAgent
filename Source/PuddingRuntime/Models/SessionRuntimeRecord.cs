namespace PuddingRuntime.Models;

/// <summary>Runtime 侧会话热状态——跟踪 Agent 实例活跃状态与心跳。</summary>
public sealed class SessionRuntimeRecord
{
    public required string SessionId { get; init; }
    public required string AgentInstanceId { get; set; }
    public required string WorkspaceId { get; init; }
    public required string AgentTemplateId { get; init; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset LastActiveAt { get; set; } = DateTimeOffset.UtcNow;
    public int TurnCount { get; set; }
    public bool IsActive { get; set; } = true;
    public string? TerminationReason { get; set; }
}
