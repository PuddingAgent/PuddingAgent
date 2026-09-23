using PuddingCodeIndex.Contracts;
using PuddingCodeIndex.Services.CodeIndex;

namespace PuddingCodeIndexTests.Services.CodeIndex;

/// <summary>Indexer double: records every call, can run custom logic and can block.</summary>
internal sealed class RecordingCodeIndexer : ICodeIndexer, ICodeIndexFileUpdater
{
    private readonly object _gate = new();
    private readonly List<CodeWorkspaceDescriptor> _calls = new();
    private readonly List<string> _indexedFiles = new();

    /// <summary>Runs at the start of every indexing call, before the result is produced.</summary>
    public Func<CodeWorkspaceDescriptor, CancellationToken, Task>? OnIndexAsync { get; set; }

    /// <summary>Completes when the first indexing call has been observed.</summary>
    public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public int CallCount
    {
        get { lock (_gate) return _calls.Count; }
    }

    public IReadOnlyList<string> CalledProjectPaths
    {
        get { lock (_gate) return _calls.Select(call => call.ProjectPath).ToArray(); }
    }

    /// <summary>Absolute paths passed to <see cref="IndexFileAsync"/>, in call order.</summary>
    public IReadOnlyList<string> IndexedFiles
    {
        get { lock (_gate) return _indexedFiles.ToArray(); }
    }

    /// <summary>Runs at the start of every per-file call, before the result is produced.</summary>
    public Func<CodeWorkspaceDescriptor, string, CancellationToken, Task>? OnIndexFileAsync { get; set; }

    /// <summary>Completes when the first per-file call has been observed.</summary>
    public TaskCompletionSource FileStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>When set, per-file calls report this failure (the driver must escalate to a scope run).</summary>
    public string? IndexFileFailureMessage { get; set; }

    public async Task<CodeIndexResult> IndexFileAsync(
        CodeWorkspaceDescriptor workspace,
        string filePath,
        CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            _indexedFiles.Add(filePath);
        }

        FileStarted.TrySetResult();

        if (OnIndexFileAsync is not null)
            await OnIndexFileAsync(workspace, filePath, cancellationToken).ConfigureAwait(false);

        if (IndexFileFailureMessage is { } failure)
        {
            return new CodeIndexResult(false, CodeIndexStatus.Failed, failure,
                WorkspaceId: workspace.WorkspaceId, ProjectId: workspace.ProjectId);
        }

        return new CodeIndexResult(true, CodeIndexStatus.Completed, "recording indexer (per file)",
            WorkspaceId: workspace.WorkspaceId, ProjectId: workspace.ProjectId);
    }

    public async Task<CodeIndexResult> IndexWorkspaceAsync(
        CodeWorkspaceDescriptor workspace,
        CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            _calls.Add(workspace);
        }

        Started.TrySetResult();

        if (OnIndexAsync is not null)
            await OnIndexAsync(workspace, cancellationToken).ConfigureAwait(false);

        return new CodeIndexResult(
            true,
            CodeIndexStatus.Completed,
            "recording indexer",
            WorkspaceId: workspace.WorkspaceId,
            ProjectId: workspace.ProjectId);
    }

    public Task<CodeIndexResult> RemoveWorkspaceIndexAsync(
        string workspaceId,
        string projectId,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(new CodeIndexResult(true, CodeIndexStatus.Completed));
}

/// <summary>
/// Indexer double that implements <b>only</b> the full-workspace port. It stands for a registered indexer
/// without the per-file capability: the driver must escalate such a batch to a scope-level run instead of
/// pretending the changed file was indexed.
/// </summary>
internal sealed class WorkspaceOnlyCodeIndexer : ICodeIndexer
{
    private readonly object _gate = new();
    private int _callCount;

    public int CallCount
    {
        get { lock (_gate) return _callCount; }
    }

    public Task<CodeIndexResult> IndexWorkspaceAsync(
        CodeWorkspaceDescriptor workspace,
        CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            _callCount++;
        }

        return Task.FromResult(new CodeIndexResult(true, CodeIndexStatus.Completed, "workspace-only indexer",
            WorkspaceId: workspace.WorkspaceId, ProjectId: workspace.ProjectId));
    }

    public Task<CodeIndexResult> RemoveWorkspaceIndexAsync(
        string workspaceId,
        string projectId,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(new CodeIndexResult(true, CodeIndexStatus.Completed));
}

/// <summary>Change-source double: records lifecycle calls and exposes the queue it was handed.</summary>
internal sealed class FakeCodeIndexChangeWatcher : ICodeIndexChangeWatcher
{
    public FakeCodeIndexChangeWatcher(
        string workspaceId,
        string scopeId,
        string rootPath,
        CodeIndexChangeQueue queue,
        CodeIndexScopeState state)
    {
        WorkspaceId = workspaceId;
        ScopeId = scopeId;
        RootPath = rootPath;
        Queue = queue;
        State = state;
    }

    public string WorkspaceId { get; }

    public string ScopeId { get; }

    public string RootPath { get; }

    public CodeIndexChangeQueue Queue { get; }

    public CodeIndexScopeState State { get; }

    public bool Started { get; private set; }

    public bool Stopped { get; private set; }

    public bool Disposed { get; private set; }

    public void Start() => Started = true;

    public void Stop() => Stopped = true;

    public void Dispose() => Disposed = true;
}

/// <summary>Watcher factory double. Never touches the file system.</summary>
internal sealed class FakeCodeIndexWatcherFactory : ICodeIndexWatcherFactory
{
    private readonly object _gate = new();
    private readonly Dictionary<(string WorkspaceId, string ScopeId), FakeCodeIndexChangeWatcher> _watchers = new();

    public IReadOnlyList<FakeCodeIndexChangeWatcher> Watchers
    {
        get { lock (_gate) return _watchers.Values.ToArray(); }
    }

    /// <summary>Watcher of the (single) scope attached by the test.</summary>
    public FakeCodeIndexChangeWatcher Watcher
    {
        get
        {
            lock (_gate)
            {
                Assert.AreEqual(1, _watchers.Count, "the test expects exactly one attached scope");
                return _watchers.Values.First();
            }
        }
    }

    public ICodeIndexChangeWatcher? Create(
        string workspaceId,
        string scopeId,
        string rootPath,
        CodeIndexChangeQueue queue,
        CodeIndexScopeState state)
    {
        var watcher = new FakeCodeIndexChangeWatcher(workspaceId, scopeId, rootPath, queue, state);
        lock (_gate)
        {
            _watchers[(workspaceId, scopeId)] = watcher;
        }

        return watcher;
    }
}

/// <summary>Shared identifiers and observation builders for the U3-B1 tests.</summary>
internal static class MaintenanceTestData
{
    public const string WorkspaceId = "workspace-u3b1";

    public const string ScopeId = "scope-u3b1";

    public static IndexChange Change(
        string rootPath,
        string relativePath,
        IndexChangeKind kind,
        long sequence,
        DateTimeOffset observedAtUtc,
        string? oldRelativePath = null) =>
        new(
            WorkspaceId,
            ScopeId,
            Combine(rootPath, relativePath),
            kind,
            oldRelativePath is null ? null : Combine(rootPath, oldRelativePath),
            IsDirectory: false,
            sequence,
            observedAtUtc);

    public static string Combine(string rootPath, string relativePath) =>
        Path.Combine(rootPath, relativePath.Replace('/', Path.DirectorySeparatorChar));
}
