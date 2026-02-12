using System.Text.Json;
using Microsoft.Extensions.Logging;
using PuddingCode.Abstractions;
using PuddingCode.Models;
using PuddingCode.Observability;
using PuddingCode.Platform;

namespace PuddingRuntime.Services;

public sealed record SubconsciousPlanGenerationRequest
{
    public required string WorkspaceId { get; init; }
    public required string SessionId { get; init; }
    public required SubconsciousMemoryScope MemoryScope { get; init; }
    public required string EvidenceSummary { get; init; }
    public string? AgentId { get; init; }
    public string? AgentTemplateId { get; init; }
    public string? HookEventId { get; init; }
    public string? SubconsciousJobId { get; init; }
    public IReadOnlyList<MemoryPlanReference> CandidateReads { get; init; } = [];
    public IReadOnlySet<string> AllowedReferenceIds { get; init; } =
        new HashSet<string>(StringComparer.Ordinal);
    public double MinimumOperationConfidence { get; init; } = 0.7;
    public MemoryLlmConfig? MemoryLlmConfig { get; init; }
}

public sealed record SubconsciousPlanGenerationResult
{
    public required string RawResponse { get; init; }
    public MemoryMaintenancePlan? Plan { get; init; }
    public required MemoryMaintenancePlanValidationResult Validation { get; init; }
    public bool DryRun { get; init; } = true;

    public SubconsciousJobResultEnvelope ToJobResultEnvelope(
        IReadOnlyList<MemoryWriteResultEnvelope>? memoryWriteResults = null)
    {
        var metadata = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!string.IsNullOrWhiteSpace(Plan?.WorkspaceId))
            metadata["workspace_id"] = Plan!.WorkspaceId;
        if (!string.IsNullOrWhiteSpace(Plan?.Source.SessionId))
            metadata["session_id"] = Plan!.Source.SessionId!;
        if (!string.IsNullOrWhiteSpace(Plan?.Source.HookEventId))
            metadata["hook_event_id"] = Plan!.Source.HookEventId!;
        if (!string.IsNullOrWhiteSpace(Plan?.Source.SubconsciousJobId))
            metadata["subconscious_job_id"] = Plan!.Source.SubconsciousJobId!;
        if (!string.IsNullOrWhiteSpace(Plan?.Source.AgentId))
            metadata["agent_id"] = Plan!.Source.AgentId!;
        if (!string.IsNullOrWhiteSpace(Plan?.Source.AgentTemplateId))
            metadata["agent_template_id"] = Plan!.Source.AgentTemplateId!;
        if (!string.IsNullOrWhiteSpace(Plan?.Source.MemoryLibraryId))
            metadata["memory_library_id"] = Plan!.Source.MemoryLibraryId!;
        var decision = DecideNextAction(Validation);

        return new SubconsciousJobResultEnvelope
        {
            Kind = SubconsciousJobResultKinds.MemoryMaintenancePlanDryRun,
            Status = decision.Status,
            Decision = decision.Decision,
            NextAction = decision.NextAction,
            PlanId = Plan?.PlanId,
            Valid = Validation.IsValid,
            OperationCount = Plan?.Operations.Count ?? 0,
            ErrorCount = Validation.Errors.Count,
            ErrorCodes = Validation.Errors.Select(e => e.Code).Distinct(StringComparer.Ordinal).ToArray(),
            Summary = Validation.IsValid
                ? "Dry-run memory maintenance plan accepted."
                : "Dry-run memory maintenance plan rejected.",
            MemoryWriteResults = memoryWriteResults ?? [],
            Metadata = metadata,
        };
    }

    private static (string Status, string Decision, string NextAction) DecideNextAction(
        MemoryMaintenancePlanValidationResult validation)
    {
        if (validation.IsValid)
        {
            return (
                SubconsciousJobResultStatuses.Accepted,
                SubconsciousJobResultDecisions.AcceptForExecution,
                SubconsciousJobResultNextActions.EnqueueForExecution);
        }

        var errorCodes = validation.Errors.Select(e => e.Code).ToHashSet(StringComparer.Ordinal);
        if (errorCodes.Contains(MemoryMaintenancePlanValidationErrors.LowConfidence))
        {
            return (
                SubconsciousJobResultStatuses.Quarantined,
                SubconsciousJobResultDecisions.DeferForRecheck,
                SubconsciousJobResultNextActions.CompleteQuarantined);
        }

        if (errorCodes.Contains(MemoryMaintenancePlanValidationErrors.InvalidJson))
        {
            return (
                SubconsciousJobResultStatuses.Rejected,
                SubconsciousJobResultDecisions.RetryLater,
                SubconsciousJobResultNextActions.RetryJob);
        }

        return (
            SubconsciousJobResultStatuses.Rejected,
            SubconsciousJobResultDecisions.RejectComplete,
            SubconsciousJobResultNextActions.CompleteRejected);
    }
}

