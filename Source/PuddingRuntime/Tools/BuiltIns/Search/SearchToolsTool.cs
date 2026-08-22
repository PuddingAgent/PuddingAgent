using System.Text.Json.Serialization;
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
    description: "在可用 Pudding 工具目录中搜索（search tools）后再使用当前未暴露的能力。返回匹配的工具 id 与简短描述；这些工具定义将在下一轮模型回合可用。使用简洁的领域/动作关键词，如 files、git、web search、database、memory 或 terminal。Search the available Pudding tool catalog before using a capability that is not currently exposed",
    category: ToolCategory.Query,
    permission: ToolPermissionLevel.Low,
    safety: ToolSafetyFlags.ReadOnly | ToolSafetyFlags.ConcurrencySafe,
    SortOrder = 0)]
public sealed class SearchToolsTool(IServiceProvider services) : PuddingToolBase<SearchToolsArgs, SearchToolsResult>
{
    private static readonly Regex QueryTermRegex = new(
        @"[\p{L}\p{N}_-]+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    protected override Task<SearchToolsResult> ExecuteCoreAsync(
        SearchToolsArgs args,
        ToolExecutionContext context,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var query = args.Query?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(query))
            throw new InvalidOperationException("Query is required.");

        // Resolve lazily: SearchToolsTool itself is part of the registry, so constructor injection
        // of IPuddingToolRegistry would create a singleton dependency cycle.
        var registry = services.GetRequiredService<IPuddingToolRegistry>();
        var descriptors = registry.ListAvailable(context.CapabilityPolicy, context.WorkspaceId);
        // 2026-08-22 能耗治理：默认 8/上限 20 会把每次搜索的全部命中永久装载，
        // 长会话棘轮到 50+ 工具、每轮 3.4 万 schema tokens。收紧为默认 3/上限 8；
        // 精确 tool_id 查询仍按 1000 分优先命中，不受影响。
        var maxResults = Math.Clamp(args.MaxResults ?? 3, 1, 8);
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
        var result = new SearchToolsResult
        {
            Query = query,
            LoadedToolIds = loadedToolIds,
            Matches = matches.Select(descriptor => new SearchToolsMatch
            {
                ToolId = descriptor.ToolId,
                Name = descriptor.Name,
                Description = descriptor.Description,
                Category = descriptor.Category.ToString(),
                Source = descriptor.SourceKind,
            }).ToArray(),
            Message = loadedToolIds.Length == 0
                ? "No matching tools were found. Try broader English domain/action keywords."
                : "The matching tool definitions will be exposed on the next model round.",
        };

        return Task.FromResult(result);
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

    [ToolParam("Maximum number of candidate tools to load. Range 1-8; default 3. Loaded tools stay exposed for the whole session — query narrowly so only what you need loads.")]
    public int? MaxResults { get; init; }
}

public sealed record SearchToolsResult
{
    [JsonPropertyName("query")]
    public required string Query { get; init; }

    [JsonPropertyName("loaded_tool_ids")]
    public required string[] LoadedToolIds { get; init; }

    [JsonPropertyName("matches")]
    public required SearchToolsMatch[] Matches { get; init; }

    [JsonPropertyName("message")]
    public required string Message { get; init; }
}

public sealed record SearchToolsMatch
{
    [JsonPropertyName("tool_id")]
    public required string ToolId { get; init; }

    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("description")]
    public required string Description { get; init; }

    [JsonPropertyName("category")]
    public required string Category { get; init; }

    [JsonPropertyName("source")]
    public required string Source { get; init; }
}
