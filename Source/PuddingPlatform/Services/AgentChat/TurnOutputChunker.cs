using System.Text;
using System.Text.Json;
using PuddingCode.Platform;
using PuddingCode.Runtime;

namespace PuddingPlatform.Services.AgentChat;

/// <summary>
/// ADR-057: Delta 聚合输出分块器。
/// 单 token 不直接成为持久事件。按时间窗口或大小聚合 delta 事件。
/// </summary>
public sealed class TurnOutputChunker
{
    private readonly int _maxBatchMs;
    private readonly int _maxBatchBytes;
    private readonly StringBuilder _contentBuffer = new();
    private readonly StringBuilder _thinkingBuffer = new();
    private readonly List<NewConversationEvent> _batchedEvents = [];
    private long _lastFlushTick;

    public TurnOutputChunker(int maxBatchMs = 50, int maxBatchBytes = 4096)
    {
        _maxBatchMs = maxBatchMs;
        _maxBatchBytes = maxBatchBytes;
        _lastFlushTick = Environment.TickCount64;
    }

    /// <summary>
    /// Feed a Runtime event into the chunker. Returns events that should be persisted immediately.
    /// Terminal events flush pending content but do NOT include the terminal in the returned batch.
    /// Terminal submission is the caller's responsibility via CommitTerminalAsync.
    /// </summary>
    public IReadOnlyList<NewConversationEvent> Feed(TurnExecutionEvent evt, string conversationId, string workspaceId, string turnId, string commandId, string runId, string? messageId)
    {
        var now = Environment.TickCount64;
        var elapsed = now - _lastFlushTick;

        // Terminal events flush pending content but do NOT return the terminal itself.
        // Terminal must be committed atomically via IExecutionJournal.CommitTerminalAsync.
        if (evt.IsTerminal)
        {
            FlushPendingContent(conversationId, workspaceId, turnId, commandId, runId, messageId);
            var result = _batchedEvents.ToList();
            _batchedEvents.Clear();
            return result;
        }

        // Accumulate content deltas.
        if (evt.Type == ConversationEventTypes.MessageContentAppended)
        {
            if (evt.Payload.TryGetProperty("delta", out var delta))
                _contentBuffer.Append(delta.GetString() ?? "");
        }
        else if (evt.Type == ConversationEventTypes.MessageThinkingSummaryAppended)
        {
            if (evt.Payload.TryGetProperty("delta", out var delta))
                _thinkingBuffer.Append(delta.GetString() ?? "");
        }
        else
        {
            // Non-delta events pass through immediately.
            _batchedEvents.Add(MapEvent(evt, conversationId, workspaceId, turnId, commandId, runId, messageId));
            return Drain();
        }

        // Flush if batch exceeded size/time threshold.
        var totalBytes = _contentBuffer.Length + _thinkingBuffer.Length;
        if (totalBytes >= _maxBatchBytes || elapsed >= _maxBatchMs)
        {
            FlushPendingContent(conversationId, workspaceId, turnId, commandId, runId, messageId);
        }

        return Drain();
    }

    /// <summary>
    /// Force flush all pending content. Called before terminal write.
    /// </summary>
    public IReadOnlyList<NewConversationEvent> Flush(string conversationId, string workspaceId, string turnId, string commandId, string runId, string? messageId)
    {
        FlushPendingContent(conversationId, workspaceId, turnId, commandId, runId, messageId);
        var result = _batchedEvents.ToList();
        _batchedEvents.Clear();
        return result;
    }

    private void FlushPendingContent(string conversationId, string workspaceId, string turnId, string commandId, string runId, string? messageId)
    {
        if (_thinkingBuffer.Length > 0)
        {
            using var doc = JsonDocument.Parse(
                $"{{\"delta\":{JsonSerializer.Serialize(_thinkingBuffer.ToString())}}}");
            _batchedEvents.Add(NewEvent(
                ConversationEventTypes.MessageThinkingSummaryAppended,
                conversationId, workspaceId, turnId, commandId, runId, messageId, doc.RootElement));
            _thinkingBuffer.Clear();
        }
        if (_contentBuffer.Length > 0)
        {
            using var doc = JsonDocument.Parse(
                $"{{\"delta\":{JsonSerializer.Serialize(_contentBuffer.ToString())}}}");
            _batchedEvents.Add(NewEvent(
                ConversationEventTypes.MessageContentAppended,
                conversationId, workspaceId, turnId, commandId, runId, messageId, doc.RootElement));
            _contentBuffer.Clear();
        }
        _lastFlushTick = Environment.TickCount64;
    }

    private IReadOnlyList<NewConversationEvent> Drain()
    {
        if (_batchedEvents.Count == 0) return Array.Empty<NewConversationEvent>();
        var result = _batchedEvents.ToList();
        _batchedEvents.Clear();
        return result;
    }

    private static NewConversationEvent MapEvent(
        TurnExecutionEvent evt, string conversationId, string workspaceId, string turnId, string commandId, string runId, string? messageId)
        => NewEvent(evt.Type, conversationId, workspaceId, turnId, commandId, runId, messageId, evt.Payload, evt.ProducerEventId);

    private static NewConversationEvent MapTerminal(
        TurnExecutionEvent evt, string conversationId, string workspaceId, string turnId, string commandId, string runId, string? messageId)
        => NewEvent(evt.Type, conversationId, workspaceId, turnId, commandId, runId, messageId, evt.Payload, evt.ProducerEventId);

    private static NewConversationEvent NewEvent(
        string type, string conversationId, string workspaceId, string turnId, string commandId, string runId, string? messageId,
        JsonElement payload, string? producerEventId = null)
        => new(
            EventId: Guid.NewGuid().ToString("N"),
            Type: type,
            SchemaVersion: 1,
            WorkspaceId: workspaceId,
            TurnId: turnId,
            CommandId: commandId,
            RunId: runId,
            MessageId: messageId,
            CorrelationId: conversationId,
            CausationId: turnId,
            ProducerEventId: producerEventId,
            // NewConversationEvent may outlive the JsonDocument that produced the
            // runtime payload. Persist an owned value at this boundary.
            Payload: payload.Clone()
        );
}
