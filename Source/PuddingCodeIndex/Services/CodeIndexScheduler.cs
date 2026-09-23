using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using PuddingCodeIndex.Contracts;

namespace PuddingCodeIndex.Services;

/// <summary>
/// Explicitly driven code-index scheduler.
/// <para>
/// Jobs are queued per scope and executed only when a caller pumps the queue through
/// <see cref="ProcessPendingAsync"/>. The scheduler starts <b>no</b> background loop: the single driver
/// is the caller (today <c>CodeIndexMaintenanceService</c>, later the Host-level hosted service), which
/// removes the double-writer window between a self-started worker and a host-level driver
/// (ADR-089 §2, U3-B "驱动归属").
/// </para>
/// <para>
/// A request that arrives while a scope is already being indexed is <b>not</b> discarded: the scope is
/// marked dirty (its desired version advances) and re-queued as soon as the running job finishes. The
/// old implementation returned early from <c>Enqueue</c> in that case, which could leave an index stale
/// forever.
/// </para>
/// </summary>
public sealed class CodeIndexScheduler : ICodeIndexSchedulerDriver, IDisposable
{
    /// <summary>Mutable per-scope bookkeeping. Every field is guarded by <see cref="_lock"/>.</summary>
    private sealed class ScopeProgress
    {
        public long DesiredVersion;
        public long CommittedVersion;
        public bool Pending;
        public bool InFlight;
        public long MarkedWhileInFlightCount;
    }

    private readonly ICodeIndexer _indexer;
    private readonly ICodeWorkspaceResolver _resolver;
    private readonly ICodeIndexStore _store;
    private readonly ILogger<CodeIndexScheduler> _logger;

    private readonly ConcurrentQueue<(string WorkspaceId, string ScopeId)> _queue = new();
    private readonly Dictionary<(string WorkspaceId, string ScopeId), ScopeProgress> _progress = new();
    private readonly SemaphoreSlim _pumpGate = new(1, 1);
    private readonly object _lock = new();
    private int _disposed;

    /// <summary>
    /// Creates a scheduler. No job is processed and no thread is started until
    /// <see cref="ProcessPendingAsync"/> is called.
    /// </summary>
    /// <param name="indexer">Indexer the jobs are executed against.</param>
    /// <param name="resolver">Resolves a scope id into a workspace descriptor.</param>
    /// <param name="store">Project registry / status store.</param>
    /// <param name="logger">Logger.</param>
    public CodeIndexScheduler(
        ICodeIndexer indexer,
        ICodeWorkspaceResolver resolver,
        ICodeIndexStore store,
        ILogger<CodeIndexScheduler> logger)
    {
        _indexer = indexer;
        _resolver = resolver;
        _store = store;
        _logger = logger;
    }

    /// <inheritdoc />
    public void Enqueue(string workspaceId, string scopeId)
    {
        if (Volatile.Read(ref _disposed) != 0)
            return;

        lock (_lock)
        {
            var key = (workspaceId, scopeId);
            var progress = GetOrCreateProgress(key);

            progress.DesiredVersion++;

            if (progress.InFlight)
            {
                // The scope is being indexed right now. Record the fact and let the running job re-queue
                // the scope when it finishes — returning here without recording it is exactly the
                // "request silently dropped" defect this slice removes.
                progress.MarkedWhileInFlightCount++;
                return;
            }

            if (progress.Pending)
                return; // already queued; the queued job will observe the newer desired version

            progress.Pending = true;
            _queue.Enqueue(key);
        }

        _logger.LogInformation("[CodeIndexScheduler] Enqueued {ScopeId} in {WorkspaceId}", scopeId, workspaceId);
    }

    /// <inheritdoc />
    public int GetQueueDepth(string workspaceId)
    {
        var count = 0;
        foreach (var j in _queue)
            if (j.WorkspaceId == workspaceId) count++;

        return count;
    }

    /// <inheritdoc />
    public bool IsIndexing(string workspaceId, string scopeId)
    {
        lock (_lock)
        {
            return _progress.TryGetValue((workspaceId, scopeId), out var progress) && progress.InFlight;
        }
    }

    /// <inheritdoc />
    public async Task<int> ProcessPendingAsync(CancellationToken cancellationToken = default)
    {
        if (Volatile.Read(ref _disposed) != 0)
            return 0;

        // One driver at a time: two concurrent pumps would run the same scope twice (double write).
        await _pumpGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var completed = 0;

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (!_queue.TryDequeue(out var job))
                    break;

                if (await ProcessJobAsync(job, cancellationToken).ConfigureAwait(false))
                    completed++;
            }

