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

        // 单一会话：渠道与 web 共用主会话，不再按会话类型禁止 steering。
        // 仍拒绝非本 turn 所有者的插入请求。
        if (cmd.UserId != null && cmd.UserId != command.UserId)
            throw new InvalidOperationException("Steering rejected: the current sender does not own this turn.");

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
