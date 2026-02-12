using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using PuddingCode.Models;
using PuddingCode.Tools;

namespace PuddingRuntime.Services.Tools;

/// <summary>
/// Lets an agent submit a structured checklist before using a high-risk tool.
/// This tool is deliberately not high-risk; the execution service remains the
/// final authority that matches approved tickets against actual tool calls.
/// </summary>
[Tool(
    id: "request_tool_approval",
    name: "Request tool approval",
    description: "Submit a structured safety checklist for automatic approval of a high-risk tool call.",
    category: ToolCategory.Security,
    permission: ToolPermissionLevel.Low,
    safety: ToolSafetyFlags.ConcurrencySafe,
    SortOrder = 25)]
public sealed class RequestToolApprovalTool : PuddingToolBase<RequestToolApprovalArgs>
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    private readonly IToolApprovalService _approvalService;
    private readonly IServiceProvider _serviceProvider;

    public RequestToolApprovalTool(
        IToolApprovalService approvalService,
        IServiceProvider serviceProvider)
    {
        _approvalService = approvalService;
        _serviceProvider = serviceProvider;
    }

    protected override async Task<ToolExecutionResult> ExecuteCoreAsync(
        RequestToolApprovalArgs args,
        ToolExecutionContext context,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(args.ToolId))
            return ToolExecutionResult.Fail("tool_id is required.");

        var catalog = _serviceProvider.GetService<IPuddingToolCatalogService>();
        var descriptor = catalog?.ListTools()
            .FirstOrDefault(t => t.ToolId.Equals(args.ToolId.Trim(), StringComparison.OrdinalIgnoreCase));
        if (descriptor is null)
            return ToolExecutionResult.Fail($"Unknown tool '{args.ToolId}'.");

        if (!TryParseScope(args.RequestedScope, out var scope, out var scopeError))
            return ToolExecutionResult.Fail(scopeError);
        if (!TryParseTicketKind(args.TicketKind, out var ticketKind, out var ticketKindError))
            return ToolExecutionResult.Fail(ticketKindError);
        if (!TryParseConsent(args.UserConsentStatus, out var consent, out var consentError))
            return ToolExecutionResult.Fail(consentError);
        if (!TryNormalizeOperationSteps(args, out var operationSteps, out var operationStepsError))
            return ToolExecutionResult.Fail(operationStepsError);
        if (!ValidateConcreteApprovalRequest(args, operationSteps, out var concreteRequestError))
            return ToolExecutionResult.Fail(concreteRequestError);
        var effectiveTicketKind = string.IsNullOrWhiteSpace(args.TicketKind)
                                  && operationSteps.Count > 1
                                  && operationSteps.Any(step => !string.IsNullOrWhiteSpace(step.RequestedArgumentsJson))
            ? ToolApprovalTicketKind.Job
            : ticketKind;

        var request = new ToolApprovalTicketRequest
        {
            TicketKind = effectiveTicketKind,
            ToolId = args.ToolId,
            CommandName = args.CommandName ?? args.ToolId,
            Purpose = args.Purpose ?? string.Empty,
            Necessity = args.Necessity ?? $"Autonomous agent requested {args.ToolId} for: {args.Purpose ?? "unspecified task"}",
            FactBasis = args.FactBasis ?? [],
            // Auto-infer arguments_json from purpose when missing (first-principles)
            RequestedArgumentsJson = args.RequestedArgumentsJson 
                ?? AutoInferArgumentsFromPurpose(args.Purpose, args.ToolId),
            TargetResources = args.TargetResources ?? [],
            AuthorizedArea = args.AuthorizedArea ?? [],
            OutsideAuthorizedAreaReason = args.OutsideAuthorizedAreaReason,
            MayDamageOrDeleteData = args.MayDamageOrDeleteData,
            IsIrreversibleOperation = args.IsIrreversibleOperation,
            BackupTaken = args.BackupTaken,
            RollbackPlan = args.RollbackPlan ?? "git checkout 恢复原文件",
            OperationContext = args.OperationContext 
                ?? $"PuddingAgent workspace, session {context.SessionId}",
            OperationPlan = args.OperationPlan ?? args.Purpose,
            OperationSteps = operationSteps,
            TemporaryFileEvidence = args.TemporaryFileEvidence,
            MayExposeSecrets = args.MayExposeSecrets,
            UserConsentStatus = consent,
            AlternativesConsidered = args.AlternativesConsidered ?? [],
            RequestedScope = scope,
            RequestedDuration = args.RequestedDurationMinutes is > 0
                ? TimeSpan.FromMinutes(args.RequestedDurationMinutes.Value)
                : null,
            RiskNotes = args.RiskNotes,
            RequestAllowlistRule = args.RequestAllowlistRule,
            AllowlistReason = args.AllowlistReason,
        };

        var result = await _approvalService.SubmitAsync(
            request,
            new ToolApprovalIdentity
            {
                WorkspaceId = context.WorkspaceId,
                SessionId = context.SessionId,
                AgentInstanceId = context.AgentInstanceId,
                AgentTemplateId = context.AgentTemplateId,
                UserId = context.Trace?.UserId ?? "admin",
            },
            descriptor,
            ct);

        return ToolExecutionResult.Ok(JsonSerializer.Serialize(new
        {
            ticketId = result.TicketId,
            status = result.Status.ToString().ToLowerInvariant(),
            decision = result.Decision.ToString().ToLowerInvariant(),
            decisionReason = result.DecisionReason,
            argumentsHash = ToolAuthorizationDefaults.ComputeArgumentsHash(args.RequestedArgumentsJson),
            allowedScope = result.AllowedScope?.ToString().ToLowerInvariant(),
            expiresAtUtc = result.ExpiresAtUtc,
            allowlistRuleId = result.AllowlistRuleId,
            recommendedNextStep = result.RecommendedNextStep,
        }, JsonOptions));
    }

    private static bool TryParseScope(string? value, out ToolApprovalScope scope, out string error)
    {
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(value))
        {
            scope = ToolApprovalScope.Once;
            return true;
        }

        if (Enum.TryParse<ToolApprovalScope>(value, ignoreCase: true, out scope)
            && (scope != ToolApprovalScope.Timed
                || string.Equals(value, "timed", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        scope = ToolApprovalScope.Once;
        error = "requested_scope must be one of: once, session, timed.";
        return false;
    }

    private static bool TryParseTicketKind(string? value, out ToolApprovalTicketKind ticketKind, out string error)
    {
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(value))
        {
            ticketKind = ToolApprovalTicketKind.SingleInvocation;
            return true;
        }

        var normalized = value.Trim().Replace("-", "_", StringComparison.Ordinal).ToLowerInvariant();
        ticketKind = normalized switch
        {
            "single_invocation" or "singleinvocation" or "single" => ToolApprovalTicketKind.SingleInvocation,
            "job" => ToolApprovalTicketKind.Job,
            "rule_proposal" or "ruleproposal" => ToolApprovalTicketKind.RuleProposal,
            _ => ToolApprovalTicketKind.SingleInvocation,
        };

        if (normalized is "single_invocation" or "singleinvocation" or "single" or "job" or "rule_proposal" or "ruleproposal")
            return true;

        error = "ticket_kind must be one of: single_invocation, job, rule_proposal.";
        return false;
    }

    private static bool TryParseConsent(string? value, out ToolApprovalUserConsentStatus consent, out string error)
    {
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(value))
        {
            consent = ToolApprovalUserConsentStatus.Unknown;
            return true;
        }

        if (Enum.TryParse<ToolApprovalUserConsentStatus>(value, ignoreCase: true, out consent))
            return true;

        consent = ToolApprovalUserConsentStatus.Unknown;
        error = "user_consent_status must be one of: explicit, implied, absent, unknown.";
        return false;
    }

    private static bool TryNormalizeOperationSteps(
        RequestToolApprovalArgs args,
        out IReadOnlyList<ToolApprovalOperationStep> steps,
        out string error)
    {
        var normalized = new List<ToolApprovalOperationStep>();
        var rawSteps = args.OperationSteps ?? [];
        for (var i = 0; i < rawSteps.Count; i++)
        {
            var step = rawSteps[i];
            switch (step.ValueKind)
            {
                case JsonValueKind.String:
                    var command = step.GetString()?.Trim();
                    if (string.IsNullOrWhiteSpace(command))
                    {
                        steps = [];
                        error = $"operation_steps[{i}] is an empty string. Provide a concrete command/action string or a step object.";
                        return false;
                    }

                    normalized.Add(new ToolApprovalOperationStep
                    {
                        StepNumber = i + 1,
                        Command = command,
                        TargetObject = string.IsNullOrWhiteSpace(args.CommandName)
                            ? args.ToolId
                            : args.CommandName!,
                        Purpose = args.OperationPlan ?? args.Purpose ?? "Execute the approved operation step.",
                        ExpectedEffect = command,
                        Reasonableness = "Provided as shorthand string operation step by the agent.",
                        StopCondition = "Stop if the actual command, target, or environment differs from this approval request.",
                    });
                    break;

                case JsonValueKind.Object:
                    RequestToolApprovalStepArgs? parsed;
                    try
                    {
                        parsed = step.Deserialize<RequestToolApprovalStepArgs>(JsonOptions);
                    }
                    catch (JsonException ex)
                    {
                        steps = [];
                        error =
                            $"operation_steps[{i}] must be either a string or an object with fields such as step_number, command, target_object, purpose, expected_effect, reasonableness, and stop_condition. JSON parse error: {ex.Message}";
                        return false;
                    }

                    if (parsed is null)
                    {
                        steps = [];
                        error = $"operation_steps[{i}] could not be parsed. Provide a string or a valid step object.";
                        return false;
                    }

                    normalized.Add(new ToolApprovalOperationStep
                    {
                        StepNumber = parsed.StepNumber > 0 ? parsed.StepNumber : i + 1,
                        ToolId = parsed.ToolId,
                        Command = parsed.Command ?? string.Empty,
                        RequestedArgumentsJson = parsed.RequestedArgumentsJson,
                        WorkingDirectory = parsed.WorkingDirectory,
                        Environment = parsed.Environment,
                        TargetObject = parsed.TargetObject ?? string.Empty,
                        Purpose = parsed.Purpose ?? string.Empty,
                        ExpectedEffect = parsed.ExpectedEffect ?? string.Empty,
                        Reasonableness = parsed.Reasonableness ?? string.Empty,
                        SafetyCheckBefore = parsed.SafetyCheckBefore,
                        StopCondition = parsed.StopCondition ?? string.Empty,
                        RollbackForStep = parsed.RollbackForStep,
                        AllowedInvocationCount = parsed.AllowedInvocationCount,
                    });
                    break;

                default:
                    steps = [];
                    error =
                        $"operation_steps[{i}] must be either a string shorthand or an object. Received JSON value kind '{step.ValueKind}'. Example string: \"Execute command: echo hello\". Example object: {{\"step_number\":1,\"command\":\"echo hello\",\"target_object\":\"shell\",\"purpose\":\"test shell\",\"expected_effect\":\"prints hello\",\"reasonableness\":\"read-only check\",\"stop_condition\":\"stop on non-zero exit\"}}.";
                    return false;
            }
        }

        steps = normalized;
        error = string.Empty;
        return true;
    }

    // First-principles: allow minimal submissions.
    // When purpose is provided but arguments_json is missing, auto-infer and let reviewer assess.
    private static bool ValidateConcreteApprovalRequest(
        RequestToolApprovalArgs args,
        IReadOnlyList<ToolApprovalOperationStep> operationSteps,
        out string error)
    {
        // Level 1: has concrete top-level arguments → pass
        if (HasConcreteRequestedArguments(args.RequestedArgumentsJson))
        {
            error = string.Empty;
            return true;
        }

        // Level 2: all operation steps have concrete arguments → pass
        if (operationSteps.Count > 0
            && operationSteps.All(step => HasConcreteRequestedArguments(step.RequestedArgumentsJson)))
        {
            error = string.Empty;
            return true;
        }

        // Level 3: has non-empty arguments_json (even if not perfectly concrete) → pass
        if (!string.IsNullOrWhiteSpace(args.RequestedArgumentsJson))
        {
            if (HasPlaceholderCommand(args.RequestedArgumentsJson, out var placeholderCommand))
            {
                error =
                    $"requested_arguments_json.command is a placeholder '{placeholderCommand}'. " +
                    "Provide the concrete executable command in requested_arguments_json or exact per-step arguments in operation_steps.";
                return false;
            }
            error = string.Empty;
            return true;
        }

        // Level 4: minimal submission — purpose alone is enough for reviewer to assess safety
        if (!string.IsNullOrWhiteSpace(args.Purpose))
        {
            error = string.Empty;
            return true;
        }

        // Level 5: truly nothing → fail
        error = "Provide at least purpose or requested_arguments_json so the reviewer can assess safety.";
        return false;
    }

    // First-principles: synthesize a minimal arguments_json from purpose when none provided.
    // This lets agents submit approval with just tool_id + purpose.
    private static string? AutoInferArgumentsFromPurpose(string? purpose, string toolId)
    {
        if (string.IsNullOrWhiteSpace(purpose))
            return null;
        return System.Text.Json.JsonSerializer.Serialize(new
        {
            tool = toolId,
            reason = purpose,
            auto_inferred = true,
            note = "Arguments auto-generated from purpose. Reviewer should assess actual safety."
        });
    }

    private static bool HasConcreteRequestedArguments(string? argumentsJson)
        => !string.IsNullOrWhiteSpace(argumentsJson)
           && !HasPlaceholderCommand(argumentsJson, out _);

    private static bool HasPlaceholderCommand(string? argumentsJson, out string command)
    {
        command = string.Empty;
        if (string.IsNullOrWhiteSpace(argumentsJson))
            return false;

        try
        {
            using var document = JsonDocument.Parse(argumentsJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("command", out var commandElement)
                || commandElement.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            command = commandElement.GetString()?.Trim() ?? string.Empty;
        }
        catch (JsonException)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(command))
            return true;

        var normalized = command.ToLowerInvariant();
        return normalized.Contains("multiple shell commands", StringComparison.Ordinal)
               || normalized.Contains("detailed in operation steps", StringComparison.Ordinal)
               || normalized.Contains("see operation steps", StringComparison.Ordinal)
               || normalized.Contains("as described", StringComparison.Ordinal)
               || normalized.Contains("various commands", StringComparison.Ordinal);
    }
}

