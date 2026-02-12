using PuddingCode.Abstractions;
using PuddingCode.Models;
using PuddingCode.Observability;
using PuddingCode.Platform;
using PuddingCode.Runtime;
using PuddingPlatform.Data.Dtos;

namespace PuddingPlatform.Services;

public sealed record ChatOmniRealtimeSessionRunRequest
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
    public string? Voice { get; init; }
    public string Transport { get; init; } = OmniRealtimeTransports.WebSocket;
    public string? SessionId { get; init; }
    public string? MessageId { get; init; }
    public string? OmniSessionId { get; init; }
    public RuntimeTraceContext? Trace { get; init; }
}

public sealed record ChatOmniRealtimeSessionRunResult
{
    public required string SessionId { get; init; }
    public required string MessageId { get; init; }
    public bool Success { get; init; }
    public string? Reply { get; init; }
    public string? ErrorMessage { get; init; }
}

/// <summary>
/// Projects realtime multimodal model sessions into the existing chat SSE stream.
/// </summary>
public sealed class ChatOmniRealtimeSessionRunner(
    IOmniRealtimeService omniRealtimeService,
    ISessionOutputWriter outputWriter)
{
    public async Task<ChatOmniRealtimeSessionRunResult> RunAsync(
        ChatOmniRealtimeSessionRunRequest request,
        IAsyncEnumerable<OmniRealtimeInputFrame> inputFrames,
        CancellationToken ct = default)
    {
        var chatSessionId = string.IsNullOrWhiteSpace(request.SessionId)
            ? request.ChatRequest.SessionId ?? Guid.NewGuid().ToString("N")
            : request.SessionId;
        var messageId = string.IsNullOrWhiteSpace(request.MessageId)
            ? Guid.NewGuid().ToString("N")
            : request.MessageId;
        var omniSessionId = string.IsNullOrWhiteSpace(request.OmniSessionId)
            ? ResolveMetadataValue(request.ChatRequest.Metadata, "omniSessionId") ?? Guid.NewGuid().ToString("N")
            : request.OmniSessionId;
        var trace = (request.Trace ?? RuntimeTraceContext.CreateNew(
                sessionId: chatSessionId,
                workspaceId: request.WorkspaceId))
            .WithSession(chatSessionId, request.WorkspaceId);

        var omniRequest = BuildOmniRequest(request, chatSessionId, messageId, omniSessionId, trace.TraceId);

        await WriteAsync(
            chatSessionId,
            request.WorkspaceId,
            ServerSentEventFrame.Json(SseEventTypes.Metadata, BuildMetadataPayload(
                request,
                omniRequest,
                chatSessionId,
                messageId,
                omniSessionId)),
            trace,
            ct);

        var reply = new System.Text.StringBuilder();
        string? providerResponseId = null;
        OmniRealtimeUsage? usage = null;

        try
        {
            await foreach (var item in omniRealtimeService.StartAsync(omniRequest, inputFrames, ct))
            {
                providerResponseId = item.ProviderResponseId ?? providerResponseId;
                usage = item.Usage ?? usage;

                if (item.Type == OmniRealtimeStreamEventTypes.InputTranscriptDelta
                    && !string.IsNullOrEmpty(item.TextDelta))
                {
                    await WriteVoiceCaptureStatusAsync(
                        chatSessionId,
                        request.WorkspaceId,
                        messageId,
                        omniSessionId,
                        "transcribing",
                        trace,
                        ct,
                        text: item.TextDelta);
                }
                else if (item.Type == OmniRealtimeStreamEventTypes.InputTranscriptCompleted)
                {
                    await WriteVoiceCaptureStatusAsync(
                        chatSessionId,
                        request.WorkspaceId,
                        messageId,
                        omniSessionId,
                        "awaiting_confirmation",
                        trace,
                        ct,
                        transcript: item.Transcript);
                }
                else if ((item.Type == OmniRealtimeStreamEventTypes.ResponseTextDelta
                        || item.Type == OmniRealtimeStreamEventTypes.ResponseAudioTranscriptDelta)
                    && !string.IsNullOrEmpty(item.TextDelta))
                {
                    reply.Append(item.TextDelta);
                    await WriteAsync(
                        chatSessionId,
                        request.WorkspaceId,
                        ServerSentEventFrame.Json(SseEventTypes.Delta, new
                        {
                            messageId,
                            delta = item.TextDelta,
                        }),
                        trace,
                        ct);
                }
                else if (item.Type == OmniRealtimeStreamEventTypes.ResponseAudioDelta
                    && item.AudioBytes is { Length: > 0 })
                {
                    await WriteAsync(
                        chatSessionId,
                        request.WorkspaceId,
                        ServerSentEventFrame.Json(SseEventTypes.VoicePlaybackStatus, new
                        {
                            messageId,
                            sessionId = omniSessionId,
                            voiceSessionId = omniSessionId,
                            status = "buffering",
                            sampleRate = omniRequest.OutputSampleRate,
                            sequence = item.Sequence,
                            audioBase64 = Convert.ToBase64String(item.AudioBytes),
                        }),
                        trace,
                        ct);
                }
                else if (item.Type == OmniRealtimeStreamEventTypes.ResponseAudioDone)
                {
                    await WriteAsync(
                        chatSessionId,
                        request.WorkspaceId,
                        ServerSentEventFrame.Json(SseEventTypes.VoicePlaybackStatus, new
                        {
                            messageId,
                            sessionId = omniSessionId,
                            voiceSessionId = omniSessionId,
                            status = "completed",
                        }),
                        trace,
                        ct);
                }
                else if (item.Type == OmniRealtimeStreamEventTypes.Failed)
                {
                    var message = string.IsNullOrWhiteSpace(item.ErrorMessage)
                        ? "Omni realtime session failed."
                        : item.ErrorMessage;
                    await WriteFailureAsync(chatSessionId, request.WorkspaceId, messageId, omniSessionId, message, trace, ct);
                    return new ChatOmniRealtimeSessionRunResult
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
                    responseId = providerResponseId,
                    sessionId = chatSessionId,
                    omniSessionId,
                    traceId = trace.TraceId,
                    usage = usage is null ? null : new
                    {
                        inputTokens = usage.InputTokens,
                        outputTokens = usage.OutputTokens,
                        totalTokens = usage.TotalTokens,
                        audioInputTokens = usage.AudioInputTokens,
                        audioOutputTokens = usage.AudioOutputTokens,
                        imageInputTokens = usage.ImageInputTokens,
                        searchCount = usage.SearchCount,
                        searchStrategy = usage.SearchStrategy,
                    },
                }),
                trace,
                ct);

            return new ChatOmniRealtimeSessionRunResult
            {
                SessionId = chatSessionId,
                MessageId = messageId,
                Success = true,
                Reply = answer,
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await WriteFailureAsync(chatSessionId, request.WorkspaceId, messageId, omniSessionId, ex.Message, trace, CancellationToken.None);
            return new ChatOmniRealtimeSessionRunResult
            {
                SessionId = chatSessionId,
                MessageId = messageId,
                Success = false,
                ErrorMessage = ex.Message,
            };
        }
    }

    private static OmniRealtimeSessionRequest BuildOmniRequest(
        ChatOmniRealtimeSessionRunRequest request,
        string chatSessionId,
        string messageId,
        string omniSessionId,
        string traceId)
    {
        var metadata = new Dictionary<string, string>(
            request.ChatRequest.Metadata ?? new Dictionary<string, string>(),
            StringComparer.OrdinalIgnoreCase)
        {
            ["inputMode"] = "omni",
            ["chatSessionId"] = chatSessionId,
            ["messageId"] = messageId,
            ["omniSessionId"] = omniSessionId,
            ["agent_id"] = request.AgentId,
            ["source_type"] = "agent",
            ["source_id"] = request.AgentId,
        };

        return new OmniRealtimeSessionRequest
        {
            WorkspaceId = request.WorkspaceId,
            RoomId = request.RoomId,
            ParticipantId = request.ParticipantId,
            SessionId = omniSessionId,
            Provider = request.Provider,
            Model = request.Model,
            Transport = request.Transport,
            OutputModalities = [OmniRealtimeModalities.Text, OmniRealtimeModalities.Audio],
            Voice = request.Voice,
            TurnMode = ResolveMetadataValue(request.ChatRequest.Metadata, "turnMode") ?? OmniRealtimeTurnModes.SemanticVad,
            VadThreshold = TryParseDouble(request.ChatRequest.Metadata, "vadThreshold"),
            SilenceDurationMs = TryParseInt(request.ChatRequest.Metadata, "silenceDurationMs"),
            Instructions = ResolveMetadataValue(request.ChatRequest.Metadata, "instructions"),
            EnableInputAudioTranscription = true,
            InputAudioTranscriptionModel = ResolveMetadataValue(request.ChatRequest.Metadata, "inputAudioTranscriptionModel")
                ?? "qwen3-asr-flash-realtime",
            TraceId = traceId,
            Metadata = metadata,
        };
    }

    private async Task WriteFailureAsync(
        string chatSessionId,
        string workspaceId,
        string messageId,
        string omniSessionId,
        string message,
        RuntimeTraceContext trace,
        CancellationToken ct)
    {
        await WriteAsync(
            chatSessionId,
            workspaceId,
            ServerSentEventFrame.Json(SseEventTypes.VoicePlaybackStatus, new
            {
                messageId,
                sessionId = omniSessionId,
                voiceSessionId = omniSessionId,
                status = "failed",
                error = message,
            }),
            trace,
            ct);

        await WriteAsync(
            chatSessionId,
            workspaceId,
            ServerSentEventFrame.Json(SseEventTypes.Error, new
            {
                messageId,
                message,
            }),
            trace,
            ct);
    }

    private async Task WriteVoiceCaptureStatusAsync(
        string chatSessionId,
        string workspaceId,
        string messageId,
        string omniSessionId,
        string status,
        RuntimeTraceContext trace,
        CancellationToken ct,
        string? text = null,
        string? transcript = null)
    {
        await WriteAsync(
            chatSessionId,
            workspaceId,
            ServerSentEventFrame.Json(SseEventTypes.VoiceCaptureStatus, new
            {
                messageId,
                sessionId = omniSessionId,
                voiceSessionId = omniSessionId,
                status,
                text,
                transcript,
            }),
            trace,
            ct);
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

    private static object BuildMetadataPayload(
        ChatOmniRealtimeSessionRunRequest request,
        OmniRealtimeSessionRequest omniRequest,
        string chatSessionId,
        string messageId,
        string omniSessionId)
    {
        return new
        {
            messageId,
            sessionId = chatSessionId,
            routeDecisionId = $"omni:{messageId}",
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
            inputMode = "omni",
            omniSessionId,
            omniProvider = omniRequest.Provider,
            omniModel = omniRequest.Model,
            voice = omniRequest.Voice,
            transport = omniRequest.Transport,
        };
    }

    private static string? ResolveMetadataValue(
        IReadOnlyDictionary<string, string>? metadata,
        string key)
    {
        if (metadata is null)
            return null;

        return metadata.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : null;
    }

    private static int? TryParseInt(IReadOnlyDictionary<string, string>? metadata, string key)
    {
        var value = ResolveMetadataValue(metadata, key);
        return int.TryParse(value, out var parsed) ? parsed : null;
    }

    private static double? TryParseDouble(IReadOnlyDictionary<string, string>? metadata, string key)
    {
        var value = ResolveMetadataValue(metadata, key);
        return double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;
    }
}
