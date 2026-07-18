using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using PuddingCode.Platform;

namespace PuddingPlatform.Services.AgentChat;

/// <summary>
/// ADR-059: Chat Execution Worker v5 — passes real ExecutionLease to Coordinator.
/// No longer extracts lease.Command; Coordinator receives the full Lease from Worker.
/// Per-conversation SessionLock is retained as optimization; DB active-run constraint
/// is the correctness source.
/// </summary>
public sealed class ChatExecutionWorker : BackgroundService
{
    private const int IdlePollDelayMs = 2_000;
    private const string WorkerId = "chat-execution-worker";

    private readonly IExecutionLeaseStore _leaseStore;
    private readonly IExecutionJournal _journal;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ChatExecutionWorker> _logger;
    private readonly int _maxConcurrency;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _sessionLocks = new();

    public ChatExecutionWorker(
        IExecutionLeaseStore leaseStore,
        IExecutionJournal journal,
        IServiceScopeFactory scopeFactory,
        ILogger<ChatExecutionWorker> logger,
        IConfiguration? configuration = null)
    {
        _leaseStore = leaseStore;
        _journal = journal;
        _scopeFactory = scopeFactory;
        _logger = logger;
        _maxConcurrency = configuration?.GetValue<int?>("Pudding:ChatExecutionMaxConcurrency") ?? 3;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("[ChatWorker] Started v5 maxConcurrency={Max}", _maxConcurrency);

        var running = new ConcurrentDictionary<string, Task>();
        var duration = TimeSpan.FromMinutes(2);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                while (running.Count < _maxConcurrency)
                {
                    var lease = await _leaseStore.TryAcquireAsync(
                        WorkerId, duration, stoppingToken);
                    if (lease is null) break;

                    var task = ProcessWithSessionLockAsync(lease, stoppingToken);
                    running.TryAdd(lease.CommandId, task);
                }

                foreach (var entry in running.Where(x => x.Value.IsCompleted).ToArray())
                {
                    if (!running.TryRemove(entry.Key, out var completedTask))
                        continue;

                    try
                    {
                        await completedTask;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex,
                            "[ChatWorker] Observed unexpected task escape command={CommandId}",
                            entry.Key);
                    }
                }

                await Task.Delay(IdlePollDelayMs, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[ChatWorker] Poll error");
                await Task.Delay(IdlePollDelayMs, stoppingToken);
            }
        }

        _logger.LogInformation("[ChatWorker] Stopping, {Count} running", running.Count);
        await Task.WhenAll(running.Values);
        _logger.LogInformation("[ChatWorker] Stopped");
    }

    private async Task ProcessWithSessionLockAsync(
        ExecutionLease lease, CancellationToken stoppingToken)
    {
        var sessionLock = _sessionLocks.GetOrAdd(
            lease.ConversationId, _ => new SemaphoreSlim(1, 1));
        await sessionLock.WaitAsync(stoppingToken);
        try
        {
            try
            {
                await using var scope = _scopeFactory.CreateAsyncScope();
                var coordinator = scope.ServiceProvider
                    .GetRequiredService<IExecutionRunCoordinator>();

                // Pass real Lease through — Coordinator must NOT recreate it
                var outcome = await coordinator.ExecuteAsync(lease, stoppingToken);
                _logger.LogInformation(
                    "[ChatWorker] Run finished run={RunId} kind={Kind} seq={Seq}",
                    outcome.RunId, outcome.Terminal.Kind, outcome.TerminalSequence);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                await _leaseStore.ReleaseAsync(lease, CancellationToken.None);
            }
            catch (Exception ex)
            {
                var errorId = Guid.NewGuid().ToString("N")[..12];
                _logger.LogError(ex,
                    "[ChatWorker] Execution escaped coordinator run={RunId} command={CommandId} errorId={ErrorId}",
                    lease.RunId, lease.CommandId, errorId);

                try
                {
                    await _journal.TryCommitInfrastructureFailureAsync(
                        lease,
                        TurnTerminal.Failure(
                            "execution_infrastructure_error",
                            $"Execution infrastructure failed. errorId={errorId}"),
                        [],
                        CancellationToken.None);
                }
                catch (Exception commitEx)
                {
                    _logger.LogCritical(commitEx,
                        "[ChatWorker] Failed to close escaped run={RunId} command={CommandId} errorId={ErrorId}",
                        lease.RunId, lease.CommandId, errorId);
                }
            }
        }
        finally
        {
            sessionLock.Release();
        }
    }
}
