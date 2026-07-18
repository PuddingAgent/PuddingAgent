using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PuddingCode.Platform;
using PuddingPlatform.Data;
using PuddingPlatform.Data.Entities;

namespace PuddingPlatform.Services;

/// <summary>
/// ADR-059: 原子受理存储 — 使用 PlatformDbContext 单事务写入 Message + Batch + Commands + Events + Head。
/// Scoped 服务，不创建新 Scope 或跨连接 Store 调用。
/// </summary>
public sealed class ConversationAcceptanceStore(
    PlatformDbContext db,
    ICommittedEventSignal committedSignal,
    ILogger<ConversationAcceptanceStore> logger) : IConversationAcceptanceStore
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    public async Task<AcceptanceResult> AcceptBatchAsync(
        SubmitTurnRequest request,
        string workspaceId,
        string conversationId,
        string? userId,
        CancellationToken ct)
    {
        // ── Step 1: 幂等检查 — 按 (workspace_id, client_request_id) 查已存在批次 ──
        var existingBatch = await db.AcceptanceBatches
            .AsNoTracking()
            .FirstOrDefaultAsync(b =>
                b.WorkspaceId == workspaceId &&
                b.ClientRequestId == request.ClientRequestId, ct);

        if (existingBatch is not null)
        {
            var existingCommands = await db.ChatExecutionCommands
                .AsNoTracking()
                .Where(c => c.BatchId == existingBatch.BatchId)
                .ToListAsync(ct);

            logger.LogInformation(
                "[AcceptStore] Idempotent hit batch={BatchId} conv={ConvId} cmds={Count} seq={Seq}",
                existingBatch.BatchId, existingBatch.ConversationId,
                existingCommands.Count, existingBatch.AcceptedSequence);

            return new AcceptanceResult
            {
                ConversationId = existingBatch.ConversationId,
                MessageId = existingBatch.MessageId,
                TurnIds = existingCommands.Select(c => c.TurnId).ToList(),
                CommandIds = existingCommands.Select(c => c.CommandId).ToList(),
                AcceptedSequence = existingBatch.AcceptedSequence,
            };
        }

        // ── Step 2: 单事务写入全部事实 ──
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        try
        {
            var batchId = Guid.NewGuid().ToString("N");
            var agentIds = request.Recipients.AgentIds
                ?? throw new InvalidOperationException(
                    "Validated turn acceptance requires explicit agent IDs.");
            var textContent = request.Content
                .FirstOrDefault(c => c.Type == "text")?.Text ?? "";
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            // 2a: 用户消息
            var message = new ChatMessageEntity
            {
                MessageId = request.ClientMessageId,
                SessionId = conversationId,
                WorkspaceId = workspaceId,
                Role = "user",
                Content = textContent,
                UserId = userId,
                CreatedAt = now,
            };
            db.ChatMessages.Add(message);

            // 2b: 受理批次
            var batch = new AcceptanceBatchEntity
            {
                BatchId = batchId,
                WorkspaceId = workspaceId,
                ClientRequestId = request.ClientRequestId,
                ConversationId = conversationId,
                MessageId = request.ClientMessageId,
                Status = "accepted",
                TurnCount = agentIds.Count,
                UserId = userId,
                CreatedAt = now,
            };
            db.AcceptanceBatches.Add(batch);

            // 2c: 为每个 Agent 创建执行命令
            var commands = new List<ChatExecutionCommandEntity>();
            foreach (var agentId in agentIds)
            {
                var turnId = Guid.NewGuid().ToString("N");
                var commandId = Guid.NewGuid().ToString("N");
                commands.Add(new ChatExecutionCommandEntity
                {
                    BatchId = batchId,
                    CommandId = commandId,
                    ClientRequestId = request.ClientRequestId,
                    WorkspaceId = workspaceId,
                    SessionId = conversationId,
                    MessageId = Guid.NewGuid().ToString("N"),
                    UserMessageId = request.ClientMessageId,
                    TurnId = turnId,
                    AgentInstanceId = agentId,
                    UserId = userId,
                    Status = "pending",
                    CreatedAt = now,
                });
            }
            db.ChatExecutionCommands.AddRange(commands);

            // 2d: 为每个 Command 分配 Event Store sequence 并写 turn.accepted
            var headSeq = await AllocateSequencesAsync(conversationId, commands.Count, ct);
            var events = new List<ConversationEventEntity>();
            for (int i = 0; i < commands.Count; i++)
            {
                var seq = headSeq + i + 1;
                var cmd = commands[i];
                var payload = JsonSerializer.SerializeToElement(new
                {
                    batchId,
                    commandId = cmd.CommandId,
                    turnId = cmd.TurnId,
                    conversationId,
                    userMessageId = request.ClientMessageId,
                    clientRequestId = request.ClientRequestId,
                    agentId = cmd.AgentInstanceId,
                }, JsonOpts);

                events.Add(new ConversationEventEntity
                {
                    ConversationId = conversationId,
                    Sequence = seq,
                    EventId = Guid.NewGuid().ToString("N"),
                    WorkspaceId = workspaceId,
                    TurnId = cmd.TurnId,
                    CommandId = cmd.CommandId,
                    RunId = null,
                    MessageId = request.ClientMessageId,
                    Type = ConversationEventTypes.TurnAccepted,
                    SchemaVersion = 1,
                    Payload = payload.GetRawText(),
                    OccurredAt = DateTimeOffset.UtcNow.ToString("O"),
                    CommittedAt = DateTimeOffset.UtcNow.ToString("O"),
                    CorrelationId = conversationId,
                });
            }
            db.ConversationEvents.AddRange(events);

            // 2d.5: Create ConversationTurn for each command
            foreach (var cmd in commands)
            {
                db.ConversationTurns.Add(new ConversationTurnEntity
                {
                    ConversationId = conversationId,
                    TurnId = cmd.TurnId,
                    CommandId = cmd.CommandId,
                    WorkspaceId = workspaceId,
                    UserId = userId,
                    Status = "accepted",
                    AcceptedSequence = headSeq + commands.Count,
                    CreatedAt = now,
                });
            }

            // 2e: 更新 Conversation Head
            var head = await db.ConversationHeads
                .FirstOrDefaultAsync(h => h.ConversationId == conversationId, ct);
            if (head is null)
            {
                head = new ConversationHeadEntity
                {
                    ConversationId = conversationId,
                    HeadSequence = headSeq + commands.Count,
                };
                db.ConversationHeads.Add(head);
            }
            else
            {
                head.HeadSequence = headSeq + commands.Count;
                db.ConversationHeads.Update(head);
            }

            // 2f: 记录 acceptedSequence
            batch.AcceptedSequence = headSeq + commands.Count;

            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);

            committedSignal.Signal(conversationId, batch.AcceptedSequence);

            logger.LogInformation(
                "[AcceptStore] Committed batch={BatchId} conv={ConvId} cmds={Count} seq=[{First},{Last}]",
                batchId, conversationId, commands.Count,
                headSeq + 1, batch.AcceptedSequence);

            return new AcceptanceResult
            {
                ConversationId = conversationId,
                MessageId = request.ClientMessageId,
                TurnIds = commands.Select(c => c.TurnId).ToList(),
                CommandIds = commands.Select(c => c.CommandId).ToList(),
                AcceptedSequence = batch.AcceptedSequence,
            };
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }

    /// <summary>
    /// 原子分配 N 个 sequence 号（单行 UPDATE conversation_heads SET head_sequence = head_sequence + N）。
    /// </summary>
    private async Task<long> AllocateSequencesAsync(
        string conversationId, int count, CancellationToken ct)
    {
        // Ensure head row exists
        var head = await db.ConversationHeads
            .FirstOrDefaultAsync(h => h.ConversationId == conversationId, ct);

        if (head is null)
        {
            head = new ConversationHeadEntity
            {
                ConversationId = conversationId,
                HeadSequence = 0,
            };
            db.ConversationHeads.Add(head);
            await db.SaveChangesAsync(ct);
            return 0;
        }

        var prev = head.HeadSequence;
        head.HeadSequence += count;
        db.ConversationHeads.Update(head);
        await db.SaveChangesAsync(ct);

        return prev;
    }
}
