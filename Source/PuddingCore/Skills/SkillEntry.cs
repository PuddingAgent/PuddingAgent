using System.Reflection;
using PuddingCode.Models;

namespace PuddingCode.Skills;

/// <summary>
/// Parsed metadata for a single registered skill, including its JSON Schema
/// and the reflection info needed to invoke it.
/// </summary>
public sealed class SkillEntry
{
    /// <summary>Function name exposed to LLM (snake_case by convention).</summary>
    public required string Name { get; init; }

    /// <summary>Human-readable description for LLM.</summary>
    public required string Description { get; init; }

    /// <summary>Skill group (e.g. Environment, Social, Orchestration).</summary>
    public required string Group { get; init; }

    /// <summary>Roles allowed to invoke this skill. Empty = all roles.</summary>
    public required AgentRole[] AllowedRoles { get; init; }

    /// <summary>JSON Schema parameters for LLM function calling.</summary>
    public required ToolParameterSchema Parameters { get; init; }

    /// <summary>The reflected method to invoke.</summary>
    public required MethodInfo Method { get; init; }

    /// <summary>The skill class instance that owns the method.</summary>
    public required object Instance { get; init; }

    /// <summary>Checks whether <paramref name="role"/> is permitted to invoke this skill.</summary>
    public bool IsAllowed(AgentRole role)
        => AllowedRoles.Length == 0 || AllowedRoles.Contains(role);
}
