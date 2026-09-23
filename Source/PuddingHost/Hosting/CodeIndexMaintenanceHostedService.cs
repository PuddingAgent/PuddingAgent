using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PuddingCode.Configuration;
using PuddingCodeIndex.Contracts;

namespace PuddingHost.Hosting;

/// <summary>
/// U3-B2a: the host-side lifecycle driver of the code-index maintenance component.
/// <para>
/// This class owns <b>no</b> maintenance logic — no loop, no queue, no scheduler call. It starts the
/// component's single driver, hooks up the scopes that are already registered, and stops the driver within a
/// bounded time. All maintenance (change capture, coalescing, pumping the index scheduler) stays inside
/// <c>PuddingCodeIndex</c>, which must not depend on the Host (ADR-089 §2「驱动归属」).
/// </para>
/// <para>
/// Why it exists: U3-B1 (<c>a378a9d8</c>) removed the index scheduler's self-started worker, so from that
/// commit on an enqueue is serviced only while something pumps the queue. The only production acceptor
/// (<c>code_index_register_project</c>) enqueues directly, with no change batch behind it — without a
/// host-level driver the queue would never drain. That is the P0 defect this service closes.
/// </para>
/// </summary>
public sealed class CodeIndexMaintenanceHostedService : IHostedService
{
    /// <summary>
    /// Outer ceiling for shutdown. The component bounds its own stop as well; this guard exists so a stuck
    /// component can never hold up host shutdown.
    /// </summary>
    public static readonly TimeSpan DefaultStopTimeout = TimeSpan.FromSeconds(10);

    private readonly ICodeIndexMaintenance _maintenance;
    private readonly ICodeIndexScopeRegistry _scopeRegistry;
    private readonly PuddingDataPaths _dataPaths;
    private readonly ILogger<CodeIndexMaintenanceHostedService> _logger;

    private Task? _attachTask;
    private int _attachedScopeCount;
    private int _attachCompleted;

    /// <summary>Creates the driver.</summary>
    /// <param name="maintenance">Component driver (change capture plus scheduler pump).</param>
    /// <param name="scopeRegistry">Source of truth for scopes that are already registered.</param>
    /// <param name="dataPaths">Data root; a workspace id is the directory name below its workspaces folder.</param>
    /// <param name="logger">Logger.</param>
    public CodeIndexMaintenanceHostedService(
        ICodeIndexMaintenance maintenance,
        ICodeIndexScopeRegistry scopeRegistry,
        PuddingDataPaths dataPaths,
        ILogger<CodeIndexMaintenanceHostedService> logger)
    {
        _maintenance = maintenance;
        _scopeRegistry = scopeRegistry;
        _dataPaths = dataPaths;
        _logger = logger;
    }

    /// <summary>Number of scopes the startup attachment hooked up to the driver.</summary>
    public int AttachedScopeCount => Volatile.Read(ref _attachedScopeCount);

