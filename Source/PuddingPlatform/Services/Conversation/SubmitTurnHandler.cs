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
        // ── @all 拒绝 ──
        if (string.Equals(command.Recipients.Type, "all", StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException("@all broadcast is not yet supported. Use explicit agent IDs.");

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
