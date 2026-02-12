using System.Text.Json;
using PuddingCode.Tools;

namespace PuddingRuntime.Services.Tools;

/// <summary>Builds the clean-room prompt pair for automatic approval review.</summary>
public static class ToolApprovalPromptBuilder
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    public static ToolApprovalPrompt Build(
        ToolApprovalTicketRequest request,
        ToolApprovalIdentity identity,
        ToolDescriptor descriptor)
    {
        var payload = new
        {
            approvalRequest = request,
            identity = new
            {
                identity.WorkspaceId,
                identity.SessionId,
                identity.AgentInstanceId,
                identity.UserId,
            },
            toolDescriptor = new
            {
                descriptor.ToolId,
                descriptor.Name,
                descriptor.Description,
                category = descriptor.Category.ToString(),
                permissionLevel = descriptor.PermissionLevel.ToString(),
                safety = descriptor.Safety.ToString(),
            },
            expectedResponseJson = new
            {
                decision = "approved | denied | need_human",
                reason = "short reason",
                allowedScope = "once | session | timed | null",
                allowedDurationMinutes = (int?)null,
                requiresHumanAuthorization = false,
                checklistFindings = Array.Empty<string>(),
                missingRequirements = Array.Empty<string>(),
                allowlistProposals = new[]
                {
                    new
                    {
                        toolId = "tool id or null to use the approved request tool",
                        command = "exact reusable command or null",
                        argumentsJson = "exact reusable arguments JSON or null",
                        reason = "why this exact reusable shape is safe",
                    },
                },
                recommendedFix = (string?)null,
            },
        };

        return new ToolApprovalPrompt
        {
            SystemPrompt = """
            You are performing a single clean-room approval review for a high-risk tool request.
            Do not use chat history, prior memory, hidden context, or assumptions not present in the JSON payload.
            Return strict JSON only. Do not include markdown.
            Refuse or require human authorization when facts, scope, rollback, consent, or safety checks are insufficient.
            A job ticket may contain multiple operationSteps. Review every step independently.
            Approve a job only when every step has a concrete tool id, exact requested arguments or an exact command, bounded targets, expected effect, and rollback or stop condition.
            Deny the whole job if any step mixes in unapproved destructive, irreversible, secret-exposing, or out-of-scope behavior; include the failing step number and reason.
            After approving a ticket, include allowlistProposals only for exact command and parameter shapes that are safe to reuse.
            Return an empty allowlistProposals array or omit it when no reusable allowlist entry is appropriate.
            Only propose reusable allowlist entries when the command and parameters are narrow, read-only or clearly reversible, and workspace-scoped.
            Never propose broad shell patterns, destructive operations, secret-exposing operations, or entries that rely on hidden context.
            """,
            UserPrompt = JsonSerializer.Serialize(payload, JsonOptions),
        };
    }
}

public sealed record ToolApprovalPrompt
{
    public required string SystemPrompt { get; init; }
    public required string UserPrompt { get; init; }
}
