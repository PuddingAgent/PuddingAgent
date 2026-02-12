using Microsoft.EntityFrameworkCore;
using PuddingCode.Platform;
using PuddingCode.Services;
using PuddingPlatform.Data;
using PuddingPlatform.Data.Entities;
using System.Text;
using System.Text.Json;

namespace PuddingPlatform.Services.AgentChat;

/// <summary>Builds renderable Agent conversation projections from existing session and transcript facts.</summary>
public interface IAgentConversationProjectionService
{
    Task<AgentConversationView> GetConversationAsync(string workspaceId, string ownerUserId, string agentId, CancellationToken ct);

    /// <summary>Lightweight cursor check — returns max event sequence without building the full projection.</summary>
    Task<int> GetConversationCursorAsync(string workspaceId, string ownerUserId, string agentId, CancellationToken ct);
}

/// <summary>Default conversation projection service for the single-user Agent chat client.</summary>
public sealed class AgentConversationProjectionService(
    PlatformApiClient api,
    WorkspaceAgentFileService workspaceAgentFileService,
    SessionRedirectStore redirectStore,
    PlatformDbContext db) : IAgentConversationProjectionService
{
    private const string DefaultOwnerUserId = "single-user";
    private static readonly TimeSpan ActiveRunStaleAfter = TimeSpan.FromMinutes(5);
    private static readonly HashSet<string> StatusEventTypes = new(StringComparer.Ordinal)
    {
        "delta",
        "thinking",
        "tool_call",
        "tool_result",
        "subagent.spawned",
        "subagent.delta",
        "subagent.thinking",
        "subagent.tool_call",
        "subagent.tool_result",
        "subagent.completed",
        "done",
        "error",
        "cancelled",
        "session.closed",
    };

    public async Task<AgentConversationView> GetConversationAsync(string workspaceId, string ownerUserId, string agentId, CancellationToken ct)
    {
        ownerUserId = NormalizeOwnerUserId(ownerUserId);

        var sessions = await api.GetSessionsAsync(workspaceId, ct);
        var main = await ResolveAgentMainSessionAsync(workspaceId, ownerUserId, agentId, sessions, ct);

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

        var eventRows = await db.SessionEventLogs
            .AsNoTracking()
            .Where(e => e.SessionId == main.SessionId)
            .OrderBy(e => e.SequenceNum)
            .ToListAsync(ct);

        var completedProcessItems = BuildCompletedProcessItemsByReply(eventRows);
        var messageRows = await db.ChatMessages
            .AsNoTracking()
            .Where(m => m.SessionId == main.SessionId)
            .OrderByDescending(m => m.CreatedAt)
            .Take(100)
            .ToListAsync(ct);
        messageRows = messageRows
            .OrderBy(m => m.CreatedAt)
            .ThenBy(m => m.Id)
            .ToList();

        var messages = messageRows
            .Select(m => BuildConversationMessageView(m, ownerUserId, agentId, main.Title, completedProcessItems))
            .ToList();

        var activeRun = BuildActiveRun(
            workspaceId,
            ownerUserId,
            agentId,
            main,
            eventRows);
        var eventCursor = eventRows.Count == 0 ? 0 : eventRows[^1].SequenceNum;

        return new AgentConversationView(
            workspaceId,
            ownerUserId,
            agentId,
            main.SessionId,
            messages,
            activeRun,
            eventCursor,
            main.LastActiveAt);
    }

    /// <summary>Lightweight cursor query — only reads the max sequence number, no projection or message fetch.</summary>
    public async Task<int> GetConversationCursorAsync(string workspaceId, string ownerUserId, string agentId, CancellationToken ct)
    {
        ownerUserId = NormalizeOwnerUserId(ownerUserId);

        var sessions = await api.GetSessionsAsync(workspaceId, ct);
        var main = await ResolveAgentMainSessionAsync(workspaceId, ownerUserId, agentId, sessions, ct);

        if (main is null) return 0;

        var maxSeq = await db.SessionEventLogs
            .AsNoTracking()
            .Where(e => e.SessionId == main.SessionId)
            .MaxAsync(e => (int?)e.SequenceNum, ct);

        return maxSeq ?? 0;
    }

    private static string NormalizeOwnerUserId(string? ownerUserId)
        => string.IsNullOrWhiteSpace(ownerUserId) || ownerUserId == "admin"
            ? DefaultOwnerUserId
            : ownerUserId;

    private async Task<SessionRecord?> ResolveAgentMainSessionAsync(
        string workspaceId,
        string ownerUserId,
        string agentId,
        IReadOnlyList<SessionRecord> sessions,
        CancellationToken ct)
    {
        var agent = await workspaceAgentFileService.GetAgentAsync(workspaceId, agentId, ct);
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

    private static AgentRunView? BuildActiveRun(
        string workspaceId,
        string ownerUserId,
        string agentId,
        SessionRecord main,
        IReadOnlyList<SessionEventLogEntity> events)
    {
        var latestStatusEvent = events.LastOrDefault(e => StatusEventTypes.Contains(e.EventType));
        if (latestStatusEvent is null || IsTerminalEvent(latestStatusEvent.EventType))
            return null;

        var activeEvents = SelectActiveRunEvents(events, latestStatusEvent);
        if (activeEvents.Count == 0)
            return null;

        var markdown = new StringBuilder();
        var processItems = new List<ProcessSummaryItem>();
        foreach (var evt in activeEvents)
        {
            if (evt.EventType == "delta")
            {
                markdown.Append(ReadString(evt.Data, "delta")
                    ?? ReadString(evt.Data, "text")
                    ?? ReadString(evt.Data, "content")
                    ?? "");
                continue;
            }

            if (evt.EventType is "metadata" or "done")
                continue;

            if (TryBuildEventProcessItem(evt, out var item))
                processItems.Add(item);
        }

        var startedAt = ParseRecordedAt(activeEvents[0].RecordedAt);
        var updatedAt = ParseRecordedAt(activeEvents[^1].RecordedAt);
        if (DateTimeOffset.UtcNow - updatedAt > ActiveRunStaleAfter)
            return null;

        var cursor = activeEvents[^1].SequenceNum;
        var activeMessageId = activeEvents
            .Select(e => ReadMessageId(e.Data))
            .FirstOrDefault(id => !string.IsNullOrWhiteSpace(id));
        return new AgentRunView(
            activeEvents.LastOrDefault(e => !string.IsNullOrWhiteSpace(e.ExecutionId))?.ExecutionId
                ?? $"{main.SessionId}:{cursor}",
            workspaceId,
            ownerUserId,
            agentId,
            main.SessionId,
            activeMessageId,
            "running",
            "正在输出",
            main.Title ?? "",
            cursor,
            new AgentOutputSnapshot(markdown.ToString(), processItems),
            startedAt,
            updatedAt,
            null);
    }

    private static IReadOnlyList<SessionEventLogEntity> SelectActiveRunEvents(
        IReadOnlyList<SessionEventLogEntity> events,
        SessionEventLogEntity latestStatusEvent)
    {
        var activeMessageId = ReadMessageId(latestStatusEvent.Data);
        if (!string.IsNullOrWhiteSpace(activeMessageId))
        {
            var matching = events
                .Where(e => string.Equals(ReadMessageId(e.Data), activeMessageId, StringComparison.Ordinal))
                .ToList();
            if (matching.Count > 0)
                return matching;
        }

        var latestIndex = -1;
        for (var i = events.Count - 1; i >= 0; i--)
        {
            if (ReferenceEquals(events[i], latestStatusEvent) || events[i].SequenceNum == latestStatusEvent.SequenceNum)
            {
                latestIndex = i;
                break;
            }
        }

        if (latestIndex < 0)
            latestIndex = events.Count - 1;

        var startIndex = 0;
        for (var i = latestIndex - 1; i >= 0; i--)
        {
            if (IsTerminalEvent(events[i].EventType))
            {
                startIndex = i + 1;
                break;
            }
        }

        return events
            .Skip(startIndex)
            .Take(latestIndex - startIndex + 1)
            .ToList();
    }

    private static string? ReadString(string json, string propertyName)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? ReadMessageId(string json)
        => ReadString(json, "messageId")
            ?? ReadString(json, "message_id")
            ?? ReadString(json, "MessageId");

    private static ConversationMessageView BuildConversationMessageView(
        ChatMessageEntity message,
        string ownerUserId,
        string agentId,
        string? agentTitle,
        IReadOnlyDictionary<string, IReadOnlyList<ProcessSummaryItem>> completedProcessItems)
    {
        var metadata = ParsePuddingMessageMetadata(message.Content);
        var sourceKind = metadata?.SourceKind
            ?? (string.Equals(message.Role, "agent", StringComparison.OrdinalIgnoreCase) ? "agent" : "user");
        var sourceId = metadata?.SourceId
            ?? (string.Equals(sourceKind, "agent", StringComparison.OrdinalIgnoreCase) ? agentId : ownerUserId);
        var sourceName = metadata?.SourceName
            ?? (string.Equals(sourceKind, "agent", StringComparison.OrdinalIgnoreCase) ? agentTitle ?? agentId : "Pudding Admin");
        var messageType = metadata?.MessageType
            ?? (string.Equals(message.Role, "agent", StringComparison.OrdinalIgnoreCase) ? "agent_output" : "user_message");
        var uiRole = string.Equals(sourceKind, "agent", StringComparison.OrdinalIgnoreCase)
            ? "agent"
            : string.Equals(sourceKind, "system", StringComparison.OrdinalIgnoreCase)
                ? "system"
                : "user";

        return new ConversationMessageView(
            message.Id.ToString(),
            null,
            uiRole,
            sourceId,
            sourceName,
            DateTimeOffset.FromUnixTimeMilliseconds(message.CreatedAt),
            message.Content,
            "succeeded",
            BuildMessageProcessItems(message, completedProcessItems))
        {
            SourceKind = sourceKind,
            MessageType = messageType,
            LlmRole = message.Role,
        };
    }

    private static PuddingMessageMetadata? ParsePuddingMessageMetadata(string content)
    {
        var envelope = AgentContextEnvelopeRenderer.TryParse(content);
        if (envelope is null)
            return null;

        var from = envelope.From;
        return new PuddingMessageMetadata(
            string.IsNullOrWhiteSpace(from?.Kind) ? null : from!.Kind,
            string.IsNullOrWhiteSpace(from?.Id) ? null : from!.Id,
            string.IsNullOrWhiteSpace(from?.DisplayName) ? null : from!.DisplayName,
            string.IsNullOrWhiteSpace(envelope.MessageType) ? null : envelope.MessageType);
    }

    private static IReadOnlyList<ProcessSummaryItem> BuildMessageProcessItems(
        ChatMessageEntity message,
        IReadOnlyDictionary<string, IReadOnlyList<ProcessSummaryItem>> completedProcessItems)
    {
        var transcriptItems = BuildTranscriptProcessItems(message);
        if (message.Role != "agent"
            || !completedProcessItems.TryGetValue(message.Content, out var eventItems)
            || eventItems.Count == 0)
        {
            return transcriptItems;
        }

        if (eventItems.Any(item => item.Kind == "thinking") || transcriptItems.Count == 0)
            return eventItems;

        return transcriptItems.Concat(eventItems).ToList();
    }

    private static IReadOnlyDictionary<string, IReadOnlyList<ProcessSummaryItem>> BuildCompletedProcessItemsByReply(
        IReadOnlyList<SessionEventLogEntity> events)
    {
        var byReply = new Dictionary<string, IReadOnlyList<ProcessSummaryItem>>(StringComparer.Ordinal);
        var grouped = events
            .Select(e => new { Event = e, MessageId = ReadMessageId(e.Data) })
            .Where(x => !string.IsNullOrWhiteSpace(x.MessageId))
            .GroupBy(x => x.MessageId!, StringComparer.Ordinal);

        foreach (var group in grouped)
        {
            var runEvents = group.Select(x => x.Event).OrderBy(e => e.SequenceNum).ToList();
            var done = runEvents.LastOrDefault(e => e.EventType == "done");
            var reply = done is null ? null : ReadString(done.Data, "reply");
            if (string.IsNullOrWhiteSpace(reply))
                continue;

            var processItems = runEvents
                .Where(e => e.EventType is not "metadata" and not "delta" and not "usage" and not "done")
                .Select(e => TryBuildEventProcessItem(e, out var item) ? item : null)
                .Where(item => item is not null)
                .Cast<ProcessSummaryItem>()
                .ToList();

            if (processItems.Count > 0)
                byReply[reply] = processItems;
        }

        return byReply;
    }

    private static IReadOnlyList<ProcessSummaryItem> BuildTranscriptProcessItems(ChatMessageEntity message)
    {
        if (message.Role != "agent" || string.IsNullOrWhiteSpace(message.ThinkingJson))
            return Array.Empty<ProcessSummaryItem>();

        try
        {
            using var doc = JsonDocument.Parse(message.ThinkingJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                return Array.Empty<ProcessSummaryItem>();

            var items = new List<ProcessSummaryItem>();
            var index = 0;
            foreach (var element in doc.RootElement.EnumerateArray())
            {
                var text = element.TryGetProperty("text", out var textValue)
                           && textValue.ValueKind == JsonValueKind.String
                    ? textValue.GetString()
                    : null;
                if (string.IsNullOrWhiteSpace(text))
                    continue;

                var timestamp = element.TryGetProperty("timestamp", out var timestampValue)
                    && timestampValue.TryGetInt64(out var timestampMs)
                        ? DateTimeOffset.FromUnixTimeMilliseconds(timestampMs)
                        : DateTimeOffset.FromUnixTimeMilliseconds(message.CreatedAt);

                items.Add(new ProcessSummaryItem(
                    $"{message.Id}:thinking:{index++}",
                    "thinking",
                    "done",
                    text,
                    timestamp));
            }

            return items;
        }
        catch (JsonException)
        {
            return Array.Empty<ProcessSummaryItem>();
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

    private static bool TryBuildEventProcessItem(SessionEventLogEntity evt, out ProcessSummaryItem item)
    {
        var name = ReadString(evt.Data, "name");
        var arguments = ReadString(evt.Data, "arguments");
        var output = ReadString(evt.Data, "output");
        var error = ReadString(evt.Data, "error");
        var exitCode = ReadInt(evt.Data, "exitCode") ?? ReadInt(evt.Data, "exit_code");
        var message = ReadString(evt.Data, "message")
            ?? BuildToolProcessMessage(evt.EventType, name, arguments, output, error, exitCode);
        var text = ReadString(evt.Data, "delta")
            ?? ReadString(evt.Data, "text")
            ?? message
            ?? output
            ?? error
            ?? name
            ?? evt.EventType;

        if (string.IsNullOrWhiteSpace(text))
        {
            item = null!;
            return false;
        }

        var status = ReadString(evt.Data, "status")
            ?? (evt.EventType == "tool_result"
                ? exitCode == 0 ? "success" : "error"
                : "done");

        item = new ProcessSummaryItem(
            evt.Id == 0 ? $"{evt.SessionId}:{evt.SequenceNum}" : evt.Id.ToString(),
            evt.EventType,
            status,
            text,
            ParseRecordedAt(evt.RecordedAt),
            name,
            arguments,
            output,
            exitCode,
            message);
        return true;
    }

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
            return $"{name ?? "工具"} {(exitCode == 0 ? "✓" : "✗")}\n{output ?? error ?? "(empty)"}";
        return null;
    }

    private static DateTimeOffset ParseRecordedAt(string value)
        => DateTimeOffset.TryParse(value, out var parsed)
            ? parsed
            : DateTimeOffset.UtcNow;

    private static bool IsTerminalEvent(string eventType)
        => eventType switch
        {
            "done" or "error" or "cancelled" or "session.closed" or
            "context.compaction.completed" or "context.compaction.failed" => true,
            _ => false,
        };

    private sealed record PuddingMessageMetadata(
        string? SourceKind,
        string? SourceId,
        string? SourceName,
        string? MessageType);
}