/// <summary>
/// Generates and validates subconscious memory maintenance plans without executing them.
/// </summary>
public sealed class SubconsciousPlanGenerationService
{
    private const int RawPreviewMaxChars = 500;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = false,
    };

    private readonly IMemoryLlmClient _memoryLlmClient;
    private readonly MemoryMaintenancePlanValidator _validator;
    private readonly ITelemetryMetricSink? _telemetrySink;
    private readonly IRuntimeActivitySink? _activitySink;
    private readonly ILogger<SubconsciousPlanGenerationService>? _logger;

    public SubconsciousPlanGenerationService(
        IMemoryLlmClient memoryLlmClient,
        MemoryMaintenancePlanValidator validator,
        ITelemetryMetricSink? telemetrySink = null,
        IRuntimeActivitySink? activitySink = null,
        ILogger<SubconsciousPlanGenerationService>? logger = null)
    {
        _memoryLlmClient = memoryLlmClient;
        _validator = validator;
        _telemetrySink = telemetrySink;
        _activitySink = activitySink;
        _logger = logger;
    }

    public async Task<SubconsciousPlanGenerationResult> GenerateDryRunAsync(
        SubconsciousPlanGenerationRequest request,
        CancellationToken ct = default)
    {
        ValidateRequest(request);

        var systemPrompt = BuildSystemPrompt();
        var userPrompt = BuildUserPrompt(request);
        var raw = await _memoryLlmClient.ChatWithScopedConfigAsync(
            systemPrompt,
            userPrompt,
            request.MemoryLlmConfig,
            request.MemoryScope,
            tools: null,
            ct);

        var context = new MemoryMaintenancePlanValidationContext
        {
            WorkspaceId = request.WorkspaceId,
            MemoryScope = request.MemoryScope,
            AllowedReferenceIds = request.AllowedReferenceIds,
            MinimumOperationConfidence = request.MinimumOperationConfidence,
        };
        var validation = _validator.ValidateJson(raw, context);
        var plan = TryDeserializePlan(raw);
        var result = new SubconsciousPlanGenerationResult
        {
            RawResponse = raw,
            Plan = validation.IsValid ? plan : null,
            Validation = validation,
            DryRun = true,
        };

        if (!validation.IsValid)
        {
            _logger?.LogWarning(
                "[SubconsciousPlan] Raw plan rejected workspace={WorkspaceId} session={SessionId} rawLen={RawLen} rawPreview={RawPreview}",
                request.WorkspaceId,
                request.SessionId,
                raw?.Length ?? 0,
                BuildRawPreview(raw));
        }

        await RecordValidationAsync(request, result, ct);
        return result;
    }

    private static void ValidateRequest(SubconsciousPlanGenerationRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.WorkspaceId))
            throw new ArgumentException("WorkspaceId is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.SessionId))
            throw new ArgumentException("SessionId is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.EvidenceSummary))
            throw new ArgumentException("EvidenceSummary is required.", nameof(request));
        if (request.MemoryScope is null)
            throw new ArgumentException("MemoryScope is required.", nameof(request));
        if (!string.Equals(request.MemoryScope.WorkspaceId, request.WorkspaceId, StringComparison.Ordinal))
            throw new ArgumentException("MemoryScope workspace must match request workspace.", nameof(request));
        if (!string.Equals(request.MemoryScope.SessionId, request.SessionId, StringComparison.Ordinal))
            throw new ArgumentException("MemoryScope session must match request session.", nameof(request));
        if (!string.IsNullOrWhiteSpace(request.AgentId)
            && !string.Equals(request.MemoryScope.AgentId, request.AgentId, StringComparison.Ordinal))
        {
            throw new ArgumentException("MemoryScope agent must match request agent.", nameof(request));
        }
        if (!string.IsNullOrWhiteSpace(request.AgentTemplateId)
            && !string.Equals(request.MemoryScope.AgentTemplateId, request.AgentTemplateId, StringComparison.Ordinal))
        {
            throw new ArgumentException("MemoryScope agent template must match request agent template.", nameof(request));
        }
    }

    private static string BuildSystemPrompt() =>
        """
        You are Pudding's subconscious memory planner.
        Return only one JSON MemoryMaintenancePlan object at the root. Do not wrap it in another object.
        Required root fields: planId, workspaceId, source, operations, confidence, rationale.
        Required source fields: workspaceId, sessionId, subconsciousJobId, agentId, agentTemplateId.
        Do not write memory. Do not call tools. Do not invent candidate IDs.
        Supported actions: reuse_existing, append_new, supersede_existing, merge_candidates, deprecate, delete, update_index, update_skill_pointer.
        Every operation must include operationId, action, confidence, and rationale.
        If the evidence contains a stable user preference, durable decision, durable constraint, or project rule, emit append_new with proposedContent.
        Do not return an empty operations array unless there is truly no durable memory candidate.
        References must stay inside the provided workspace and candidate set.
        """;

    private static string BuildUserPrompt(SubconsciousPlanGenerationRequest request)
    {
        var envelope = new
        {
            instruction = "Generate a dry-run MemoryMaintenancePlan for the session evidence.",
            dryRun = true,
            source = new
            {
                workspaceId = request.WorkspaceId,
                sessionId = request.SessionId,
                hookEventId = request.HookEventId,
                subconsciousJobId = request.SubconsciousJobId,
                agentId = request.AgentId,
                agentTemplateId = request.AgentTemplateId,
                memoryLibraryId = request.MemoryScope.MemoryLibraryId,
            },
            memoryScope = request.MemoryScope,
            evidence = request.EvidenceSummary,
            candidateReads = request.CandidateReads,
            allowedReferenceIds = request.AllowedReferenceIds,
            minimumOperationConfidence = request.MinimumOperationConfidence,
            requiredRootShape = new
            {
                planId = "plan-...",
                workspaceId = request.WorkspaceId,
                source = new
                {
                    workspaceId = request.WorkspaceId,
                    sessionId = request.SessionId,
                    hookEventId = request.HookEventId,
                    subconsciousJobId = request.SubconsciousJobId,
                    agentId = request.AgentId,
                    agentTemplateId = request.AgentTemplateId,
                    memoryLibraryId = request.MemoryScope.MemoryLibraryId,
                },
                operations = new[]
                {
                    new
                    {
                        operationId = "op-...",
                        action = "append_new",
                        proposedContent = "Durable memory content from the evidence.",
                        confidence = 0.8,
                        rationale = "Why this is stable and worth remembering.",
                    },
                },
                confidence = 0.8,
                rationale = "Overall plan rationale.",
            },
        };
        return JsonSerializer.Serialize(envelope, JsonOptions);
    }

    private static MemoryMaintenancePlan? TryDeserializePlan(string raw)
    {
        try
        {
            return JsonSerializer.Deserialize<MemoryMaintenancePlan>(raw, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string BuildRawPreview(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return string.Empty;

        var normalized = raw
            .Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal);
        return normalized.Length <= RawPreviewMaxChars
            ? normalized
            : normalized[..RawPreviewMaxChars] + "...";
    }

    private async Task RecordValidationAsync(
        SubconsciousPlanGenerationRequest request,
        SubconsciousPlanGenerationResult result,
        CancellationToken ct)
    {
        var firstError = result.Validation.Errors.FirstOrDefault();
        var status = result.Validation.IsValid
            ? TelemetryMetricStatuses.Succeeded
            : TelemetryMetricStatuses.Failed;
        var trace = RuntimeTraceContext.CreateNew(
            sessionId: request.SessionId,
            workspaceId: request.WorkspaceId,
            eventId: request.HookEventId);
        var dimensions = BuildDimensions(request, result);

        if (_activitySink is not null)
        {
            await _activitySink.RecordAsync(new RuntimeActivity
            {
                Trace = trace,
                Component = RuntimeActivityComponents.Memory,
                Operation = "memory_maintenance_plan.validate",
                Status = result.Validation.IsValid
                    ? RuntimeActivityStatuses.Succeeded
                    : RuntimeActivityStatuses.Failed,
                Severity = result.Validation.IsValid ? "info" : "warning",
                Summary = result.Validation.IsValid
                    ? "Memory maintenance plan validation succeeded."
                    : "Memory maintenance plan validation failed.",
                Metadata = dimensions,
                ErrorCode = firstError?.Code,
                ErrorMessage = firstError?.Message,
            }, ct);
        }

        if (_telemetrySink is not null)
        {
            await _telemetrySink.RecordAsync(new TelemetryMetric
            {
                Trace = trace,
                Source = "pudding.runtime.subconscious_plan_generation",
                Category = TelemetryMetricCategories.Memory,
                Name = "memory_maintenance_plan.validation",
                Status = status,
                CountValue = 1,
                Unit = "plan",
                Severity = result.Validation.IsValid ? "info" : "warning",
                Summary = result.Validation.IsValid
                    ? "Memory maintenance plan validation succeeded."
                    : "Memory maintenance plan validation failed.",
                Dimensions = dimensions,
                ErrorCode = firstError?.Code,
                ErrorMessage = firstError?.Message,
            }, ct);
        }

        if (!result.Validation.IsValid)
        {
            _logger?.LogWarning(
                "[SubconsciousPlan] Dry-run validation failed workspace={WorkspaceId} session={SessionId} error={ErrorCode}",
                request.WorkspaceId,
                request.SessionId,
                firstError?.Code);
        }
    }

    private static IReadOnlyDictionary<string, string> BuildDimensions(
        SubconsciousPlanGenerationRequest request,
        SubconsciousPlanGenerationResult result)
    {
        var dimensions = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["workspace_id"] = request.WorkspaceId,
            ["session_id"] = request.SessionId,
            ["dry_run"] = result.DryRun ? "true" : "false",
            ["valid"] = result.Validation.IsValid ? "true" : "false",
            ["operation_count"] = (result.Plan?.Operations.Count ?? 0).ToString(),
            ["candidate_count"] = request.CandidateReads.Count.ToString(),
            ["error_count"] = result.Validation.Errors.Count.ToString(),
        };

        AddIfPresent(dimensions, "agent_id", request.AgentId);
        AddIfPresent(dimensions, "agent_template_id", request.AgentTemplateId);
        AddIfPresent(dimensions, "memory_library_id", request.MemoryScope.MemoryLibraryId);
        AddIfPresent(dimensions, "hook_event_id", request.HookEventId);
        AddIfPresent(dimensions, "subconscious_job_id", request.SubconsciousJobId);
        AddIfPresent(dimensions, "plan_id", result.Plan?.PlanId);
        AddIfPresent(dimensions, "first_error_code", result.Validation.Errors.FirstOrDefault()?.Code);
        return dimensions;
    }

    private static void AddIfPresent(Dictionary<string, string> dimensions, string key, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
            dimensions[key] = value;
    }
}
