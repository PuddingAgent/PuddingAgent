namespace PuddingCode.Models;

public static class MemoryWriteIntents
{
    public const string ReuseExisting = "reuse_existing";
    public const string AppendNew = "append_new";
    public const string SupersedeExisting = "supersede_existing";
    public const string MergeCandidates = "merge_candidates";
    public const string Archive = "archive";
    public const string DeleteRequested = "delete_requested";
    public const string UpdateIndex = "update_index";
    public const string UpdateSkillPointer = "update_skill_pointer";
}

public static class MemoryWriteExecutionModes
{
    public const string ValidateOnly = "validate_only";
    public const string DryRun = "dry_run";
    public const string Execute = "execute";
}

public static class MemoryWriteSourceKinds
{
    public const string RuntimeTool = "runtime_tool";
    public const string SubconsciousPlan = "subconscious_plan";
    public const string Admin = "admin";
    public const string Migration = "migration";
    public const string Test = "test";
}

public static class MemoryWriteResultStatuses
{
    public const string Accepted = "accepted";
    public const string DryRun = "dry_run";
    public const string Executed = "executed";
    public const string Reused = "reused";
    public const string Quarantined = "quarantined";
    public const string Rejected = "rejected";
}

public static class MemoryWriteValidationErrors
{
    public const string MissingSource = "missing_source";
    public const string MissingSourceIdentity = "missing_source_identity";
    public const string MissingRequiredField = "missing_required_field";
    public const string CrossWorkspaceReference = "cross_workspace_reference";
    public const string UnsupportedIntent = "unsupported_intent";
    public const string UnsupportedMode = "unsupported_mode";
    public const string AutonomousDeleteNotAllowed = "autonomous_delete_not_allowed";
}

public sealed record MemoryWriteCommand
{
    public required string CommandId { get; init; }
    public required string WorkspaceId { get; init; }
    public required string Intent { get; init; }
    public required MemoryWriteSource Source { get; init; }
    public IReadOnlyList<MemoryWriteCandidate> Candidates { get; init; } = [];
    public MemoryWriteCandidate? Target { get; init; }
    public MemoryWritePayload? Payload { get; init; }
    public string Mode { get; init; } = MemoryWriteExecutionModes.DryRun;
    public IReadOnlyDictionary<string, string> Metadata { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);
}

public sealed record MemoryWriteSource
{
    public required string SourceKind { get; init; }
    public string? SessionId { get; init; }
    public string? HookEventId { get; init; }
    public string? SubconsciousJobId { get; init; }
    public string? PlanId { get; init; }
    public string? OperationId { get; init; }
    public string? ToolCallId { get; init; }
    public string? AdminUserId { get; init; }
    public string? AgentId { get; init; }
    public string? AgentTemplateId { get; init; }
    public string? MemoryLibraryId { get; init; }
}

public sealed record MemoryWriteCandidate
{
    public required string WorkspaceId { get; init; }
    public string? BookId { get; init; }
    public string? ChapterId { get; init; }
    public string? FactId { get; init; }
    public string? PointerId { get; init; }

    public string? StableReferenceId => ChapterId ?? FactId ?? PointerId ?? BookId;
}

public sealed record MemoryWritePayload
{
    public string? Title { get; init; }
    public string? Content { get; init; }
    public double Confidence { get; init; } = 1.0;
    public string? Rationale { get; init; }
    public IReadOnlyList<string> RiskFlags { get; init; } = [];
}

public sealed record MemoryWriteValidationResult
{
    public bool IsValid => Errors.Count == 0;
    public IReadOnlyList<MemoryWriteValidationError> Errors { get; init; } = [];
}

public sealed record MemoryWriteValidationError(string Code, string Message);

public sealed record MemoryWriteResultEnvelope
{
    public string Schema { get; init; } = "pudding.memory_write_result.v1";
    public required string CommandId { get; init; }
    public required string WorkspaceId { get; init; }
    public required string Status { get; init; }
    public required string Mode { get; init; }
    public required string Intent { get; init; }
    public string? Decision { get; init; }
    public string? BookId { get; init; }
    public string? ChapterId { get; init; }
    public string? SupersededChapterId { get; init; }
    public IReadOnlyList<string> ErrorCodes { get; init; } = [];
    public IReadOnlyDictionary<string, string> Metadata { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);
}

public sealed class MemoryWriteCommandValidator
{
    private static readonly HashSet<string> SupportedIntents = new(StringComparer.Ordinal)
    {
        MemoryWriteIntents.ReuseExisting,
        MemoryWriteIntents.AppendNew,
        MemoryWriteIntents.SupersedeExisting,
        MemoryWriteIntents.MergeCandidates,
        MemoryWriteIntents.Archive,
        MemoryWriteIntents.DeleteRequested,
        MemoryWriteIntents.UpdateIndex,
        MemoryWriteIntents.UpdateSkillPointer,
    };

