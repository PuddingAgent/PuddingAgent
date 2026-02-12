namespace PuddingController.Data.Entities;

/// <summary>
/// Persisted controller workspace definition with JSON encoded child configuration.
/// </summary>
public sealed class WorkspaceDefinitionEntity
{
    public string WorkspaceId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public bool IsEnabled { get; set; }
    public bool IsFrozen { get; set; }
    public string ChannelBindingsJson { get; set; } = "[]";
    public string AgentTemplateIdsJson { get; set; } = "[]";
    public string AuditAgentTemplateIdsJson { get; set; } = "[]";
    public string? PermissionPolicyJson { get; set; }
    public string? ExtrasJson { get; set; }
}
