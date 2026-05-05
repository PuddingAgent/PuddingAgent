namespace PuddingWebApiTests;

/// <summary>
/// Session API 测试 DTO。
/// </summary>
public sealed class SessionDto
{
    public string SessionId { get; set; } = string.Empty;
    public string WorkspaceId { get; set; } = string.Empty;
    public string AgentTemplateId { get; set; } = string.Empty;
    public string? Title { get; set; }
    public int Status { get; set; }
}
