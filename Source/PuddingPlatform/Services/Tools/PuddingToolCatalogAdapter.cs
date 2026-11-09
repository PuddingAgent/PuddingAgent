using System.Collections.Immutable;
using System.Text.Json;
using PuddingCode.Tools;
using PuddingTools.Contracts;
using CoreDescriptor = PuddingCode.Tools.ToolDescriptor;
using ContractsDescriptor = PuddingTools.Contracts.ToolDescriptor;

namespace PuddingPlatform.Services.Tools;

/// <summary>
/// PA-05B catalog read adapter over the existing tool authority. It is query-only: no execution entry is created here,
/// unavailable tools are not exposed (no existence leak), and unknown state stays explicit.
/// </summary>
public sealed class PuddingToolCatalogAdapter(IPuddingToolCatalogService catalog) : IToolCatalogSource
{
    public ValueTask<ToolSearchPage> SearchAsync(ToolQuery query, CancellationToken ct = default)
    {
        if (query is null || query.MaxCount is < 1 or > 100 || query.Cursor is { Length: > 128 })
            throw new ToolFacadeException(ToolErrorCodes.QueryInvalid, "A bounded tool query is required.");
        var all = VisibleTools()
            .Where(d => query.Text is null || d.Name.Contains(query.Text, StringComparison.OrdinalIgnoreCase)
                || d.Description.Contains(query.Text, StringComparison.OrdinalIgnoreCase))
            .Where(d => query.Category is null || string.Equals(d.Category.ToString(), query.Category, StringComparison.OrdinalIgnoreCase))
            .Where(d => query.SideEffect is null || SideEffect(d) == query.SideEffect)
            .OrderBy(d => d.ToolId, StringComparer.Ordinal)
            .Where(d => query.Cursor is null || string.CompareOrdinal(d.ToolId, query.Cursor) > 0)
            .ToList();
        var page = all.Take(query.MaxCount).Select(Map).ToImmutableArray();
        var complete = all.Count <= query.MaxCount;
        return ValueTask.FromResult(new ToolSearchPage(page, complete || page.IsEmpty ? null : page[^1].ToolId, complete));
    }

    public ValueTask<ToolSchema?> GetSchemaAsync(string toolId, CancellationToken ct = default)
    {
        var descriptor = Find(toolId);
        return ValueTask.FromResult(descriptor is null
            ? null
            : new ToolSchema(descriptor.ToolId, JsonSerializer.Serialize(descriptor.Parameters),
                OutputSchemaJson: "{}", Help: descriptor.Description, Revision: 1));
    }

    public ValueTask<ToolState?> GetStateAsync(string toolId, CancellationToken ct = default)
    {
        var descriptor = Find(toolId);
        return ValueTask.FromResult(descriptor is null
            ? null
            : new ToolState(descriptor.ToolId, descriptor.RuntimeStatus,
                string.Equals(descriptor.RuntimeStatus, "Available", StringComparison.OrdinalIgnoreCase)
                    ? null
                    : $"tool.{descriptor.RuntimeStatus.ToLowerInvariant()}",
                Revision: 1));
    }

    private CoreDescriptor? Find(string toolId)
    {
        if (string.IsNullOrWhiteSpace(toolId) || toolId.Length > 128) return null;
        return VisibleTools().FirstOrDefault(d => string.Equals(d.ToolId, toolId, StringComparison.Ordinal));
    }

    // "Hidden" tools must not leak existence anywhere; other non-available states stay explicit.
    private IReadOnlyList<CoreDescriptor> VisibleTools() => catalog.ListTools()
        .Where(d => !string.Equals(d.RuntimeStatus, "Hidden", StringComparison.OrdinalIgnoreCase))
        .ToList();

    private static ContractsDescriptor Map(CoreDescriptor d) => new(d.ToolId, d.Name, d.Category.ToString(), d.Description,
        SideEffect(d), true, Revision: 1);

    private static ToolSideEffect SideEffect(CoreDescriptor d)
    {
        if (d.Safety.HasFlag(ToolSafetyFlags.RequiresShell)) return ToolSideEffect.Process;
        if (d.Safety.HasFlag(ToolSafetyFlags.RequiresFileWrite)
            || d.Safety.HasFlag(ToolSafetyFlags.Destructive)) return ToolSideEffect.Write;
        if (d.Safety.HasFlag(ToolSafetyFlags.RequiresNetwork)) return ToolSideEffect.Network;
        if (d.Safety.HasFlag(ToolSafetyFlags.ReadOnly)) return ToolSideEffect.Read;
        return ToolSideEffect.None;
    }
}
