using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PuddingCode.Orchestration;

namespace PuddingPlatform.Services.Orchestration;

public enum AgentOrchestrationHttpHookResultKind
{
    Success,
    Invalid,
    NotFound,
    Conflict
}

public sealed record AgentOrchestrationHttpHookInvokeRequest
{
    public required string SourceEventId { get; init; }
    public JsonElement Payload { get; init; }
}

public sealed record AgentOrchestrationHttpHookInvokeReceipt
{
    public required string TriggerId { get; init; }
    public required string SourceEventId { get; init; }
    public required AgentOrchestrationRunSnapshot Run { get; init; }
    public bool Created { get; init; }
    public bool Activated { get; init; }
}

public sealed record AgentOrchestrationHttpHookResult
{
    public required AgentOrchestrationHttpHookResultKind Kind { get; init; }
    public AgentOrchestrationHttpHookInvokeReceipt? Receipt { get; init; }
    public string? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }
}

/// <summary>
/// Authenticated debug HTTP hook. The caller must name an immutable Revision; Graph Head is never
/// treated as deployed state. A deterministic run id makes source event retries idempotent.
/// </summary>
public sealed class AgentOrchestrationHttpHookService(
    IAgentOrchestrationQueryStore queryStore,
    IAgentOrchestrationStore store,
    ILogger<AgentOrchestrationHttpHookService> logger)
{
    public async Task<AgentOrchestrationHttpHookResult> InvokeAsync(
        string graphId,
        string revisionId,
        string triggerId,
        AgentOrchestrationHttpHookInvokeRequest request,
        string requestedByAgentId,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(graphId) ||
            string.IsNullOrWhiteSpace(revisionId) ||
            string.IsNullOrWhiteSpace(triggerId) ||
            string.IsNullOrWhiteSpace(request.SourceEventId))
        {
            return Invalid(
                "orchestration.http_hook_request_invalid",
                "graphId, revisionId, triggerId, and sourceEventId are required.");
        }
        if (request.SourceEventId.Trim().Length > 256)
        {
            return Invalid(
                "orchestration.http_hook_event_id_too_long",
                "sourceEventId cannot exceed 256 characters.");
        }

        var definition = await queryStore.GetRevisionAsync(revisionId.Trim(), ct);
        if (definition is null || !IdEquals(definition.GraphId, graphId))
        {
            return NotFound(
                "orchestration.http_hook_revision_not_found",
                $"Revision '{revisionId}' was not found for graph '{graphId}'.");
        }

        var trigger = definition.Triggers.FirstOrDefault(item => IdEquals(item.TriggerId, triggerId));
        if (trigger is null)
        {
            return NotFound(
                "orchestration.http_hook_trigger_not_found",
                $"HTTP hook trigger '{triggerId}' was not found in revision '{revisionId}'.");
        }
        if (!string.Equals(
                trigger.Trigger.TriggerType,
                AgentOrchestrationTriggerTypes.Webhook,
                StringComparison.OrdinalIgnoreCase))
        {
            return Invalid(
                "orchestration.http_hook_trigger_type_invalid",
                $"Trigger '{trigger.TriggerId}' is not a webhook trigger.");
        }
        if (!trigger.Enabled)
        {
            return Invalid(
                "orchestration.http_hook_disabled",
                $"HTTP hook trigger '{trigger.TriggerId}' is disabled.");
        }

        var payload = request.Payload.ValueKind == JsonValueKind.Undefined
            ? JsonSerializer.SerializeToElement(new Dictionary<string, object?>())
            : request.Payload.Clone();
        var inputDefinitions = definition.Inputs.ToDictionary(
            input => input.InputId,
            StringComparer.OrdinalIgnoreCase);
        var runInputs = new Dictionary<string, AgentOrchestrationValueEnvelope>(StringComparer.Ordinal);
        foreach (var binding in trigger.InputBindings)
        {
            if (!inputDefinitions.TryGetValue(binding.TargetInputId, out var input))
            {
                return Invalid(
                    "orchestration.http_hook_target_input_missing",
                    $"Graph input '{binding.TargetInputId}' is missing from revision '{revisionId}'.");
            }
            if (runInputs.ContainsKey(input.InputId))
            {
                return Invalid(
                    "orchestration.http_hook_input_duplicate",
                    $"HTTP hook maps more than one payload value to graph input '{input.InputId}'.");
            }
            if (!TryResolveSourcePath(payload, binding.SourcePath, out var value))
            {
                return Invalid(
                    "orchestration.http_hook_source_path_missing",
                    $"Payload path '{binding.SourcePath}' was not found for graph input '{input.InputId}'.");
            }
            if (!input.Contract.Deliveries.Contains(AgentOrchestrationValueDelivery.Inline))
            {
                return Invalid(
                    "orchestration.http_hook_inline_not_allowed",
                    $"Graph input '{input.InputId}' does not accept inline HTTP payloads.");
            }

            runInputs[input.InputId] = new AgentOrchestrationValueEnvelope
            {
                DataType = input.Contract.DataType,
                ContentType = ResolveContentType(input.Contract, value),
                InlineValue = value.Clone()
            };
        }

        var sourceEventId = request.SourceEventId.Trim();
        var runId = CreateRunId(definition.GraphId, definition.RevisionId, trigger.TriggerId, sourceEventId);
        var created = await store.CreateRunAsync(new AgentOrchestrationRunCreateRequest
        {
            RunId = runId,
            RevisionId = definition.RevisionId,
            RequestedByAgentId = $"http-hook:{trigger.TriggerId}:{requestedByAgentId.Trim()}",
            Inputs = runInputs
        }, ct);
        if (!created.Success || created.Value is null)
            return FromStoreFailure(created);

        var current = created.Value;
        var activationApplied = false;
        if (current.Status == AgentOrchestrationRunStatus.Draft)
        {
            var activated = await store.ActivateRunAsync(new AgentOrchestrationRunActivationRequest
            {
                RunId = current.RunId,
                ExpectedVersion = current.Version
            }, ct);
            if (!activated.Success || activated.Value is null)
                return FromStoreFailure(activated);
            current = activated.Value;
            activationApplied = activated.Status == AgentOrchestrationStoreStatus.Applied;
        }

        logger.LogInformation(
            "[AgentOrchestrationHttpHook] Invoked graph={GraphId} revision={RevisionId} trigger={TriggerId} sourceEvent={SourceEventId} run={RunId} created={Created}",
            definition.GraphId,
            definition.RevisionId,
            trigger.TriggerId,
            sourceEventId,
            current.RunId,
            created.Status == AgentOrchestrationStoreStatus.Applied);
        return new AgentOrchestrationHttpHookResult
        {
            Kind = AgentOrchestrationHttpHookResultKind.Success,
            Receipt = new AgentOrchestrationHttpHookInvokeReceipt
            {
                TriggerId = trigger.TriggerId,
                SourceEventId = sourceEventId,
                Run = current,
                Created = created.Status == AgentOrchestrationStoreStatus.Applied,
                Activated = activationApplied || current.Status != AgentOrchestrationRunStatus.Draft
            }
        };
    }

    private static AgentOrchestrationHttpHookResult FromStoreFailure(
        AgentOrchestrationStoreResult<AgentOrchestrationRunSnapshot> result)
        => new()
        {
            Kind = result.Status switch
            {
                AgentOrchestrationStoreStatus.NotFound => AgentOrchestrationHttpHookResultKind.NotFound,
                AgentOrchestrationStoreStatus.Conflict => AgentOrchestrationHttpHookResultKind.Conflict,
                _ => AgentOrchestrationHttpHookResultKind.Invalid
            },
            ErrorCode = result.ErrorCode ?? "orchestration.http_hook_store_failed",
            ErrorMessage = result.ErrorMessage ?? "The HTTP hook could not create an orchestration run."
        };

    private static AgentOrchestrationHttpHookResult Invalid(string code, string message)
        => new() { Kind = AgentOrchestrationHttpHookResultKind.Invalid, ErrorCode = code, ErrorMessage = message };

    private static AgentOrchestrationHttpHookResult NotFound(string code, string message)
        => new() { Kind = AgentOrchestrationHttpHookResultKind.NotFound, ErrorCode = code, ErrorMessage = message };

    private static string CreateRunId(
        string graphId,
        string revisionId,
        string triggerId,
        string sourceEventId)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{graphId}\n{revisionId}\n{triggerId}\n{sourceEventId}"));
        return $"http-hook-{Convert.ToHexString(bytes).ToLowerInvariant()[..32]}";
    }

    private static string? ResolveContentType(
        AgentOrchestrationDataContract contract,
        JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.String &&
            contract.MediaTypes.Any(media => string.Equals(media, "text/plain", StringComparison.OrdinalIgnoreCase)))
        {
            return "text/plain";
        }

        return contract.MediaTypes.FirstOrDefault(media => !media.Contains('*', StringComparison.Ordinal))
            ?? "application/json";
    }

    internal static bool TryResolveSourcePath(JsonElement payload, string? sourcePath, out JsonElement value)
    {
        var path = string.IsNullOrWhiteSpace(sourcePath) ? "$" : sourcePath.Trim();
        value = payload;
        if (path == "$")
            return true;
        if (!path.StartsWith('$'))
            return false;

        var index = 1;
        while (index < path.Length)
        {
            if (path[index] == '.')
            {
                index++;
                var start = index;
                while (index < path.Length && path[index] is not '.' and not '[')
                    index++;
                if (start == index || value.ValueKind != JsonValueKind.Object ||
                    !value.TryGetProperty(path[start..index], out value))
                {
                    return false;
                }
                continue;
            }
            if (path[index] == '[')
            {
                var close = path.IndexOf(']', index + 1);
                if (close < 0 || value.ValueKind != JsonValueKind.Array ||
                    !int.TryParse(path[(index + 1)..close], out var arrayIndex) ||
                    arrayIndex < 0 || arrayIndex >= value.GetArrayLength())
                {
                    return false;
                }
                value = value[arrayIndex];
                index = close + 1;
                continue;
            }
            return false;
        }

        return true;
    }

    private static bool IdEquals(string? left, string? right)
        => !string.IsNullOrWhiteSpace(left) &&
           !string.IsNullOrWhiteSpace(right) &&
           string.Equals(left.Trim(), right.Trim(), StringComparison.OrdinalIgnoreCase);
}
