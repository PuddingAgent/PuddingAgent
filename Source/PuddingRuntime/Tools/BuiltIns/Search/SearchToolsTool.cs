using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using PuddingCode.Models;
using PuddingCode.Tools;

namespace PuddingRuntime.Services.Tools;

/// <summary>
/// Searches the permission-filtered runtime catalog. The returned ids are consumed by the Agent
/// loop and exposed as ordinary top-level function definitions on the following LLM round.
/// </summary>
[Tool(
    id: "search_tools",
    name: "search_tools",
    description: "Search the available Pudding tool catalog before using a capability that is not currently exposed. Returns matching tool ids and short descriptions; those tool definitions become available on the next model round. Use concise domain/action keywords such as files, git, web search, database, memory, or terminal.",
    category: ToolCategory.Query,
    permission: ToolPermissionLevel.Low,
    safety: ToolSafetyFlags.ReadOnly | ToolSafetyFlags.ConcurrencySafe,
    SortOrder = 0)]
public sealed class SearchToolsTool(IServiceProvider services) : PuddingToolBase<SearchToolsArgs>
{
    private static readonly Regex QueryTermRegex = new(
        @"[\p{L}\p{N}_-]+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    protected override Task<ToolExecutionResult> ExecuteCoreAsync(
        SearchToolsArgs args,
        ToolExecutionContext context,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var query = args.Query?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(query))
            return Task.FromResult(ToolExecutionResult.Fail("Query is required."));

        // Resolve lazily: SearchToolsTool itself is part of the registry, so constructor injection
        // of IPuddingToolRegistry would create a singleton dependency cycle.
        var registry = services.GetRequiredService<IPuddingToolRegistry>();
        var descriptors = registry.ListAvailable(context.CapabilityPolicy, context.WorkspaceId);
        var maxResults = Math.Clamp(args.MaxResults ?? 8, 1, 20);
        var terms = QueryTermRegex.Matches(query)
            .Select(match => match.Value)
            .Where(term => term.Length > 1)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var matches = descriptors
            .Where(descriptor => !descriptor.ToolId.Equals(
                ToolExposurePlanner.SearchToolId,
                StringComparison.OrdinalIgnoreCase))
            .Select(descriptor => new
            {
                Descriptor = descriptor,
                Score = Score(descriptor, query, terms),
            })
            .Where(candidate => candidate.Score > 0)
            .OrderByDescending(candidate => candidate.Score)
            .ThenBy(candidate => candidate.Descriptor.SortOrder)
            .ThenBy(candidate => candidate.Descriptor.ToolId, StringComparer.OrdinalIgnoreCase)
            .Take(maxResults)
            .Select(candidate => candidate.Descriptor)
            .ToList();

        var loadedToolIds = matches.Select(descriptor => descriptor.ToolId).ToArray();
        var output = JsonSerializer.Serialize(new
        {
            query,
            loaded_tool_ids = loadedToolIds,
            matches = matches.Select(descriptor => new
            {
                tool_id = descriptor.ToolId,
                name = descriptor.Name,
                description = descriptor.Description,
                category = descriptor.Category.ToString(),
                source = descriptor.SourceKind,
            }),
            message = loadedToolIds.Length == 0
                ? "No matching tools were found. Try broader English domain/action keywords."
                : "The matching tool definitions will be exposed on the next model round.",
        });

        return Task.FromResult(ToolExecutionResult.Ok(output));
    }

    private static int Score(ToolDescriptor descriptor, string query, IReadOnlyList<string> terms)
    {
        var score = 0;
        if (descriptor.ToolId.Equals(query, StringComparison.OrdinalIgnoreCase))
            score += 1_000;
        if (descriptor.ToolId.Contains(query, StringComparison.OrdinalIgnoreCase))
            score += 400;
        if (descriptor.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
            score += 300;
        if (descriptor.Description.Contains(query, StringComparison.OrdinalIgnoreCase))
            score += 200;
        if (descriptor.Category.ToString().Contains(query, StringComparison.OrdinalIgnoreCase))
            score += 150;

        foreach (var term in terms)
        {
            if (descriptor.ToolId.Contains(term, StringComparison.OrdinalIgnoreCase))
                score += 80;
            if (descriptor.Name.Contains(term, StringComparison.OrdinalIgnoreCase))
                score += 60;
            if (descriptor.Description.Contains(term, StringComparison.OrdinalIgnoreCase))
                score += 30;
            if (descriptor.Category.ToString().Contains(term, StringComparison.OrdinalIgnoreCase))
                score += 20;
        }

        return score;
    }
}

public sealed record SearchToolsArgs
{
    [ToolParam("Keywords describing the required capability or action. Prefer concise English domain/action terms when possible.")]
    public required string Query { get; init; }

    [ToolParam("Maximum number of candidate tools to load. Range 1-20; default 8.")]
    public int? MaxResults { get; init; }
}
