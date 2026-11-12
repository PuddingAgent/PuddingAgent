using System.Collections.Immutable;
using PuddingTools.Contracts;

namespace PuddingToolsTests;

public sealed class FakeToolSource : IToolCatalogSource
{
    public ImmutableArray<ToolDescriptor> Tools { get; set; } = [];
    public ImmutableDictionary<string, ToolSchema> Schemas { get; set; } =
        ImmutableDictionary<string, ToolSchema>.Empty;
    public ImmutableDictionary<string, ToolState> States { get; set; } =
        ImmutableDictionary<string, ToolState>.Empty;
    public Exception? Failure { get; set; }
    public int Searches { get; private set; }

    public ValueTask<ToolSearchPage> SearchAsync(ToolQuery query, CancellationToken ct = default)
    {
        Searches++;
        if (Failure is not null) throw Failure;
        return ValueTask.FromResult(new ToolSearchPage(Tools, null, true));
    }

    public ValueTask<ToolSchema?> GetSchemaAsync(string toolId, CancellationToken ct = default)
    {
        if (Failure is not null) throw Failure;
        return ValueTask.FromResult(Schemas.GetValueOrDefault(toolId));
    }

    public ValueTask<ToolState?> GetStateAsync(string toolId, CancellationToken ct = default)
    {
        if (Failure is not null) throw Failure;
        return ValueTask.FromResult(States.GetValueOrDefault(toolId));
    }
}

internal static class ToolFixtures
{
    public static ToolDescriptor Tool(string id, bool visible = true, string category = "General",
        ToolSideEffect effect = ToolSideEffect.None, long revision = 1) => new(id, $"Tool {id}", category,
        $"Description for {id}", effect, visible, revision);

    public static FakeToolSource Source() => new()
    {
        Tools =
        [
            Tool("zeta-write", category: "FileSystem", effect: ToolSideEffect.Write),
            Tool("alpha-read", category: "FileSystem", effect: ToolSideEffect.Read),
            Tool("hidden-tool", visible: false),
            Tool("beta-query", category: "Query"),
        ],
        Schemas = new Dictionary<string, ToolSchema>
        {
            ["alpha-read"] = new("alpha-read", """{"type":"object"}""", """{"type":"object"}""", "Read help", 3),
            ["hidden-tool"] = new("hidden-tool", """{"type":"object"}""", """{"type":"object"}""", "Hidden help", 3),
        }.ToImmutableDictionary(),
        States = new Dictionary<string, ToolState>
        {
            ["alpha-read"] = new("alpha-read", "unknown", "upstream-not-configured", 4),
            ["hidden-tool"] = new("hidden-tool", "available", null, 4),
        }.ToImmutableDictionary(),
    };
}
