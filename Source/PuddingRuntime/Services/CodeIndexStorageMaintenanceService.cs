using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PuddingCodeIndex.Storage;

namespace PuddingRuntime.Services;

/// <summary>
/// Hosts the background free-page reclamation loop for the code index database.
/// <para>
/// This service is deliberately thin: every decision lives in
/// <see cref="CodeIndexMaintenanceRunner"/> and <c>CodeIndexMaintenanceScheduler</c>, both of which
/// are covered by component tests that need no host and no timer. What remains here is a clock, a
/// try/catch, and logging.
/// </para>
/// <para>
/// It never runs a full <c>VACUUM</c>. That rewrite takes tens of seconds and belongs to an
/// operator-chosen window via <c>RunFullMaintenanceAsync</c>; this loop only releases a small batch
/// of free pages at a time.
/// </para>
/// </summary>
public sealed class CodeIndexStorageMaintenanceService : BackgroundService
{
    /// <summary>
    /// Delay before the first tick. Startup is the busiest moment for the indexer, and maintenance
    /// has no reason to compete with it.
    /// </summary>
    private static readonly TimeSpan StartupDelay = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Tick interval. The policy enforces a 60 s minimum between batches, so ticking more often
    /// than that would only add wake-ups without adding work.
    /// </summary>
    private static readonly TimeSpan TickInterval = TimeSpan.FromMinutes(1);

    private readonly SqliteCodeIndexStore _store;
    private readonly ILogger<CodeIndexStorageMaintenanceService> _logger;

    /// <summary>Creates the service over the host's index store.</summary>
    /// <param name="store">The shared index store instance; must not be null.</param>
    /// <param name="logger">Logger for tick outcomes; must not be null.</param>
    public CodeIndexStorageMaintenanceService(
        SqliteCodeIndexStore store,
        ILogger<CodeIndexStorageMaintenanceService> logger)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(logger);
        _store = store;
        _logger = logger;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(StartupDelay, stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        var runner = new CodeIndexMaintenanceRunner(_store);

        using var timer = new PeriodicTimer(TickInterval);
        try
        {
            do
            {
                await TickAsync(runner, stoppingToken).ConfigureAwait(false);
            }
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
            // Host shutdown: nothing to flush, the batch either committed or it did not.
        }
    }

    private async Task TickAsync(CodeIndexMaintenanceRunner runner, CancellationToken stoppingToken)
    {
        try
        {
            // The store uses a 250 ms busy timeout for maintenance work and the runner treats
            // SQLITE_BUSY as "writer busy, try later", so claiming idleness here is safe: the
            // database itself is the authority on whether a writer is present.
            var outcome = await runner.TickAsync(DateTimeOffset.UtcNow, writeIdle: true, stoppingToken)
                .ConfigureAwait(false);

            if (outcome.WriterBusy)
            {
                _logger.LogDebug("Code index reclamation skipped: writer busy.");
            }
            else if (outcome.Ran)
            {
                _logger.LogInformation(
                    "Code index reclamation reclaimed {Pages} page(s): {Reason}",
                    outcome.ReclaimedPages,
                    outcome.Decision.Reason);
            }
            else
            {
                _logger.LogDebug("Code index reclamation idle: {Reason}", outcome.Decision.Reason);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // A maintenance failure must never take the host down, and must never be silent.
            _logger.LogWarning(ex, "Code index reclamation tick failed.");
        }
    }
}