            return completed;
        }
        finally
        {
            _pumpGate.Release();
        }
    }

    /// <inheritdoc />
    public CodeIndexScopeProgress GetProgress(string workspaceId, string scopeId)
    {
        lock (_lock)
        {
            if (!_progress.TryGetValue((workspaceId, scopeId), out var progress))
                return new CodeIndexScopeProgress(workspaceId, scopeId, 0, 0, false, false, 0);

            return ToProgress(workspaceId, scopeId, progress);
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<CodeIndexScopeProgress> GetProgress()
    {
        lock (_lock)
        {
            return _progress
                .Select(pair => ToProgress(pair.Key.WorkspaceId, pair.Key.ScopeId, pair.Value))
                .ToArray();
        }
    }

    /// <summary>
    /// Runs one indexing job. Returns <c>true</c> only when the indexer really ran and the status was
    /// committed: a skipped scope, an unresolvable workspace and a cancelled run all return <c>false</c>.
    /// </summary>
    private async Task<bool> ProcessJobAsync(
        (string WorkspaceId, string ScopeId) job,
        CancellationToken cancellationToken)
    {
        long startDesired;
        lock (_lock)
        {
            var progress = GetOrCreateProgress(job);
            progress.Pending = false;
            progress.InFlight = true;
            startDesired = progress.DesiredVersion;
        }

        var cancelled = false;
        var ranIndexer = false;

        try
        {
            var project = await _store.GetProjectAsync(job.WorkspaceId, job.ScopeId, cancellationToken)
                .ConfigureAwait(false);
            if (project is null || project.Status == CodeProjectStatus.Removed)
            {
                // Nothing was indexed: the caller must not read this as a completed run.
                _logger.LogWarning("[CodeIndexScheduler] Scope {ScopeId} not found or removed, skipping",
                    job.ScopeId);
            }
            else
            {
                await _store.UpdateProjectStatusAsync(
                    job.WorkspaceId, job.ScopeId, CodeProjectStatus.Registering,
                    cancellationToken: cancellationToken).ConfigureAwait(false);

                var descriptor = await _resolver.ResolveWorkspaceAsync(
                    job.WorkspaceId, job.ScopeId, cancellationToken).ConfigureAwait(false);

                if (descriptor is null)
                {
                    await _store.UpdateProjectStatusAsync(
                        job.WorkspaceId, job.ScopeId, CodeProjectStatus.Failed,
                        "Unable to resolve workspace descriptor.", cancellationToken).ConfigureAwait(false);
                    _logger.LogWarning("[CodeIndexScheduler] Could not resolve workspace for {ScopeId}", job.ScopeId);
                }
                else
                {
                    _logger.LogInformation("[CodeIndexScheduler] Starting index for {ScopeId} at {Path}",
                        job.ScopeId, descriptor.ProjectPath);

                    var result = await _indexer.IndexWorkspaceAsync(descriptor, cancellationToken).ConfigureAwait(false);

                    await _store.UpdateProjectStatusAsync(
                        job.WorkspaceId, job.ScopeId,
                        result.Success ? CodeProjectStatus.Active : CodeProjectStatus.Failed,
                        result.Message, cancellationToken).ConfigureAwait(false);

                    _logger.LogInformation("[CodeIndexScheduler] Index {ScopeId}: {Status} — {Message}",
                        job.ScopeId, result.Status, result.Message);
                    ranIndexer = true;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Explicitly cancelled: never counted as processed, and re-queued below.
            cancelled = true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[CodeIndexScheduler] Failed to index {ScopeId}", job.ScopeId);
            try
            {
                await _store.UpdateProjectStatusAsync(
                    job.WorkspaceId, job.ScopeId, CodeProjectStatus.Failed,
                    ex.Message, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception storeEx)
            {
                _logger.LogWarning(storeEx, "[CodeIndexScheduler] Could not record failure for {ScopeId}",
                    job.ScopeId);
            }
        }
        finally
        {
            var requeued = false;
            lock (_lock)
            {
                var progress = GetOrCreateProgress(job);
                progress.InFlight = false;

                if (!cancelled)
                    progress.CommittedVersion = startDesired;

                // Still behind (a request arrived while this run was in flight) or cancelled before it
                // could commit: the scope must stay queued. Dropping it here is never acceptable.
                if (cancelled || progress.DesiredVersion > startDesired)
                {
                    progress.Pending = true;
                    _queue.Enqueue(job);
                    requeued = true;
                }
            }

            if (requeued)
            {
                _logger.LogInformation(
                    "[CodeIndexScheduler] Scope {ScopeId} was marked again (or cancelled) during indexing; re-queued",
                    job.ScopeId);
            }
        }

        return !cancelled && ranIndexer;
    }

    /// <summary>Reads or creates the progress record for a key. Must be called under <see cref="_lock"/>.</summary>
    private ScopeProgress GetOrCreateProgress((string WorkspaceId, string ScopeId) key)
    {
        if (!_progress.TryGetValue(key, out var progress))
        {
            progress = new ScopeProgress();
            _progress[key] = progress;
        }

        return progress;
    }

    private static CodeIndexScopeProgress ToProgress(
        string workspaceId,
        string scopeId,
        ScopeProgress progress) =>
        new(
            workspaceId,
            scopeId,
            progress.DesiredVersion,
            progress.CommittedVersion,
            progress.Pending,
            progress.InFlight,
            progress.MarkedWhileInFlightCount);

    /// <summary>
    /// Stops accepting new work. Idempotent.
    /// <para>
    /// The scheduler owns no unmanaged resource and no thread any more (the background worker was removed
    /// in U3-B1), so nothing has to be torn down here; the pump gate is intentionally not disposed
    /// because a pump could still be in flight when DI disposes the singleton.
    /// </para>
    /// </summary>
    public void Dispose() => Interlocked.Exchange(ref _disposed, 1);
}
