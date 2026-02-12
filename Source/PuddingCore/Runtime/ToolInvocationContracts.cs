using PuddingCode.Observability;
using PuddingCode.Platform;

namespace PuddingCode.Runtime;

/// <summary>工具调用请求。</summary>
public sealed record ToolInvocationRequest
{
    public required string WorkspaceId { get; init; }
    public required string SessionId { get; init; }
    public required string AgentInstanceId { get; init; }
    public string? AgentTemplateId { get; init; }
    public required string ToolCallId { get; init; }
    public required string ToolName { get; init; }
    public required string ArgumentsJson { get; init; }
    public CapabilityPolicy? CapabilityPolicy { get; init; }
    public RuntimeTraceContext? Trace { get; init; }
}

/// <summary>工具调用结果。</summary>
public sealed record ToolInvocationResult
{
    public required bool Success { get; init; }
    public required string ToolCallId { get; init; }
    public required string ToolName { get; init; }
    public string? Output { get; init; }
    public string? Error { get; init; }
    public long DurationMs { get; init; }
    public string ArgsHash { get; init; } = "";
    public int OutputLength { get; init; }
}

/// <summary>工具调用服务，统一权限、审计、耗时、错误处理。</summary>
public interface IToolInvocationService
{
    Task<ToolInvocationResult> InvokeAsync(ToolInvocationRequest request, CancellationToken ct = default);
}