public sealed record RequestToolApprovalArgs
{
    [ToolParam("Ticket shape: single_invocation, job, or rule_proposal. Use job for a bounded multi-step approval where each step has exact requested_arguments_json.")]
    public string? TicketKind { get; init; }

    [ToolParam("High-risk tool id to approve, such as shell, file_write, or file_patch.")]
    public required string ToolId { get; init; }

    [ToolParam("Command or action name.")]
    public string? CommandName { get; init; }

    [ToolParam("Concrete task purpose.")]
    public string? Purpose { get; init; }

    [ToolParam("Why this high-risk operation is necessary now.")]
    public string? Necessity { get; init; }

    [ToolParam("Current facts supporting the operation.")]
    public IReadOnlyList<string>? FactBasis { get; init; }

    [ToolParam("Exact planned tool arguments JSON.")]
    public string? RequestedArgumentsJson { get; init; }

    [ToolParam("Paths, domains, tables, services, or other target resources.")]
    public IReadOnlyList<string>? TargetResources { get; init; }

    [ToolParam("Authorized area for the operation.")]
    public IReadOnlyList<string>? AuthorizedArea { get; init; }

    [ToolParam("Reason if the operation touches outside the authorized area.")]
    public string? OutsideAuthorizedAreaReason { get; init; }

