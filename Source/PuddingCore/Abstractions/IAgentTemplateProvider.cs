namespace PuddingCode.Abstractions;

/// <summary>
/// Agent 模板个性字段提供者。由 Platform 层实现，Runtime 层只依赖此接口。
/// </summary>
public interface IAgentTemplateProvider
{
    Task<AgentTemplatePersona?> GetPersonaAsync(string templateId, string? workspaceId, CancellationToken ct = default);
}

public sealed record AgentTemplatePersona
{
    public string? DisplayName { get; init; }
    public string? PersonaPrompt { get; init; }
    public string? ToolsDescription { get; init; }
    public string? BootstrapTemplate { get; init; }
    public string? AvatarEmoji { get; init; }
    public string? MemorySearchMode { get; init; }
    public string? MemoryLlmEndpoint { get; init; }
    public string? MemoryLlmApiKey { get; init; }
    public string? MemoryLlmModelId { get; init; }
}
