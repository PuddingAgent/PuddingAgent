using Microsoft.Extensions.Logging;
using PuddingCode.Platform;

namespace PuddingPlatform.Services.Conversation;

/// <summary>
/// ADR-059: Cancel Handler — uses IExecutionControlService for unified control entry.
/// </summary>
public sealed class RequestTurnCancellationHandler(
    IChatCommandStore commandStore,
    IExecutionControlService controlService,
    ILogger<RequestTurnCancellationHandler> logger) : IRequestTurnCancellationHandler
{
    public async Task<CancelTurnResult> HandleAsync(
        RequestTurnCancellationCommand command, CancellationToken ct)
    {
        var cmd = await commandStore.FindByTurnIdAsync(command.ConversationId, command.TurnId, ct)
            ?? throw new InvalidOperationException(
                $"Turn '{command.TurnId}' not found in '{command.ConversationId}'.");

        if (cmd.Status != CommandStatus.Running)
            throw new InvalidOperationException(
                $"Turn '{command.TurnId}' is not running (status={cmd.Status}).");

        await controlService.SubmitAsync(
            new ExecutionControlCommand(
                ConversationId: command.ConversationId,
                TurnId: command.TurnId,
                Kind: ControlMessageKind.CancelRequested,
                Payload: "user_cancelled",
                SourceUserId: command.UserId,
                Priority: 1000),
            ct);

        logger.LogInformation("[Cancel] conv={ConvId} turn={TurnId}", command.ConversationId, command.TurnId);

        return new CancelTurnResult(command.ConversationId, command.TurnId, "cancel_requested");
    }
}
