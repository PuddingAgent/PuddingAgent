using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PuddingCode.Platform;
using PuddingPlatform.Data;
using PuddingPlatform.Data.Entities;

namespace PuddingPlatform.Services.Execution;

/// <summary>
/// ADR-059: 持久化执行控制服务 — 统一 Cancel/Steering/Approval 入口。
/// 同事务写 Inbox + Event + 状态。
/// </summary>
public sealed class ExecutionControlService(
    IServiceScopeFactory scopeFactory,
    ICommittedEventSignal signal,
    ILogger<ExecutionControlService> logger) : IExecutionControlService
{
    public async Task<ControlReceipt> SubmitAsync(
        ExecutionControlCommand command, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();

        var controlId = Guid.NewGuid().ToString("N");
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        try
        {
            // Control and its Conversation Event share one sequence allocation.
            var head = await db.ConversationHeads
                .FirstOrDefaultAsync(h => h.ConversationId == command.ConversationId, ct);
            if (head is null)
            {
                head = new ConversationHeadEntity
                {
                    ConversationId = command.ConversationId,
                    HeadSequence = 0
                };
                db.ConversationHeads.Add(head);
            }
            var eventSeq = head.HeadSequence + 1;
            head.HeadSequence = eventSeq;
            var controlSeq = eventSeq;

            // 1. Write control message
            db.ControlMessages.Add(new ControlMessageEntity
            {
                ControlId = controlId,
                Sequence = controlSeq,
                ConversationId = command.ConversationId,
                TurnId = command.TurnId,
                Kind = command.Kind.ToString(),
                Payload = command.Payload,
                SourceUserId = command.SourceUserId,
                Priority = command.Priority,
                Status = "pending",
                CreatedAt = nowMs,
            });

            // 2. Update Command status if Cancel
            if (command.Kind == ControlMessageKind.CancelRequested && command.TurnId is not null)
            {
                await db.ChatExecutionCommands
                    .Where(c =>
                        c.SessionId == command.ConversationId &&
                        c.TurnId == command.TurnId &&
                        c.Status == "running")
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(c => c.Status, (string)"cancel_requested"), ct);
            }

            // 3. Write the corresponding event with the same sequence.
            var eventType = command.Kind switch
            {
                ControlMessageKind.CancelRequested => ConversationEventTypes.TurnCancelRequested,
                ControlMessageKind.Steering => "steering.created",
                _ => "control.created",
            };

            var eventPayload = JsonSerializer.Serialize(new
            {
                controlId,
                controlSequence = controlSeq,
                kind = command.Kind.ToString(),
                payload = command.Payload,
                turnId = command.TurnId,
            });

            db.ConversationEvents.Add(new ConversationEventEntity
            {
                ConversationId = command.ConversationId,
                Sequence = eventSeq,
                EventId = Guid.NewGuid().ToString("N"),
                WorkspaceId = "",
                TurnId = command.TurnId ?? "",
                Type = eventType,
                SchemaVersion = 1,
                Payload = eventPayload,
                OccurredAt = DateTimeOffset.UtcNow.ToString("O"),
                CommittedAt = DateTimeOffset.UtcNow.ToString("O"),
                CorrelationId = command.ConversationId,
            });

            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);

            signal.Signal(command.ConversationId, eventSeq);

            logger.LogInformation("[ControlService] kind={Kind} conv={ConvId} turn={TurnId} ctrlSeq={Seq}",
                command.Kind, command.ConversationId, command.TurnId, controlSeq);

            return new ControlReceipt(controlId, controlSeq, eventSeq);
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }
}
