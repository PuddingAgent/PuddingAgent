namespace PuddingCode.Platform;

/// <summary>Runtime 节点信息——Controller 感知的 Runtime 节点。</summary>
public sealed record RuntimeNodeInfo
{
    public required string NodeId { get; init; }
    public required string Endpoint { get; init; }
    public RuntimeNodeStatus Status { get; init; } = RuntimeNodeStatus.Online;
    public DateTimeOffset LastHeartbeat { get; init; } = DateTimeOffset.UtcNow;
    public int ActiveSessionCount { get; init; }
    /// <summary>是否为嵌入式桌面宿主节点。</summary>
    public bool EmbeddedMode { get; init; }
    /// <summary>宿主类型标识，例如 "DemoDesktopApp"。</summary>
    public string? HostType { get; init; }
    /// <summary>嵌入节点暴露的原生能力列表（注册时上报）。</summary>
    public IReadOnlyList<NativeCapabilityDescriptor> NativeCapabilities { get; init; } = [];
    /// <summary>嵌入节点是否被冻结（冻结时拒绝原生能力调用）。</summary>
    public bool IsFrozen { get; init; }
}

public enum RuntimeNodeStatus
{
    Online,
    Offline,
    Degraded
}

/// <summary>Agent 实例记录——Runtime 内运行的 Agent 实例。</summary>
public sealed record AgentInstanceRecord
{
    public required string AgentInstanceId { get; init; }
    public required string AgentTemplateId { get; init; }
    public required string SessionId { get; init; }
    public AgentInstanceStatus Status { get; init; } = AgentInstanceStatus.Running;
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset LastActiveAt { get; init; } = DateTimeOffset.UtcNow;
}

public enum AgentInstanceStatus
{
    Creating,
    Running,
    Idle,
    Suspended,
    Terminated,
    Failed
}

/// <summary>Agent 执行请求。</summary>
public sealed record AgentExecutionRequest
{
    public required string SessionId { get; init; }
    public required string AgentInstanceId { get; init; }
    public required string MessageText { get; init; }
    public PermissionSnapshot? PermissionSnapshot { get; init; }
}

/// <summary>Agent 执行结果。</summary>
public sealed record AgentExecutionResult
{
    public required string AgentInstanceId { get; init; }
    public required string SessionId { get; init; }
    public string? ReplyText { get; init; }
    public bool IsSuccess { get; init; }
    public string? ErrorMessage { get; init; }
    public int TokensUsed { get; init; }
}

// ── Runtime 节点注册协议 ──────────────────────────────────────────────────────

/// <summary>Runtime 向 Controller 发送的节点注册/心跳请求。</summary>
public sealed record RuntimeRegisterRequest
{
    /// <summary>Runtime 节点 ID（启动时生成的 GUID 或配置中的固定 ID）。</summary>
    public required string NodeId { get; init; }
    /// <summary>Runtime 对外可访问的 HTTP 端点，例如 "http://localhost:5100"。</summary>
    public required string Endpoint { get; init; }
    /// <summary>当前活跃 Session 数，用于负载感知路由。</summary>
    public int ActiveSessionCount { get; init; }
    /// <summary>是否为嵌入式桌面宿主节点。</summary>
    public bool EmbeddedMode { get; init; }
    /// <summary>宿主类型标识，例如 "DemoDesktopApp"。</summary>
    public string? HostType { get; init; }
    /// <summary>注册时上报的原生能力列表（嵌入模式专用）。</summary>
    public IReadOnlyList<NativeCapabilityDescriptor> NativeCapabilities { get; init; } = [];
}

/// <summary>Controller 返回给 Runtime 的注册确认。</summary>
public sealed record RuntimeRegisterResponse
{
    public bool Accepted { get; init; }
    public string? Message { get; init; }
    /// <summary>当节点处于嵌入模式时，Controller 下发当前冻结状态。</summary>
    public bool IsFrozen { get; init; }
}

