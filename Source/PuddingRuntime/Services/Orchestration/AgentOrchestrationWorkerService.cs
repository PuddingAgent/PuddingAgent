using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PuddingCode.Orchestration;

namespace PuddingRuntime.Services.Orchestration;

/// <summary>
/// Claims executable nodes from the durable orchestration store. This first vertical slice handles
/// Image Generate nodes; unsupported ready nodes remain untouched for later executor registrations.
/// </summary>
public sealed class AgentOrchestrationWorkerService(
    IAgentOrchestrationStore store,
    IEnumerable<IAgentOrchestrationNodeExecutor> executors,
    ILogger<AgentOrchestrationWorkerService> logger) : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(750);
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan LeaseRenewInterval = TimeSpan.FromSeconds(90);
    private readonly IReadOnlyList<IAgentOrchestrationNodeExecutor> executorList = executors.ToArray();
    private readonly string workerId = $"orchestration-{Environment.MachineName}-{Environment.ProcessId}";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            var didWork = false;
            try
            {
                var activeRuns = await store.ListRunsAsync(
                    workspaceId: null,
                    graphId: null,
                    AgentOrchestrationRunStatus.Active,
                    limit: 100,
                    offset: 0,
                    stoppingToken);
                foreach (var summary in activeRuns)
                    didWork |= await TryExecuteNextSupportedNodeAsync(summary.RunId, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "[AgentOrchestrationWorker] Poll failed; retrying.");
            }

            if (!didWork)
                await Task.Delay(PollInterval, stoppingToken);
        }
    }

    internal async Task<bool> TryExecuteNextSupportedNodeAsync(
        string runId,
        CancellationToken ct)
    {
        var run = await store.GetRunAsync(runId, ct);
        if (run is null || run.Status != AgentOrchestrationRunStatus.Active)
            return false;
        var definition = await store.GetRevisionAsync(run.RevisionId, ct);
        if (definition is null)
            return false;

        var readyNodes = run.Nodes
            .Where(item => item.Status == AgentOrchestrationNodeRunStatus.Ready)
            .Select(snapshot => definition.Nodes.FirstOrDefault(node =>
                string.Equals(node.NodeId, snapshot.NodeId, StringComparison.Ordinal)))
            .Where(node => node is not null)
            .Cast<AgentOrchestrationNodeDefinition>()
            .ToArray();
        if (readyNodes.Length == 0 || readyNodes.Any(node => ResolveExecutor(node) is null))
            return false;

        var claimResult = await store.TryClaimNextReadyNodeAsync(new AgentOrchestrationNodeClaimRequest
        {
            RunId = run.RunId,
            WorkerId = workerId,
            LeaseDuration = LeaseDuration
        }, ct);
        if (claimResult.Status == AgentOrchestrationStoreStatus.NoWork || claimResult.Value is null)
            return false;
        if (!claimResult.Success)
        {
            logger.LogDebug(
                "[AgentOrchestrationWorker] Claim skipped run={RunId} code={ErrorCode}",
                run.RunId,
                claimResult.ErrorCode);
            return false;
        }

        var claim = claimResult.Value;
        var node = definition.Nodes.First(item =>
            string.Equals(item.NodeId, claim.NodeId, StringComparison.Ordinal));
        var executor = ResolveExecutor(node)
            ?? throw new InvalidOperationException($"Claimed node '{claim.NodeId}' has no registered executor.");
        var executionRunId = $"{run.RunId}:{claim.NodeId}:a{claim.Attempt}";
        var started = await store.MarkNodeRunningAsync(new AgentOrchestrationNodeStartRequest
        {
            RunId = run.RunId,
            NodeId = claim.NodeId,
            ClaimId = claim.ClaimId,
            WorkerId = claim.WorkerId,
            FencingToken = claim.FencingToken,
            ExecutionRunId = executionRunId
        }, ct);
        if (!started.Success || started.Value is null)
            return false;

        try
        {
            using var execution = CancellationTokenSource.CreateLinkedTokenSource(ct);
            if (node.TimeoutSeconds is { } timeoutSeconds)
                execution.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
            var renewal = RenewLeaseUntilCancelledAsync(claim, execution, ct);
            AgentOrchestrationNodeExecutionResult result;
            try
            {
                result = await executor.ExecuteAsync(new AgentOrchestrationNodeExecutionContext
                {
                    Definition = definition,
                    Run = started.Value,
                    Node = node,
                    Claim = claim
                }, execution.Token);
            }
            finally
            {
                execution.Cancel();
                try
                {
                    await renewal;
                }
                catch (OperationCanceledException) when (execution.IsCancellationRequested)
                {
                    // Normal executor completion or node timeout stops the lease heartbeat.
                }
            }

            await CommitTerminalAsync(
                claim,
                succeeded: true,
                result.Summary,
                result.ArtifactReference,
                result.Outputs,
                result.ExecutionRunId,
                result.SubSessionId,
                errorMessage: null,
                ct);
            logger.LogInformation(
                "[AgentOrchestrationWorker] Node completed run={RunId} node={NodeId} attempt={Attempt} artifact={ArtifactId}",
                run.RunId,
                node.NodeId,
                claim.Attempt,
                result.ArtifactReference);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            await CommitTerminalAsync(
                claim,
                succeeded: false,
                summary: null,
                artifactReference: null,
                outputs: null,
                executionRunId: null,
                subSessionId: null,
                errorMessage: ex.Message,
                ct);
            logger.LogWarning(
                ex,
                "[AgentOrchestrationWorker] Node failed run={RunId} node={NodeId} attempt={Attempt}",
                run.RunId,
                node.NodeId,
                claim.Attempt);
        }

        return true;
    }

    private IAgentOrchestrationNodeExecutor? ResolveExecutor(AgentOrchestrationNodeDefinition node)
        => executorList.FirstOrDefault(candidate => candidate.CanExecute(node));

    private async Task RenewLeaseUntilCancelledAsync(
        AgentOrchestrationNodeClaim claim,
        CancellationTokenSource execution,
        CancellationToken serviceStoppingToken)
    {
        while (!execution.IsCancellationRequested)
        {
            await Task.Delay(LeaseRenewInterval, execution.Token);
            var renewed = await store.RenewClaimAsync(new AgentOrchestrationClaimRenewalRequest
            {
                RunId = claim.RunId,
                NodeId = claim.NodeId,
                ClaimId = claim.ClaimId,
                WorkerId = claim.WorkerId,
                FencingToken = claim.FencingToken,
                LeaseDuration = LeaseDuration
            }, serviceStoppingToken);
            if (!renewed.Success)
            {
                execution.Cancel();
                throw new InvalidOperationException(
                    renewed.ErrorMessage ?? $"Lost orchestration claim for node '{claim.NodeId}'.");
            }
        }
    }

    private async Task CommitTerminalAsync(
        AgentOrchestrationNodeClaim claim,
        bool succeeded,
        string? summary,
        string? artifactReference,
        IReadOnlyDictionary<string, AgentOrchestrationValueEnvelope>? outputs,
        string? executionRunId,
        string? subSessionId,
        string? errorMessage,
        CancellationToken ct)
    {
        var committed = await store.CommitNodeTerminalAsync(new AgentOrchestrationNodeTerminalRequest
        {
            RunId = claim.RunId,
            NodeId = claim.NodeId,
            ClaimId = claim.ClaimId,
            WorkerId = claim.WorkerId,
            FencingToken = claim.FencingToken,
            Succeeded = succeeded,
            Summary = summary,
            ArtifactReference = artifactReference,
            Outputs = outputs ?? new Dictionary<string, AgentOrchestrationValueEnvelope>(StringComparer.Ordinal),
            ExecutionRunId = executionRunId,
            SubSessionId = subSessionId,
            ErrorMessage = errorMessage
        }, ct);
        if (!committed.Success)
        {
            throw new InvalidOperationException(
                committed.ErrorMessage ?? $"Failed to commit node '{claim.NodeId}' terminal state.");
        }
    }
}
