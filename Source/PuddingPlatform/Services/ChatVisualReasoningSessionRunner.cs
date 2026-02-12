using PuddingCode.Abstractions;
using PuddingCode.Models;
using PuddingCode.Observability;
using PuddingCode.Platform;
using PuddingCode.Runtime;
using PuddingPlatform.Data.Dtos;

namespace PuddingPlatform.Services;

public sealed record ChatVisualReasoningSessionRunRequest
{
    public required string WorkspaceId { get; init; }
    public required string RoomId { get; init; }
    public required string ParticipantId { get; init; }
    public required string AgentId { get; init; }
    public string? AgentDisplayName { get; init; }
    public string? AvatarUrl { get; init; }
    public required AdminChatRequest ChatRequest { get; init; }
    public required string Provider { get; init; }
    public required string Model { get; init; }
    public string? SessionId { get; init; }
    public string? MessageId { get; init; }
    public RuntimeTraceContext? Trace { get; init; }
}

public sealed record ChatVisualReasoningSessionRunResult
{
    public required string SessionId { get; init; }
    public required string MessageId { get; init; }
    public bool Success { get; init; }
    public string? Reply { get; init; }
    public string? ErrorMessage { get; init; }
}

/// <summary>
/// Projects visual reasoning streams into the existing chat SSE protocol.
/// This keeps camera-based reasoning on the same session timeline as text chat.
/// </summary>
public sealed class ChatVisualReasoningSessionRunner(
    ChatVisualReasoningRequestFactory requestFactory,
    IVisualReasoningService visualReasoningService,
    ISessionOutputWriter outputWriter)
{
    public async Task<ChatVisualReasoningSessionRunResult> RunAsync(
        ChatVisualReasoningSessionRunRequest request,
        CancellationToken ct = default)
    {
        var chatSessionId = string.IsNullOrWhiteSpace(request.SessionId)
            ? request.ChatRequest.SessionId ?? Guid.NewGuid().ToString("N")
            : request.SessionId;
        var messageId = string.IsNullOrWhiteSpace(request.MessageId)
            ? Guid.NewGuid().ToString("N")
            : request.MessageId;
        var trace = (request.Trace ?? RuntimeTraceContext.CreateNew(
                sessionId: chatSessionId,
                workspaceId: request.WorkspaceId))
            .WithSession(chatSessionId, request.WorkspaceId);

        var chatRequest = request.ChatRequest with
        {
            SessionId = request.ChatRequest.SessionId ?? chatSessionId,
        };

        var visualRequest = await requestFactory.BuildAsync(
            request.WorkspaceId,
            request.RoomId,
            request.ParticipantId,
            chatRequest,
            request.Provider,
            request.Model,
            trace.TraceId,
            ct);

        visualRequest = visualRequest with
        {
            Metadata = MergeRunMetadata(visualRequest.Metadata, chatSessionId, messageId, request.AgentId),
        };

        await WriteAsync(
            chatSessionId,
            request.WorkspaceId,
            ServerSentEventFrame.Json(SseEventTypes.Metadata, BuildMetadataPayload(
                request,
                visualRequest,
                chatSessionId,
                messageId)),
            trace,
            ct);

        var reply = new System.Text.StringBuilder();
        string? providerRequestId = null;

        try
        {
            await foreach (var item in visualReasoningService.StreamAsync(visualRequest, ct))
            {
                providerRequestId = item.ProviderRequestId ?? providerRequestId;

                if (item.Type == VisualReasoningStreamEventTypes.ReasoningDelta
                    && !string.IsNullOrEmpty(item.ReasoningDelta))
                {
                    await WriteAsync(
                        chatSessionId,
                        request.WorkspaceId,
                        ServerSentEventFrame.Json(SseEventTypes.Thinking, new
                        {
                            messageId,
                            delta = item.ReasoningDelta,
                        }),
                        trace,
                        ct);
                }
                else if (item.Type == VisualReasoningStreamEventTypes.AnswerDelta
                    && !string.IsNullOrEmpty(item.AnswerDelta))
                {
                    reply.Append(item.AnswerDelta);
                    await WriteAsync(
                        chatSessionId,
                        request.WorkspaceId,
                        ServerSentEventFrame.Json(SseEventTypes.Delta, new
                        {
                            messageId,
                            delta = item.AnswerDelta,
                        }),
                        trace,
                        ct);
                }
                else if (item.Type == VisualReasoningStreamEventTypes.Failed)
                {
                    var message = string.IsNullOrWhiteSpace(item.ErrorMessage)
                        ? "Visual reasoning failed."
                        : item.ErrorMessage;
                    await WriteErrorAsync(chatSessionId, request.WorkspaceId, messageId, message, trace, ct);
                    return new ChatVisualReasoningSessionRunResult
                    {
                        SessionId = chatSessionId,
                        MessageId = messageId,
                        Success = false,
                        ErrorMessage = message,
                    };
                }
            }

            var answer = reply.ToString();
            await WriteAsync(
                chatSessionId,
                request.WorkspaceId,
                ServerSentEventFrame.Json(SseEventTypes.Done, new
                {
                    messageId,
                    reply = answer,
                    requestId = providerRequestId,
                    sessionId = chatSessionId,
                    visualSessionId = visualRequest.SessionId,
                    traceId = trace.TraceId,
                }),
                trace,
                ct);

            return new ChatVisualReasoningSessionRunResult
            {
                SessionId = chatSessionId,
                MessageId = messageId,
                Success = true,
                Reply = answer,
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await WriteErrorAsync(chatSessionId, request.WorkspaceId, messageId, ex.Message, trace, CancellationToken.None);
            return new ChatVisualReasoningSessionRunResult
            {
                SessionId = chatSessionId,
                MessageId = messageId,
                Success = false,
                ErrorMessage = ex.Message,
            };
        }
    }

    private async Task WriteAsync(
        string sessionId,
        string workspaceId,
        ServerSentEventFrame frame,
        RuntimeTraceContext trace,
        CancellationToken ct)
    {
        await outputWriter.WriteFrameAsync(sessionId, workspaceId, frame, trace, ct);
    }

    private async Task WriteErrorAsync(
        string sessionId,
        string workspaceId,
        string messageId,
        string message,
        RuntimeTraceContext trace,
        CancellationToken ct)
    {
        await WriteAsync(
            sessionId,
            workspaceId,
            ServerSentEventFrame.Json(SseEventTypes.Error, new
            {
                messageId,
                message,
            }),
            trace,
            ct);
    }

    private static IReadOnlyDictionary<string, string> MergeRunMetadata(
        IReadOnlyDictionary<string, string> source,
        string chatSessionId,
        string messageId,
        string agentId)
    {
        var metadata = new Dictionary<string, string>(source, StringComparer.OrdinalIgnoreCase)
        {
            ["chatSessionId"] = chatSessionId,
            ["messageId"] = messageId,
            ["agent_id"] = agentId,
            ["source_type"] = "agent",
            ["source_id"] = agentId,
        };

        return metadata;
    }

    private static object BuildMetadataPayload(
        ChatVisualReasoningSessionRunRequest request,
        VisualReasoningRequest visualRequest,
        string chatSessionId,
        string messageId)
    {
        visualRequest.Metadata.TryGetValue("cameraSessionId", out var cameraSessionId);
        visualRequest.Metadata.TryGetValue("visionArtifactId", out var visionArtifactId);
        visualRequest.Metadata.TryGetValue("inputMode", out var inputMode);

        return new
        {
            messageId,
            sessionId = chatSessionId,
            routeDecisionId = $"vision:{messageId}",
            source_type = "agent",
            source_id = request.AgentId,
            source_name = string.IsNullOrWhiteSpace(request.AgentDisplayName) ? request.AgentId : request.AgentDisplayName,
            agent_id = request.AgentId,
            audience = request.ChatRequest.Audience,
            target_agent_ids = request.ChatRequest.TargetAgentIds is { Count: > 0 }
                ? string.Join(",", request.ChatRequest.TargetAgentIds)
                : null,
            avatar_url = request.AvatarUrl,
            fanout_index = "0",
            fanout_count = "1",
            inputMode = string.IsNullOrWhiteSpace(inputMode) ? "camera" : inputMode,
            cameraSessionId = string.IsNullOrWhiteSpace(cameraSessionId) ? visualRequest.SessionId : cameraSessionId,
            visionArtifactId = visionArtifactId,
            visualProvider = visualRequest.Provider,
            visualModel = visualRequest.Model,
        };
    }
}
