using Microsoft.Extensions.Logging;
using PuddingCode.Platform;

namespace PuddingPlatform.Services.Conversation;

/// <summary>
/// ADR-059: SubmitTurnHandler — 委托给 IConversationAcceptanceStore。
/// </summary>
public sealed class SubmitTurnHandler(
    IConversationAcceptanceStore acceptanceStore,
    ILogger<SubmitTurnHandler> logger) : ISubmitTurnHandler
{
    public async Task<AcceptanceResult> HandleAsync(SubmitTurnCommand command, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command.ConversationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.WorkspaceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.ClientRequestId);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.ClientMessageId);

        if (!string.Equals(command.Recipients.Type, "agent", StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException(
                "Only explicit agent recipients are supported. Broadcast is not accepted.");
        if (command.Recipients.AgentIds is null ||
            command.Recipients.AgentIds.Count == 0 ||
            command.Recipients.AgentIds.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException(
                "At least one explicit agent ID is required.",
                nameof(command.Recipients));
        if (command.Content.Count == 0 ||
            command.Content.Any(part =>
                !string.Equals(part.Type, "text", StringComparison.OrdinalIgnoreCase)) ||
            !command.Content.Any(part => !string.IsNullOrWhiteSpace(part.Text)))
            throw new ArgumentException(
                "At least one non-empty text content part is required.",
                nameof(command.Content));

        logger.LogInformation(
            "[SubmitTurn] conv={ConvId} msg={MsgId} agents={Agents}",
            command.ConversationId, command.ClientMessageId,
            string.Join(",", command.Recipients.AgentIds ?? []));

        return await acceptanceStore.AcceptBatchAsync(
            new SubmitTurnRequest
            {
                ClientRequestId = command.ClientRequestId,
                ClientMessageId = command.ClientMessageId,
                Recipients = command.Recipients,
                Content = command.Content,
            },
            command.WorkspaceId,
            command.ConversationId,
            command.UserId,
            ct);
    }
}
