using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using PuddingCode.Abstractions;
using PuddingCode.Models;
using PuddingCode.Observability;
using PuddingCode.Platform;
using PuddingCode.Runtime;
using PuddingPlatform.Controllers.Api;
using PuddingPlatform.Data.Dtos;

namespace PuddingPlatform.Services;

public sealed class ChatMessageExecutionService
{
    private readonly ChatVisualReasoningSessionRunner _visualReasoningRunner;
    private readonly ChatTranscriptWriter _transcriptWriter;
    private readonly ISessionStateManager _ssm;
    private readonly TokenUsageRecorder _tokenUsageRecorder;
    private readonly ILogger<ChatMessageExecutionService> _logger;

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
    };

    public ChatMessageExecutionService(
        ChatVisualReasoningSessionRunner visualReasoningRunner,
        ChatTranscriptWriter transcriptWriter,
        ISessionStateManager ssm,
        TokenUsageRecorder tokenUsageRecorder,
        ILogger<ChatMessageExecutionService> logger)
    {
        _visualReasoningRunner = visualReasoningRunner;
        _transcriptWriter = transcriptWriter;
        _ssm = ssm;
        _tokenUsageRecorder = tokenUsageRecorder;
        _logger = logger;
    }

    public async Task<IActionResult> StartCameraVisualReasoning(
        string workspaceId,
        AdminChatRequest req,
        ChatAgentDispatch dispatch,
        string userExternalId,
        RuntimeTraceContext trace)
    {
        var chatSessionId = req.SessionId ?? Guid.NewGuid().ToString("N");
        var messageId = Guid.NewGuid().ToString("N");
        var transcriptMessageText = string.IsNullOrWhiteSpace(req.OriginalMessageText)
            ? req.MessageText
            : req.OriginalMessageText;
        var userCreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var visualProvider = ResolveVisualProvider(req.Metadata, dispatch.PreferredProviderId);
        var visualModel = ResolveVisualModel(req.Metadata, dispatch.LlmConfig?.ModelId);

        _ = Task.Run(async () =>
        {
            try
            {
                if (!req.SuppressUserTranscript)
                {
                    await _transcriptWriter.PersistMessageAsync(
                        chatSessionId,
                        role: "user",
                        content: transcriptMessageText,
                        createdAt: userCreatedAt,
                        thinkingJson: null,
                        usageJson: null,
                        workspaceId: workspaceId,
                        agentInstanceId: dispatch.AgentId,
                        agentTemplateId: dispatch.AgentTemplateId,
                        ct: CancellationToken.None);
                }

                var result = await _visualReasoningRunner.RunAsync(new ChatVisualReasoningSessionRunRequest
                {
                    WorkspaceId = workspaceId,
                    RoomId = $"web-chat-{workspaceId}",
                    ParticipantId = userExternalId,
                    AgentId = dispatch.AgentId,
                    AgentDisplayName = dispatch.DisplayName,
                    AvatarUrl = dispatch.AvatarUrl,
                    ChatRequest = req with { SessionId = chatSessionId },
                    Provider = visualProvider,
                    Model = visualModel,
                    SessionId = chatSessionId,
                    MessageId = messageId,
                    Trace = trace.WithSession(chatSessionId, workspaceId).WithAgent(dispatch.AgentId, dispatch.AgentTemplateId),
                }, CancellationToken.None);

                if (result.Success)
                {
                    await _transcriptWriter.PersistMessageAsync(
                        chatSessionId,
                        role: "agent",
                        content: result.Reply ?? string.Empty,
                        createdAt: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                        thinkingJson: null,
                        usageJson: null,
                        workspaceId: workspaceId,
                        agentInstanceId: dispatch.AgentId,
                        agentTemplateId: dispatch.AgentTemplateId,
                        ct: CancellationToken.None);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "[Chat:Vision] Background visual reasoning failed ws={Workspace} session={Session} agent={AgentId}",
                    workspaceId,
                    chatSessionId,
                    dispatch.AgentId);
            }
        });

        _logger.LogInformation(
            "[Chat:Vision] Returned ws={Workspace} msgId={MessageId} sessionId={SessionId} provider={Provider} model={Model}",
            workspaceId,
            messageId,
            chatSessionId,
            visualProvider,
            visualModel);

        return new OkObjectResult(new { messageId, sessionId = chatSessionId });
    }

    public static bool IsCameraVisualReasoningRequest(AdminChatRequest req)
    {
        return req.Metadata is not null
               && req.Metadata.TryGetValue("inputMode", out var inputMode)
               && string.Equals(inputMode, "camera", StringComparison.OrdinalIgnoreCase);
    }

    private static string ResolveVisualProvider(
        IReadOnlyDictionary<string, string>? metadata,
        string? preferredProviderId)
    {
        if (metadata is not null
            && metadata.TryGetValue("visualProvider", out var metadataProvider)
            && !string.IsNullOrWhiteSpace(metadataProvider))
        {
            return metadataProvider.Trim();
        }

        return string.Equals(preferredProviderId, VisualReasoningProviders.DashScope, StringComparison.OrdinalIgnoreCase)
            ? VisualReasoningProviders.DashScope
            : VisualReasoningProviders.DashScope;
    }

    private static string ResolveVisualModel(
        IReadOnlyDictionary<string, string>? metadata,
        string? preferredModelId)
    {
        if (metadata is not null
            && metadata.TryGetValue("visualModel", out var metadataModel)
            && !string.IsNullOrWhiteSpace(metadataModel))
        {
            return metadataModel.Trim();
        }

        if (!string.IsNullOrWhiteSpace(preferredModelId))
        {
            var model = preferredModelId.Trim();
            if (model.Contains("vl", StringComparison.OrdinalIgnoreCase)
                || model.StartsWith("qvq", StringComparison.OrdinalIgnoreCase))
            {
                return model;
            }
        }

        return "qwen3-vl-plus";
    }

    public static async Task RunSecondaryChatFanoutAsync(
        PlatformApiClient apiClient,
        ChatTranscriptWriter transcriptWriter,
        ISessionStateManager ssm,
        TokenUsageRecorder tokenUsageRecorder,
        ILogger logger,
        RuntimeTraceContext trace,
        string channelId,
        string userExternalId,
        string workspaceId,
        AdminChatRequest baseRequest,
        ChatAgentDispatch dispatch,
        string sessionId,
        int fanoutIndex,
        int fanoutCount,
        CancellationToken ct)
    {
        var request = baseRequest with
        {
            AgentId = dispatch.AgentId,
            SessionId = sessionId,
            SuppressUserTranscript = true,
            ForceNewSession = false,
        };
        var metadata = BuildChatIngressMetadata(request, dispatch, fanoutIndex, fanoutCount);
        var replyBuilder = new StringBuilder();
        var thinkingChunks = new List<TranscriptThinkingChunk>();
        string? latestUsageJson = null;
        string? streamMessageId = null;
        var assistantTranscriptPersisted = false;
        var framesWritten = 0;

        try
        {
            await foreach (var frame in apiClient.SendMessageStreamAsync(
                channelId: channelId,
                userExternalId: userExternalId,
                messageText: request.MessageText,
                workspaceId: workspaceId,
                sessionId: sessionId,
                llmConfig: dispatch.LlmConfig,
                agentTemplateId: dispatch.AgentTemplateId,
                agentInstanceId: dispatch.AgentId,
                capabilityPolicy: dispatch.CapabilityPolicy,
                toolDefinitions: dispatch.ToolDefinitions,
                skillPackages: dispatch.SkillPackages,
                forceNewSession: false,
                metadata: metadata,
                ct: ct))
            {
                if (frame.Event == "metadata")
                {
                    try
                    {
                        using var doc = JsonDocument.Parse(frame.Data);
                        if (doc.RootElement.TryGetProperty("messageId", out var mid))
                            streamMessageId = mid.GetString();
                    }
                    catch
                    {
                        // Best-effort metadata extraction; the frame itself is still persisted.
                    }

                    var frameTrace = trace.WithSession(sessionId, workspaceId).WithAgent(dispatch.AgentId, dispatch.AgentTemplateId);
                    await ssm.AppendAsync(
                        sessionId,
                        workspaceId,
                        frame,
                        CancellationToken.None,
                        frameTrace,
                        RuntimeActivityComponents.AgentExecution,
                        $"chat.stream.fanout.{frame.Event}");
                    framesWritten++;
                }
                else if (frame.Event == "delta")
                {
                    var delta = TryReadStringProperty(frame.Data, "delta");
                    if (!string.IsNullOrEmpty(delta))
                        replyBuilder.Append(delta);
                }
                else if (frame.Event == "thinking")
                {
                    var delta = TryReadStringProperty(frame.Data, "delta");
                    if (!string.IsNullOrEmpty(delta))
                    {
                        thinkingChunks.Add(new TranscriptThinkingChunk(
                            delta,
                            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
                    }
                }
                else if (frame.Event == "usage")
                {
                    latestUsageJson = TryReadUsageJson(frame.Data) ?? latestUsageJson;
                }
                else if (frame.Event == "done" && !string.IsNullOrEmpty(frame.Data))
                {
                    if (!assistantTranscriptPersisted)
                    {
                        var reply = TryReadStringProperty(frame.Data, "reply");
                        var assistantContent = !string.IsNullOrWhiteSpace(reply)
                            ? reply
                            : replyBuilder.ToString();
                        var doneUsageJson = TryReadUsageJson(frame.Data) ?? latestUsageJson;
                        var thinkingJson = thinkingChunks.Count > 0
                            ? JsonSerializer.Serialize(thinkingChunks, JsonOpts)
                            : null;

                        await transcriptWriter.PersistMessageAsync(
                            sessionId,
                            role: "agent",
                            content: assistantContent,
                            createdAt: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                            thinkingJson,
                            doneUsageJson,
                            workspaceId: workspaceId,
                            agentInstanceId: dispatch.AgentId,
                            agentTemplateId: dispatch.AgentTemplateId,
                            ct: CancellationToken.None);
                        assistantTranscriptPersisted = true;
                    }

                    await RecordChatUsageAsync(
                        tokenUsageRecorder,
                        logger,
                        frame.Data,
                        sourceId: streamMessageId ?? $"{sessionId}:{dispatch.AgentId}:{fanoutIndex}",
                        workspaceId,
                        sessionId,
                        dispatch.PreferredProviderId,
                        dispatch.LlmConfig?.ModelId);
                }
            }

            logger.LogInformation(
                "[Chat:Fanout] Stream completed ws={Workspace} session={Session} agent={AgentId} idx={Index}/{Count} metadataFrames={Frames}",
                workspaceId, sessionId, dispatch.AgentId, fanoutIndex + 1, fanoutCount, framesWritten);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "[Chat:Fanout] Secondary stream failed ws={Workspace} session={Session} agent={AgentId}",
                workspaceId, sessionId, dispatch.AgentId);
        }
    }

    public static async Task RecordChatUsageAsync(
        TokenUsageRecorder tokenUsageRecorder,
        ILogger logger,
        string data,
        string sourceId,
        string workspaceId,
        string? sessionId,
        string? providerId,
        string? modelId)
    {
        try
        {
            using var doc = JsonDocument.Parse(data);
            var root = doc.RootElement;
            if (!root.TryGetProperty("usage", out var usageEl)) return;

            var usageTokens = JsonSerializer.Deserialize<TokenUsageDto>(usageEl.GetRawText(), JsonOpts);
            if (usageTokens is null) return;
            PromptPrefixSnapshot? prefixSnapshot = null;
            if (root.TryGetProperty("prefixSnapshot", out var prefixEl)
                && prefixEl.ValueKind == JsonValueKind.Object)
            {
                prefixSnapshot = JsonSerializer.Deserialize<PromptPrefixSnapshot>(
                    prefixEl.GetRawText(),
                    JsonOpts);
            }

            await tokenUsageRecorder.RecordAsync(
                usageTokens,
                sourceType: "chat_message",
                sourceId: sourceId,
                workspaceId: workspaceId,
                sessionId: sessionId,
                providerId: providerId,
                modelId: modelId,
                prefixSnapshot: prefixSnapshot);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[Chat:Stats] Failed to record token usage via recorder");
        }
    }

    public static Dictionary<string, string>? BuildChatIngressMetadata(
        AdminChatRequest req,
        ChatAgentDispatch dispatch,
        int fanoutIndex,
        int fanoutCount,
        string? turnId = null,
        string? clientRequestId = null)
    {
        var metadata = new Dictionary<string, string>(
            req.Metadata ?? new Dictionary<string, string>(),
            StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(dispatch.AgentId))
        {
            metadata["agent_id"] = dispatch.AgentId;
            metadata["source_type"] = "agent";
            metadata["source_id"] = dispatch.AgentId;
            metadata["source_name"] = dispatch.DisplayName;
        }
        if (!string.IsNullOrWhiteSpace(dispatch.AvatarUrl))
            metadata["avatar_url"] = dispatch.AvatarUrl;
        if (!string.IsNullOrWhiteSpace(req.Audience))
            metadata["audience"] = req.Audience;
        if (req.TargetAgentIds is { Count: > 0 })
            metadata["target_agent_ids"] = string.Join(",", req.TargetAgentIds.Where(id => !string.IsNullOrWhiteSpace(id)));
        if (req.SuppressUserTranscript)
            metadata["suppress_user_transcript"] = "true";
        metadata["fanout_index"] = fanoutIndex.ToString(System.Globalization.CultureInfo.InvariantCulture);
        metadata["fanout_count"] = fanoutCount.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (!string.IsNullOrWhiteSpace(turnId))
            metadata["turn_id"] = turnId;
        if (!string.IsNullOrWhiteSpace(clientRequestId))
            metadata["client_request_id"] = clientRequestId;

        return metadata.Count > 0 ? metadata : null;
    }

    public static string? TryReadStringProperty(string json, string propertyName)
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
        catch
        {
            return null;
        }
    }

    public static string? TryReadUsageJson(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
                return usage.GetRawText();

            return LooksLikeUsagePayload(root)
                ? root.GetRawText()
                : null;
        }
        catch
        {
            return null;
        }
    }

    private static bool LooksLikeUsagePayload(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
            return false;

        return root.TryGetProperty("promptTokens", out _)
            || root.TryGetProperty("PromptTokens", out _)
            || root.TryGetProperty("completionTokens", out _)
            || root.TryGetProperty("CompletionTokens", out _)
            || root.TryGetProperty("totalTokens", out _)
            || root.TryGetProperty("TotalTokens", out _);
    }
}

internal sealed record TranscriptThinkingChunk(string Text, long Timestamp);
