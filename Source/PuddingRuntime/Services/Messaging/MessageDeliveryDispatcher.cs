using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PuddingCode.Abstractions;
using PuddingCode.Models;
using PuddingCode.Platform;
using PuddingCode.Runtime;
using PuddingCode.Services;
using PuddingCode.Abstractions;

namespace PuddingRuntime.Services.Messaging;

/// <summary>
/// Subscription-driven dispatcher for durable agent message deliveries.
/// </summary>
public sealed class MessageDeliveryDispatcher : IHostedService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
    };

        private readonly IInternalEventBus _eventBus;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly AgentWakeQueue _wakeQueue;
    private readonly ILogger<MessageDeliveryDispatcher> _logger;
    private readonly ConcurrentDictionary<string, AgentDeliveryTarget> _knownAgentTargets = new();
    private IEventSubscriptionHandle? _messageDeliverSubscription;
    private IEventSubscriptionHandle? _availabilitySubscription;
    private CancellationTokenSource? _recoveryCts;
    private Task? _recoveryTask;

        public MessageDeliveryDispatcher(
        IInternalEventBus eventBus,
        IServiceScopeFactory scopeFactory,
        AgentWakeQueue wakeQueue,
        ILogger<MessageDeliveryDispatcher> logger)
    {
        _eventBus = eventBus;
        _scopeFactory = scopeFactory;
        _wakeQueue = wakeQueue;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _messageDeliverSubscription = await _eventBus.SubscribeAsync("message.deliver", OnMessageDeliverAsync, cancellationToken);
        _availabilitySubscription = await _eventBus.SubscribeAsync("agent.availability.changed", OnAvailabilityChangedAsync, cancellationToken);
        _recoveryCts = new CancellationTokenSource();
        _recoveryTask = RunRecoveryLoopAsync(_recoveryCts.Token);
        _logger.LogInformation(
            "[MessageDeliveryDispatcher] Subscribed to message.deliver subscription={MessageSubscriptionId} availability={AvailabilitySubscriptionId}",
            _messageDeliverSubscription.SubscriptionId,
            _availabilitySubscription.SubscriptionId);
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _messageDeliverSubscription?.Dispose();
        _availabilitySubscription?.Dispose();
        _messageDeliverSubscription = null;
        _availabilitySubscription = null;
        _recoveryCts?.Cancel();
        if (_recoveryTask is not null)
        {
            try
            {
                await _recoveryTask.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                // Expected during hosted service shutdown.
            }
        }

        _recoveryCts?.Dispose();
        _recoveryCts = null;
        _recoveryTask = null;
        _logger.LogInformation("[MessageDeliveryDispatcher] Stopped");
    }

    /// <summary>
    /// Handles a message delivery event by claiming, executing, and acknowledging the delivery.
    /// </summary>
    public async Task HandleAsync(InternalEvent evt, CancellationToken ct = default)
    {
        if (string.Equals(evt.Type, "message.deliver", StringComparison.OrdinalIgnoreCase))
        {
            await HandleMessageDeliverAsync(evt, ct);
            return;
        }

        if (string.Equals(evt.Type, "agent.availability.changed", StringComparison.OrdinalIgnoreCase))
            await HandleAvailabilityChangedAsync(evt, ct);
    }

    private async Task HandleMessageDeliverAsync(InternalEvent evt, CancellationToken ct)
    {
        var payload = TryReadPayload<MessageDeliverEventPayload>(evt);
        if (payload is null)
        {
            _logger.LogWarning(
                "[MessageDeliveryDispatcher] message.deliver missing payload event={EventId}",
                evt.EventId);
            return;
        }

        if (!string.Equals(payload.Target.Kind, MessageEndpointKinds.Agent, StringComparison.OrdinalIgnoreCase))
        {
            LogDecision(
                payload.WorkspaceId,
                payload.RoomId,
                payload.MessageId,
                payload.DeliveryId,
                payload.Target.Kind,
                payload.Target.Id,
                "ignored_non_agent",
                attemptCount: 0,
                executionId: null,
                correlationId: evt.CorrelationId,
                causationId: evt.CausationId);
            _logger.LogDebug(
                "[MessageDeliveryDispatcher] Ignored non-agent delivery event={EventId} target={Kind}:{Id}",
                evt.EventId,
                payload.Target.Kind,
                payload.Target.Id);
            return;
        }

        var isHeartbeat = IsHeartbeat(payload.From);
        RememberTarget(payload.WorkspaceId, payload.RoomId, payload.Target.Id);

        // Non-heartbeat messages (user messages, sub-agent results, agent-to-agent)
        // must wake the agent — same path as user messages.
        if (!isHeartbeat)
        {
            await _wakeQueue.NotifyUserActivityAsync(payload.Target.Id, ct);
        }

        await TryClaimAndDispatchAsync(
            payload.WorkspaceId,
            payload.RoomId,
            payload.Target.Id,
            evt.SessionId,
            evt.EventId,
            payload.MessageId,
            payload.DeliveryId,
            evt.CorrelationId,
            evt.CausationId,
            payload.Metadata,
            ct,
            isHeartbeat: isHeartbeat);
    }

    private async Task HandleAvailabilityChangedAsync(InternalEvent evt, CancellationToken ct)
    {
        var payload = TryReadPayload<AgentAvailabilityChangedEventPayload>(evt);
        if (payload is null)
        {
            _logger.LogWarning(
                "[MessageDeliveryDispatcher] agent.availability.changed missing payload event={EventId}",
                evt.EventId);
            return;
        }

        if (!string.Equals(payload.Status, "idle", StringComparison.OrdinalIgnoreCase))
        {
            LogDecision(
                payload.WorkspaceId,
                payload.RoomId,
                messageId: null,
                deliveryId: null,
                MessageEndpointKinds.Agent,
                payload.AgentId,
                $"availability_{payload.Status}",
                attemptCount: 0,
                executionId: null,
                correlationId: evt.CorrelationId,
                causationId: evt.CausationId);
            return;
        }

        RememberTarget(payload.WorkspaceId, payload.RoomId, payload.AgentId);
        await TryClaimAndDispatchAsync(
            payload.WorkspaceId,
            payload.RoomId,
            payload.AgentId,
            evt.SessionId,
            evt.EventId,
            messageIdHint: null,
            deliveryHint: null,
            correlationId: evt.CorrelationId,
            causationId: evt.CausationId,
            metadata: null,
            ct: ct);
    }

    private async Task TryClaimAndDispatchAsync(
        string workspaceId,
        string? roomId,
        string agentId,
        string? sessionId,
        string eventId,
        string? messageIdHint,
        string? deliveryHint,
        string? correlationId,
        string? causationId,
        IReadOnlyDictionary<string, string>? metadata,
        CancellationToken ct,
        bool isHeartbeat = false)
    {
        var executionId = $"msg-{deliveryHint ?? agentId}-{Guid.NewGuid():N}";
        using var scope = _scopeFactory.CreateScope();
        var inbox = scope.ServiceProvider.GetRequiredService<IMessageInbox>();
        var runtime = scope.ServiceProvider.GetRequiredService<IRuntimeAgentDispatcher>();
        // StateGate cooldown check for heartbeat deliveries (Phase 3: unified via AgentFirewall)
        if (isHeartbeat)
        {
            var firewall = scope.ServiceProvider.GetService<IAgentFirewall>();
            if (firewall is not null)
            {
                var fwCtx = new FirewallContext
                {
                    WorkspaceId = workspaceId,
                    SessionId = sessionId ?? string.Empty,
                    AgentInstanceId = agentId,
                    ToolId = "message.deliver",
                    RuntimeMode = RuntimeExecutionMode.Normal,
                    IsHeartbeat = true,
                    IsAgentToAgent = false,
                };
                var fwDecision = await firewall.EvaluateAsync(fwCtx, ct);
                if (!fwDecision.Allowed)
                {
                    LogDecision(
                        workspaceId,
                        roomId,
                        messageIdHint,
                        deliveryHint,
                        MessageEndpointKinds.Agent,
                        agentId,
                        "skipped_unavailable",
                        attemptCount: 0,
                        executionId: executionId,
                        correlationId: correlationId,
                        causationId: causationId);
                    _logger.LogInformation(
                        "[MessageDeliveryDispatcher] Firewall blocked heartbeat delivery target={AgentId} gate={Gate} reason={Reason}",
                        agentId,
                        fwDecision.DeniedAtGate,
                        fwDecision.DenyReason);
                    return;
                }
            }
        }

        var claimed = await inbox.ClaimNextAsync(new MessageClaimRequest
        {
            Endpoint = new MessageAddress { Kind = MessageEndpointKinds.Agent, Id = agentId, WorkspaceId = workspaceId },
            WorkspaceId = workspaceId,
            RoomId = roomId,
            ExecutionId = executionId,
            LeaseDuration = TimeSpan.FromMinutes(5),
        }, ct);

        if (claimed is null)
        {
            LogDecision(
                workspaceId,
                roomId,
                messageIdHint,
                deliveryHint,
                MessageEndpointKinds.Agent,
                agentId,
                "no_claim",
                attemptCount: 0,
                executionId: executionId,
                correlationId: correlationId,
                causationId: causationId);
            _logger.LogDebug(
                "[MessageDeliveryDispatcher] No claimable delivery for event={EventId} delivery={DeliveryId}",
                eventId,
                deliveryHint);
            return;
        }

        var claimedIsHeartbeat = IsHeartbeat(claimed);

        // 批量声明：同一目标的其他排队消息一并取出合并。
        // 心跳是低优先级探活，不参与批处理，避免插入或打断真实用户/Agent 消息。
        var batch = new List<MessageInboxItem> { claimed };
        if (!claimedIsHeartbeat)
        {
            var batchRequest = new MessageClaimRequest
            {
                Endpoint = new MessageAddress { Kind = MessageEndpointKinds.Agent, Id = agentId, WorkspaceId = workspaceId },
                WorkspaceId = workspaceId,
                RoomId = roomId,
                ExecutionId = executionId,
                LeaseDuration = TimeSpan.FromMinutes(5),
            };
            var additional = await inbox.ClaimBatchAsync(batchRequest, maxBatch: 9, ct);
            if (additional.Count > 0)
            {
                foreach (var item in additional)
                {
                    if (IsHeartbeat(item))
                    {
                        await inbox.AckAsync(item.DeliveryId, executionId, ct);
                        LogExecutionResult(
                            item,
                            MessageDeliveryStatuses.Delivered,
                            executionId,
                            correlationId: correlationId,
                            causationId: causationId);
                        _logger.LogInformation(
                            "[MessageDeliveryDispatcher] Dropped heartbeat from message batch delivery={DeliveryId} agent={AgentId}",
                            item.DeliveryId,
                            item.Target.Id);
                        continue;
                    }

                    batch.Add(item);
                }

                if (batch.Count > 1)
                {
                    _logger.LogInformation(
                        "[MessageDeliveryDispatcher] Batch-claimed {BatchSize} non-heartbeat deliveries for agent={AgentId}",
                        batch.Count, agentId);
                }
            }
        }

        // 合并消息内容
        var mergedContent = batch.Count == 1
            ? claimed.Content
            : string.Join("\n\n---\n\n", batch.Select((item, i) =>
                $"[批 {i + 1}/{batch.Count}] From: {item.From.DisplayName ?? item.From.Id}\n{item.Content}"));

        LogDecision(
            claimed.WorkspaceId,
            claimed.RoomId,
            claimed.MessageId,
            claimed.DeliveryId,
            claimed.Target.Kind,
            claimed.Target.Id,
            "claimed",
            claimed.AttemptCount,
            executionId,
            correlationId,
            causationId);

        try
        {
            var dispatchFactory = scope.ServiceProvider.GetService<IAgentInvocationDispatchFactory>()
                ?? throw new InvalidOperationException("Agent invocation dispatch factory is required for agent message delivery.");
            var dispatch = await dispatchFactory.CreateForWorkspaceAgentAsync(
                new WorkspaceAgentInvocation
                {
                    WorkspaceId = claimed.WorkspaceId,
                    AgentId = claimed.Target.Id,
                    MessageId = claimed.MessageId,
                    MessageText = mergedContent,
                    EventSessionId = sessionId,
                    From = claimed.From,
                    CorrelationId = correlationId,
                    CausationId = causationId,
                    Metadata = metadata,
                },
                ct);

            var dispatchRequest = dispatch.Request;

            var transcriptWriter = scope.ServiceProvider.GetService<IChatTranscriptWriter>();
            var result = await DispatchStreamAndCollectAsync(
                runtime,
                dispatchRequest,
                claimed.Target.Id,
                transcriptWriter,
                dispatch.UsesStreamDispatch ? null : claimed,
                ct);

            if (result.IsSuccess)
            {
                await SendReplyToSenderIfNeededAsync(
                    scope.ServiceProvider,
                    claimed,
                    result,
                    metadata,
                    ct);

                await inbox.AckAsync(claimed.DeliveryId, executionId, ct);
                // 确认批量声明的其余消息
                foreach (var item in batch.Skip(1))
                    await inbox.AckAsync(item.DeliveryId, executionId, ct);
                LogExecutionResult(
                    claimed,
                    MessageDeliveryStatuses.Delivered,
                    executionId,
                    correlationId: correlationId,
                    causationId: causationId);
                _logger.LogInformation(
                    "[MessageDeliveryDispatcher] Delivery acked delivery={DeliveryId} agent={AgentId}",
                    claimed.DeliveryId,
                    claimed.Target.Id);
                return;
            }

            var error = result.ErrorMessage ?? $"Runtime finished with state {result.ExecutionState}";
            if (claimedIsHeartbeat && result.ExecutionState == AgentExecutionState.Busy)
            {
                await inbox.AckAsync(claimed.DeliveryId, executionId, ct);
                LogExecutionResult(
                    claimed,
                    MessageDeliveryStatuses.Delivered,
                    executionId,
                    correlationId: correlationId,
                    causationId: causationId);
                _logger.LogInformation(
                    "[MessageDeliveryDispatcher] Dropped heartbeat delivery={DeliveryId} agent={AgentId} because target is busy",
                    claimed.DeliveryId,
                    claimed.Target.Id);
                return;
            }

            var deadLettered = await RetryOrDeadLetterAsync(inbox, claimed, executionId, error, ct);
            if (deadLettered)
            {
                LogExecutionResult(
                    claimed,
                    MessageDeliveryStatuses.DeadLetter,
                    executionId,
                    correlationId: correlationId,
                    causationId: causationId);
                _logger.LogWarning(
                    "[MessageDeliveryDispatcher] Delivery dead-lettered delivery={DeliveryId} agent={AgentId} attempts={Attempts}",
                    claimed.DeliveryId,
                    claimed.Target.Id,
                    claimed.AttemptCount);
                return;
            }

            LogExecutionResult(
                claimed,
                MessageDeliveryStatuses.Retrying,
                executionId,
                correlationId: correlationId,
                causationId: causationId);
            _logger.LogWarning(
                "[MessageDeliveryDispatcher] Delivery retrying delivery={DeliveryId} agent={AgentId} state={State}",
                claimed.DeliveryId,
                claimed.Target.Id,
                result.ExecutionState);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            var deadLettered = await RetryOrDeadLetterAsync(inbox, claimed, executionId, ex.Message, CancellationToken.None);
            LogExecutionResult(
                claimed,
                deadLettered ? MessageDeliveryStatuses.DeadLetter : MessageDeliveryStatuses.Retrying,
                executionId,
                correlationId: correlationId,
                causationId: causationId);
            _logger.LogError(
                ex,
                "[MessageDeliveryDispatcher] Delivery failed delivery={DeliveryId} agent={AgentId}",
                claimed.DeliveryId,
                claimed.Target.Id);
        }
    }

    private static async Task<RuntimeDispatchResult> DispatchStreamAndCollectAsync(
        IRuntimeAgentDispatcher runtime,
        RuntimeDispatchRequest request,
        string agentId,
        IChatTranscriptWriter? transcriptWriter,
        MessageInboxItem? inboundTranscript,
        CancellationToken ct)
    {
        var frameCount = 0;
        string? lastEvent = null;
        string? errorMessage = null;
        var seenDone = false;
        var seenError = false;
        var seenCancelled = false;
        var replyBuilder = new StringBuilder();
        var thinkingChunks = new List<TranscriptThinkingChunk>();
        string? latestUsageJson = null;
        string? doneReply = null;
        string? doneUsageJson = null;
        int doneToolFailureCount = 0;
        int doneToolOutputTruncatedCount = 0;
        long doneToolOutputChars = 0;
        string? doneToolFailureSummary = null;
        var inboundPersisted = false;

        if (transcriptWriter is not null && inboundTranscript is not null)
        {
            var transcriptContent = request.Origin is not null
                ? BuildTranscriptEnvelopeText(request, inboundTranscript)
                : request.MessageText;
            await transcriptWriter.PersistMessageAsync(
                request.SessionId,
                role: "user",
                content: transcriptContent,
                createdAt: inboundTranscript.CreatedAt > 0
                    ? inboundTranscript.CreatedAt
                    : DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                thinkingJson: null,
                usageJson: null,
                workspaceId: request.WorkspaceId,
                agentInstanceId: agentId,
                agentTemplateId: request.AgentTemplateId,
                ct: ct);
            inboundPersisted = true;
        }

        await foreach (var frame in runtime.DispatchStreamAsync(request, ct))
        {
            frameCount++;
            lastEvent = frame.Event;
            if (string.Equals(frame.Event, "done", StringComparison.OrdinalIgnoreCase))
            {
                seenDone = true;
                doneReply = TryReadStringProperty(frame.Data, "reply") ?? doneReply;
                doneUsageJson = TryReadUsageJson(frame.Data) ?? latestUsageJson;
                doneToolFailureCount = TryReadIntProperty(frame.Data, "toolFailureCount") ?? doneToolFailureCount;
                doneToolOutputTruncatedCount = TryReadIntProperty(frame.Data, "toolOutputTruncatedCount") ?? doneToolOutputTruncatedCount;
                doneToolOutputChars = TryReadLongProperty(frame.Data, "toolOutputChars") ?? doneToolOutputChars;
                doneToolFailureSummary = TryReadStringProperty(frame.Data, "toolFailureSummary") ?? doneToolFailureSummary;
            }
            else if (string.Equals(frame.Event, "error", StringComparison.OrdinalIgnoreCase))
            {
                seenError = true;
                errorMessage ??= frame.Data;
                if (TryReadExecutionState(frame.Data) == AgentExecutionState.Busy)
                    return new RuntimeDispatchResult
                    {
                        SessionId = request.SessionId,
                        AgentInstanceId = agentId,
                        IsSuccess = false,
                        ErrorMessage = errorMessage,
                        ExecutionState = AgentExecutionState.Busy,
                    };
            }
            else if (string.Equals(frame.Event, "cancelled", StringComparison.OrdinalIgnoreCase))
                seenCancelled = true;
            else if (string.Equals(frame.Event, "delta", StringComparison.OrdinalIgnoreCase))
            {
                var delta = TryReadStringProperty(frame.Data, "delta")
                    ?? TryReadStringProperty(frame.Data, "text")
                    ?? TryReadStringProperty(frame.Data, "content");
                if (!string.IsNullOrEmpty(delta))
                    replyBuilder.Append(delta);
            }
            else if (string.Equals(frame.Event, "thinking", StringComparison.OrdinalIgnoreCase))
            {
                var delta = TryReadStringProperty(frame.Data, "delta")
                    ?? TryReadStringProperty(frame.Data, "text")
                    ?? TryReadStringProperty(frame.Data, "content");
                if (!string.IsNullOrEmpty(delta))
                {
                    thinkingChunks.Add(new TranscriptThinkingChunk(
                        delta,
                        DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
                }
            }
            else if (string.Equals(frame.Event, "usage", StringComparison.OrdinalIgnoreCase))
            {
                latestUsageJson = TryReadUsageJson(frame.Data) ?? latestUsageJson;
            }
        }

        var assistantReply = !string.IsNullOrWhiteSpace(doneReply)
            ? doneReply
            : replyBuilder.ToString();
        var success = seenDone
            && !seenError
            && !seenCancelled
            && !(doneToolFailureCount > 0 && LooksLikeFailureReply(assistantReply));
        if (success && transcriptWriter is not null)
        {
            var assistantContent = assistantReply;
            var thinkingJson = thinkingChunks.Count > 0
                ? JsonSerializer.Serialize(thinkingChunks, JsonOptions)
                : null;

            await transcriptWriter.PersistMessageAsync(
                request.SessionId,
                role: "agent",
                content: assistantContent,
                createdAt: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                thinkingJson,
                doneUsageJson ?? latestUsageJson,
                workspaceId: request.WorkspaceId,
                agentInstanceId: agentId,
                agentTemplateId: request.AgentTemplateId,
                ct: ct);
        }

        return new RuntimeDispatchResult
        {
            SessionId = request.SessionId,
            AgentInstanceId = agentId,
            IsSuccess = success,
            ReplyText = success ? assistantReply : null,
            ErrorMessage = success
                ? null
                : errorMessage
                  ?? doneToolFailureSummary
                  ?? $"Stream dispatch ended without success. frames={frameCount} last={lastEvent ?? "(none)"} inboundPersisted={inboundPersisted}",
            ExecutionState = success
                ? AgentExecutionState.Completed
                : seenCancelled ? AgentExecutionState.Cancelled : AgentExecutionState.Failed,
            ToolFailureCount = doneToolFailureCount,
            ToolOutputTruncatedCount = doneToolOutputTruncatedCount,
            ToolOutputChars = doneToolOutputChars,
            ToolFailureSummary = doneToolFailureSummary,
        };
    }

    private sealed record TranscriptThinkingChunk(string Text, long Timestamp);

    private static string? TryReadStringProperty(string json, string propertyName)
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

    private static AgentExecutionState? TryReadExecutionState(string json)
    {
        var state = TryReadStringProperty(json, "executionState")
            ?? TryReadStringProperty(json, "ExecutionState");
        return Enum.TryParse<AgentExecutionState>(state, ignoreCase: true, out var parsed)
            ? parsed
            : null;
    }

    private static int? TryReadIntProperty(string json, string propertyName)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty(propertyName, out var value) && value.TryGetInt32(out var number)
                ? number
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static long? TryReadLongProperty(string json, string propertyName)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty(propertyName, out var value) && value.TryGetInt64(out var number)
                ? number
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? TryReadUsageJson(string json)
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
        catch (JsonException)
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

    private static bool IsHeartbeat(MessageInboxItem item) =>
        IsHeartbeat(item.From);

    private static bool IsHeartbeat(MessageAddress from) =>
        string.Equals(from.Kind, MessageEndpointKinds.System, StringComparison.OrdinalIgnoreCase)
        && string.Equals(from.Id, "heartbeat", StringComparison.OrdinalIgnoreCase);

    private static async Task<bool> RetryOrDeadLetterAsync(
        IMessageInbox inbox,
        MessageInboxItem claimed,
        string executionId,
        string error,
        CancellationToken ct)
    {
        if (claimed.AttemptCount >= 3)
        {
            await inbox.DeadLetterAsync(claimed.DeliveryId, executionId, error, ct);
            return true;
        }

        await inbox.RetryAsync(
            claimed.DeliveryId,
            executionId,
            error,
            DateTimeOffset.UtcNow.AddSeconds(30),
            ct);
        return false;
    }

    private Task OnMessageDeliverAsync(InternalEvent evt) =>
        HandleAsync(evt, CancellationToken.None);

    private Task OnAvailabilityChangedAsync(InternalEvent evt) =>
        HandleAsync(evt, CancellationToken.None);

    private async Task RunRecoveryLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(10));
        var nextLeaseRecovery = DateTimeOffset.MinValue;

        while (await timer.WaitForNextTickAsync(ct))
        {
            try
            {
                await TryDispatchKnownTargetsAsync(ct);

                var now = DateTimeOffset.UtcNow;
                if (now < nextLeaseRecovery)
                    continue;

                nextLeaseRecovery = now.AddSeconds(60);
                await RecoverExpiredLeasesAsync(now, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[MessageDeliveryDispatcher] Recovery loop failed");
            }
        }
    }

    private async Task TryDispatchKnownTargetsAsync(CancellationToken ct)
    {
        foreach (var target in _knownAgentTargets.Values.ToArray())
        {
            await TryClaimAndDispatchAsync(
                target.WorkspaceId,
                target.RoomId,
                target.AgentId,
                sessionId: null,
                eventId: "periodic-recovery",
                messageIdHint: null,
                deliveryHint: null,
                correlationId: null,
                causationId: null,
                metadata: null,
                ct: ct);
        }
    }

    private async Task RecoverExpiredLeasesAsync(DateTimeOffset now, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var inbox = scope.ServiceProvider.GetRequiredService<IMessageInbox>();
        var recovered = await inbox.RecoverExpiredLeasesAsync(now, ct);
        if (recovered > 0)
        {
            _logger.LogInformation(
                "[MessageDeliveryDispatcher] Recovered expired delivery leases count={Count}",
                recovered);
        }
    }

    private void RememberTarget(string workspaceId, string? roomId, string agentId)
    {
        var key = $"{workspaceId}\u001f{roomId}\u001f{agentId}";
        _knownAgentTargets[key] = new AgentDeliveryTarget(workspaceId, roomId, agentId);
    }

    private void LogDecision(
        string workspaceId,
        string? roomId,
        string? messageId,
        string? deliveryId,
        string targetKind,
        string targetId,
        string status,
        int attemptCount,
        string? executionId,
        string? correlationId,
        string? causationId)
    {
        _logger.LogInformation(
            "[MessageDeliveryDispatcher] decision workspace_id={workspace_id} room_id={room_id} message_id={message_id} delivery_id={delivery_id} target_kind={target_kind} target_id={target_id} status={status} attempt_count={attempt_count} execution_id={execution_id} correlation_id={correlation_id} causation_id={causation_id}",
            workspaceId,
            roomId,
            messageId,
            deliveryId,
            targetKind,
            targetId,
            status,
            attemptCount,
            executionId,
            correlationId,
            causationId);
    }

    private void LogExecutionResult(
        MessageInboxItem claimed,
        string status,
        string executionId,
        string? correlationId,
        string? causationId)
    {
        _logger.LogInformation(
            "[MessageDeliveryDispatcher] execution_result workspace_id={workspace_id} room_id={room_id} message_id={message_id} delivery_id={delivery_id} target_kind={target_kind} target_id={target_id} status={status} attempt_count={attempt_count} execution_id={execution_id} correlation_id={correlation_id} causation_id={causation_id}",
            claimed.WorkspaceId,
            claimed.RoomId,
            claimed.MessageId,
            claimed.DeliveryId,
            claimed.Target.Kind,
            claimed.Target.Id,
            status,
            claimed.AttemptCount,
            executionId,
            correlationId,
            causationId);
    }

    private static T? TryReadPayload<T>(InternalEvent evt)
        where T : class
    {
        if (evt.Payload is T payload)
            return payload;

        if (evt.Payload is JsonElement json && json.ValueKind == JsonValueKind.Object)
            return JsonSerializer.Deserialize<T>(json.GetRawText(), JsonOptions);

        return null;
    }

    private static string? GetMetadataValue(IReadOnlyDictionary<string, string>? metadata, params string[] keys)
    {
        if (metadata is null)
            return null;

        foreach (var key in keys)
        {
            if (metadata.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
                return value;
        }

        return null;
    }

    private static async Task SendReplyToSenderIfNeededAsync(
        IServiceProvider serviceProvider,
        MessageInboxItem claimed,
        RuntimeDispatchResult result,
        IReadOnlyDictionary<string, string>? metadata,
        CancellationToken ct)
    {
        if (!ShouldSendReplyToSender(claimed, metadata, result))
            return;

        var messageSystem = serviceProvider.GetService<IMessageSystem>()
            ?? throw new InvalidOperationException("Message system is required to send agent replies.");

        var reply = new MessageEnvelope
        {
            From = new MessageAddress
            {
                Kind = MessageEndpointKinds.Agent,
                Id = claimed.Target.Id,
                WorkspaceId = claimed.WorkspaceId,
                DisplayName = claimed.Target.DisplayName,
            },
            To =
            [
                new MessageAddress
                {
                    Kind = claimed.From.Kind,
                    Id = claimed.From.Id,
                    WorkspaceId = claimed.WorkspaceId,
                    DisplayName = claimed.From.DisplayName,
                },
            ],
            RoomId = claimed.RoomId,
            ConversationId = GetMetadataValue(metadata, "conversation_id", "conversationId", "ConversationId"),
            ReplyToMessageId = claimed.MessageId,
            Audience = MessageAudiences.Direct,
            Visibility = MessageVisibilities.Public,
            Content = result.ReplyText!,
            Priority = claimed.Priority,
            Metadata = new Dictionary<string, string>
            {
                ["source"] = "message_delivery_dispatcher",
                ["intent"] = "agent_reply",
                ["requires_response"] = "false",
                ["reply_to_message_id"] = claimed.MessageId,
            },
        };

        await messageSystem.SendAsync(reply, ct);
    }

    private static bool ShouldSendReplyToSender(
        MessageInboxItem claimed,
        IReadOnlyDictionary<string, string>? metadata,
        RuntimeDispatchResult result)
    {
        if (!string.Equals(claimed.From.Kind, MessageEndpointKinds.Agent, StringComparison.OrdinalIgnoreCase))
            return false;

        if (string.IsNullOrWhiteSpace(result.ReplyText))
            return false;

        var intent = GetMetadataValue(metadata, "intent", "Intent");
        if (string.Equals(intent, "agent_reply", StringComparison.OrdinalIgnoreCase)
            || string.Equals(intent, "subagent_result", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var existingEnvelope = AgentContextEnvelopeRenderer.TryParse(claimed.Content);
        if (existingEnvelope is not null
            && string.Equals(existingEnvelope.MessageType, "agent_reply", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return true;
    }

    private static string BuildTranscriptEnvelopeText(RuntimeDispatchRequest request, MessageInboxItem inbound)
    {
        var envelope = new AgentContextEnvelope
        {
            Version = 1,
            MessageId = inbound.MessageId,
            MessageType = request.Origin!.MessageType,
            ContentType = "text/markdown",
            CreatedAt = inbound.CreatedAt > 0 ? inbound.CreatedAt : DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            WorkspaceId = request.WorkspaceId,
            CorrelationId = request.Origin.CorrelationId,
            CausationId = request.Origin.CausationId,
            From = new AgentContextEndpoint(request.Origin.FromKind, request.Origin.FromId, request.Origin.FromDisplayName),
            To = [new AgentContextEndpoint("agent", request.AgentTemplateId, null)],
            Constraints =
            [
                "This message was delivered by Pudding Message Fabric.",
            ],
            Context = new AgentContextPayload("text/markdown", request.MessageText),
        };

        return AgentContextEnvelopeRenderer.RenderForAgent(envelope);
    }

    private sealed record AgentDeliveryTarget(string WorkspaceId, string? RoomId, string AgentId);

    private static bool LooksLikeFailureReply(string? reply)
    {
        if (string.IsNullOrWhiteSpace(reply))
            return false;

        return reply.Contains("执行失败", StringComparison.OrdinalIgnoreCase)
            || reply.Contains("FAILED", StringComparison.OrdinalIgnoreCase)
            || reply.Contains("failed", StringComparison.OrdinalIgnoreCase)
            || reply.Contains("Command timed out", StringComparison.OrdinalIgnoreCase)
            || reply.Contains("timed out", StringComparison.OrdinalIgnoreCase);
    }
}
