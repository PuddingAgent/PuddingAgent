namespace PuddingPlatform.Data.Entities;

/// <summary>工作空间内配置的消息渠道管道（外部系统与 Agent 的对接通道）。</summary>
public class WorkspaceChannelEntity
{
    public int Id { get; set; }

    /// <summary>业务层唯一 ID（GUID）。</summary>
    public string ChannelId { get; set; } = Guid.NewGuid().ToString();

    /// <summary>所属工作空间的 PK。</summary>
    public int WorkspaceEntityId { get; set; }

    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }

    /// <summary>渠道类型：HTTP | RabbitMQ | WebSocket | CLI | Telegram | Email。</summary>
    public string ChannelType { get; set; } = "HTTP";

    /// <summary>该渠道默认路由到的 Agent ID（空则按工作空间默认策略）。</summary>
    public string? DefaultAgentId { get; set; }

    /// <summary>渠道连接配置 JSON（端点地址、凭据名称等，不存储明文密钥）。</summary>
    public string? ConfigJson { get; set; }

    public bool IsEnabled { get; set; } = true;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    // 导航属性
    public WorkspaceEntity Workspace { get; set; } = null!;
}
