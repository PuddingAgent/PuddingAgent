namespace PuddingWebApiTests;

/// <summary>
/// Session API 测试 DTO。
/// </summary>
public sealed class SessionDto
{
    public string SessionId { get; set; } = string.Empty;
    public string WorkspaceId { get; set; } = string.Empty;
    public string AgentTemplateId { get; set; } = string.Empty;
    public string? AgentInstanceId { get; set; }
    public string? SessionRole { get; set; }
    public string? ParentSessionId { get; set; }
    public string? RootSessionId { get; set; }
    public string? PrincipalKind { get; set; }
    public string? PrincipalId { get; set; }
    public string? Title { get; set; }
    public int Status { get; set; }
}
