using Microsoft.Extensions.Logging;
using PuddingCodeIndex.Contracts;

namespace PuddingCodeIndex.Services.CodeIndex;

/// <summary>
/// Change source attached to exactly one scope.
/// <para>
/// The abstraction exists so the maintenance driver can own the lifecycle of a scope's change source
/// (start on attach, stop on shutdown) without depending on <see cref="FileSystemWatcher"/> — and so the
/// pipeline can be tested without touching the real file system.
/// </para>
/// </summary>
public interface ICodeIndexChangeWatcher : IDisposable
{
    /// <summary>Enables event delivery. Never throws.</summary>
    void Start();

    /// <summary>Stops event delivery. Never throws.</summary>
    void Stop();
}

/// <summary>Creates the change source of one scope.</summary>
public interface ICodeIndexWatcherFactory
{
    /// <summary>
    /// Creates (and does not yet start) the change source for a scope.
    /// </summary>
    /// <param name="workspaceId">Workspace that owns the scope.</param>
    /// <param name="scopeId">Scope to watch.</param>
    /// <param name="rootPath">Root the scope is watched at.</param>
    /// <param name="queue">Bounded queue the source publishes observations into.</param>
    /// <param name="state">Scope state used to allocate sequences and record overflow.</param>
    /// <returns>
    /// The change source, or <c>null</c> when the scope cannot be watched (root missing, permission
    /// denied). A null result is reported by the caller as "watcher not attached" — it is never treated
    /// as "nothing to watch, all quiet".
    /// </returns>
    ICodeIndexChangeWatcher? Create(
        string workspaceId,
        string scopeId,
        string rootPath,
        CodeIndexChangeQueue queue,
        CodeIndexScopeState state);
}

/// <summary>
/// Production <see cref="ICodeIndexWatcherFactory"/>: wraps the U3-A <see cref="CodeIndexWatcher"/>
/// without changing it.
/// </summary>
public sealed class FileSystemCodeIndexWatcherFactory : ICodeIndexWatcherFactory
{
    private readonly ILogger? _logger;
    private readonly TimeProvider _timeProvider;

    /// <summary>Creates the factory.</summary>
    /// <param name="logger">Optional logger forwarded to the watcher.</param>
    /// <param name="timeProvider">Optional clock forwarded to the watcher.</param>
    public FileSystemCodeIndexWatcherFactory(ILogger? logger = null, TimeProvider? timeProvider = null)
    {
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public ICodeIndexChangeWatcher? Create(
        string workspaceId,
        string scopeId,
        string rootPath,
        CodeIndexChangeQueue queue,
        CodeIndexScopeState state)
    {
        ArgumentNullException.ThrowIfNull(queue);
        ArgumentNullException.ThrowIfNull(state);

        try
        {
            // The scope record is only used by the watcher for its root path and its id in log lines, so
            // the registry-owned ScopeState/ScopeSource values are irrelevant here.
            var scope = new CodeIndexScope(workspaceId, scopeId, rootPath, ScopeState.Active, ScopeSource.Auto);
            return new WatcherAdapter(new CodeIndexWatcher(scope, queue, state, _logger, _timeProvider));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            _logger?.LogWarning(ex,
                "[FileSystemCodeIndexWatcherFactory] Cannot watch scope {ScopeId} at {RootPath}",
                scopeId, rootPath);
            return null;
        }
    }

    /// <summary>Adapts the U3-A watcher to the lifecycle-only abstraction.</summary>
    private sealed class WatcherAdapter : ICodeIndexChangeWatcher
    {
        private readonly CodeIndexWatcher _watcher;

        public WatcherAdapter(CodeIndexWatcher watcher) => _watcher = watcher;

        public void Start() => _watcher.Start();

        public void Stop() => _watcher.Stop();

        public void Dispose() => _watcher.Dispose();
    }
}
