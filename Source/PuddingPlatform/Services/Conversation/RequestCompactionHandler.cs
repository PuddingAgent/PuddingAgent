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
                SkillPackages: profile.SkillPackages);

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
            Payload: element);

        return eventStore.AppendAsync(
            conversationId,
            expectedVersion: -1,
            [evt],
            EventWriteCondition.ForRun($"compaction:{compactionId}", 0),
            ct);
    }
}
