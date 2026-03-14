using System.Text;

namespace PuddingCode.Skills.BuiltIn;

/// <summary>
/// Self-inspection skills — allow the Agent to discover its own capabilities,
/// check resource usage, and lazily load skill documentation.
/// Usable by all roles including the desktop spirit.
/// </summary>
public sealed class IntrospectionSkills(ISkillRegistry registry)
{
    /// <summary>Returns a summary of available skills for the current session.</summary>
    [PuddingSkill("List all available skills with brief descriptions. Use this to discover what you can do.",
        Group = "Introspection")]
    public Task<string> SearchSkills(
        [SkillParam("Optional keyword to filter skills. Leave empty to list all.")] string? keyword,
        CancellationToken ct)
    {
        var results = string.IsNullOrWhiteSpace(keyword)
            ? registry.GetAllSkills()
            : registry.SearchSkills(keyword);

        if (results.Count == 0)
            return Task.FromResult("No skills found matching the query.");

        var sb = new StringBuilder();
        sb.AppendLine($"Found {results.Count} skill(s):");
        sb.AppendLine();

        var groups = results.GroupBy(s => s.Group).OrderBy(g => g.Key);
        foreach (var group in groups)
        {
            sb.AppendLine($"## {group.Key}");
            foreach (var skill in group)
            {
                var roles = skill.AllowedRoles.Length == 0
                    ? "all"
                    : string.Join(", ", skill.AllowedRoles);
                sb.AppendLine($"  - `{skill.Name}` — {skill.Description} [roles: {roles}]");
            }
            sb.AppendLine();
        }

        return Task.FromResult(sb.ToString());
    }

    /// <summary>Returns detailed parameter documentation for a specific skill.</summary>
    [PuddingSkill("Get detailed usage and parameters for a specific skill.",
        Group = "Introspection")]
    public Task<string> FetchSkillDocs(
        [SkillParam("The skill function name, e.g. 'execute_command'")] string skillName,
        CancellationToken ct)
    {
        var entry = registry.FindSkill(skillName);
        if (entry is null)
            return Task.FromResult($"Skill '{skillName}' not found. Use 'search_skills' to list available skills.");

        var sb = new StringBuilder();
        sb.AppendLine($"## {entry.Name}");
        sb.AppendLine($"**Description:** {entry.Description}");
        sb.AppendLine($"**Group:** {entry.Group}");

        var roles = entry.AllowedRoles.Length == 0
            ? "all roles"
            : string.Join(", ", entry.AllowedRoles);
        sb.AppendLine($"**Allowed roles:** {roles}");
        sb.AppendLine();
        sb.AppendLine("### Parameters");

        if (entry.Parameters.Properties.Count == 0)
        {
            sb.AppendLine("  (none)");
        }
        else
        {
            foreach (var p in entry.Parameters.Properties)
            {
                var req = entry.Parameters.Required.Contains(p.Name) ? " **(required)**" : "";
                sb.AppendLine($"  - `{p.Name}` ({p.Type}): {p.Description}{req}");
            }
        }

        return Task.FromResult(sb.ToString());
    }

    /// <summary>Returns the Agent's current status information.</summary>
    [PuddingSkill("Check your current status: loaded skills count, available groups.",
        Group = "Introspection")]
    public Task<string> CheckStatus(CancellationToken ct)
    {
        var all = registry.GetAllSkills();
        var groups = all.GroupBy(s => s.Group).ToDictionary(g => g.Key, g => g.Count());

        var sb = new StringBuilder();
        sb.AppendLine("## Agent Status");
        sb.AppendLine($"- **Total skills loaded:** {all.Count}");
        sb.AppendLine($"- **Skill groups:** {string.Join(", ", groups.Select(kv => $"{kv.Key}({kv.Value})"))}");

        return Task.FromResult(sb.ToString());
    }
}
