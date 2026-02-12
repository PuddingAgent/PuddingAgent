namespace PuddingAssistant.Skills;

/// <summary>
/// Manages skill discovery, role-filtered schema generation, and execution.
/// This is the "bridge layer" between LLM function calling and physical skill implementations.
/// </summary>
public interface ISkillRegistry
{
    /// <summary>Registers all [PuddingSkill]-annotated methods from the given skill class instance.</summary>
    void Register(object skillInstance);

    /// <summary>Returns all skill entries visible to the given role.</summary>
    IReadOnlyList<SkillEntry> GetSkills(AgentRole role);

    /// <summary>Returns all registered skill entries regardless of role.</summary>
    IReadOnlyList<SkillEntry> GetAllSkills();

    /// <summary>Finds a skill by function name.</summary>
    SkillEntry? FindSkill(string name);

    /// <summary>Searches skills by keyword in name or description.</summary>
    IReadOnlyList<SkillEntry> SearchSkills(string keyword);

    /// <summary>
    /// Executes a skill by name with the given JSON arguments.
    /// Performs role-based permission check before invocation.
    /// </summary>
    Task<SkillResult> ExecuteAsync(string skillName, string argumentsJson, AgentRole callerRole, CancellationToken ct = default);
}

/// <summary>Result of a skill execution.</summary>
public sealed record SkillResult(bool Success, string Output)
{
    public static SkillResult Ok(string output) => new(true, output);
    public static SkillResult Error(string message) => new(false, message);
    public static SkillResult PermissionDenied(string skillName, AgentRole role)
        => new(false, $"Permission denied: role '{role}' cannot invoke '{skillName}'.");
}