    /// <summary>
    /// True once the startup attachment has finished, successfully or not. Attachment deliberately runs off
    /// the startup path, so this flag is the only way to observe its outcome without racing a timer.
    /// </summary>
    public bool ScopeAttachmentCompleted => Volatile.Read(ref _attachCompleted) != 0;

    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            // Non-blocking by contract: starting the driver only flips a flag and launches its loop, so no
            // indexing run and no file-system scan happens on the startup path.
            await _maintenance.StartAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // A driver that cannot start must not take the host down. Index maintenance stays paused, loudly.
            _logger.LogError(ex,
                "[CodeIndexMaintenanceHost] The index maintenance driver could not be started; code index " +
                "maintenance stays paused until the next host start.");
            Volatile.Write(ref _attachCompleted, 1);
            return;
        }

        // Attachment touches the file system and the index store, so it is deliberately off the startup path:
        // host startup must never wait for it. Failures are logged inside, never thrown.
        _attachTask = AttachRegisteredScopesAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        // (1) Let a still-running attachment settle first, bounded by the same ceiling.
        var attaching = _attachTask;
        _attachTask = null;
        if (attaching is { IsCompleted: false })
        {
            var settle = await Task.WhenAny(attaching, Task.Delay(DefaultStopTimeout, cancellationToken))
                .ConfigureAwait(false);
            if (!ReferenceEquals(settle, attaching))
            {
                _logger.LogWarning(
                    "[CodeIndexMaintenanceHost] Scope attachment was still running after {Timeout}; stopping anyway.",
                    DefaultStopTimeout);
            }
        }

        // (2) Stop the component driver within the ceiling. A failing stop must never escape into host
        // shutdown, and queued requests must stay queued rather than be presented as handled.
        try
        {
            var stopping = _maintenance.StopAsync(cancellationToken);
            var settle = await Task.WhenAny(stopping, Task.Delay(DefaultStopTimeout, cancellationToken))
                .ConfigureAwait(false);
            if (!ReferenceEquals(settle, stopping))
            {
                _logger.LogWarning(
                    "[CodeIndexMaintenanceHost] The index maintenance driver did not stop within {Timeout}; " +
                    "shutdown continues. Requests that are still queued stay queued (they are not dropped).",
                    DefaultStopTimeout);
                return;
            }

            await stopping.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "[CodeIndexMaintenanceHost] Stopping the index maintenance driver failed; shutdown continues.");
        }
    }

    /// <summary>
    /// Hooks up every already-registered scope to the driver, so the change-capture pipeline watches exactly
    /// the directories that have an index. With nothing registered this is a safe no-op.
    /// </summary>
    private async Task AttachRegisteredScopesAsync(CancellationToken cancellationToken)
    {
        try
        {
            await AttachRegisteredScopesCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            // Recorded even on failure: the flag means "no longer pending", not "everything was attached".
            Volatile.Write(ref _attachCompleted, 1);
        }
    }

    private async Task AttachRegisteredScopesCoreAsync(CancellationToken cancellationToken)
    {
        try
        {
            var attached = 0;
            foreach (var workspaceId in EnumerateWorkspaceIds())
            {
                cancellationToken.ThrowIfCancellationRequested();

                var scopes = await _scopeRegistry.ListScopesAsync(workspaceId, cancellationToken)
                    .ConfigureAwait(false);

                foreach (var scope in scopes)
                {
                    // A covered child has no index of its own: the covering scope already watches it.
                    if (scope.State != ScopeState.Active || string.IsNullOrWhiteSpace(scope.RootPath))
                        continue;

                    if (_maintenance.EnsureScope(scope.WorkspaceId, scope.ScopeId, scope.RootPath))
                        attached++;
                }
            }

            Volatile.Write(ref _attachedScopeCount, attached);
            _logger.LogInformation(
                "[CodeIndexMaintenanceHost] Index maintenance driver attached; {AttachedCount} registered scope(s) hooked up.",
                attached);
        }
        catch (OperationCanceledException)
        {
            // Shutdown while attaching: nothing to report.
        }
        catch (Exception ex)
        {
            // Attachment is best effort: a missing scope means no change source, never a broken host.
            _logger.LogWarning(ex,
                "[CodeIndexMaintenanceHost] Attaching registered scopes to the index maintenance driver failed; " +
                "the driver keeps running without those change sources.");
        }
    }

    /// <summary>
    /// Workspace ids of this data root. The platform's own convention is that a workspace id is the directory
    /// name below <c>workspaces</c> (see <see cref="PuddingDataPaths.WorkspaceRoot"/>, and the shipped default
    /// agent manifest using <c>workspaceId: default</c> for <c>workspaces/default</c>).
    /// </summary>
    private IReadOnlyList<string> EnumerateWorkspaceIds()
    {
        try
        {
            if (!Directory.Exists(_dataPaths.WorkspacesRoot))
                return [];

            return Directory.GetDirectories(_dataPaths.WorkspacesRoot)
                .Select(Path.GetFileName)
                .Where(static name => !string.IsNullOrWhiteSpace(name))
                .Select(static name => name!)
                .OrderBy(static name => name, StringComparer.Ordinal)
                .ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(
                ex,
                "[CodeIndexMaintenanceHost] The workspace list could not be read from {Root}.",
                _dataPaths.WorkspacesRoot);
            return [];
        }
    }
}
