using PuddingAssistant.Abstractions;
using PuddingAssistant.Models;

namespace PuddingAssistant.Skills;

/// <summary>
/// Adapts a single <see cref="SkillEntry"/> to the <see cref="ITool"/> interface,
/// allowing skills to be consumed by the existing <see cref="IToolRegistry"/> and <see cref="IAgentOrchestrator"/>.
/// </summary>
public sealed class SkillTool(SkillEntry entry, ISkillRegistry registry, AgentRole callerRole) : ITool
{
    public string Name => entry.Name;
    public string Description => entry.Description;
    public ToolParameterSchema Parameters => entry.Parameters;

    public async Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default)
    {
        var result = await registry.ExecuteAsync(Name, argumentsJson, callerRole, ct);
        return result.Output;
    }
}

/// <summary>
/// Extension methods to bridge the skill system into the existing tool registry.
/// </summary>
public static class SkillRegistryExtensions
{
    /// <summary>
    /// Registers all skills visible to <paramref name="role"/> as <see cref="ITool"/>
    /// instances into the given <see cref="IToolRegistry"/>.
    /// </summary>
    public static void RegisterSkillsAsTools(
        this IToolRegistry toolRegistry, ISkillRegistry skillRegistry, AgentRole role)
    {
        foreach (var skill in skillRegistry.GetSkills(role))
        {
            toolRegistry.Register(new SkillTool(skill, skillRegistry, role));
        }
    }
}
