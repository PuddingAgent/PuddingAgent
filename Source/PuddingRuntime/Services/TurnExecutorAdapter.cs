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
            LlmProfile = context.LlmProfile,
            LlmConfig = context.LlmConfig,
            CapabilityPolicy = context.CapabilityPolicy,
            ToolDefinitions = context.ToolDefinitions,
            SkillPackages = context.SkillPackages,
            MaxRounds = context.MaxRounds ?? 0,
            MaxElapsedSeconds = context.MaxElapsedSeconds ?? 0,
            ExecutionDeadlineUtc = context.ExecutionDeadlineUtc,
            MaxToolCallsTotal = context.MaxToolCallsTotal ?? 0,
            ExecutionIdentity = context.ExecutionIdentity,
            VisualArtifactIds = context.VisualArtifactIds,
        };

        var sawTerminal = false;
        var usageInvocationIndex = 0;

        await foreach (var frame in executionService.ExecuteStreamAsync(request, ct))
        {
            sawTerminal |= frame.Event == "done" || frame.Event == "error" || frame.Event == "cancelled";

            var payload = ParsePayload(frame.Data);
            var (eventType, terminal, terminalInfo) = ConvertFrame(frame.Event, payload);
            var schemaVersion = 1;
            if (eventType == ConversationEventTypes.UsageRecorded)
            {
                usageInvocationIndex++;
                payload = CreateUsageRecordedPayload(
                    payload,
                    context.LlmProfile,
                    usageInvocationIndex);
                schemaVersion = 2;
            }

            yield return new TurnExecutionEvent(
                ProducerEventId: Guid.NewGuid().ToString("N"),
                Type: eventType,
                SchemaVersion: schemaVersion,
                Payload: payload,
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
                Payload: errDoc.RootElement.Clone(),
                IsTerminal: true,
                TerminalInfo: TurnTerminalInfo.Failure(
                    TerminalErrorCodes.ExecutionProtocolError,
                    "Stream ended without terminal event.")
            );
        }
    }

    private static (string Type, bool IsTerminal, TurnTerminalInfo? Info) ConvertFrame(
        string eventType,
        JsonElement payload)
    {
        return eventType switch
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
            _ => (eventType, false, null),
        };
    }

    private static JsonElement CreateUsageRecordedPayload(
        JsonElement usage,
        LlmInvocationProfile profile,
        int invocationIndex)
    {
        var json = JsonSerializer.Serialize(new
        {
            usage,
            providerId = profile.ProviderId,
            profileId = profile.ProfileId,
            modelId = profile.ModelId,
            role = profile.Role,
            invocationIndex,
        });
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static string? TryGetString(JsonElement el, string name)
        => el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static JsonElement? TryGetProperty(JsonElement el, string name)
        => el.TryGetProperty(name, out var v) ? v : null;

    private static JsonElement ParsePayload(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return EmptyPayload();

        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return EmptyPayload();
        }
    }

    private static JsonElement EmptyPayload()
    {
        using var document = JsonDocument.Parse("{}");
        return document.RootElement.Clone();
    }
}
