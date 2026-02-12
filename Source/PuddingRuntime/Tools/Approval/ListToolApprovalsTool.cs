using System.Text.Json;
using PuddingCode.Models;
using PuddingCode.Tools;

namespace PuddingRuntime.Services.Tools;

/// <summary>Lists automatic approval tickets visible to the current workspace context.</summary>
[Tool(
    id: "list_tool_approvals",
    name: "List tool approvals",
    description: "List automatic approval tickets by status, tool, session, agent, user, or ticket id.",
    category: ToolCategory.Security,
    permission: ToolPermissionLevel.Low,
    safety: ToolSafetyFlags.ReadOnly | ToolSafetyFlags.ConcurrencySafe,
    SortOrder = 26)]
public sealed class ListToolApprovalsTool : PuddingToolBase<ListToolApprovalsArgs>
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private readonly IToolApprovalTicketStore _ticketStore;

    public ListToolApprovalsTool(IToolApprovalTicketStore ticketStore)
    {
        _ticketStore = ticketStore;
    }

    protected override async Task<ToolExecutionResult> ExecuteCoreAsync(
        ListToolApprovalsArgs args,
        ToolExecutionContext context,
        CancellationToken ct)
    {
        if (!TryParseStatus(args.Status, out var status, out var statusError))
            return ToolExecutionResult.Fail(statusError);

        var limit = Math.Clamp(args.Limit ?? 50, 1, 200);
        var workspaceId = string.IsNullOrWhiteSpace(args.WorkspaceId)
            ? context.WorkspaceId
            : args.WorkspaceId.Trim();
        var tickets = await _ticketStore.ListAsync(ct);
        var filtered = tickets
            .Where(t => string.Equals(t.Identity.WorkspaceId, workspaceId, StringComparison.Ordinal))
            .Where(t => string.IsNullOrWhiteSpace(args.TicketId)
                        || string.Equals(t.TicketId, args.TicketId.Trim(), StringComparison.Ordinal))
            .Where(t => string.IsNullOrWhiteSpace(args.ToolId)
                        || string.Equals(t.ToolId, ToolAuthorizationDefaults.NormalizeToolId(args.ToolId), StringComparison.OrdinalIgnoreCase))
            .Where(t => status is null || t.Status == status)
            .Where(t => string.IsNullOrWhiteSpace(args.SessionId)
                        || string.Equals(t.Identity.SessionId, args.SessionId.Trim(), StringComparison.Ordinal))
            .Where(t => string.IsNullOrWhiteSpace(args.AgentInstanceId)
                        || string.Equals(t.Identity.AgentInstanceId, args.AgentInstanceId.Trim(), StringComparison.Ordinal))
            .Where(t => string.IsNullOrWhiteSpace(args.UserId)
                        || string.Equals(t.Identity.UserId, args.UserId.Trim(), StringComparison.Ordinal))
            .OrderByDescending(t => t.CreatedAtUtc)
            .Take(limit)
            .Select(t => new
            {
                ticketId = t.TicketId,
                status = t.Status.ToString().ToLowerInvariant(),
                toolId = t.ToolId,
                scope = t.Scope.ToString().ToLowerInvariant(),
                argumentsHash = t.ArgumentsHash,
                workspaceId = t.Identity.WorkspaceId,
                sessionId = t.Identity.SessionId,
                agentInstanceId = t.Identity.AgentInstanceId,
                userId = t.Identity.UserId,
                decisionReason = t.DecisionReason,
                createdAtUtc = t.CreatedAtUtc,
                decidedAtUtc = t.DecidedAtUtc,
                expiresAtUtc = t.ExpiresAtUtc,
                consumedAtUtc = t.ConsumedAtUtc,
                remainingUses = t.RemainingUses,
            })
            .ToArray();

        return ToolExecutionResult.Ok(JsonSerializer.Serialize(new
        {
            count = filtered.Length,
            tickets = filtered,
        }, JsonOptions));
    }

    private static bool TryParseStatus(
        string? value,
        out ToolApprovalTicketStatus? status,
        out string error)
    {
        status = null;
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(value))
            return true;

        if (Enum.TryParse<ToolApprovalTicketStatus>(value, ignoreCase: true, out var parsed))
        {
            status = parsed;
            return true;
        }

        error = "status must be one of: pending, approved, denied, expired, consumed.";
        return false;
    }
}

public sealed record ListToolApprovalsArgs
{
    [ToolParam("Optional ticket id filter.")]
    public string? TicketId { get; init; }

    [ToolParam("Optional tool id filter.")]
    public string? ToolId { get; init; }

    [ToolParam("Optional ticket status filter: pending, approved, denied, expired, consumed.")]
    public string? Status { get; init; }

    [ToolParam("Optional workspace id filter; defaults to the current workspace.")]
    public string? WorkspaceId { get; init; }

    [ToolParam("Optional session id filter.")]
    public string? SessionId { get; init; }

    [ToolParam("Optional agent instance id filter.")]
    public string? AgentInstanceId { get; init; }

    [ToolParam("Optional user id filter.")]
    public string? UserId { get; init; }

    [ToolParam("Maximum tickets to return, from 1 to 200.")]
    public int? Limit { get; init; }
}
