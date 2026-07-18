using System.Runtime.CompilerServices;
using System.Text.Json;
using PuddingCode.Abstractions;
using PuddingCode.Platform;
using PuddingCode.Runtime;

namespace PuddingRuntime.Services;

/// <summary>
/// ADR-057 Phase 3: ITurnExecutor 桥接适配器。
/// 将现有 AgentExecutionService.ExecuteStreamAsync（SSE 帧）→ TurnExecutionEvent（领域事件）。
/// 短期方案；长期应直接在 AgentExecutionService 中实现 ITurnExecutor。
/// </summary>
public sealed class TurnExecutorAdapter(
    AgentExecutionService executionService,
    ILogger<TurnExecutorAdapter> logger) : ITurnExecutor
{
    public async IAsyncEnumerable<TurnExecutionEvent> ExecuteAsync(
        TurnExecutionContext context,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var request = new RuntimeDispatchRequest
        {
            SessionId = context.ConversationId,
            AgentTemplateId = context.AgentTemplateId ?? "global:general-assistant",
            MessageText = context.MessageText,
            WorkspaceId = context.WorkspaceId,
            UserId = context.UserId,
            AgentInstanceId = context.AgentInstanceId,
            LlmConfig = context.LlmConfig,
            CapabilityPolicy = context.CapabilityPolicy,
            ToolDefinitions = context.ToolDefinitions,
            SkillPackages = context.SkillPackages,
        };

        var sawTerminal = false;

        await foreach (var frame in executionService.ExecuteStreamAsync(request, ct))
        {
            sawTerminal |= frame.Event == "done" || frame.Event == "error" || frame.Event == "cancelled";

            var (eventType, terminal, terminalInfo) = ConvertFrame(frame);

            yield return new TurnExecutionEvent(
                ProducerEventId: Guid.NewGuid().ToString("N"),
                Type: eventType,
                SchemaVersion: 1,
                Payload: ParsePayload(frame.Data),
                IsTerminal: terminal,
                TerminalInfo: terminalInfo
            );
        }

        if (!sawTerminal)
        {
            using var errDoc = JsonDocument.Parse(
                $"{{\"errorCode\":\"{TerminalErrorCodes.ExecutionProtocolError}\"," +
                $"\"message\":\"Stream ended without terminal event.\"}}");
            yield return new TurnExecutionEvent(
                ProducerEventId: Guid.NewGuid().ToString("N"),
                Type: ConversationEventTypes.TurnFailed,
                SchemaVersion: 1,
                Payload: errDoc.RootElement,
                IsTerminal: true,
                TerminalInfo: TurnTerminalInfo.Failure(
                    TerminalErrorCodes.ExecutionProtocolError,
                    "Stream ended without terminal event.")
            );
        }
    }

    private static (string Type, bool IsTerminal, TurnTerminalInfo? Info) ConvertFrame(ServerSentEventFrame frame)
    {
        var payload = ParsePayload(frame.Data);

        return frame.Event switch
        {
            "metadata" => (ConversationEventTypes.TurnStarted, false, null),
            "thinking" => (ConversationEventTypes.MessageThinkingSummaryAppended, false, null),
            "delta" => (ConversationEventTypes.MessageContentAppended, false, null),
            "tool_call" => (ConversationEventTypes.ToolCallRequested, false, null),
            "tool_result" => (ConversationEventTypes.ToolCallCompleted, false, null),
            "usage" => (ConversationEventTypes.UsageRecorded, false, null),
            "done" => (
                ConversationEventTypes.TurnCompleted,
                true,
                TurnTerminalInfo.Success(
                    TryGetString(payload, "reply"),
                    TryGetProperty(payload, "usage"))),
            "error" => (
                ConversationEventTypes.TurnFailed,
                true,
                TurnTerminalInfo.Failure(
                    TryGetString(payload, "code") ?? TerminalErrorCodes.RuntimeExecutionFailed,
                    TryGetString(payload, "message") ?? "Execution failed.")),
            "cancelled" => (
                ConversationEventTypes.TurnCancelled,
                true,
                TurnTerminalInfo.Cancelled()),
            _ => (frame.Event, false, null),
        };
    }

    private static string? TryGetString(JsonElement el, string name)
        => el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static JsonElement? TryGetProperty(JsonElement el, string name)
        => el.TryGetProperty(name, out var v) ? v : null;

    private static JsonElement ParsePayload(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return JsonDocument.Parse("{}").RootElement;
        try { return JsonDocument.Parse(json).RootElement; }
        catch { return JsonDocument.Parse("{}").RootElement; }
    }
}
