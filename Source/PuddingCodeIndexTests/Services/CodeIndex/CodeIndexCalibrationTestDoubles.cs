using Microsoft.Extensions.Logging;
using PuddingCodeIndex.Contracts;
using PuddingCodeIndex.Services.CodeIndex;

namespace PuddingCodeIndexTests.Services.CodeIndex;

/// <summary>
/// Logger that keeps every message, so a test can assert on what the component said instead of trusting that it
/// said anything (U3-C requires an error log when a sweep is refused).
/// </summary>
internal sealed class RecordingLogger<T> : ILogger<T>
{
    private readonly object _gate = new();
    private readonly List<string> _entries = new();

    /// <summary>Everything logged so far, as <c>"Level: message"</c>.</summary>
    public IReadOnlyList<string> Entries
    {
        get { lock (_gate) return _entries.ToArray(); }
    }

    /// <summary>True when at least one entry of <paramref name="level"/> contains <paramref name="fragment"/>.</summary>
    public bool Contains(LogLevel level, string fragment)
    {
        lock (_gate)
        {
            return _entries.Any(entry =>
                entry.StartsWith($"{level}: ", StringComparison.Ordinal)
                && entry.Contains(fragment, StringComparison.Ordinal));
        }
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        lock (_gate)
        {
            _entries.Add($"{logLevel}: {formatter(state, exception)}");
        }
    }
}

/// <summary>
/// Change-source factory that cannot create a watcher at all — the "attachment failed" case: the scope exists, but
/// nothing observes it, so fine-grained capture is untrustworthy for it.
/// </summary>
internal sealed class ThrowingWatcherFactory : ICodeIndexWatcherFactory
{
    public ICodeIndexChangeWatcher? Create(
        string workspaceId,
        string scopeId,
        string rootPath,
        CodeIndexChangeQueue queue,
        CodeIndexScopeState state) =>
        throw new IOException("change source could not be created (test double)");
}

/// <summary>
/// Pass-through <see cref="ICodeIndexStore"/> that records which members the code under test called, how many
/// files each removal carried, and can fire a callback on the first removal.
/// <para>
/// This is how "the sweep only removes index rows" and "each removal is bounded" become observable facts instead
/// of claims about the source: the calibration goes through the store, so what it asks the store to do is the
/// whole story.
/// </para>
/// </summary>
internal sealed class RecordingCodeIndexStore : ICodeIndexStore
{
    private readonly ICodeIndexStore _inner;
    private readonly object _gate = new();
    private readonly List<string> _calls = new();
    private readonly List<int> _removalBatchSizes = new();

    public RecordingCodeIndexStore(ICodeIndexStore inner) => _inner = inner;

    /// <summary>Names of the store members called so far, in call order.</summary>
    public IReadOnlyList<string> Calls
    {
        get { lock (_gate) return _calls.ToArray(); }
    }

    /// <summary>Number of file paths handed to <see cref="RemoveFilesAsync"/>, one entry per call.</summary>
    public IReadOnlyList<int> RemovalBatchSizes
    {
        get { lock (_gate) return _removalBatchSizes.ToArray(); }
    }

    /// <summary>Runs right after the first <see cref="RemoveFilesAsync"/> call.</summary>
    public Action? OnFirstRemoval { get; set; }

    private void Record(string member)
    {
        lock (_gate)
        {
            _calls.Add(member);
        }
    }

    public Task UpsertProjectAsync(CodeProjectRecord project, CancellationToken cancellationToken = default)
    {
        Record(nameof(UpsertProjectAsync));
        return _inner.UpsertProjectAsync(project, cancellationToken);
    }

    public Task RemoveProjectAsync(
        string workspaceId,
        string projectId,
        bool removeIndexedArtifacts = true,
        CancellationToken cancellationToken = default)
    {
        Record(nameof(RemoveProjectAsync));
        return _inner.RemoveProjectAsync(workspaceId, projectId, removeIndexedArtifacts, cancellationToken);
    }

    public Task<IReadOnlyList<CodeProjectRecord>> ListProjectsAsync(
        string workspaceId,
        CancellationToken cancellationToken = default)
    {
        Record(nameof(ListProjectsAsync));
        return _inner.ListProjectsAsync(workspaceId, cancellationToken);
    }

    public Task<CodeProjectRecord?> GetProjectAsync(
        string workspaceId,
        string projectId,
        CancellationToken cancellationToken = default)
    {
        Record(nameof(GetProjectAsync));
        return _inner.GetProjectAsync(workspaceId, projectId, cancellationToken);
    }

    public Task UpdateProjectStatusAsync(
        string workspaceId,
        string projectId,
        CodeProjectStatus status,
        string? statusMessage = null,
        CancellationToken cancellationToken = default)
    {
        Record(nameof(UpdateProjectStatusAsync));
        return _inner.UpdateProjectStatusAsync(workspaceId, projectId, status, statusMessage, cancellationToken);
    }

    public Task UpsertFilesAsync(
        string workspaceId,
        string projectId,
        IReadOnlyCollection<CodeFileRecord> files,
        CancellationToken cancellationToken = default)
    {
        Record(nameof(UpsertFilesAsync));
        return _inner.UpsertFilesAsync(workspaceId, projectId, files, cancellationToken);
    }

