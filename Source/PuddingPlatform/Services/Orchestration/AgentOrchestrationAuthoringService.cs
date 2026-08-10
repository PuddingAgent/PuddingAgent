using PuddingCode.Orchestration;

namespace PuddingPlatform.Services.Orchestration;

/// <summary>
/// Server-side authoring commands for immutable graph revisions (S1). The service owns every audit
/// field that defines the revision chain — revision number, revisionId, parentRevisionId,
/// createdByAgentId and createdAtUtc — and never trusts client-supplied copies. Validation is
/// side-effect free; saves compile before entering the write path and rely on the store's head CAS.
/// </summary>
public sealed class AgentOrchestrationAuthoringService(
    IAgentOrchestrationStore store,
    AgentOrchestrationGraphCompiler compiler,
    TimeProvider timeProvider)
{
    /// <summary>Compiles a draft without touching any durable fact.</summary>
    public Task<AgentOrchestrationDraftValidationResult> ValidateAsync(
        AgentOrchestrationDraftValidateRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Definition);

        var compilation = compiler.Compile(request.Definition);
        return Task.FromResult(new AgentOrchestrationDraftValidationResult
        {
            IsValid = compilation.Success,
            NormalizedDefinition = compilation.Definition,
            Issues = compilation.Issues,
            TopologicalNodeIds = compilation.TopologicalNodeIds
        });
    }

    /// <summary>
    /// Appends the next immutable revision onto the graph head using optimistic concurrency.
    /// Returns Applied with the server-authored revision, Conflict with current head facts when the
    /// expected head is stale, or InvalidState with compiler diagnostics for a failed draft.
    /// </summary>
    public async Task<AgentOrchestrationStoreResult<AgentOrchestrationGraphDefinition>> CreateRevisionAsync(
        AgentOrchestrationRevisionCreateRequest request,
        string actorId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Definition);

        if (string.IsNullOrWhiteSpace(request.GraphId))
            return Invalid<AgentOrchestrationGraphDefinition>(
                "orchestration.graph_id_required",
                "GraphId is required.");
        if (!IdEquals(request.GraphId, request.Definition.GraphId))
        {
            return Invalid<AgentOrchestrationGraphDefinition>(
                "orchestration.revision_graph_mismatch",
                "Route graphId must match the definition graphId.");
        }
        if (request.ExpectedCurrentRevision < 0)
        {
            return Invalid<AgentOrchestrationGraphDefinition>(
                "orchestration.revision_expected_invalid",
                "ExpectedCurrentRevision cannot be negative.");
        }

        var actor = string.IsNullOrWhiteSpace(actorId) ? "system" : actorId.Trim();
        var graphId = request.GraphId.Trim();

        // Read-only head facts first so a stale editor never waits in the write queue merely to
        // learn it lost the CAS race (doc 83 §7 invariants).
        var head = await store.GetLatestRevisionAsync(graphId, ct);
        if (head is null)
        {
            return NotFound<AgentOrchestrationGraphDefinition>(
                "orchestration.graph_not_found",
                $"Graph '{graphId}' was not found.");
        }
        if (head.Revision != request.ExpectedCurrentRevision)
        {
            return Conflict<AgentOrchestrationGraphDefinition>(
                "orchestration.revision_conflict",
                $"Graph head advanced from r{request.ExpectedCurrentRevision} to r{head.Revision}.",
                head.Revision,
                head.RevisionId);
        }

        // Server-authors every audit field; client values are preview information only.
        var revision = head.Revision + 1;
        var serverAuthored = request.Definition with
        {
            GraphId = head.GraphId,
            RevisionId = $"{head.GraphId}/r{revision:D3}",
            Revision = revision,
            ParentRevisionId = head.RevisionId,
            WorkspaceId = head.WorkspaceId,
            RootSessionId = head.RootSessionId,
            CreatedByAgentId = actor,
            CreatedAtUtc = timeProvider.GetUtcNow()
        };

        // Compile before any write so failed drafts return full diagnostics (422) instead of a
        // joined error string. The compiler also freezes contract hashes from the current registry.
        var compilation = compiler.Compile(serverAuthored);
        if (!compilation.Success)
        {
            return InvalidWithIssues<AgentOrchestrationGraphDefinition>(
                "orchestration.definition_invalid",
                "Draft validation failed; no revision was saved.",
                compilation.Issues);
        }

        var result = await store.SaveRevisionAsync(
            new AgentOrchestrationRevisionWriteRequest
            {
                Definition = compilation.Definition!,
                ExpectedCurrentRevision = head.Revision
            },
            ct);
        if (result.Status == AgentOrchestrationStoreStatus.Conflict)
        {
            // A concurrent writer advanced the head between our read and the CAS. Report current
            // head facts so the editor can reload instead of overwriting silently.
            var latest = await store.GetLatestRevisionAsync(graphId, ct);
            return Conflict<AgentOrchestrationGraphDefinition>(
                "orchestration.revision_conflict",
                latest is null
                    ? "Graph head changed before the revision could be committed."
                    : $"Graph head advanced to r{latest.Revision}.",
                latest?.Revision ?? 0,
                latest?.RevisionId);
        }

        return result;
    }

    private static AgentOrchestrationStoreResult<T> Invalid<T>(string code, string message)
        where T : class
        => new()
        {
            Status = AgentOrchestrationStoreStatus.InvalidState,
            ErrorCode = code,
            ErrorMessage = message
        };

    private static AgentOrchestrationStoreResult<T> InvalidWithIssues<T>(
        string code,
        string message,
        IReadOnlyList<AgentOrchestrationValidationIssue> issues)
        where T : class
        => new()
        {
            Status = AgentOrchestrationStoreStatus.InvalidState,
            ErrorCode = code,
            ErrorMessage = message,
            Issues = issues
        };

    private static AgentOrchestrationStoreResult<T> Conflict<T>(
        string code,
        string message,
        long? currentRevision,
        string? currentRevisionId)
        where T : class
        => new()
        {
            Status = AgentOrchestrationStoreStatus.Conflict,
            ErrorCode = code,
            ErrorMessage = message,
            CurrentVersion = currentRevision,
            CurrentRevisionId = currentRevisionId
        };

    private static AgentOrchestrationStoreResult<T> NotFound<T>(string code, string message)
        where T : class
        => new()
        {
            Status = AgentOrchestrationStoreStatus.NotFound,
            ErrorCode = code,
            ErrorMessage = message
        };

    private static bool IdEquals(string? left, string? right)
        => !string.IsNullOrWhiteSpace(left) &&
           !string.IsNullOrWhiteSpace(right) &&
           string.Equals(left.Trim(), right.Trim(), StringComparison.OrdinalIgnoreCase);
}
