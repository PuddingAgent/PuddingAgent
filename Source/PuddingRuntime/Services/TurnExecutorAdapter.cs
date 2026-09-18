using System.Runtime.CompilerServices;
using System.Text.Json;
using PuddingCode.Abstractions;
using PuddingCode.Goals;
using PuddingCode.Platform;
using PuddingCode.Runtime;
using PuddingRuntime.Services.AgentLoop;
using PuddingRuntime.Services.Messaging;

namespace PuddingRuntime.Services;

/// <summary>
/// ADR-057 Phase 3: ITurnExecutor 桥接适配器。
/// 将统一 Runtime Dispatcher 的 SSE 帧转换为 TurnExecutionEvent（领域事件）。
/// Runtime Dispatcher 是所有入口共享的 Agent Busy/Idle 权威；Busy 时等待后重试。
/// </summary>
public sealed class TurnExecutorAdapter(
    IRuntimeAgentDispatcher runtimeDispatcher,
    ILogger<TurnExecutorAdapter> logger,
    AgentExecutionAdmissionCoordinator? admissionCoordinator = null) : ITurnExecutor
{
    private readonly AgentExecutionAdmissionCoordinator _admissionCoordinator =
        admissionCoordinator ?? new AgentExecutionAdmissionCoordinator();

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
            MessageId = context.InboundMessageId,
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
            AudioArtifactIds = context.AudioArtifactIds,
            ContentParts = context.ContentParts,
            CallerLlmSnapshot = context.CallerLlmSnapshot,
            Origin = context.Origin,
            OutputOwnership = context.OutputOwnership,
            TaskPlanId = context.TaskPlanId,
            TaskNodeId = context.TaskNodeId,
            ParentTaskNodeId = context.ParentTaskNodeId,
            UsageBudget = context.UsageBudget,
            ActiveTask = context.ActiveTask,
        };

        var agentId = !string.IsNullOrWhiteSpace(context.AgentInstanceId)
            ? context.AgentInstanceId
            : context.AgentTemplateId ?? "global:general-assistant";
        using var foregroundLease = _admissionCoordinator.AcquireForeground(
            context.WorkspaceId,
            agentId);

        var sawTerminal = false;
        var usageInvocationIndex = 0;
        var busyAttempt = 0;
        var lastBusyLogAt = DateTimeOffset.MinValue;

        while (true)
        {
            var retryAfterBusy = false;
            await foreach (var frame in runtimeDispatcher.DispatchStreamAsync(request, ct))
            {
                var payload = ParsePayload(frame.Data);
                if (IsBusyFrame(frame.Event, payload))
                {
                    retryAfterBusy = true;
                    busyAttempt++;
                    var now = DateTimeOffset.UtcNow;
                    if (busyAttempt == 1 || now - lastBusyLogAt >= TimeSpan.FromSeconds(10))
                    {
                        lastBusyLogAt = now;
                        logger.LogInformation(
                            "[TurnExecutorAdapter] Waiting for foreground admission conversation={ConversationId} run={RunId} attempt={Attempt}",
                            context.ConversationId,
                            context.RunId,
                            busyAttempt);
                    }
                    break;
                }

                sawTerminal |= frame.Event == "done" || frame.Event == "error" || frame.Event == "cancelled";

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

            if (!retryAfterBusy)
                break;

            var delayMs = Math.Min(1000, 100 * (1 << Math.Min(busyAttempt - 1, 4)));
            await Task.Delay(TimeSpan.FromMilliseconds(delayMs), ct);
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
                BuildCompletedTerminalInfo(payload)),
            "error" => (
                ConversationEventTypes.TurnFailed,
                true,
                TurnTerminalInfo.Failure(
                    TryGetString(payload, "errorCode")
                        ?? TryGetString(payload, "code")
                        ?? TerminalErrorCodes.RuntimeExecutionFailed,
                    TryGetString(payload, "message") ?? "Execution failed.")),
            "cancelled" => (
                ConversationEventTypes.TurnCancelled,
                true,
                TurnTerminalInfo.Cancelled()),
            _ => (eventType, false, null),
        };
    }

    /// <summary>
    /// A1（G92-1 S1-c 片6）：从 terminal done payload 构造 Completed 终态信息。
    /// 流式 Runtime 把模型结构化 envelope 原文作为 reply 传播；若 reply 是 envelope JSON，
    /// 提取其中 hidden meta.goal_contract_proposal 的 typed proposal（由
    /// <see cref="AgentLoopResponse"/> fail-closed 解析）；普通 reply 不产生 proposal，
    /// 提取失败也绝不影响 Turn 终态（fail-safe）。
    /// </summary>
    private static TurnTerminalInfo BuildCompletedTerminalInfo(JsonElement payload)
    {
        var reply = TryGetString(payload, "reply");
        return TurnTerminalInfo.Success(
            reply,
            TryGetProperty(payload, "usage"),
            TryExtractGoalContractProposal(reply));
    }

    private static GoalContractProposal? TryExtractGoalContractProposal(string? reply)
    {
        if (string.IsNullOrWhiteSpace(reply))
            return null;

        var trimmed = reply.TrimStart();
        if (!trimmed.StartsWith('{'))
            return null;

        try
        {
            return AgentLoopResponse.Parse(trimmed).Meta?.GoalContractProposal;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static bool IsBusyFrame(string eventType, JsonElement payload)
    {
        if (!string.Equals(eventType, "error", StringComparison.OrdinalIgnoreCase))
            return false;

        var state = TryGetString(payload, "executionState")
            ?? TryGetString(payload, "ExecutionState");
        return string.Equals(state, AgentExecutionState.Busy.ToString(), StringComparison.OrdinalIgnoreCase);
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
