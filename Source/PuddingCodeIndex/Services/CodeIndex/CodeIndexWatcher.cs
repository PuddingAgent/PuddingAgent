using Microsoft.Extensions.Logging;
using PuddingCodeIndex.Contracts;
using PuddingPathFiltering;

namespace PuddingCodeIndex.Services.CodeIndex;

/// <summary>
/// Watches one code index scope root and turns file-system notifications into cheap
/// <see cref="IndexChange"/> observations on a bounded queue.
/// <para>
/// Callback discipline (hard requirement): the event callbacks only filter, build an
/// <see cref="IndexChange"/> and call <see cref="CodeIndexChangeQueue.TryPublish"/>. They never read
/// file content, never hash, never touch a database, never <c>await</c> and never block. Every
/// callback body is wrapped so that <b>no exception can escape onto the FileSystemWatcher thread</b> —
/// an escaped exception silently stops the watcher.
/// </para>
/// <para>
/// Logging is deliberately sparse: only queue overflow, watcher errors and callback faults log, because
/// a busy tree can raise tens of thousands of <c>Changed</c> events per minute.
/// </para>
/// </summary>
public sealed class CodeIndexWatcher : IDisposable
{
    /// <summary>
    /// Watcher internal buffer size (64 KB). The Windows default of 8 KB overflows on any busy tree,
    /// which makes the watcher stop delivering events; enlarging it is the point of this slice.
    /// </summary>
    public const int InternalBufferSizeBytes = 64 * 1024;

    /// <summary>
    /// Directory used by the index for its own artifacts. Never re-ingested as source.
    /// The name is part of the single-source noise set (<c>PathNoiseRules.DirectoryNames</c>); this
    /// constant is retained only as the public, documented spelling of that name.
    /// </summary>
    public const string IndexOwnDirectoryName = ".pudding-code";

    private readonly CodeIndexScope _scope;
    private readonly CodeIndexChangeQueue _queue;
    private readonly CodeIndexScopeState _state;
    private readonly ILogger? _logger;
    private readonly TimeProvider _timeProvider;
    private readonly string _rootPath;
    private readonly string _rootPrefix;
    private readonly string _ignoreRootPath;
    private readonly WorkspacePathFilter _pathFilter;

    private FileSystemWatcher? _watcher;
    private int _disposed;
    private long _watcherErrorCount;

    /// <summary>Creates and starts a watcher for <paramref name="scope"/>.</summary>
    /// <param name="scope">Scope being watched; <see cref="CodeIndexScope.RootPath"/> must exist.</param>
    /// <param name="queue">Bounded queue observations are published into.</param>
    /// <param name="state">Scope state used to allocate sequences and record overflow.</param>
    /// <param name="logger">Optional logger (only overflow/error/fault are logged).</param>
    /// <param name="timeProvider">Optional clock, injectable for deterministic tests.</param>
    public CodeIndexWatcher(
        CodeIndexScope scope,
        CodeIndexChangeQueue queue,
        CodeIndexScopeState state,
        ILogger? logger = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(queue);
        ArgumentNullException.ThrowIfNull(state);

        if (string.IsNullOrWhiteSpace(scope.RootPath))
            throw new ArgumentException("CodeIndexScope.RootPath is required.", nameof(scope));

        _scope = scope;
        _queue = queue;
        _state = state;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;

        _rootPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(scope.RootPath));
        _rootPrefix = _rootPath.EndsWith(Path.DirectorySeparatorChar)
            ? _rootPath
            : _rootPath + Path.DirectorySeparatorChar;

        // ADR-089 U4-4 D3: .gitignore 真正生效。基准是 git 仓库根（不是 scope 根），
        // 且只收集 scope 树内的 .gitignore（避免把索引范围登记在被忽略目录里时被祖先规则整体清空）。
        _ignoreRootPath = IndexExcludePatterns.ResolveRepositoryRoot(_rootPath);
        _pathFilter = IndexExcludePatterns.CreateWorkspaceFilter(_ignoreRootPath, _rootPath);

        if (!Directory.Exists(_rootPath))
            throw new DirectoryNotFoundException($"Scope root '{_rootPath}' does not exist.");