    private static readonly HashSet<string> SupportedModes = new(StringComparer.Ordinal)
    {
        MemoryWriteExecutionModes.ValidateOnly,
        MemoryWriteExecutionModes.DryRun,
        MemoryWriteExecutionModes.Execute,
    };

    public MemoryWriteValidationResult Validate(MemoryWriteCommand command)
    {
        var errors = new List<MemoryWriteValidationError>();

        Require(command.CommandId, "commandId", errors);
        Require(command.WorkspaceId, "workspaceId", errors);
        Require(command.Intent, "intent", errors);
        Require(command.Mode, "mode", errors);

        if (!SupportedIntents.Contains(command.Intent))
        {
            errors.Add(new MemoryWriteValidationError(
                MemoryWriteValidationErrors.UnsupportedIntent,
                $"Unsupported memory write intent: {command.Intent}"));
        }

        if (!SupportedModes.Contains(command.Mode))
        {
            errors.Add(new MemoryWriteValidationError(
                MemoryWriteValidationErrors.UnsupportedMode,
                $"Unsupported memory write mode: {command.Mode}"));
        }

        ValidateSource(command.Source, errors);
        ValidateReference(command.Target, command.WorkspaceId, errors);
        foreach (var candidate in command.Candidates)
            ValidateReference(candidate, command.WorkspaceId, errors);

        if (command.Intent == MemoryWriteIntents.AppendNew)
            Require(command.Payload?.Content, "payload.content", errors);

        if (command.Intent == MemoryWriteIntents.SupersedeExisting)
        {
            Require(command.Target?.ChapterId, "target.chapterId", errors);
            Require(command.Payload?.Content, "payload.content", errors);
        }

        if (command.Intent == MemoryWriteIntents.ReuseExisting)
            Require(command.Target?.ChapterId, "target.chapterId", errors);

        if (command.Intent == MemoryWriteIntents.DeleteRequested)
        {
            errors.Add(new MemoryWriteValidationError(
                MemoryWriteValidationErrors.AutonomousDeleteNotAllowed,
                "delete_requested cannot execute through the autonomous memory maintenance path."));
        }

        return new MemoryWriteValidationResult { Errors = errors };
    }

    private static void ValidateSource(MemoryWriteSource? source, List<MemoryWriteValidationError> errors)
    {
        if (source is null)
        {
            errors.Add(new MemoryWriteValidationError(
                MemoryWriteValidationErrors.MissingSource,
                "Memory write source is required."));
            return;
        }

        Require(source.SourceKind, "source.sourceKind", errors);

        if (source.SourceKind == MemoryWriteSourceKinds.SubconsciousPlan
            && (string.IsNullOrWhiteSpace(source.SubconsciousJobId)
                || string.IsNullOrWhiteSpace(source.PlanId)
                || string.IsNullOrWhiteSpace(source.OperationId)
                || string.IsNullOrWhiteSpace(source.AgentId)))
        {
            errors.Add(new MemoryWriteValidationError(
                MemoryWriteValidationErrors.MissingSourceIdentity,
                "subconscious_plan source requires subconsciousJobId, planId, operationId and agentId."));
        }

        if (source.SourceKind == MemoryWriteSourceKinds.RuntimeTool
            && string.IsNullOrWhiteSpace(source.SessionId)
            && string.IsNullOrWhiteSpace(source.ToolCallId))
        {
            errors.Add(new MemoryWriteValidationError(
                MemoryWriteValidationErrors.MissingSourceIdentity,
                "runtime_tool source requires sessionId or toolCallId."));
        }

        if (source.SourceKind == MemoryWriteSourceKinds.Admin
            && string.IsNullOrWhiteSpace(source.AdminUserId))
        {
            errors.Add(new MemoryWriteValidationError(
                MemoryWriteValidationErrors.MissingSourceIdentity,
                "admin source requires adminUserId."));
        }
    }

    private static void ValidateReference(
        MemoryWriteCandidate? reference,
        string workspaceId,
        List<MemoryWriteValidationError> errors)
    {
        if (reference is null)
            return;

        if (!string.Equals(reference.WorkspaceId, workspaceId, StringComparison.Ordinal))
        {
            errors.Add(new MemoryWriteValidationError(
                MemoryWriteValidationErrors.CrossWorkspaceReference,
                "Memory write reference workspace does not match command workspace."));
        }
    }

    private static void Require(
        string? value,
        string fieldName,
        List<MemoryWriteValidationError> errors)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            errors.Add(new MemoryWriteValidationError(
                MemoryWriteValidationErrors.MissingRequiredField,
                $"Required field is missing: {fieldName}"));
        }
    }
}
