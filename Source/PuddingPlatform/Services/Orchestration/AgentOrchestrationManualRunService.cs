using System.Security.Cryptography;
using System.Text;
using PuddingCode.Orchestration;

namespace PuddingPlatform.Services.Orchestration;

public enum AgentOrchestrationManualRunResultKind
{
    Success,
    Invalid,
    NotFound,
    Conflict
}

public sealed record AgentOrchestrationManualRunRequest
{
    public required string GraphId { get; init; }
    public required string RevisionId { get; init; }
    public required string RequestId { get; init; }
    public IReadOnlyDictionary<string, AgentOrchestrationValueEnvelope> Inputs { get; init; }
        = new Dictionary<string, AgentOrchestrationValueEnvelope>(StringComparer.Ordinal);
}

public sealed record AgentOrchestrationManualRunReceipt
{
    public required string RequestId { get; init; }
    public required AgentOrchestrationRunSnapshot Run { get; init; }
    public bool Created { get; init; }
    public bool Activated { get; init; }
}

public sealed record AgentOrchestrationManualRunResult
{
    public required AgentOrchestrationManualRunResultKind Kind { get; init; }
    public AgentOrchestrationManualRunReceipt? Receipt { get; init; }
    public string? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }
}

/// <summary>Creates and activates one run pinned to the exact Revision shown by the editor.</summary>
public sealed class AgentOrchestrationManualRunService(
    IAgentOrchestrationQueryStore queryStore,
    IAgentOrchestrationStore store,
    ILogger<AgentOrchestrationManualRunService> logger)
{
    public async Task<AgentOrchestrationManualRunResult> StartAsync(
        AgentOrchestrationManualRunRequest request,
        string requestedByAgentId,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.GraphId) ||
            string.IsNullOrWhiteSpace(request.RevisionId) ||
            string.IsNullOrWhiteSpace(request.RequestId) ||
            string.IsNullOrWhiteSpace(requestedByAgentId))
        {
            return Invalid(
                "orchestration.manual_run_request_invalid",
                "graphId, revisionId, requestId, and requestedByAgentId are required.");
        }
        if (request.RequestId.Trim().Length > 256)
            return Invalid("orchestration.manual_run_request_id_too_long", "requestId cannot exceed 256 characters.");

        var definition = await queryStore.GetRevisionAsync(request.RevisionId.Trim(), ct);
        if (definition is null || !IdEquals(definition.GraphId, request.GraphId))
        {
            return new AgentOrchestrationManualRunResult
            {
                Kind = AgentOrchestrationManualRunResultKind.NotFound,
                ErrorCode = "orchestration.manual_run_revision_not_found",
                ErrorMessage = $"Revision '{request.RevisionId}' was not found for graph '{request.GraphId}'."
            };
        }

        var requestId = request.RequestId.Trim();
        var runId = CreateRunId(definition.GraphId, definition.RevisionId, requestId);
        var created = await store.CreateRunAsync(new AgentOrchestrationRunCreateRequest
        {
            RunId = runId,
            RevisionId = definition.RevisionId,
            RequestedByAgentId = $"manual:{requestedByAgentId.Trim()}",
            Inputs = request.Inputs
        }, ct);
        if (!created.Success || created.Value is null)
            return FromStoreFailure(created);

        var run = created.Value;
        var activationApplied = false;
        if (run.Status == AgentOrchestrationRunStatus.Draft)
        {
            var activated = await store.ActivateRunAsync(new AgentOrchestrationRunActivationRequest
            {
                RunId = run.RunId,
                ExpectedVersion = run.Version
            }, ct);
            if (!activated.Success || activated.Value is null)
                return FromStoreFailure(activated);
            run = activated.Value;
            activationApplied = activated.Status == AgentOrchestrationStoreStatus.Applied;
        }

        logger.LogInformation(
            "[AgentOrchestrationManualRun] Started graph={GraphId} revision={RevisionId} run={RunId} created={Created}",
            definition.GraphId,
            definition.RevisionId,
            run.RunId,
            created.Status == AgentOrchestrationStoreStatus.Applied);
        return new AgentOrchestrationManualRunResult
        {
            Kind = AgentOrchestrationManualRunResultKind.Success,
            Receipt = new AgentOrchestrationManualRunReceipt
            {
                RequestId = requestId,
                Run = run,
                Created = created.Status == AgentOrchestrationStoreStatus.Applied,
                Activated = activationApplied || run.Status != AgentOrchestrationRunStatus.Draft
            }
        };
    }

    private static string CreateRunId(string graphId, string revisionId, string requestId)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"{graphId}\n{revisionId}\n{requestId}"));
        return $"manual-{Convert.ToHexString(bytes).ToLowerInvariant()[..32]}";
    }

    private static AgentOrchestrationManualRunResult FromStoreFailure(
        AgentOrchestrationStoreResult<AgentOrchestrationRunSnapshot> result)
        => new()
        {
            Kind = result.Status switch
            {
                AgentOrchestrationStoreStatus.NotFound => AgentOrchestrationManualRunResultKind.NotFound,
                AgentOrchestrationStoreStatus.Conflict => AgentOrchestrationManualRunResultKind.Conflict,
                _ => AgentOrchestrationManualRunResultKind.Invalid
            },
            ErrorCode = result.ErrorCode ?? "orchestration.manual_run_store_failed",
            ErrorMessage = result.ErrorMessage ?? "The orchestration run could not be started."
        };

    private static AgentOrchestrationManualRunResult Invalid(string code, string message)
        => new() { Kind = AgentOrchestrationManualRunResultKind.Invalid, ErrorCode = code, ErrorMessage = message };

    private static bool IdEquals(string? left, string? right)
        => !string.IsNullOrWhiteSpace(left) &&
           !string.IsNullOrWhiteSpace(right) &&
           string.Equals(left.Trim(), right.Trim(), StringComparison.OrdinalIgnoreCase);
}
