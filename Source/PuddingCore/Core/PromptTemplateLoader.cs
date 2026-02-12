using System.Text;
using PuddingCode.Models;
using PuddingCode.Skills;

namespace PuddingCode.Core;

internal static class PromptTemplateLoader
{
    public static string BuildSystemPrompt(AgentRole role, WorkerScope? scope, ProjectContext? project)
    {
        var projectRoot = project?.RootPath;
        var templates = ResolveTemplates(projectRoot, role);
        if (templates.Count == 0)
            return BuildDefault(role, scope, project);

        var merged = string.Join("\n\n", templates);
        return ApplyVariables(merged, role, scope, project);
    }

    private static List<string> ResolveTemplates(string? projectRoot, AgentRole role)
    {
        if (string.IsNullOrWhiteSpace(projectRoot))
            return [];

        var dir = Path.Combine(projectRoot, ".pudding", "prompts");
        if (!Directory.Exists(dir))
            return [];

        var files = new[]
        {
            Path.Combine(dir, "system.md"),
            Path.Combine(dir, $"{role.ToString().ToLowerInvariant()}.md")
        };

        var templates = new List<string>();
        foreach (var file in files)
        {
            if (!File.Exists(file))
                continue;
            var text = File.ReadAllText(file).Trim();
            if (!string.IsNullOrWhiteSpace(text))
                templates.Add(text);
        }

        return templates;
    }

    private static string ApplyVariables(string template, AgentRole role, WorkerScope? scope, ProjectContext? project)
    {
        var scopeInfo = role == AgentRole.Worker && scope is not null
            ? BuildScopeInfo(scope)
            : "No scope restrictions.";

        return template
            .Replace("{{role}}", role.ToString(), StringComparison.OrdinalIgnoreCase)
            .Replace("{{project_name}}", project?.Name ?? "-", StringComparison.OrdinalIgnoreCase)
            .Replace("{{project_root}}", project?.RootPath ?? "-", StringComparison.OrdinalIgnoreCase)
            .Replace("{{worker_scope}}", scopeInfo, StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildDefault(AgentRole role, WorkerScope? scope, ProjectContext? project)
    {
        var basePrompt = role switch
        {
            AgentRole.Leader => """
                You are the Leader Agent of PuddingCode Swarm.
                Your responsibilities:
                - Design contracts (create interfaces and empty implementations with method signatures)
                - Define contracts with clear specifications (parameters, return values, exceptions, constraints)
                - Split tasks and assign them to Worker Agents
                - Monitor Worker progress and make merge decisions
                - Validate that Worker implementations match contract signatures

                You can work in parallel with Workers while monitoring their progress.
                """,

            AgentRole.Worker => """
                You are a Worker Agent of PuddingCode Swarm.
                You are a focused software engineer implementing assigned modules.

                SCOPE RESTRICTIONS:
                - You can ONLY modify files within your assigned scope.
                - You MUST NOT modify files outside your scope.
                - Follow the contract specifications in method comments.
                - Notify the Leader when you complete your tasks.
                """,

            AgentRole.Spirit => """
                You are PuddingCode, an AI programming assistant.
                Use the provided tools to help the user with coding tasks.
                Always use tools when the user asks to read files, write files, or run commands.
                After using a tool, summarize the result for the user.
                """,

            _ => """
                You are PuddingCode, an AI programming assistant.
                Use the provided tools to help the user with coding tasks.
                Always use tools when the user asks to read files, write files, or run commands.
                After using a tool, summarize the result for the user.
                """
        };

        if (role == AgentRole.Worker && scope is not null)
        {
            var scopeInfo = BuildScopeInfo(scope);
            basePrompt += $"""

                YOUR ASSIGNED SCOPE:
                {scopeInfo}

                REMEMBER: Any attempt to modify files outside this scope will be rejected.
                """;
        }

        if (project is not null)
        {
            basePrompt += $"""

                Current project: {project.Name}
                Project root: {project.RootPath}
                All relative file paths are resolved from the project root.
                When using the file tool, use paths relative to the project root.
                When using the shell tool, commands run in the project root by default.
                """;
        }

        return basePrompt;
    }

    private static string BuildScopeInfo(WorkerScope scope)
    {
        var sb = new StringBuilder();

        if (scope.AllowedPaths.Count > 0)
        {
            sb.AppendLine("Allowed file paths:");
            foreach (var path in scope.AllowedPaths)
                sb.AppendLine($"  - {path}");
        }

        if (scope.AllowedSymbols.Count > 0)
        {
            if (sb.Length > 0) sb.AppendLine();
            sb.AppendLine("Allowed symbols:");
            foreach (var symbol in scope.AllowedSymbols)
                sb.AppendLine($"  - {symbol}");
        }

        return sb.Length > 0 ? sb.ToString() : "No scope restrictions.";
    }
}
