namespace PuddingController.Data.Entities;

/// <summary>
/// Persisted controller route decision for a message.
/// </summary>
public sealed class RouteDecisionEntity
{
    public string RouteDecisionId { get; set; } = string.Empty;
    public string MessageId { get; set; } = string.Empty;
    public string ChannelId { get; set; } = string.Empty;
    public string? WorkspaceId { get; set; }
    public string? AgentTemplateId { get; set; }
    public string? SessionId { get; set; }
    public bool IsSuccess { get; set; }
    public string? FailureReason { get; set; }
    public DateTimeOffset Timestamp { get; set; }
}
