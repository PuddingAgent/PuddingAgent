using System.Collections.Immutable;
using PuddingTools.Contracts;

namespace PuddingTools;

/// <summary>
/// PA-05B read-only tool facade: search/inspect/state only — there is no execution entry point. Invisible tools
/// never leak their existence (NotFound, not a filtered hint), ordering is deterministic by tool id, page sizes are
/// bounded, and source failures are typed instead of returning an empty page.
/// </summary>
public sealed class ToolFacade
{
    private readonly IToolCatalogSource _source;

    public ToolFacade(IToolCatalogSource source) => _source = source ?? throw new ArgumentNullException(nameof(source));

    public async ValueTask<ToolSearchPage> SearchAsync(ToolQuery query, CancellationToken ct = default)
    {
        if (query is null || query.MaxCount is < 1 or > 100 || query.Text is { Length: > 256 }
            || query.Category is { Length: > 128 } || query.Cursor is { Length: > 128 })
            throw Error(ToolErrorCodes.QueryInvalid, "A bounded tool query is required.");
        ToolSearchPage page;
        try
        {
            page = await _source.SearchAsync(query, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (ToolFacadeException) { throw; }
        catch (Exception) { throw Error(ToolErrorCodes.SourceUnavailable, "The tool catalog is unavailable."); }
        var tools = page.Tools
            .Where(t => t is not null && t.IsValid && t.Visible)
            .Where(t => string.IsNullOrWhiteSpace(query.Text)
                || t.ToolId.Contains(query.Text, StringComparison.OrdinalIgnoreCase)
                || t.Name.Contains(query.Text, StringComparison.OrdinalIgnoreCase))
            .Where(t => string.IsNullOrWhiteSpace(query.Category)
                || string.Equals(t.Category, query.Category, StringComparison.OrdinalIgnoreCase))
            .Where(t => query.SideEffect is null || t.SideEffect == query.SideEffect)
            .OrderBy(t => t.ToolId, StringComparer.Ordinal)
            .Take(query.MaxCount)
            .ToImmutableArray();
        return new(tools, page.Cursor, page.Complete && tools.Length == page.Tools.Length);
    }

    public async ValueTask<ToolSchema> InspectAsync(string toolId, CancellationToken ct = default)
    {
        EnsureToolId(toolId);
        var descriptor = await FindDescriptorAsync(toolId, ct).ConfigureAwait(false);
        if (descriptor is null)
            throw Error(ToolErrorCodes.NotFound, "The tool is not visible to this caller.");
        var schema = await CallAsync(() => _source.GetSchemaAsync(toolId, ct)).ConfigureAwait(false);
        return schema ?? throw Error(ToolErrorCodes.NotFound, "The tool is not visible to this caller.");
    }

    public async ValueTask<ToolState> GetStateAsync(string toolId, CancellationToken ct = default)
    {
        EnsureToolId(toolId);
        var descriptor = await FindDescriptorAsync(toolId, ct).ConfigureAwait(false);
        if (descriptor is null)
            throw Error(ToolErrorCodes.NotFound, "The tool is not visible to this caller.");
        var state = await CallAsync(() => _source.GetStateAsync(toolId, ct)).ConfigureAwait(false);
        if (state is null) throw Error(ToolErrorCodes.NotFound, "The tool is not visible to this caller.");
        return state;
    }

    private async ValueTask<ToolDescriptor?> FindDescriptorAsync(string toolId, CancellationToken ct)
    {
        var page = await CallAsync(() => _source.SearchAsync(
            new ToolQuery(toolId, null, null, null, 100), ct)).ConfigureAwait(false);
        return page.Tools.FirstOrDefault(t => t is not null && t.IsValid && t.Visible
            && string.Equals(t.ToolId, toolId, StringComparison.Ordinal));
    }

    private async ValueTask<T> CallAsync<T>(Func<ValueTask<T>> call)
    {
        try
        {
            return await call().ConfigureAwait(false);
        }
        catch (ToolFacadeException) { throw; }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw Error(ToolErrorCodes.SourceUnavailable, "The tool catalog is unavailable.");
        }
    }

    private static void EnsureToolId(string? toolId)
    {
        if (string.IsNullOrWhiteSpace(toolId) || toolId.Length > 128 || toolId.Contains('/')
            || toolId.Contains('\\') || toolId.Contains(':'))
            throw Error(ToolErrorCodes.ToolInvalid, "A bounded tool id is required.");
    }

    private static ToolFacadeException Error(string code, string message) => new(code, message);
}
