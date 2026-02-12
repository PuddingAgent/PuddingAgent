namespace PuddingCode.Platform;

/// <summary>宿主原生能力描述符——描述嵌入式宿主暴露给平台的单项原生能力。</summary>
public sealed record NativeCapabilityDescriptor
{
    /// <summary>能力唯一标识，建议形如 "demo.query_status"。</summary>
    public required string CapabilityId { get; init; }
    /// <summary>能力名称，供 UI 展示。</summary>
    public required string Name { get; init; }
    /// <summary>能力说明。</summary>
    public string? Description { get; init; }
    /// <summary>能力分类。</summary>
    public NativeCapabilityCategory Category { get; init; } = NativeCapabilityCategory.Custom;
    /// <summary>调用前是否必须通过审批。</summary>
    public bool RequiresApproval { get; init; }
}

/// <summary>原生能力分类。</summary>
public enum NativeCapabilityCategory
{
    /// <summary>查询宿主软件状态（只读）。</summary>
    QueryState,
    /// <summary>驱动/执行测试。</summary>
    RunTest,
    /// <summary>读取测试结果或日志。</summary>
    ReadResult,
    /// <summary>调用宿主命令（写入操作）。</summary>
    ExecuteCommand,
    /// <summary>自定义。</summary>
    Custom
}

/// <summary>原生能力调用请求——Controller 向 Runtime 节点发送的调用请求。</summary>
public sealed record NativeCapabilityInvokeRequest
{
    /// <summary>目标 Runtime 节点 ID。</summary>
    public required string NodeId { get; init; }
    /// <summary>要调用的能力 ID。</summary>
    public required string CapabilityId { get; init; }
    /// <summary>关联 Session ID（用于审计）。</summary>
    public required string SessionId { get; init; }
    /// <summary>关联 Workspace ID（用于权限与审计）。</summary>
    public required string WorkspaceId { get; init; }
    /// <summary>请求方 AgentTemplate ID（可选，用于权限校验）。</summary>
    public string? AgentTemplateId { get; init; }
    /// <summary>调用参数 key-value map（可为空）。</summary>
    public Dictionary<string, string>? Parameters { get; init; }
}

/// <summary>原生能力调用结果。</summary>
public sealed record NativeCapabilityInvokeResult
{
    public bool IsSuccess { get; init; }
    /// <summary>能力执行产出（文本形式）。</summary>
    public string? Output { get; init; }
    public string? ErrorMessage { get; init; }
    public DateTimeOffset InvokedAt { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>嵌入节点冻结/解冻请求（Controller → RuntimeRegistry）。</summary>
public sealed record EmbeddedNodeFreezeRequest
{
    public required string NodeId { get; init; }
    public required string Reason { get; init; }
    public string? OperatorId { get; init; }
}
