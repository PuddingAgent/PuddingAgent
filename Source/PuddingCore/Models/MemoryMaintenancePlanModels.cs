using System.Text.Json;
using PuddingCode.Platform;

namespace PuddingCode.Models;

/// <summary>
/// Actions a subconscious memory maintenance plan may request.
/// The validator authorizes these actions; execution belongs to the write coordinator.
/// </summary>
public static class MemoryMaintenanceActions
{
    public const string ReuseExisting = "reuse_existing";
    public const string AppendNew = "append_new";
    public const string SupersedeExisting = "supersede_existing";
    public const string MergeCandidates = "merge_candidates";
    public const string Deprecate = "deprecate";
    public const string Delete = "delete";
    public const string UpdateIndex = "update_index";
    public const string UpdateSkillPointer = "update_skill_pointer";
}

public sealed record MemoryMaintenancePlan
{
    public required string PlanId { get; init; }
    public required string WorkspaceId { get; init; }
    public required MemoryMaintenancePlanSource Source { get; init; }
    public IReadOnlyList<MemoryPlanReference> CandidateReads { get; init; } = [];
    public required IReadOnlyList<MemoryMaintenanceOperation> Operations { get; init; }
    public double Confidence { get; init; }
    public string? Rationale { get; init; }
    public IReadOnlyList<string> RiskFlags { get; init; } = [];
}

public sealed record MemoryMaintenancePlanSource
{
    public required string WorkspaceId { get; init; }
    public string? SessionId { get; init; }
    public string? HookEventId { get; init; }
    public string? SubconsciousJobId { get; init; }
    public string? AgentId { get; init; }
    public string? AgentTemplateId { get; init; }
    public string? MemoryLibraryId { get; init; }
}

public sealed record MemoryMaintenanceOperation
{
    public required string OperationId { get; init; }
    public required string Action { get; init; }
    public MemoryPlanReference? Target { get; init; }
    public IReadOnlyList<MemoryPlanReference> Sources { get; init; } = [];
    public string? ProposedTitle { get; init; }
    public string? ProposedContent { get; init; }
    public double Confidence { get; init; }
    public string? Rationale { get; init; }
    public IReadOnlyList<string> RiskFlags { get; init; } = [];
}

public sealed record MemoryPlanReference
{
    public required string WorkspaceId { get; init; }
    public string? BookId { get; init; }
    public string? ChapterId { get; init; }
    public string? FactId { get; init; }
    public string? PointerId { get; init; }

    public string? StableReferenceId =>
        ChapterId ?? FactId ?? PointerId ?? BookId;
}

public sealed record MemoryMaintenancePlanValidationContext
{
    public required string WorkspaceId { get; init; }
    public SubconsciousMemoryScope? MemoryScope { get; init; }
    public IReadOnlySet<string> AllowedReferenceIds { get; init; } =
        new HashSet<string>(StringComparer.Ordinal);
    public double MinimumOperationConfidence { get; init; } = 0.7;
}

public sealed record MemoryMaintenancePlanValidationResult
{
    public bool IsValid => Errors.Count == 0;
    public IReadOnlyList<MemoryMaintenancePlanValidationError> Errors { get; init; } = [];
}

public sealed record MemoryMaintenancePlanValidationError(
    string Code,
    string Message,
    string? OperationId = null);

public static class MemoryMaintenancePlanValidationErrors
{
    public const string InvalidJson = "invalid_json";
    public const string MissingRequiredField = "missing_required_field";
    public const string CrossWorkspaceReference = "cross_workspace_reference";
    public const string CrossAgentReference = "cross_agent_reference";
    public const string CrossMemoryLibraryReference = "cross_memory_library_reference";
    public const string CrossSessionReference = "cross_session_reference";
    public const string LowConfidence = "low_confidence";
    public const string UnknownReference = "unknown_reference";
    public const string UnsupportedAction = "unsupported_action";
}

