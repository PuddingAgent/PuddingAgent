using PuddingCode.Platform;

namespace PuddingRuntime.Services.Sandbox;

/// <summary>Agent 与 Docker 容器的绑定状态。</summary>
public enum AgentContainerStatus
{
    Starting,
    Running,
    Stopped,
    Removed,
    Error
}

/// <summary>
/// 记录一个 Agent 实例与其对应 Docker 容器的绑定关系。
/// </summary>
public sealed class AgentContainerBinding
{
    public required string AgentInstanceId { get; set; }
    public required string WorkspaceId { get; set; }
    public required string ContainerId { get; set; }
    public required string ContainerName { get; set; }
    public required string Image { get; set; }
    public AgentContainerStatus Status { get; set; } = AgentContainerStatus.Starting;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? TerminatedAt { get; set; }
    public string? LastError { get; set; }
    /// <summary>Agent 本次任务关联的 Skill 包列表（已下载到宿主机 /pudding-skills/ 目录下）。</summary>
    public IReadOnlyList<SkillPackageInfo> SkillPackages { get; set; } = [];
}
