using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PuddingCode.Models;
using PuddingCode.Platform;
using PuddingCode.Services;
using PuddingPlatform.Data;
using PuddingPlatform.Data.Dtos;
using PuddingPlatform.Data.Entities;
using System.Text;
using System.Text.Json;

namespace PuddingPlatform.Services.AgentChat;

/// <summary>Builds renderable Agent conversation projections from the canonical Conversation Event Store.</summary>
public interface IAgentConversationProjectionService
{
    Task<AgentConversationView> GetConversationAsync(string workspaceId, string ownerUserId, string agentId, CancellationToken ct);

    Task<MessageProcessDetailsView?> GetMessageProcessItemsAsync(
        string workspaceId,
        string ownerUserId,
        string agentId,
        string messageId,
        CancellationToken ct);

    /// <summary>Lightweight cursor check — returns the canonical conversation head.</summary>
    Task<long> GetConversationCursorAsync(string workspaceId, string ownerUserId, string agentId, CancellationToken ct);
}

/// <summary>Default conversation projection service for the single-user Agent chat client.</summary>
public sealed class AgentConversationProjectionService(
    PlatformApiClient api,
    WorkspaceAgentFileService workspaceAgentFileService,
    SessionRedirectStore redirectStore,
    PlatformDbContext db,
    ILogger<AgentConversationProjectionService> logger) : IAgentConversationProjectionService
{
    private const string DefaultOwnerUserId = "single-user";
    private const int ConversationMessageLimit = 20;
    private const int ConversationMessageCandidateLimit = ConversationMessageLimit * 3;
    private const int ActiveRunProcessItemLimit = 64;
    private static readonly TimeSpan ActiveRunStaleAfter = TimeSpan.FromMinutes(5);
    private static readonly string[] ActiveRunVisibleProcessEventTypes =
    [
        ConversationEventTypes.MessageThinkingSummaryAppended,
        ConversationEventTypes.ToolCallRequested,
        ConversationEventTypes.ToolCallCompleted,
        ConversationEventTypes.ToolCallFailed,
    ];
    private static readonly string[] MessageProcessEventTypes =
    [
        ConversationEventTypes.MessageThinkingSummaryAppended,
        ConversationEventTypes.ToolCallRequested,
        ConversationEventTypes.ToolCallCompleted,
        ConversationEventTypes.ToolCallFailed,
        "subagent.spawned",
        "subagent.delta",
        "subagent.thinking",
        "subagent.tool_call",
        "subagent.tool_result",
        "subagent.completed",
    ];
    private static readonly string[] MessageProjectionEventTypes =
    [
        .. MessageProcessEventTypes,
        ConversationEventTypes.TurnCompleted,
    ];

    public async Task<AgentConversationView> GetConversationAsync(
        string workspaceId,
        string ownerUserId,
        string agentId,
        CancellationToken ct)
    {
        ownerUserId = NormalizeOwnerUserId(ownerUserId);

        var sessions = await api.GetSessionsAsync(workspaceId, ct);
        var agent = await workspaceAgentFileService.GetAgentAsync(workspaceId, agentId, ct);
        var main = await ResolveAgentMainSessionAsync(
            workspaceId,
            ownerUserId,
            agentId,
            agent,
            sessions,
            ct);

        if (main is null)
        {
            return new AgentConversationView(
                workspaceId,
                ownerUserId,
                agentId,
                "",
                [],
                null,
                0,
                DateTimeOffset.UtcNow);
        }

        var agentDisplayName = ResolveAgentDisplayName(agent, agentId);
        var messageRows = await db.ChatMessages
            .AsNoTracking()
            .Where(m => m.SessionId == main.SessionId)
            .Where(m => m.Content != RuntimeDispatchMarkers.DuplicateMessagePlaceholder)
            .Where(m => m.Content != RuntimeDispatchMarkers.DuplicateMessagePlaceholderLegacyHyphen)
            .OrderByDescending(m => m.CreatedAt)
            .ThenByDescending(m => m.Id)
            .Take(ConversationMessageCandidateLimit)
            .Select(m => new ConversationMessageRow(
                m.Id,
                m.MessageId,
                m.Role,
                m.Content,
                m.TurnId,
                m.MetadataJson,
                m.CreatedAt))
            .ToListAsync(ct);
        messageRows.Reverse();
        messageRows = DeduplicateCanonicalMessageRows(messageRows)
            .TakeLast(ConversationMessageLimit)
            .ToList();

        var messageIds = messageRows
            .Select(m => m.MessageId)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var messageTurnRows = messageIds.Count == 0
            ? []
            : await db.ChatExecutionCommands
                .AsNoTracking()
                .Where(c => c.SessionId == main.SessionId)
                .Where(c => messageIds.Contains(c.UserMessageId) || messageIds.Contains(c.MessageId))
                .Select(c => new { c.UserMessageId, c.MessageId, c.TurnId })
                .ToListAsync(ct);
        var turnIdByMessageId = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var command in messageTurnRows)
        {
            if (!string.IsNullOrWhiteSpace(command.UserMessageId))
                turnIdByMessageId[command.UserMessageId] = command.TurnId;
            if (!string.IsNullOrWhiteSpace(command.MessageId))
                turnIdByMessageId[command.MessageId] = command.TurnId;
        }
        var messageEvents = messageIds.Count == 0
            ? []
            : await db.ConversationEvents
                .AsNoTracking()
                .Where(e => e.ConversationId == main.SessionId)
                .Where(e => e.MessageId != null && messageIds.Contains(e.MessageId))
                .Where(e => MessageProjectionEventTypes.Contains(e.Type))
                .OrderBy(e => e.Sequence)
                .Select(e => new MessageProcessEvent(
                    e.MessageId!,
                    e.RunId,
                    e.Type,
                    e.Sequence,
                    e.OccurredAt))
                .ToListAsync(ct);

        var completedProcessByMessageId = BuildCompletedProcessSummaryByMessageId(messageEvents);
        var messages = messageRows
            .Select(m => BuildConversationMessageView(
                m,
                ownerUserId,
                agentId,
                agentDisplayName,
                !string.IsNullOrWhiteSpace(m.TurnId)
                    ? m.TurnId
                    : turnIdByMessageId.GetValueOrDefault(m.MessageId),
                completedProcessByMessageId))
            .ToList();

        var latestRunEvent = await db.ConversationEvents
            .AsNoTracking()
            .Where(e => e.ConversationId == main.SessionId)
            .Where(e => e.RunId != null && e.RunId != "")
            .OrderByDescending(e => e.Sequence)
            .FirstOrDefaultAsync(ct);

        AgentRunView? activeRun = null;
        if (latestRunEvent is not null && !IsTerminalEvent(latestRunEvent.Type))
        {
            var activeRunQuery = db.ConversationEvents
                .AsNoTracking()
                .Where(e =>
                    e.ConversationId == main.SessionId
                    && e.RunId == latestRunEvent.RunId);
            var firstRunEventAt = await activeRunQuery
                .OrderBy(e => e.Sequence)
                .Select(e => e.OccurredAt)
                .FirstOrDefaultAsync(ct);
            var outputEvents = await activeRunQuery
                .Where(e =>
                    e.Type == ConversationEventTypes.TurnStarted
                    || e.Type == ConversationEventTypes.MessageContentAppended)
                .OrderBy(e => e.Sequence)
                .ToListAsync(ct);
            var processMetadata = await activeRunQuery
                .Where(e => ActiveRunVisibleProcessEventTypes.Contains(e.Type))
                .OrderBy(e => e.Sequence)
                .Select(e => new ActiveProcessEventMetadata(e.Type, e.OccurredAt))
                .ToListAsync(ct);
            var recentProcessEvents = await activeRunQuery
                .Where(e => ActiveRunVisibleProcessEventTypes.Contains(e.Type))
                .OrderByDescending(e => e.Sequence)
                .Take(ActiveRunProcessItemLimit)
                .ToListAsync(ct);
            recentProcessEvents.Reverse();
            var commandClientId = string.IsNullOrWhiteSpace(latestRunEvent.CommandId)
                ? null
                : await db.ChatExecutionCommands
                    .AsNoTracking()
                    .Where(c => c.CommandId == latestRunEvent.CommandId)
                    .Select(c => c.UserMessageId)
                    .FirstOrDefaultAsync(ct);

            activeRun = BuildActiveRun(
                workspaceId,
                ownerUserId,
                agentId,
                main,
                commandClientId,
                latestRunEvent,
                firstRunEventAt,
                outputEvents,
                recentProcessEvents,
                BuildActiveProcessSummary(processMetadata));
        }

        var eventCursor = await GetEventCursorAsync(main.SessionId, ct);
        var updatedAt = latestRunEvent is null
            ? main.LastActiveAt
            : ParseOccurredAt(latestRunEvent.OccurredAt);

        return new AgentConversationView(
            workspaceId,
            ownerUserId,
            agentId,
            main.SessionId,
            messages,
            activeRun,
            eventCursor,
            updatedAt);
    }

    public async Task<MessageProcessDetailsView?> GetMessageProcessItemsAsync(
        string workspaceId,
        string ownerUserId,
        string agentId,
        string messageId,
        CancellationToken ct)
    {
        ownerUserId = NormalizeOwnerUserId(ownerUserId);
        if (string.IsNullOrWhiteSpace(messageId))
            return null;

        var sessions = await api.GetSessionsAsync(workspaceId, ct);
        var agent = await workspaceAgentFileService.GetAgentAsync(workspaceId, agentId, ct);
        var main = await ResolveAgentMainSessionAsync(
            workspaceId,
            ownerUserId,
            agentId,
            agent,
            sessions,
            ct);
        if (main is null)
            return null;

        var message = await db.ChatMessages
            .AsNoTracking()
            .FirstOrDefaultAsync(
                m => m.SessionId == main.SessionId && m.MessageId == messageId,
                ct);
        if (message is null || !string.Equals(message.Role, "agent", StringComparison.OrdinalIgnoreCase))
            return null;

        var completedRunId = await db.ConversationEvents
            .AsNoTracking()
            .Where(e => e.ConversationId == main.SessionId)
            .Where(e => e.MessageId == messageId)
            .Where(e => e.Type == ConversationEventTypes.TurnCompleted)
            .OrderByDescending(e => e.Sequence)
            .Select(e => e.RunId)
            .FirstOrDefaultAsync(ct);

        IReadOnlyList<ProcessSummaryItem> processItems;
        if (string.IsNullOrWhiteSpace(completedRunId))
        {
            processItems = BuildTranscriptProcessItems(message, logger);
        }
        else
        {
            var processEvents = await db.ConversationEvents
                .AsNoTracking()
                .Where(e => e.ConversationId == main.SessionId)
                .Where(e => e.MessageId == messageId && e.RunId == completedRunId)
                .Where(e => MessageProcessEventTypes.Contains(e.Type))
                .OrderBy(e => e.Sequence)
                .ToListAsync(ct);
            var eventItems = processEvents
                .Select(e => TryBuildEventProcessItem(e, out var item) ? item : null)
                .Where(item => item is not null)
                .Cast<ProcessSummaryItem>()
                .ToList();
            processItems = MergeMessageProcessItems(message, eventItems, logger);
        }

        return new MessageProcessDetailsView(messageId, completedRunId, processItems);
    }

    public async Task<long> GetConversationCursorAsync(
        string workspaceId,
        string ownerUserId,
        string agentId,
        CancellationToken ct)
    {
        ownerUserId = NormalizeOwnerUserId(ownerUserId);

        var sessions = await api.GetSessionsAsync(workspaceId, ct);
        var agent = await workspaceAgentFileService.GetAgentAsync(workspaceId, agentId, ct);
        var main = await ResolveAgentMainSessionAsync(
            workspaceId,
            ownerUserId,
            agentId,
            agent,
            sessions,
            ct);

        return main is null ? 0 : await GetEventCursorAsync(main.SessionId, ct);
    }

    private async Task<long> GetEventCursorAsync(string conversationId, CancellationToken ct)
    {
        var maxSequence = await db.ConversationEvents
            .AsNoTracking()
            .Where(e => e.ConversationId == conversationId)
            .MaxAsync(e => (long?)e.Sequence, ct);
        return maxSequence ?? 0;
    }

    private static string NormalizeOwnerUserId(string? ownerUserId)
        => string.IsNullOrWhiteSpace(ownerUserId) || ownerUserId == "admin"
            ? DefaultOwnerUserId
            : ownerUserId;

    private async Task<SessionRecord?> ResolveAgentMainSessionAsync(
        string workspaceId,
        string ownerUserId,
        string agentId,
        WorkspaceAgentDto? agent,
        IReadOnlyList<SessionRecord> sessions,
        CancellationToken ct)
    {
        var redirectedSessionId = redirectStore.Resolve("main", workspaceId, agentId);
        var preferredSessionIds = new[]
            {
                string.Equals(redirectedSessionId, "main", StringComparison.OrdinalIgnoreCase) ? null : redirectedSessionId,
                agent?.MainSessionId,
            }
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)
            .Cast<string>();

        foreach (var preferredSessionId in preferredSessionIds)
        {
            var preferred = sessions.FirstOrDefault(s => string.Equals(s.SessionId, preferredSessionId, StringComparison.Ordinal))
                ?? await api.GetSessionAsync(preferredSessionId, ct);
            if (preferred is not null && string.Equals(preferred.WorkspaceId, workspaceId, StringComparison.Ordinal))
                return preferred;
        }

        return sessions
            .Where(s => s.SessionRole == SessionRole.Main)
            .Where(s => string.Equals(s.PrincipalKind, "agent", StringComparison.OrdinalIgnoreCase))
            .Where(s => string.Equals(s.PrincipalId ?? s.AgentInstanceId ?? s.AgentTemplateId, agentId, StringComparison.Ordinal))
            .Where(s => string.Equals(NormalizeOwnerUserId(s.OwnerUserId), ownerUserId, StringComparison.Ordinal))
            .OrderByDescending(s => s.LastActiveAt)
            .FirstOrDefault();
    }

    private static string ResolveAgentDisplayName(
        WorkspaceAgentDto? agent,
        string agentId)
    {
        if (!string.IsNullOrWhiteSpace(agent?.DisplayName))
            return agent.DisplayName.Trim();
        if (!string.IsNullOrWhiteSpace(agent?.Name))
            return agent.Name.Trim();
        return agentId;
    }

    private static AgentRunView? BuildActiveRun(
        string workspaceId,
        string ownerUserId,
        string agentId,
        SessionRecord main,
        string? commandClientId,
        ConversationEventEntity latestRunEvent,
        string? firstRunEventAt,
        IReadOnlyList<ConversationEventEntity> outputEvents,
        IReadOnlyList<ConversationEventEntity> recentProcessEvents,
        ConversationProcessSummary? processSummary)
    {
        if (string.IsNullOrWhiteSpace(latestRunEvent.RunId))
            return null;

        var markdown = new StringBuilder();
        foreach (var evt in outputEvents)
        {
            if (evt.Type == ConversationEventTypes.MessageContentAppended)
            {
                markdown.Append(ReadString(evt.Payload, "delta")
                    ?? ReadString(evt.Payload, "text")
                    ?? ReadString(evt.Payload, "content")
                    ?? "");
            }
        }
        var processItems = recentProcessEvents
            .Select(evt => TryBuildEventProcessItem(evt, out var item) ? item : null)
            .Where(item => item is not null)
            .Cast<ProcessSummaryItem>()
            .ToList();

        var startedAt = ParseOccurredAt(
            outputEvents.FirstOrDefault(e => e.Type == ConversationEventTypes.TurnStarted)?.OccurredAt
            ?? firstRunEventAt
            ?? latestRunEvent.OccurredAt);
        var updatedAt = ParseOccurredAt(latestRunEvent.OccurredAt);
        if (DateTimeOffset.UtcNow - updatedAt > ActiveRunStaleAfter)
            return null;

        return new AgentRunView(
            latestRunEvent.RunId,
            workspaceId,
            ownerUserId,
            agentId,
            main.SessionId,
            commandClientId,
            "running",
            "正在输出",
            main.Title ?? "",
            latestRunEvent.Sequence,
            new AgentOutputSnapshot(markdown.ToString(), processItems, processSummary),
            startedAt,
            updatedAt,
            null);
    }

    private static ConversationProcessSummary? BuildActiveProcessSummary(
        IReadOnlyList<ActiveProcessEventMetadata> events)
    {
        if (events.Count == 0)
            return null;

        var thinkingRounds = 0;
        var sawThinkingInRound = false;
        var sawToolInRound = false;
        foreach (var processEvent in events)
        {
            if (processEvent.Type == ConversationEventTypes.MessageThinkingSummaryAppended)
            {
                if (!sawThinkingInRound || sawToolInRound)
                {
                    thinkingRounds++;
                    sawThinkingInRound = true;
                    sawToolInRound = false;
                }
            }
            else
            {
                sawToolInRound = true;
            }
        }

        var firstAt = ParseOccurredAt(events[0].OccurredAt);
        var lastAt = ParseOccurredAt(events[^1].OccurredAt);
        return new ConversationProcessSummary(
            events.Count,
            thinkingRounds,
            events.Count(e => e.Type == ConversationEventTypes.MessageThinkingSummaryAppended),
            events.Count(e => e.Type == ConversationEventTypes.ToolCallRequested),
            events.Count(e => e.Type is ConversationEventTypes.ToolCallCompleted or ConversationEventTypes.ToolCallFailed),
            events.Count(e => e.Type == ConversationEventTypes.ToolCallFailed),
            Math.Max(0, (long)(lastAt - firstAt).TotalMilliseconds),
            false);
    }

    private static IEnumerable<ConversationMessageRow> DeduplicateCanonicalMessageRows(
        IReadOnlyList<ConversationMessageRow> messages)
    {
        var envelopeMessageIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var message in messages)
        {
            if (RuntimeDispatchMarkers.IsDuplicateMessagePlaceholder(message.Content))
                continue;

            var envelopeMessageId = AgentContextEnvelopeRenderer.TryParse(message.Content)?.MessageId;
            if (!string.IsNullOrWhiteSpace(envelopeMessageId)
                && !envelopeMessageIds.Add(envelopeMessageId))
            {
                continue;
            }

            yield return message;
        }
    }

    private static ConversationMessageView BuildConversationMessageView(
        ConversationMessageRow message,
        string ownerUserId,
        string agentId,
        string agentDisplayName,
        string? turnId,
        IReadOnlyDictionary<string, CompletedMessageProcess> completedProcessByMessageId)
    {
        var envelope = AgentContextEnvelopeRenderer.TryParse(message.Content);
        var metadata = ParsePuddingMessageMetadata(envelope);
        var sourceKind = metadata?.SourceKind
            ?? (string.Equals(message.Role, "agent", StringComparison.OrdinalIgnoreCase) ? "agent" : "user");
        var sourceId = metadata?.SourceId
            ?? (string.Equals(sourceKind, "agent", StringComparison.OrdinalIgnoreCase) ? agentId : ownerUserId);
        var sourceName = metadata?.SourceName
            ?? (string.Equals(sourceKind, "agent", StringComparison.OrdinalIgnoreCase) ? agentDisplayName : "Pudding Admin");
        var messageType = metadata?.MessageType
            ?? (string.Equals(message.Role, "agent", StringComparison.OrdinalIgnoreCase) ? "agent_output" : "user_message");
        var uiRole = string.Equals(sourceKind, "agent", StringComparison.OrdinalIgnoreCase)
            ? "agent"
            : string.Equals(sourceKind, "system", StringComparison.OrdinalIgnoreCase)
                ? "system"
                : "user";
        var displayContent = string.Equals(sourceKind, "system", StringComparison.OrdinalIgnoreCase)
                             && string.Equals(sourceId, "heartbeat", StringComparison.OrdinalIgnoreCase)
                             && !string.IsNullOrWhiteSpace(envelope?.Context.Text)
            ? envelope.Context.Text
            : message.Content;
        completedProcessByMessageId.TryGetValue(message.MessageId, out var completedProcess);

        return new ConversationMessageView(
            string.IsNullOrWhiteSpace(message.MessageId) ? message.Id.ToString() : message.MessageId,
            completedProcess?.RunId,
            uiRole,
            sourceId,
            sourceName,
            DateTimeOffset.FromUnixTimeMilliseconds(message.CreatedAt),
            displayContent,
            "succeeded",
            [])
        {
            TurnId = turnId,
            SourceKind = sourceKind,
            MessageType = messageType,
            LlmRole = message.Role,
            Metadata = ParseMetadataJson(message.MetadataJson),
            ProcessSummary = completedProcess?.Summary,
        };
    }

    private static PuddingMessageMetadata? ParsePuddingMessageMetadata(AgentContextEnvelope? envelope)
    {
        if (envelope is null)
            return null;

        var from = envelope.From;
        return new PuddingMessageMetadata(
            string.IsNullOrWhiteSpace(from?.Kind) ? null : from!.Kind,
            string.IsNullOrWhiteSpace(from?.Id) ? null : from!.Id,
            string.IsNullOrWhiteSpace(from?.DisplayName) ? null : from!.DisplayName,
            string.IsNullOrWhiteSpace(envelope.MessageType) ? null : envelope.MessageType);
    }

    private static IReadOnlyList<ProcessSummaryItem> MergeMessageProcessItems(
        ChatMessageEntity message,
        IReadOnlyList<ProcessSummaryItem> eventItems,
        ILogger logger)
    {
        var transcriptItems = BuildTranscriptProcessItems(message, logger);
        if (eventItems.Count == 0)
            return transcriptItems;

        if (eventItems.Any(item => item.Kind == "thinking") || transcriptItems.Count == 0)
            return eventItems;

        return transcriptItems.Concat(eventItems).ToList();
    }

    private static IReadOnlyDictionary<string, CompletedMessageProcess> BuildCompletedProcessSummaryByMessageId(
        IReadOnlyList<MessageProcessEvent> events)
    {
        var byMessageId = new Dictionary<string, CompletedMessageProcess>(StringComparer.Ordinal);
        var grouped = events
            .Where(e => !string.IsNullOrWhiteSpace(e.MessageId))
            .GroupBy(e => e.MessageId!, StringComparer.Ordinal);

        foreach (var group in grouped)
        {
            var completed = group
                .Where(e => e.Type == ConversationEventTypes.TurnCompleted)
                .OrderBy(e => e.Sequence)
                .LastOrDefault();
            if (completed is null)
                continue;

            var runEvents = group
                .Where(e => string.Equals(e.RunId, completed.RunId, StringComparison.Ordinal))
                .Where(e => MapProcessKind(e.Type) is not null)
                .OrderBy(e => e.Sequence)
                .ToList();

            if (runEvents.Count == 0)
                continue;

            var thinkingRounds = 0;
            var sawThinkingInRound = false;
            var sawToolInRound = false;
            foreach (var processEvent in runEvents)
            {
                var kind = MapProcessKind(processEvent.Type);
                if (kind == "thinking")
                {
                    if (!sawThinkingInRound || sawToolInRound)
                    {
                        thinkingRounds++;
                        sawThinkingInRound = true;
                        sawToolInRound = false;
                    }
                }
                else if (kind is "tool_call" or "tool_result")
                {
                    sawToolInRound = true;
                }
            }

            var firstAt = ParseOccurredAt(runEvents[0].OccurredAt);
            var lastAt = ParseOccurredAt(runEvents[^1].OccurredAt);
            var summary = new ConversationProcessSummary(
                runEvents.Count,
                thinkingRounds,
                runEvents.Count(e => e.Type == ConversationEventTypes.MessageThinkingSummaryAppended),
                runEvents.Count(e => e.Type == ConversationEventTypes.ToolCallRequested),
                runEvents.Count(e => e.Type is ConversationEventTypes.ToolCallCompleted or ConversationEventTypes.ToolCallFailed),
                runEvents.Count(e => e.Type == ConversationEventTypes.ToolCallFailed),
                Math.Max(0, (long)(lastAt - firstAt).TotalMilliseconds),
                true);
            byMessageId[group.Key] = new CompletedMessageProcess(completed.RunId, summary);
        }

        return byMessageId;
    }

    private static IReadOnlyList<ProcessSummaryItem> BuildTranscriptProcessItems(
        ChatMessageEntity message,
        ILogger logger)
    {
        if (message.Role != "agent" || string.IsNullOrWhiteSpace(message.ThinkingJson))
            return Array.Empty<ProcessSummaryItem>();

        // P1-3 T3：ThinkingJson 支持旧数组 / v2 紧凑双格式，统一由 ReasoningCompactCodec 解析
        // （utf8 字节偏移切片、timestamp delta 还原、SHA-256 校验均已封装在 codec 内）。
        var decoded = ReasoningCompactCodec.Decode(message.ThinkingJson);
        if (decoded is null)
        {
            // 结构无效（非法 JSON / 乱序偏移 / 偏移切在多字节字符中间）：fail-open，不阻断 UI。
            logger.LogWarning(
                "[Projection] Failed to decode ThinkingJson for message {MessageId}; process items omitted (fail-open).",
                message.MessageId);
            return Array.Empty<ProcessSummaryItem>();
        }

        if (!decoded.HashValid)
        {
            // hash 与 text 不匹配：数据可能被篡改，fail-open 返回空，不展示不可信的推理内容。
            logger.LogWarning(
                "[Projection] ThinkingJson hash mismatch for message {MessageId}; process items omitted (fail-open).",
                message.MessageId);
            return Array.Empty<ProcessSummaryItem>();
        }

        var items = new List<ProcessSummaryItem>();
        var index = 0;
        foreach (var chunk in decoded.Chunks)
        {
            if (string.IsNullOrWhiteSpace(chunk.Text))
                continue;

            items.Add(new ProcessSummaryItem(
                $"{message.MessageId}:thinking:{index++}",
                "thinking",
                "done",
                chunk.Text,
                DateTimeOffset.FromUnixTimeMilliseconds(chunk.Timestamp)));
        }

        return items;
    }

    private static string? ReadString(string json, string propertyName)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty(propertyName, out var value))
                return null;
            return value.ValueKind switch
            {
                JsonValueKind.String => value.GetString(),
                JsonValueKind.Object or JsonValueKind.Array => value.GetRawText(),
                _ => null,
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static int? ReadInt(string json, string propertyName)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty(propertyName, out var value))
                return null;
            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number))
                return number;
            if (value.ValueKind == JsonValueKind.String
                && int.TryParse(value.GetString(), out var parsed))
                return parsed;
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool TryBuildEventProcessItem(
        ConversationEventEntity evt,
        out ProcessSummaryItem item)
    {
        var kind = MapProcessKind(evt.Type);
        if (kind is null)
        {
            item = null!;
            return false;
        }

        var name = ReadString(evt.Payload, "name");
        var arguments = ReadString(evt.Payload, "arguments");
        var output = ReadString(evt.Payload, "output");
        var error = ReadString(evt.Payload, "error");
        var exitCode = ReadInt(evt.Payload, "exitCode") ?? ReadInt(evt.Payload, "exit_code");
        var message = ReadString(evt.Payload, "message")
            ?? BuildToolProcessMessage(kind, name, arguments, output, error, exitCode);
        var toolCallId = ReadString(evt.Payload, "toolCallId");
        var text = ReadString(evt.Payload, "delta")
            ?? ReadString(evt.Payload, "text")
            ?? message
            ?? output
            ?? error
            ?? name
            ?? kind;
        if (string.IsNullOrWhiteSpace(text))
        {
            item = null!;
            return false;
        }

        var status = ReadString(evt.Payload, "status") ?? kind switch
        {
            "tool_call" => "running",
            "tool_result" when !string.IsNullOrWhiteSpace(error) => "error",
            "tool_result" when exitCode.HasValue => exitCode.Value == 0 ? "success" : "error",
            "tool_result" => "success",
            _ => "done",
        };

        item = new ProcessSummaryItem(
            string.IsNullOrWhiteSpace(evt.EventId) ? $"{evt.ConversationId}:{evt.Sequence}" : evt.EventId,
            kind,
            status,
            text,
            ParseOccurredAt(evt.OccurredAt),
            name,
            arguments,
            output,
            exitCode,
            message,
            toolCallId);
        return true;
    }

    private static string? MapProcessKind(string eventType)
        => eventType switch
        {
            ConversationEventTypes.MessageThinkingSummaryAppended => "thinking",
            ConversationEventTypes.ToolCallRequested => "tool_call",
            ConversationEventTypes.ToolCallCompleted or ConversationEventTypes.ToolCallFailed => "tool_result",
            _ => null,
        };

    private static string? BuildToolProcessMessage(
        string eventType,
        string? name,
        string? arguments,
        string? output,
        string? error,
        int? exitCode)
    {
        if (eventType == "tool_call")
            return $"调用工具: {name ?? "工具"}{(string.IsNullOrWhiteSpace(arguments) ? "" : $"\n参数: {arguments}")}";
        if (eventType == "tool_result")
            return $"{name ?? "工具"} {(!string.IsNullOrWhiteSpace(error) || exitCode is not null and not 0 ? "✗" : "✓")}\n{output ?? error ?? "(empty)"}";
        return null;
    }

    private static DateTimeOffset ParseOccurredAt(string value)
        => DateTimeOffset.TryParse(value, out var parsed)
            ? parsed
            : DateTimeOffset.UtcNow;

    private static bool IsTerminalEvent(string eventType)
        => eventType is
            ConversationEventTypes.TurnCompleted or
            ConversationEventTypes.TurnFailed or
            ConversationEventTypes.TurnCancelled or
            ConversationEventTypes.RunLeaseLost;

    private sealed record CompletedMessageProcess(
        string? RunId,
        ConversationProcessSummary Summary);

    private sealed record ActiveProcessEventMetadata(string Type, string OccurredAt);

    private sealed record ConversationMessageRow(
        long Id,
        string MessageId,
        string Role,
        string Content,
        string? TurnId,
        string? MetadataJson,
        long CreatedAt);

    private sealed record MessageProcessEvent(
        string MessageId,
        string? RunId,
        string Type,
        long Sequence,
        string OccurredAt);

    private sealed record PuddingMessageMetadata(
        string? SourceKind,
        string? SourceId,
        string? SourceName,
        string? MessageType);

    private static IReadOnlyDictionary<string, string>? ParseMetadataJson(string? metadataJson)
    {
        if (string.IsNullOrWhiteSpace(metadataJson))
            return null;

        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(metadataJson);
        }
        catch
        {
            return null;
        }
    }
}
