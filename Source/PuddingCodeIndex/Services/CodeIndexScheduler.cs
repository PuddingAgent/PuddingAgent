using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using PuddingCodeIntelligence.Contracts;

namespace PuddingCodeIntelligence.Services;

/// <summary>
/// In-process background scheduler that enqueues indexing jobs and processes
/// them one at a time per workspace. Uses a simple channel-like queue backed
/// by <see cref="ConcurrentQueue{T}"/> and a background <see cref="Task"/>.
/// </summary>
public sealed class CodeIndexScheduler : ICodeIndexScheduler, IDisposable
{
    private readonly ICodeIndexer _indexer;
    private readonly ICodeWorkspaceResolver _resolver;
    private readonly ICodeIndexStore _store;
    private readonly ILogger<CodeIndexScheduler> _logger;

    private readonly ConcurrentQueue<(string WorkspaceId, string ScopeId)> _queue = new();
    private readonly HashSet<(string WorkspaceId, string ScopeId)> _inFlight = new();
    private readonly SemaphoreSlim _signal = new(0);
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _worker;
    private readonly object _lock = new();

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

        _worker = Task.Run(() => ProcessAsync(_cts.Token));
    }

    public void Enqueue(string workspaceId, string scopeId)
    {
        if (_cts.IsCancellationRequested)
            return;

        lock (_lock)
        {
            if (_inFlight.Contains((workspaceId, scopeId)))
                return;
            foreach (var j in _queue)
            {
                if (j.WorkspaceId == workspaceId && j.ScopeId == scopeId)
                    return;
            }
        }

        _queue.Enqueue((workspaceId, scopeId));
        _logger.LogInformation("[CodeIndexScheduler] Enqueued {ScopeId} in {WorkspaceId}", scopeId, workspaceId);
        _signal.Release();
    }

    public int GetQueueDepth(string workspaceId)
    {
        var count = 0;
        foreach (var j in _queue)
            if (j.WorkspaceId == workspaceId) count++;
        return count;
    }

    public bool IsIndexing(string workspaceId, string scopeId)
    {
        lock (_lock)
        {
            return _inFlight.Contains((workspaceId, scopeId));
        }
    }

    private async Task ProcessAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await _signal.WaitAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            while (_queue.TryDequeue(out var job))
            {
                ct.ThrowIfCancellationRequested();

                lock (_lock)
                {
                    _inFlight.Add((job.WorkspaceId, job.ScopeId));
                }

                try
                {
                    var project = await _store.GetProjectAsync(job.WorkspaceId, job.ScopeId, ct)
                        .ConfigureAwait(false);
                    if (project is null || project.Status == CodeProjectStatus.Removed)
                    {
                        _logger.LogWarning("[CodeIndexScheduler] Scope {ScopeId} not found or removed, skipping",
                            job.ScopeId);
                        continue;
                    }

                    await _store.UpdateProjectStatusAsync(
                        job.WorkspaceId, job.ScopeId, CodeProjectStatus.Registering,
                        cancellationToken: ct).ConfigureAwait(false);

                    var descriptor = await _resolver.ResolveWorkspaceAsync(
                        job.WorkspaceId, job.ScopeId, ct).ConfigureAwait(false);

                    if (descriptor is null)
                    {
                        await _store.UpdateProjectStatusAsync(
                            job.WorkspaceId, job.ScopeId, CodeProjectStatus.Failed,
                            "Unable to resolve workspace descriptor.", ct).ConfigureAwait(false);
                        _logger.LogWarning("[CodeIndexScheduler] Could not resolve workspace for {ScopeId}", job.ScopeId);
                        continue;
                    }

                    _logger.LogInformation("[CodeIndexScheduler] Starting index for {ScopeId} at {Path}",
                        job.ScopeId, descriptor.ProjectPath);

                    var result = await _indexer.IndexWorkspaceAsync(descriptor, ct).ConfigureAwait(false);

                    await _store.UpdateProjectStatusAsync(
                        job.WorkspaceId, job.ScopeId,
                        result.Success ? CodeProjectStatus.Active : CodeProjectStatus.Failed,
                        result.Message, ct).ConfigureAwait(false);

                    _logger.LogInformation("[CodeIndexScheduler] Index {ScopeId}: {Status} — {Message}",
                        job.ScopeId, result.Status, result.Message);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[CodeIndexScheduler] Failed to index {ScopeId}", job.ScopeId);
                    await _store.UpdateProjectStatusAsync(
                        job.WorkspaceId, job.ScopeId, CodeProjectStatus.Failed,
                        ex.Message, CancellationToken.None).ConfigureAwait(false);
                }
                finally
                {
                    lock (_lock)
                    {
                        _inFlight.Remove((job.WorkspaceId, job.ScopeId));
                    }
                }
            }
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _signal.Release();
        try { _worker.Wait(TimeSpan.FromSeconds(5)); } catch { /* best effort */ }
        _cts.Dispose();
        _signal.Dispose();
    }
}