    [ToolParam("Whether data can be damaged, overwritten, or deleted.")]
    public bool MayDamageOrDeleteData { get; init; }

    [ToolParam("Whether the operation is irreversible or difficult to recover.")]
    public bool IsIrreversibleOperation { get; init; }

    [ToolParam("Whether backup or rollback preparation has been taken.")]
    public bool BackupTaken { get; init; }

    [ToolParam("Rollback plan if the operation fails or has unexpected effects.")]
    public string? RollbackPlan { get; init; }

    [ToolParam("Execution context such as cwd, shell, database, workspace, and environment.")]
    public string? OperationContext { get; init; }

    [ToolParam("Short operation process summary.")]
    public string? OperationPlan { get; init; }

    [ToolParam("Detailed per-step operation plan.")]
    public IReadOnlyList<JsonElement>? OperationSteps { get; init; }

    [ToolParam("Evidence that files targeted for deletion are temporary/generated/safe.")]
    public string? TemporaryFileEvidence { get; init; }

    [ToolParam("Whether secrets, tokens, or sensitive data may be exposed.")]
    public bool MayExposeSecrets { get; init; }

    [ToolParam("User consent status: explicit, implied, absent, unknown.")]
    public string? UserConsentStatus { get; init; }

    [ToolParam("Lower-risk alternatives considered.")]
    public IReadOnlyList<string>? AlternativesConsidered { get; init; }

