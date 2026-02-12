namespace PuddingController.Data.Entities;

/// <summary>
/// Persisted controller audit event.
/// </summary>
public sealed class AuditEventEntity
{
    public string EventId { get; set; } = string.Empty;
    public string EventType { get; set; } = string.Empty;
    public string? SessionId { get; set; }
    public string? MessageId { get; set; }
    public string? WorkspaceId { get; set; }
    public string? AgentTemplateId { get; set; }
    public string? ApprovalId { get; set; }
    public string? Detail { get; set; }
    public DateTimeOffset Timestamp { get; set; }
}
