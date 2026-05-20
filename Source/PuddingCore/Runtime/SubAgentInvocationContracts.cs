using PuddingCode.Observability;

namespace PuddingCode.Runtime;

/// <summary>子代理调用请求。</summary>
public sealed record SubAgentInvocationRequest
{
    public required string ParentSessionId { get; init; }
    public required string WorkspaceId { get; init; }
    public required string ParentAgentInstanceId { get; init; }
    public required string TemplateId { get; init; }
    public required string Task { get; init; }
    public bool IsAsync { get; init; }
    public RuntimeTraceContext? Trace { get; init; }
}

/// <summary>子代理调用结果。</summary>
public sealed record SubAgentInvocationResult
{
    public required string SubSessionId { get; init; }
    public string? RunId { get; init; }
    public required string Status { get; init; }
    public string? Reply { get; init; }
    public string? Error { get; init; }
}

/// <summary>子代理调用服务，隔离父执行循环与子代理生命周期。</summary>
public interface ISubAgentInvocationService
{
    Task<SubAgentInvocationResult> InvokeAsync(SubAgentInvocationRequest request, CancellationToken ct = default);
}