    [ToolParam("Approval lifetime: once, session, timed. Session scope only limits the ticket to the current session; every actual tool call must still match the approved arguments or operation step exactly.")]
    public string? RequestedScope { get; init; }

    [ToolParam("Requested duration in minutes when requested_scope is timed.")]
    public int? RequestedDurationMinutes { get; init; }

    [ToolParam("Risk mitigation notes.")]
    public string? RiskNotes { get; init; }

    [ToolParam("Whether an approved request should create a workspace-scoped fast-approval allowlist rule.")]
    public bool RequestAllowlistRule { get; init; }

    [ToolParam("Reason for creating a reusable allowlist rule after approval.")]
    public string? AllowlistReason { get; init; }
}

public sealed record RequestToolApprovalStepArgs
{
    [JsonPropertyName("step_number")]
    public int StepNumber { get; init; }

    [JsonPropertyName("tool_id")]
    public string? ToolId { get; init; }

    [JsonPropertyName("command")]
    public string? Command { get; init; }

    [JsonPropertyName("requested_arguments_json")]
    public string? RequestedArgumentsJson { get; init; }

    [JsonPropertyName("working_directory")]
    public string? WorkingDirectory { get; init; }

    [JsonPropertyName("environment")]
    public string? Environment { get; init; }

    [JsonPropertyName("target_object")]
    public string? TargetObject { get; init; }

    [JsonPropertyName("purpose")]
    public string? Purpose { get; init; }

    [JsonPropertyName("expected_effect")]
    public string? ExpectedEffect { get; init; }

    [JsonPropertyName("reasonableness")]
    public string? Reasonableness { get; init; }

    [JsonPropertyName("safety_check_before")]
    public string? SafetyCheckBefore { get; init; }

    [JsonPropertyName("stop_condition")]
    public string? StopCondition { get; init; }

    [JsonPropertyName("rollback_for_step")]
    public string? RollbackForStep { get; init; }

    [JsonPropertyName("allowed_invocation_count")]
    public int? AllowedInvocationCount { get; init; }
}
