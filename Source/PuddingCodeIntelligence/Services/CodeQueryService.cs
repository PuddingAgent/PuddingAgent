using PuddingCodeIntelligence.Contracts;

namespace PuddingCodeIntelligence.Services;

/// <summary>
/// Read-only facade over the code graph store for agent-facing symbol and
/// relationship queries.
/// </summary>
public sealed class CodeQueryService : ICodeQueryService
{
    private readonly ICodeIndexStore _store;

    public CodeQueryService(ICodeIndexStore store)
    {
        _store = store;
    }

    public async Task<CodeIndexResult> GetProjectIndexStatusAsync(
        string workspaceId,
        string projectId,
        CancellationToken cancellationToken = default)
    {
        var project = await _store.GetProjectAsync(workspaceId, projectId, cancellationToken)
            .ConfigureAwait(false);
        if (project is null)
        {
            return new CodeIndexResult(
                false,
                CodeIndexStatus.Unknown,
                "Project is not registered.",
                WorkspaceId: workspaceId,
                ProjectId: projectId);
        }

        return new CodeIndexResult(
            project.Status != CodeProjectStatus.Failed && project.Status != CodeProjectStatus.Removed,
            ToIndexStatus(project.Status),
            WorkspaceId: workspaceId,
            ProjectId: projectId,
            CompletedAtUtc: project.UpdatedAtUtc);
    }

    public async Task<IReadOnlyList<CodeSymbolDetail>> SearchSymbolsAsync(
        CodeSymbolSearchRequest request,
        CancellationToken cancellationToken = default)
    {
        var symbols = await _store.SearchSymbolsAsync(request, cancellationToken).ConfigureAwait(false);
        if (symbols.Count == 0)
            return [];

        var fileCache = new Dictionary<(string WorkspaceId, string ProjectId), IReadOnlyDictionary<string, CodeFileRecord>>();
        var results = new List<CodeSymbolDetail>(symbols.Count);

        foreach (var symbol in symbols)
        {
            var files = await GetFilesAsync(symbol.WorkspaceId, symbol.ProjectId, fileCache, cancellationToken)
                .ConfigureAwait(false);
            files.TryGetValue(symbol.FilePath, out var file);
            results.Add(new CodeSymbolDetail(symbol, file, symbol.Signature ?? symbol.Name));
        }

        return results;
    }

    public async Task<IReadOnlyList<CodeSymbolRecord>> ExploreAsync(
        string workspaceId,
        string projectId,
        string symbolId,
        CancellationToken cancellationToken = default)
    {
        var symbolIds = new OrderedSymbolSet();
        symbolIds.Add(symbolId);

        foreach (var relation in await _store.ListRelationsAsync(workspaceId, projectId, symbolId, cancellationToken: cancellationToken)
                     .ConfigureAwait(false))
        {
            symbolIds.Add(relation.TargetSymbolId);
        }

        foreach (var relation in await _store.ListIncomingRelationsAsync(workspaceId, projectId, symbolId, cancellationToken: cancellationToken)
                     .ConfigureAwait(false))
        {
            symbolIds.Add(relation.SourceSymbolId);
        }

        foreach (var reference in await _store.ListReferencesAsync(workspaceId, projectId, symbolId, cancellationToken)
                     .ConfigureAwait(false))
        {
            symbolIds.Add(reference.SourceSymbolId);
            symbolIds.Add(reference.TargetSymbolId);
        }

        return await ResolveSymbolsAsync(workspaceId, projectId, symbolIds, cancellationToken).ConfigureAwait(false);
    }

    public Task<IReadOnlyList<CodeRelationRecord>> GetCallersAsync(
        string workspaceId,
        string projectId,
        string symbolId,
        CancellationToken cancellationToken = default) =>
        _store.ListIncomingRelationsAsync(workspaceId, projectId, symbolId, CodeRelationKind.Calls, cancellationToken);

