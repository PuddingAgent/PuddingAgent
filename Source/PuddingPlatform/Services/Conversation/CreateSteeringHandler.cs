using Microsoft.Extensions.Logging;
using PuddingCode.Platform;

namespace PuddingPlatform.Services.Conversation;

/// <summary>
/// Steering application boundary. It validates the target turn against the
/// canonical execution command and writes the durable queue consumed by Runtime.
/// </summary>
public sealed class CreateSteeringHandler(
    SessionSteeringService steeringService,
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

        if (!string.IsNullOrWhiteSpace(command.WorkspaceId)
            && !string.Equals(command.WorkspaceId, cmd.WorkspaceId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Steering rejected: workspace does not match the active turn.");
        }

        if (!string.IsNullOrWhiteSpace(command.AgentId)
            && !string.Equals(command.AgentId, cmd.AgentInstanceId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Steering rejected: agent does not match the active turn.");
        }

        var steering = await steeringService.CreateAsync(
            new CreateSessionSteeringMessage(
                WorkspaceId: cmd.WorkspaceId,
                SessionId: cmd.ConversationId,
                TargetTurnId: cmd.TurnId,
                AgentId: cmd.AgentInstanceId,
                MessageText: command.Text,
                SourceQueueItemId: command.SourceQueueItemId,
                CreatedBy: command.UserId,
                Priority: command.Priority),
            ct);

        logger.LogInformation(
            "[Steering] accepted conv={ConvId} turn={TurnId} steeringId={SteeringId}",
            command.ConversationId,
            command.TurnId,
            steering.SteeringId);

        return new CreateSteeringResult(steering.SteeringId);
    }
}
