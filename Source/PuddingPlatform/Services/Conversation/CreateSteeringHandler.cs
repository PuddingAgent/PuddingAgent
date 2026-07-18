using Microsoft.Extensions.Logging;
using PuddingCode.Platform;

namespace PuddingPlatform.Services.Conversation;

/// <summary>
/// ADR-059: Steering Handler — uses IExecutionControlService as single entry.
/// Eliminates the dual-ID and triple-write problems.
/// </summary>
public sealed class CreateSteeringHandler(
    IExecutionControlService controlService,
    IExecutionCommandReader commandReader,
    ILogger<CreateSteeringHandler> logger) : ICreateSteeringHandler
{
    public async Task<CreateSteeringResult> HandleAsync(
        CreateSteeringCommand command, CancellationToken ct)
    {
        var cmd = await commandReader.FindByTurnIdAsync(command.ConversationId, command.TurnId, ct)
            ?? throw new InvalidOperationException(
                $"Turn '{command.TurnId}' not found.");

        if (cmd.Status != CommandStatus.Running)
            throw new InvalidOperationException(
                $"Steering rejected: turn is {cmd.Status}.");

        var receipt = await controlService.SubmitAsync(
            new ExecutionControlCommand(
                ConversationId: command.ConversationId,
                TurnId: command.TurnId,
                Kind: ControlMessageKind.Steering,
                Payload: command.Text,
                SourceUserId: command.UserId,
                Priority: command.Priority),
            ct);

        logger.LogInformation("[Steering] conv={ConvId} turn={TurnId} controlId={ControlId}",
            command.ConversationId, command.TurnId, receipt.ControlId);

        return new CreateSteeringResult(receipt.ControlId);
    }
}
