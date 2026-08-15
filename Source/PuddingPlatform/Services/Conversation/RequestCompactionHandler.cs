using System.Text.Json;
using Microsoft.Extensions.Logging;
using PuddingCode.Abstractions;
using PuddingCode.Platform;
using PuddingCode.Runtime;

namespace PuddingPlatform.Services.Conversation;

/// <summary>
/// Manual compaction application boundary.
///
/// The HTTP endpoint supplies identity and intent only. This handler owns the
/// complete application transaction: resolve the immutable Agent runtime
/// profile, persist lifecycle facts, compact, create the successor
/// conversation, and publish the terminal fact to both conversations.
/// </summary>
public sealed class RequestCompactionHandler(
    IAgentRuntimeProfileResolver profileResolver,
    IContextCompactionService compactionService,
    ICompactionSessionSuccessor successor,
    IConversationEventStore eventStore,
    ILogger<RequestCompactionHandler> logger) : IRequestCompactionHandler
{
    /// <summary>P0-4f-1a step6: context.compaction.* 事件的固定 producer_component（runtime 域 compaction 子系统）。</summary>
    private const string CompactionProducerComponent = "runtime.compaction";

    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    public async Task<CompactionResult> HandleAsync(
        RequestCompactionCommand command,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command.ConversationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.WorkspaceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.AgentId);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.CompactionId);

        // P0-4f: TraceId 由两个手动 /compact 入口显式赋值（HTTP Controller 创建根 Trace；
        // SystemCommandHandler 在系统命令边界创建根 Trace，入站无 trace 字段可继承）。
        // 本 Handler 只透传 command.TraceId 到 started/completed/failed/successor 生命周期事件
        // 与压缩服务调用，禁止在此生成或 fallback —— 继承与创建由入口负责并显式区分；
        // 历史 trace_id=null 原样保留，不回填。
        try
        {
            var profile = await profileResolver.ResolveAsync(
                command.WorkspaceId,
                command.AgentId,
                ct);

            await AppendLifecycleEventAsync(
                command.ConversationId,
                command.WorkspaceId,
                command.CompactionId,
                ConversationEventTypes.ContextCompactionStarted,
                new
                {
                    compactionId = command.CompactionId,
                    sessionId = command.ConversationId,
                    mode = ContextCompactionMode.Manual.ToString(),
                    level = command.Level.ToString(),
                    reason = command.Reason,
                    agentId = command.AgentId,
                },
                "started",
                command.TraceId,
                ct);

            var compactRequest = new ContextCompactionRequest(
                command.WorkspaceId,
                command.ConversationId,
                command.AgentId,
                ContextCompactionMode.Manual,
                command.Level,
                command.Reason,
                CompactionId: command.CompactionId,
                AgentTemplateId: profile.SourceTemplateId,
                UserId: command.UserId,
                LlmConfig: profile.LlmConfig,
                CapabilityPolicy: profile.CapabilityPolicy,
                ToolDefinitions: profile.ToolDefinitions,
                SkillPackages: profile.SkillPackages)
            {
                TraceId = command.TraceId,
            };

            var compacted = await compactionService.CompactAsync(compactRequest, ct);
            var next = await successor.CreateAsync(
                new CreateCompactionSuccessorCommand(
                    command.ConversationId,
                    command.WorkspaceId,
                    command.AgentId,
                    profile.SourceTemplateId),
                ct);
            var completedCompaction = compacted.Diagnostics is null
                ? compacted
                : compacted with
                {
                    Diagnostics = compacted.Diagnostics with
                    {
                        NewSessionId = next.ConversationId,
                        NewSessionTitle = next.Title,
                    },
                };

            var completedPayload = new
            {
                compactionId = command.CompactionId,
                sessionId = command.ConversationId,
                sourceSessionId = command.ConversationId,
                newSessionId = next.ConversationId,
                newSessionTitle = next.Title,
                compaction = completedCompaction,
            };

            await AppendLifecycleEventAsync(
                command.ConversationId,
                command.WorkspaceId,
                command.CompactionId,
                ConversationEventTypes.ContextCompactionCompleted,
                completedPayload,
                "completed-source",
                command.TraceId,
                ct);

            // The successor conversation owns a durable origin fact. This lets
            // a freshly loaded browser reconstruct the compaction status after
            // it has switched away from the source conversation.
            await AppendLifecycleEventAsync(
                next.ConversationId,
                command.WorkspaceId,
                command.CompactionId,
                ConversationEventTypes.ContextCompactionCompleted,
                new
                {
                    compactionId = command.CompactionId,
                    sessionId = next.ConversationId,
                    sourceSessionId = command.ConversationId,
                    newSessionId = next.ConversationId,
                    newSessionTitle = next.Title,
                    compaction = completedCompaction,
                },
                "completed-successor",
                command.TraceId,
                ct);

            logger.LogInformation(
                "[Compact] completed compaction={CompactionId} old={OldConversationId} new={NewConversationId} messages={MessageCount}",
                command.CompactionId,
                command.ConversationId,
                next.ConversationId,
                completedCompaction.CompactedMessageCount);

            return new CompactionResult(
                command.CompactionId,
                completedCompaction,
                next.ConversationId,
                next.Title);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "[Compact] failed compaction={CompactionId} conversation={ConversationId} agent={AgentId}",
                command.CompactionId,
                command.ConversationId,
                command.AgentId);
            try
            {
                await AppendLifecycleEventAsync(
                    command.ConversationId,
                    command.WorkspaceId,
                    command.CompactionId,
                    ConversationEventTypes.ContextCompactionFailed,
                    new
                    {
                        compactionId = command.CompactionId,
                        sessionId = command.ConversationId,
                        error = ex.Message,
                        errorType = ex.GetType().Name,
                    },
                    "failed",
                    command.TraceId,
                    CancellationToken.None);
            }
            catch (Exception eventError)
            {
                logger.LogError(
                    eventError,
                    "[Compact] failed to persist terminal event compaction={CompactionId}",
                    command.CompactionId);
            }

            throw;
        }
    }

    private Task AppendLifecycleEventAsync(
        string conversationId,
        string workspaceId,
        string compactionId,
        string eventType,
        object payload,
        string phase,
        string? traceId,
        CancellationToken ct)
    {
        var element = JsonSerializer.SerializeToElement(payload, JsonOptions);
        var evt = new NewConversationEvent(
            EventId: $"compaction:{compactionId}:{phase}",
            Type: eventType,
            SchemaVersion: 1,
            WorkspaceId: workspaceId,
            TurnId: null,
            CommandId: compactionId,
            RunId: null,
            MessageId: null,
            CorrelationId: compactionId,
            CausationId: null,
            ProducerEventId: null,
            Payload: element,
            TraceId: traceId,
            ProducerComponent: CompactionProducerComponent);

        return eventStore.AppendAsync(
            conversationId,
            expectedVersion: -1,
            [evt],
            EventWriteCondition.ForRun($"compaction:{compactionId}", 0),
            ct);
    }
}