public sealed class MemoryMaintenancePlanValidator
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
    };

    private static readonly HashSet<string> SupportedActions = new(StringComparer.Ordinal)
    {
        MemoryMaintenanceActions.ReuseExisting,
        MemoryMaintenanceActions.AppendNew,
        MemoryMaintenanceActions.SupersedeExisting,
        MemoryMaintenanceActions.MergeCandidates,
        MemoryMaintenanceActions.Deprecate,
        MemoryMaintenanceActions.Delete,
        MemoryMaintenanceActions.UpdateIndex,
        MemoryMaintenanceActions.UpdateSkillPointer,
    };

    public MemoryMaintenancePlanValidationResult ValidateJson(
        string json,
        MemoryMaintenancePlanValidationContext context)
    {
        try
        {
            var plan = JsonSerializer.Deserialize<MemoryMaintenancePlan>(json, JsonOptions);
            return plan is null
                ? Invalid(MemoryMaintenancePlanValidationErrors.InvalidJson, "Plan JSON deserialized to null.")
                : Validate(plan, context);
        }
        catch (JsonException ex)
        {
            return Invalid(MemoryMaintenancePlanValidationErrors.InvalidJson, ex.Message);
        }
    }

    public MemoryMaintenancePlanValidationResult Validate(
        MemoryMaintenancePlan plan,
        MemoryMaintenancePlanValidationContext context)
    {
        var errors = new List<MemoryMaintenancePlanValidationError>();
        Require(plan.PlanId, "planId", errors);
        Require(plan.WorkspaceId, "workspaceId", errors);

        if (!string.Equals(plan.WorkspaceId, context.WorkspaceId, StringComparison.Ordinal))
        {
            errors.Add(new MemoryMaintenancePlanValidationError(
                MemoryMaintenancePlanValidationErrors.CrossWorkspaceReference,
                "Plan workspace does not match validation context."));
        }

        if (!string.Equals(plan.Source.WorkspaceId, context.WorkspaceId, StringComparison.Ordinal))
        {
            errors.Add(new MemoryMaintenancePlanValidationError(
                MemoryMaintenancePlanValidationErrors.CrossWorkspaceReference,
                "Plan source workspace does not match validation context."));
        }

        ValidateSourceScope(plan.Source, context, errors);

        if (plan.Operations.Count == 0)
        {
            errors.Add(new MemoryMaintenancePlanValidationError(
                MemoryMaintenancePlanValidationErrors.MissingRequiredField,
                "Plan must contain at least one operation."));
        }

        foreach (var operation in plan.Operations)
        {
            ValidateOperation(operation, context, errors);
        }

        return new MemoryMaintenancePlanValidationResult { Errors = errors };
    }

    private static void ValidateSourceScope(
        MemoryMaintenancePlanSource source,
        MemoryMaintenancePlanValidationContext context,
        List<MemoryMaintenancePlanValidationError> errors)
    {
        var scope = context.MemoryScope;
        if (scope is null)
            return;

        if (!string.Equals(source.WorkspaceId, scope.WorkspaceId, StringComparison.Ordinal))
        {
            errors.Add(new MemoryMaintenancePlanValidationError(
                MemoryMaintenancePlanValidationErrors.CrossWorkspaceReference,
                "Plan source workspace does not match subconscious memory scope."));
        }

        if (!string.Equals(source.AgentId, scope.AgentId, StringComparison.Ordinal))
        {
            errors.Add(new MemoryMaintenancePlanValidationError(
                MemoryMaintenancePlanValidationErrors.CrossAgentReference,
                "Plan source agent does not match subconscious memory scope."));
        }

        if (!string.IsNullOrWhiteSpace(scope.AgentTemplateId)
            && !string.Equals(source.AgentTemplateId, scope.AgentTemplateId, StringComparison.Ordinal))
        {
            errors.Add(new MemoryMaintenancePlanValidationError(
                MemoryMaintenancePlanValidationErrors.CrossAgentReference,
                "Plan source agent template does not match subconscious memory scope."));
        }

        if (!string.Equals(source.SessionId, scope.SessionId, StringComparison.Ordinal))
        {
            errors.Add(new MemoryMaintenancePlanValidationError(
                MemoryMaintenancePlanValidationErrors.CrossSessionReference,
                "Plan source session does not match subconscious memory scope."));
        }

        if (!string.IsNullOrWhiteSpace(scope.MemoryLibraryId)
            && !string.Equals(source.MemoryLibraryId, scope.MemoryLibraryId, StringComparison.Ordinal))
        {
            errors.Add(new MemoryMaintenancePlanValidationError(
                MemoryMaintenancePlanValidationErrors.CrossMemoryLibraryReference,
                "Plan source memory library does not match subconscious memory scope."));
        }
    }

    private static MemoryMaintenancePlanValidationResult Invalid(string code, string message) =>
        new()
        {
            Errors =
            [
                new MemoryMaintenancePlanValidationError(code, message),
            ],
        };

    private static void ValidateOperation(
        MemoryMaintenanceOperation operation,
        MemoryMaintenancePlanValidationContext context,
        List<MemoryMaintenancePlanValidationError> errors)
    {
        Require(operation.OperationId, "operationId", errors, operation.OperationId);
        Require(operation.Action, "action", errors, operation.OperationId);

        if (!SupportedActions.Contains(operation.Action))
        {
            errors.Add(new MemoryMaintenancePlanValidationError(
                MemoryMaintenancePlanValidationErrors.UnsupportedAction,
                $"Unsupported memory maintenance action: {operation.Action}",
                operation.OperationId));
        }

        if (operation.Confidence < context.MinimumOperationConfidence)
        {
            errors.Add(new MemoryMaintenancePlanValidationError(
                MemoryMaintenancePlanValidationErrors.LowConfidence,
                $"Operation confidence {operation.Confidence:F2} is below minimum {context.MinimumOperationConfidence:F2}.",
                operation.OperationId));
        }

        if (operation.Action == MemoryMaintenanceActions.AppendNew)
        {
            Require(operation.ProposedContent, "proposedContent", errors, operation.OperationId);
        }

        ValidateReference(operation.Target, context, errors, operation.OperationId);
        foreach (var source in operation.Sources)
            ValidateReference(source, context, errors, operation.OperationId);
    }

    private static void ValidateReference(
        MemoryPlanReference? reference,
        MemoryMaintenancePlanValidationContext context,
        List<MemoryMaintenancePlanValidationError> errors,
        string? operationId)
    {
        if (reference is null)
            return;

        if (!string.Equals(reference.WorkspaceId, context.WorkspaceId, StringComparison.Ordinal))
        {
            errors.Add(new MemoryMaintenancePlanValidationError(
                MemoryMaintenancePlanValidationErrors.CrossWorkspaceReference,
                "Memory reference workspace does not match validation context.",
                operationId));
        }

        var stableReferenceId = reference.StableReferenceId;
        if (!string.IsNullOrWhiteSpace(stableReferenceId)
            && context.AllowedReferenceIds.Count > 0
            && !context.AllowedReferenceIds.Contains(stableReferenceId))
        {
            errors.Add(new MemoryMaintenancePlanValidationError(
                MemoryMaintenancePlanValidationErrors.UnknownReference,
                $"Memory reference is outside the allowed candidate set: {stableReferenceId}",
                operationId));
        }
    }

    private static void Require(
        string? value,
        string fieldName,
        List<MemoryMaintenancePlanValidationError> errors,
        string? operationId = null)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            errors.Add(new MemoryMaintenancePlanValidationError(
                MemoryMaintenancePlanValidationErrors.MissingRequiredField,
                $"Required field is missing: {fieldName}",
                operationId));
        }
    }
}