    public Task<IReadOnlyList<CodeFileRecord>> ListFilesAsync(
        string workspaceId,
        string projectId,
        CancellationToken cancellationToken = default)
    {
        Record(nameof(ListFilesAsync));
        return _inner.ListFilesAsync(workspaceId, projectId, cancellationToken);
    }

    public async Task<int> RemoveFilesAsync(
        string workspaceId,
        string projectId,
        IReadOnlyCollection<string> filePaths,
        CancellationToken cancellationToken = default)
    {
        var batchSize = filePaths.Count;
        var isFirstRemoval = false;

        lock (_gate)
        {
            _calls.Add(nameof(RemoveFilesAsync));
            _removalBatchSizes.Add(batchSize);
            isFirstRemoval = _removalBatchSizes.Count == 1;
        }

        var removed = await _inner
            .RemoveFilesAsync(workspaceId, projectId, filePaths, cancellationToken)
            .ConfigureAwait(false);

        if (isFirstRemoval)
            OnFirstRemoval?.Invoke();

        return removed;
    }

    public Task UpsertSymbolsAsync(
        string workspaceId,
        string projectId,
        IReadOnlyCollection<CodeSymbolRecord> symbols,
        CancellationToken cancellationToken = default)
    {
        Record(nameof(UpsertSymbolsAsync));
        return _inner.UpsertSymbolsAsync(workspaceId, projectId, symbols, cancellationToken);
    }

    public Task<IReadOnlyList<CodeSymbolRecord>> SearchSymbolsAsync(
        CodeSymbolSearchRequest request,
        CancellationToken cancellationToken = default)
    {
        Record(nameof(SearchSymbolsAsync));
        return _inner.SearchSymbolsAsync(request, cancellationToken);
    }

    public Task<CodeSymbolRecord?> GetSymbolAsync(
        string workspaceId,
        string projectId,
        string symbolId,
        CancellationToken cancellationToken = default)
    {
        Record(nameof(GetSymbolAsync));
        return _inner.GetSymbolAsync(workspaceId, projectId, symbolId, cancellationToken);
    }

    public Task<IReadOnlyList<CodeSymbolRecord>> GetSymbolsByFileAsync(
        string workspaceId,
        string projectId,
        string filePath,
        CancellationToken cancellationToken = default)
    {
        Record(nameof(GetSymbolsByFileAsync));
        return _inner.GetSymbolsByFileAsync(workspaceId, projectId, filePath, cancellationToken);
    }

    public Task ClearSymbolsForFileAsync(
        string workspaceId,
        string projectId,
        string filePath,
        CancellationToken cancellationToken = default)
    {
        Record(nameof(ClearSymbolsForFileAsync));
        return _inner.ClearSymbolsForFileAsync(workspaceId, projectId, filePath, cancellationToken);
    }

    public Task UpsertRelationsAsync(
        string workspaceId,
        string projectId,
        IReadOnlyCollection<CodeRelationRecord> relations,
        CancellationToken cancellationToken = default)
    {
        Record(nameof(UpsertRelationsAsync));
        return _inner.UpsertRelationsAsync(workspaceId, projectId, relations, cancellationToken);
    }

    public Task<IReadOnlyList<CodeRelationRecord>> ListRelationsAsync(
        string workspaceId,
        string projectId,
        string symbolId,
        CodeRelationKind? relationKind = null,
        CancellationToken cancellationToken = default)
    {
        Record(nameof(ListRelationsAsync));
        return _inner.ListRelationsAsync(workspaceId, projectId, symbolId, relationKind, cancellationToken);
    }

    public Task<IReadOnlyList<CodeRelationRecord>> ListIncomingRelationsAsync(
        string workspaceId,
        string projectId,
        string symbolId,
        CodeRelationKind? relationKind = null,
        CancellationToken cancellationToken = default)
    {
        Record(nameof(ListIncomingRelationsAsync));
        return _inner.ListIncomingRelationsAsync(workspaceId, projectId, symbolId, relationKind, cancellationToken);
    }

    public Task ClearRelationsAsync(
        string workspaceId,
        string projectId,
        string sourceSymbolId,
        CancellationToken cancellationToken = default)
    {
        Record(nameof(ClearRelationsAsync));
        return _inner.ClearRelationsAsync(workspaceId, projectId, sourceSymbolId, cancellationToken);
    }

    public Task UpsertReferencesAsync(
        string workspaceId,
        string projectId,
        IReadOnlyCollection<CodeReferenceRecord> references,
        CancellationToken cancellationToken = default)
    {
        Record(nameof(UpsertReferencesAsync));
        return _inner.UpsertReferencesAsync(workspaceId, projectId, references, cancellationToken);
    }

    public Task<IReadOnlyList<CodeReferenceRecord>> ListReferencesAsync(
        string workspaceId,
        string projectId,
        string symbolId,
        CancellationToken cancellationToken = default)
    {
        Record(nameof(ListReferencesAsync));
        return _inner.ListReferencesAsync(workspaceId, projectId, symbolId, cancellationToken);
    }

    public Task ClearReferencesAsync(
        string workspaceId,
        string projectId,
        string sourceSymbolId,
        CancellationToken cancellationToken = default)
    {
        Record(nameof(ClearReferencesAsync));
        return _inner.ClearReferencesAsync(workspaceId, projectId, sourceSymbolId, cancellationToken);
    }
}