    public Task<IReadOnlyList<CodeRelationRecord>> GetCalleesAsync(
        string workspaceId,
        string projectId,
        string symbolId,
        CancellationToken cancellationToken = default) =>
        _store.ListRelationsAsync(workspaceId, projectId, symbolId, CodeRelationKind.Calls, cancellationToken);

    public async Task<IReadOnlyList<CodeSymbolRecord>> GetImpactAsync(
        string workspaceId,
        string projectId,
        string symbolId,
        int maxDepth = 3,
        CancellationToken cancellationToken = default)
    {
        if (maxDepth <= 0)
            return [];

        var resultIds = new OrderedSymbolSet();
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var frontier = new Queue<(string SymbolId, int Depth)>();
        frontier.Enqueue((symbolId, 0));
        visited.Add(symbolId);

        while (frontier.Count > 0)
        {
            var (current, depth) = frontier.Dequeue();
            if (depth >= Math.Min(maxDepth, 10))
                continue;

            foreach (var relation in await _store.ListIncomingRelationsAsync(workspaceId, projectId, current, cancellationToken: cancellationToken)
                         .ConfigureAwait(false))
            {
                AddImpactedSymbol(relation.SourceSymbolId, depth + 1);
            }

            foreach (var reference in await _store.ListReferencesAsync(workspaceId, projectId, current, cancellationToken)
                         .ConfigureAwait(false))
            {
                if (string.Equals(reference.TargetSymbolId, current, StringComparison.Ordinal))
                    AddImpactedSymbol(reference.SourceSymbolId, depth + 1);
            }
        }

        return await ResolveSymbolsAsync(workspaceId, projectId, resultIds, cancellationToken).ConfigureAwait(false);

        void AddImpactedSymbol(string impactedSymbolId, int depth)
        {
            if (!visited.Add(impactedSymbolId))
                return;

            resultIds.Add(impactedSymbolId);
            frontier.Enqueue((impactedSymbolId, depth));
        }
    }

    private async Task<IReadOnlyDictionary<string, CodeFileRecord>> GetFilesAsync(
        string workspaceId,
        string projectId,
        Dictionary<(string WorkspaceId, string ProjectId), IReadOnlyDictionary<string, CodeFileRecord>> cache,
        CancellationToken cancellationToken)
    {
        var key = (workspaceId, projectId);
        if (cache.TryGetValue(key, out var files))
            return files;

        var loaded = await _store.ListFilesAsync(workspaceId, projectId, cancellationToken).ConfigureAwait(false);
        files = loaded.ToDictionary(file => file.FilePath, StringComparer.Ordinal);
        cache[key] = files;
        return files;
    }

    private async Task<IReadOnlyList<CodeSymbolRecord>> ResolveSymbolsAsync(
        string workspaceId,
        string projectId,
        IEnumerable<string> symbolIds,
        CancellationToken cancellationToken)
    {
        var results = new List<CodeSymbolRecord>();
        foreach (var id in symbolIds)
        {
            var symbol = await _store.GetSymbolAsync(workspaceId, projectId, id, cancellationToken).ConfigureAwait(false);
            if (symbol is not null)
                results.Add(symbol);
        }

        return results;
    }

    private static CodeIndexStatus ToIndexStatus(CodeProjectStatus status) =>
        status switch
        {
            CodeProjectStatus.Registering => CodeIndexStatus.Pending,
            CodeProjectStatus.Active => CodeIndexStatus.Completed,
            CodeProjectStatus.Removing => CodeIndexStatus.Running,
            CodeProjectStatus.Removed => CodeIndexStatus.Completed,
            CodeProjectStatus.Failed => CodeIndexStatus.Failed,
            _ => CodeIndexStatus.Unknown,
        };

    private sealed class OrderedSymbolSet : IEnumerable<string>
    {
        private readonly List<string> _ordered = [];
        private readonly HashSet<string> _seen = new(StringComparer.Ordinal);

        public void Add(string symbolId)
        {
            if (_seen.Add(symbolId))
                _ordered.Add(symbolId);
        }

        public IEnumerator<string> GetEnumerator() => _ordered.GetEnumerator();

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
