using Microsoft.EntityFrameworkCore;
using PuddingCode.Platform;
using PuddingCode.Services;
using PuddingPlatform.Data;
using PuddingPlatform.Data.Entities;
using System.Text;
using System.Text.Json;

namespace PuddingPlatform.Services.AgentChat;

/// <summary>Builds renderable Agent conversation projections from the canonical Conversation Event Store.</summary>
public interface IAgentConversationProjectionService
{
    Task<AgentConversationView> GetConversationAsync(string workspaceId, string ownerUserId, string agentId, CancellationToken ct);

    /// <summary>Lightweight cursor check — returns the canonical conversation head.</summary>
    Task<long> GetConversationCursorAsync(string workspaceId, string ownerUserId, string agentId, CancellationToken ct);
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

        var messageRows = await db.ChatMessages
            .AsNoTracking()
            .Where(m => m.SessionId == main.SessionId)
            .OrderByDescending(m => m.CreatedAt)
            .ThenByDescending(m => m.Id)
            .Take(100)
            .ToListAsync(ct);
        messageRows.Reverse();

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
                .ToListAsync(ct);

        var completedProcessByMessageId = BuildCompletedProcessByMessageId(messageEvents);
        var messages = messageRows
            .Select(m => BuildConversationMessageView(
                m,
                ownerUserId,
                agentId,
                main.Title,
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
            var activeEvents = await db.ConversationEvents
                .AsNoTracking()
                .Where(e => e.ConversationId == main.SessionId && e.RunId == latestRunEvent.RunId)
                .OrderBy(e => e.Sequence)
                .ToListAsync(ct);
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
                activeEvents);
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

    public async Task<long> GetConversationCursorAsync(
        string workspaceId,
        string ownerUserId,
        string agentId,
        CancellationToken ct)
    {
        ownerUserId = NormalizeOwnerUserId(ownerUserId);

        var sessions = await api.GetSessionsAsync(workspaceId, ct);
        var main = await ResolveAgentMainSessionAsync(workspaceId, ownerUserId, agentId, sessions, ct);

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
        string? commandClientId,
        IReadOnlyList<ConversationEventEntity> events)
    {
        if (events.Count == 0 || events.Any(e => IsTerminalEvent(e.Type)))
            return null;

        var markdown = new StringBuilder();
        var processItems = new List<ProcessSummaryItem>();
        foreach (var evt in events)
        {
            if (evt.Type == ConversationEventTypes.MessageContentAppended)
            {
                markdown.Append(ReadString(evt.Payload, "delta")
                    ?? ReadString(evt.Payload, "text")
                    ?? ReadString(evt.Payload, "content")
                    ?? "");
                continue;
            }

            if (TryBuildEventProcessItem(evt, out var item))
                processItems.Add(item);
        }

        var startedAt = ParseOccurredAt(
            events.FirstOrDefault(e => e.Type == ConversationEventTypes.TurnStarted)?.OccurredAt
            ?? events[0].OccurredAt);
        var updatedAt = ParseOccurredAt(events[^1].OccurredAt);
        if (DateTimeOffset.UtcNow - updatedAt > ActiveRunStaleAfter)
            return null;

        var runId = events[^1].RunId;
        if (string.IsNullOrWhiteSpace(runId))
            return null;

        return new AgentRunView(
            runId,
            workspaceId,
            ownerUserId,
            agentId,
            main.SessionId,
            commandClientId,
            "running",
            "正在输出",
            main.Title ?? "",
            events[^1].Sequence,
            new AgentOutputSnapshot(markdown.ToString(), processItems),
            startedAt,
            updatedAt,
            null);
    }

    private static ConversationMessageView BuildConversationMessageView(
        ChatMessageEntity message,
        string ownerUserId,
        string agentId,
        string? agentTitle,
        string? turnId,
        IReadOnlyDictionary<string, CompletedMessageProcess> completedProcessByMessageId)
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
        completedProcessByMessageId.TryGetValue(message.MessageId, out var completedProcess);

        return new ConversationMessageView(
            string.IsNullOrWhiteSpace(message.MessageId) ? message.Id.ToString() : message.MessageId,
            completedProcess?.RunId,
            uiRole,
            sourceId,
            sourceName,
            DateTimeOffset.FromUnixTimeMilliseconds(message.CreatedAt),
            message.Content,
            "succeeded",
            BuildMessageProcessItems(message, completedProcess?.Items))
        {
            TurnId = turnId,
            SourceKind = sourceKind,
            MessageType = messageType,
            LlmRole = message.Role,
            Metadata = ParseMetadataJson(message.MetadataJson),
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
        IReadOnlyList<ProcessSummaryItem>? eventItems)
    {
        var transcriptItems = BuildTranscriptProcessItems(message);
        if (message.Role != "agent" || eventItems is null || eventItems.Count == 0)
            return transcriptItems;

        if (eventItems.Any(item => item.Kind == "thinking") || transcriptItems.Count == 0)
            return eventItems;

        return transcriptItems.Concat(eventItems).ToList();
    }

    private static IReadOnlyDictionary<string, CompletedMessageProcess> BuildCompletedProcessByMessageId(
        IReadOnlyList<ConversationEventEntity> events)
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
                .OrderBy(e => e.Sequence);
            var processItems = runEvents
                .Select(e => TryBuildEventProcessItem(e, out var item) ? item : null)
                .Where(item => item is not null)
                .Cast<ProcessSummaryItem>()
                .ToList();

            if (processItems.Count > 0)
                byMessageId[group.Key] = new CompletedMessageProcess(completed.RunId, processItems);
        }

        return byMessageId;
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
                    $"{message.MessageId}:thinking:{index++}",
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
            message);
        return true;
    }

    private static string? MapProcessKind(string eventType)
        => eventType switch
        {
            ConversationEventTypes.MessageThinkingSummaryAppended => "thinking",
            ConversationEventTypes.ToolCallRequested => "tool_call",
            ConversationEventTypes.ToolCallCompleted or ConversationEventTypes.ToolCallFailed => "tool_result",
            "subagent.spawned" or "subagent.delta" or "subagent.thinking" or
            "subagent.tool_call" or "subagent.tool_result" or "subagent.completed" => eventType,
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
        IReadOnlyList<ProcessSummaryItem> Items);

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