        var watcher = new FileSystemWatcher(_rootPath)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName
                | NotifyFilters.DirectoryName
                | NotifyFilters.LastWrite
                | NotifyFilters.Size,
            InternalBufferSize = InternalBufferSizeBytes,
        };

        watcher.Created += OnCreated;
        watcher.Changed += OnChanged;
        watcher.Deleted += OnDeleted;
        watcher.Renamed += OnRenamed;
        watcher.Error += OnError;

        try
        {
            watcher.EnableRaisingEvents = true;
        }
        catch
        {
            watcher.Dispose();
            throw;
        }

        _watcher = watcher;
    }

    /// <summary>Absolute, normalised scope root being watched.</summary>
    public string RootPath => _rootPath;

    /// <summary>Effective watcher buffer size in bytes, read back from the live watcher.</summary>
    public int BufferSizeBytes => Volatile.Read(ref _watcher)?.InternalBufferSize ?? InternalBufferSizeBytes;

    /// <summary>Number of <c>Error</c> events observed.</summary>
    public long WatcherErrorCount => Interlocked.Read(ref _watcherErrorCount);

    /// <summary>Stops raising events without tearing the watcher down. Never throws.</summary>
    public void Stop()
    {
        var watcher = Volatile.Read(ref _watcher);
        if (watcher is null)
            return;

        try
        {
            watcher.EnableRaisingEvents = false;
        }
        catch
        {
            // best effort: a failed Stop must never take the host down
        }
    }

    /// <summary>Re-enables event delivery. Never throws.</summary>
    public void Start()
    {
        if (Volatile.Read(ref _disposed) != 0)
            return;

        var watcher = Volatile.Read(ref _watcher);
        if (watcher is null)
            return;

        try
        {
            watcher.EnableRaisingEvents = true;
        }
        catch
        {
            // best effort
        }
    }

    /// <summary>Unhooks handlers, stops events and disposes the watcher. Idempotent and never throws.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        var watcher = Interlocked.Exchange(ref _watcher, null);
        if (watcher is null)
            return;

        try
        {
            watcher.Created -= OnCreated;
            watcher.Changed -= OnChanged;
            watcher.Deleted -= OnDeleted;
            watcher.Renamed -= OnRenamed;
            watcher.Error -= OnError;
        }
        catch
        {
            // best effort
        }

        try
        {
            watcher.EnableRaisingEvents = false;
        }
        catch
        {
            // best effort
        }

        try
        {
            watcher.Dispose();
        }
        catch
        {
            // best effort: Dispose must be safe to call from shutdown paths
        }
    }

    private void OnCreated(object sender, FileSystemEventArgs e) => HandleCreated(e);

    private void OnChanged(object sender, FileSystemEventArgs e) => HandleChanged(e);

    private void OnDeleted(object sender, FileSystemEventArgs e) => HandleDeleted(e);

    private void OnRenamed(object sender, RenamedEventArgs e) => HandleRenamed(e);

    private void OnError(object sender, ErrorEventArgs e) => HandleWatcherError(e);

    /// <summary>Test seam: runs the real <c>Created</c> callback body.</summary>
    internal bool HandleCreated(FileSystemEventArgs e) => HandleChange(IndexChangeKind.Created, e, oldFullPath: null);

    /// <summary>Test seam: runs the real <c>Changed</c> callback body.</summary>
    internal bool HandleChanged(FileSystemEventArgs e) => HandleChange(IndexChangeKind.Changed, e, oldFullPath: null);

    /// <summary>Test seam: runs the real <c>Deleted</c> callback body.</summary>
    internal bool HandleDeleted(FileSystemEventArgs e) => HandleChange(IndexChangeKind.Deleted, e, oldFullPath: null);

    /// <summary>Test seam: runs the real <c>Renamed</c> callback body.</summary>
    internal bool HandleRenamed(RenamedEventArgs e) => HandleChange(IndexChangeKind.Renamed, e, e.OldFullPath);

    /// <summary>Test seam: runs the real <c>Error</c> callback body.</summary>
    internal bool HandleWatcherError(ErrorEventArgs e) => HandleError(e);

    /// <summary>
    /// The whole callback body: filter → build → publish. Wrapped so that nothing escapes onto the
    /// watcher thread; any unexpected failure degrades to "scope needs reconcile".
    /// </summary>
    /// <returns><c>true</c> when the observation was published.</returns>
    private bool HandleChange(IndexChangeKind kind, FileSystemEventArgs e, string? oldFullPath)
    {
        try
        {
            if (Volatile.Read(ref _disposed) != 0)
                return false;

            if (!TryNormalizeInsideRoot(e.FullPath, out var fullPath))
                return false;

            if (IsExcluded(fullPath))
                return false;

            string? normalizedOldPath = null;
            if (oldFullPath is not null && TryNormalizeInsideRoot(oldFullPath, out var oldPath))
            {
                // A rename whose source is excluded simply has no removal to record; we still publish
                // the new path so the reindex is never lost.
                normalizedOldPath = IsExcluded(oldPath) ? null : oldPath;
            }

            var observedAtUtc = _timeProvider.GetUtcNow();
            var sequence = _state.NextSequence();
            _state.MarkObserved(observedAtUtc);

            var change = new IndexChange(
                _scope.WorkspaceId,
                _scope.ScopeId,
                fullPath,
                kind,
                normalizedOldPath,
                TryResolveIsDirectory(fullPath),
                sequence,
                observedAtUtc);

            if (_queue.TryPublish(change))
                return true;

            var overflowTotal = _state.RecordOverflow();
            _state.RecordDroppedChange();
            _state.MarkNeedsReconcile(CodeIndexScopeState.ReconcileReasons.QueueOverflow);
            LogQueueOverflow(overflowTotal);
            return false;
        }
        catch (Exception ex)
        {
            OnCallbackFault(ex);
            return false;
        }
    }

    /// <summary>
    /// Handles <c>Error</c>. A watcher must never fail silently: the buffer overflow reported by Windows
    /// arrives as this event, and both the loss and the "cannot trust capture anymore" verdict have to
    /// be recorded.
    /// </summary>
    private bool HandleError(ErrorEventArgs e)
    {
        try
        {
            if (Volatile.Read(ref _disposed) != 0)
                return false;

            var exception = e.GetException();

            if (exception is InternalBufferOverflowException)
                _state.RecordOverflow();

            _state.MarkNeedsReconcile(CodeIndexScopeState.ReconcileReasons.WatcherError);
            var errorCount = Interlocked.Increment(ref _watcherErrorCount);

            _logger?.LogWarning(
                exception,
                "[CodeIndexWatcher] watcher error for scope {ScopeId} (root {RootPath}), error #{ErrorCount}; scope marked as needing reconcile.",
                _scope.ScopeId,
                _rootPath,
                errorCount);

            return true;
        }
        catch (Exception ex)
        {
            OnCallbackFault(ex);
            return false;
        }
    }

    private void OnCallbackFault(Exception exception)
    {
        // Nothing may throw from here: we are already on a watcher callback thread.
        try
        {
            _state.MarkNeedsReconcile(CodeIndexScopeState.ReconcileReasons.CallbackFault);
        }
        catch
        {
            // best effort
        }

        try
        {
            _logger?.LogError(
                exception,
                "[CodeIndexWatcher] unexpected failure while capturing a change for scope {ScopeId}; scope marked as needing reconcile.",
                _scope.ScopeId);
        }
        catch
        {
            // logging must never re-introduce a throw on the watcher thread
        }
    }

    /// <summary>Rate-limits overflow logging: first occurrence, then every 1000th.</summary>
    private void LogQueueOverflow(long total)
    {
        if (total != 1 && total % 1000 != 0)
            return;

        try
        {
            _logger?.LogWarning(
                "[CodeIndexWatcher] change queue full for scope {ScopeId}; total overflows {OverflowTotal}. Scope marked as needing reconcile.",
                _scope.ScopeId,
                total);
        }
        catch
        {
            // best effort
        }
    }

    /// <summary>
    /// Lexical containment check: resolves <c>..</c> and rejects anything that is not the scope root or
    /// below it. Paths equal to the root itself are rejected as well — a change to the scope root is a
    /// scope-lifecycle event, not a path-level change.
    /// </summary>
    private bool TryNormalizeInsideRoot(string? rawPath, out string fullPath)
    {
        fullPath = string.Empty;

        if (string.IsNullOrWhiteSpace(rawPath))
            return false;

        try
        {
            fullPath = Path.GetFullPath(rawPath);
        }
        catch (Exception ex) when (IsPathException(ex))
        {
            return false;
        }

        var comparison = CodePathIdentity.PathComparison;

        if (string.Equals(fullPath, _rootPath, comparison))
            return false;

        return fullPath.StartsWith(_rootPrefix, comparison);
    }

    /// <summary>
    /// Directory filtering runs per event (a recursive watcher is not assumed to exclude subtrees).
    /// <para>
    /// The <b>name-level</b> check is fed the path <b>relative to the scope root</b>, not the absolute
    /// path: otherwise a scope that merely lives under a directory called <c>build</c> or <c>bin</c>
    /// would have every single event suppressed. The index's own storage directory (<c>.pudding-code</c>)
    /// is covered by that same single source (<c>PathNoiseRules</c>) — the former defensive loop here
    /// was a second copy of the rule and is gone (ADR-089 U4-4 C9).
    /// </para>
    /// <para>
    /// The <b>.gitignore</b> check is fed the path relative to the <b>git repository root</b>, because
    /// that is what gitignore patterns are anchored to.
    /// </para>
    /// </summary>
    private bool IsExcluded(string fullPath)
    {
        var relativePath = Path.GetRelativePath(_rootPath, fullPath);
        if (relativePath.Length == 0 || relativePath == ".")
            return true;

        if (IndexExcludePatterns.IsNoisePath(relativePath))
            return true;

        if (_pathFilter.GitIgnore.Count == 0)
            return false;

        var relativeToIgnoreRoot = string.Equals(_ignoreRootPath, _rootPath, StringComparison.Ordinal)
            ? relativePath
            : Path.GetRelativePath(_ignoreRootPath, fullPath);

        return _pathFilter.GitIgnore.IsIgnored(relativeToIgnoreRoot, TryResolveIsDirectory(fullPath));
    }

    /// <summary>
    /// Best-effort directory detection using a single metadata call. Needed because
    /// <see cref="FileSystemEventArgs"/> does not distinguish files from directories, and a directory
    /// whose entry has already disappeared cannot be identified at all (reported as a file; calibration
    /// resolves the difference).
    /// </summary>
    private static bool TryResolveIsDirectory(string fullPath)
    {
        try
        {
            return (File.GetAttributes(fullPath) & FileAttributes.Directory) != 0;
        }
        catch (Exception ex) when (IsPathException(ex))
        {
            return false;
        }
    }

    private static bool IsPathException(Exception ex) =>
        ex is ArgumentException or NotSupportedException or PathTooLongException
            or IOException or UnauthorizedAccessException or System.Security.SecurityException;
}
