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
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ChatExecutionWorker> _logger;
    private readonly int _maxConcurrency;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _sessionLocks = new();

    public ChatExecutionWorker(
        IExecutionLeaseStore leaseStore,
        IServiceScopeFactory scopeFactory,
        ILogger<ChatExecutionWorker> logger,
        IConfiguration? configuration = null)
    {
        _leaseStore = leaseStore;
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
                    _ = task.ContinueWith(_ =>
                        running.TryRemove(lease.CommandId, out _), CancellationToken.None);
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
            await using var scope = _scopeFactory.CreateAsyncScope();
            var coordinator = scope.ServiceProvider
                .GetRequiredService<IExecutionRunCoordinator>();

            // Pass real Lease through — Coordinator must NOT recreate it
            var outcome = await coordinator.ExecuteAsync(lease, stoppingToken);
            _logger.LogInformation(
                "[ChatWorker] Run finished run={RunId} kind={Kind} seq={Seq}",
                outcome.RunId, outcome.Terminal.Kind, outcome.TerminalSequence);
        }
        finally
        {
            sessionLock.Release();
        }
    }
}
