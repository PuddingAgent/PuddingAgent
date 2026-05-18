namespace PuddingCode.Observability;

public sealed record RuntimeTraceContext
{
    public required string TraceId { get; init; }
    public required string CorrelationId { get; init; }
    public string? SessionId { get; init; }
    public string? WorkspaceId { get; init; }
    public string? ExecutionId { get; init; }
    public string? ParentExecutionId { get; init; }
    public string? SubAgentId { get; init; }
    public string? EventId { get; init; }
    public string? ConnectorId { get; init; }
    public string? UserId { get; init; }

    public static RuntimeTraceContext CreateNew(
        string? sessionId = null,
        string? workspaceId = null,
        string? executionId = null,
        string? eventId = null,
        string? connectorId = null,
        string? userId = null,
        string? correlationId = null)
    {
        var traceId = Guid.NewGuid().ToString("N");
        return new RuntimeTraceContext
        {
            TraceId = traceId,
            CorrelationId = string.IsNullOrWhiteSpace(correlationId) ? traceId : correlationId,
            SessionId = sessionId,
            WorkspaceId = workspaceId,
            ExecutionId = executionId,
            EventId = eventId,
            ConnectorId = connectorId,
            UserId = userId,
        };
    }

    public RuntimeTraceContext WithSession(string? sessionId, string? workspaceId = null) =>
        this with
        {
            SessionId = sessionId ?? SessionId,
            WorkspaceId = workspaceId ?? WorkspaceId,
        };

    public RuntimeTraceContext WithEvent(string? eventId) =>
        this with { EventId = eventId ?? EventId };

    public RuntimeTraceContext CreateChildExecution(
        string? sessionId,
        string executionId,
        string? subAgentId = null) =>
        this with
        {
            SessionId = sessionId ?? SessionId,
            ExecutionId = executionId,
            ParentExecutionId = ExecutionId,
            SubAgentId = subAgentId ?? SubAgentId,
        };
}
