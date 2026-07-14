using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using PuddingCode.Abstractions;
using PuddingCode.Observability;
using PuddingCode.Platform;

namespace PuddingPlatform.Services.AgentChat;

public sealed class ChatExecutionWorker : BackgroundService
{
    private const long LeaseDurationMs = 120_000;
    private const int IdlePollDelayMs = 2_000;
    private const string LeaseOwner = "chat-execution-worker";

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    private readonly IChatCommandStore _commandStore;
    private readonly ISessionStateManager _ssm;
    private readonly ISessionProjectionStore _projectionStore;
    private readonly ISessionEventReader _eventReader;
    private readonly PlatformApiClient _apiClient;
    private readonly ChatTranscriptWriter _transcriptWriter;
    private readonly ILogger<ChatExecutionWorker> _logger;
    private readonly int _maxConcurrency;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _sessionLocks = new();

    public ChatExecutionWorker(
        IChatCommandStore commandStore,
        ISessionStateManager ssm,
        ISessionProjectionStore projectionStore,
        ISessionEventReader eventReader,
        PlatformApiClient apiClient,
        ChatTranscriptWriter transcriptWriter,
        ILogger<ChatExecutionWorker> logger,
        IConfiguration? configuration = null)
    {
        _commandStore = commandStore;
        _ssm = ssm;
        _projectionStore = projectionStore;
        _eventReader = eventReader;
        _apiClient = apiClient;
        _transcriptWriter = transcriptWriter;
        _logger = logger;
        _maxConcurrency = configuration?.GetValue<int?>("Pudding:ChatExecutionMaxConcurrency") ?? 3;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "[ChatWorker] Started maxConcurrency={MaxConcurrency} leaseOwner={LeaseOwner}",
            _maxConcurrency, LeaseOwner);

        var running = new ConcurrentDictionary<string, Task>();

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                while (running.Count < _maxConcurrency)
                {
                    var command = await _commandStore.LeaseNextAsync(LeaseOwner, LeaseDurationMs, stoppingToken);
                    if (command is null) break;

                    var task = ExecuteWithSessionLockAsync(command, stoppingToken);
                    running.TryAdd(command.CommandId, task);

                    _ = task.ContinueWith(_ =>
                    {
                        running.TryRemove(command.CommandId, out _);
                    }, CancellationToken.None);
                }

                await Task.Delay(IdlePollDelayMs, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[ChatWorker] Poll loop error");
                await Task.Delay(IdlePollDelayMs, stoppingToken);
            }
        }

        _logger.LogInformation("[ChatWorker] Stopping, waiting for {Count} running commands", running.Count);
        await Task.WhenAll(running.Values);

        // Release any remaining leased commands during shutdown.
        // Running commands that complete naturally are already handled.
        // Any crashed/incomplete commands will be picked up after lease expiry.

        _logger.LogInformation("[ChatWorker] Stopped");
    }

    /// <summary>
    /// Executes a command with per-session serialization.
    /// Same-session turns are processed sequentially; different sessions run concurrently.
    /// </summary>
    private async Task ExecuteWithSessionLockAsync(ChatCommandRecord command, CancellationToken stoppingToken)
    {
        var sessionLock = _sessionLocks.GetOrAdd(command.SessionId, _ => new SemaphoreSlim(1, 1));
        await sessionLock.WaitAsync(stoppingToken);
        try
        {
            await ExecuteCommandAsync(command, stoppingToken);
        }
        finally
        {
            sessionLock.Release();
        }
    }

    private async Task ExecuteCommandAsync(ChatCommandRecord command, CancellationToken stoppingToken)
    {
        var trace = RuntimeTraceContext.CreateNew(
            sessionId: command.SessionId,
            workspaceId: command.WorkspaceId);

        var replyBuilder = new StringBuilder();
        var thinkingChunks = new List<(string Text, long Timestamp)>();
        string? latestUsageJson = null;
        var framesWritten = 0;
        var userTranscriptPersisted = false;
        var assistantTranscriptPersisted = false;
        string? streamMessageId = null;
        var userCreatedAt = command.CreatedAt;

        CancellationTokenSource? renewCts = null;
        Task? renewTask = null;

        try
        {
            _logger.LogInformation(
                "[ChatWorker] Executing command={CommandId} turn={TurnId} session={SessionId} attempt={Attempt}",
                command.CommandId, command.TurnId, command.SessionId, command.AttemptCount);

            var startedFrame = ServerSentEventFrame.Json("turn.started", new
            {
                commandId = command.CommandId,
                turnId = command.TurnId,
                messageId = command.MessageId,
            });
            await _ssm.AppendAsync(command.SessionId, command.WorkspaceId, startedFrame, stoppingToken);

            var payload = DeserializePayload(command.PayloadJson);

            // Periodic lease renewal: long LLM calls (up to minutes) must keep the lease alive
            // to prevent other workers from stealing the command. Renew at half of lease duration.
            renewCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            renewTask = RenewLeasePeriodicallyAsync(command, renewCts.Token);

            await foreach (var frame in _apiClient.SendMessageStreamAsync(
                channelId: payload.ChannelId,
                userExternalId: payload.UserExternalId,
                messageText: payload.MessageText,
                workspaceId: command.WorkspaceId,
                sessionId: command.SessionId,
                llmConfig: payload.LlmConfig,
                agentTemplateId: command.AgentTemplateId,
                agentInstanceId: command.AgentInstanceId,
                capabilityPolicy: payload.CapabilityPolicy,
                toolDefinitions: payload.ToolDefinitions,
                skillPackages: payload.SkillPackages,
                forceNewSession: false,
                sessionTitle: null,
                metadata: payload.Metadata,
                ct: stoppingToken))
            {
                if (frame.Event == "metadata")
                {
                    if (TryReadProperty(frame.Data, "messageId", out var mid))
                        streamMessageId = mid;
                }

                if (!userTranscriptPersisted && streamMessageId is not null)
                {
                    await _transcriptWriter.PersistMessageAsync(
                        command.SessionId,
                        role: "user",
                        content: payload.MessageText,
                        createdAt: userCreatedAt,
                        thinkingJson: null,
                        usageJson: null,
                        workspaceId: command.WorkspaceId,
                        agentInstanceId: command.AgentInstanceId,
                        agentTemplateId: command.AgentTemplateId,
                        ct: CancellationToken.None);
                    userTranscriptPersisted = true;
                }

                if (frame.Event == "delta")
                {
                    if (TryReadProperty(frame.Data, "delta", out var delta) && !string.IsNullOrEmpty(delta))
                        replyBuilder.Append(delta);
                }
                else if (frame.Event == "thinking")
                {
                    if (TryReadProperty(frame.Data, "delta", out var delta) && !string.IsNullOrEmpty(delta))
                        thinkingChunks.Add((delta, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
                }
                else if (frame.Event == "usage")
                {
                    latestUsageJson = TryGetUsageJson(frame.Data) ?? latestUsageJson;
                }

                if (frame.Event == "done" && !string.IsNullOrEmpty(frame.Data))
                {
                    if (!assistantTranscriptPersisted)
                    {
                        var reply = TryReadProperty(frame.Data, "reply", out var r) ? r : null;
                        var content = !string.IsNullOrWhiteSpace(reply) ? reply : replyBuilder.ToString();
                        var usageJson = TryGetUsageJson(frame.Data) ?? latestUsageJson;
                        var thinkingJson = thinkingChunks.Count > 0
                            ? JsonSerializer.Serialize(thinkingChunks, JsonOpts) : null;

                        await _transcriptWriter.PersistMessageAsync(
                            command.SessionId,
                            role: "agent",
                            content: content,
                            createdAt: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                            thinkingJson: thinkingJson,
                            usageJson: usageJson,
                            workspaceId: command.WorkspaceId,
                            agentInstanceId: command.AgentInstanceId,
                            agentTemplateId: command.AgentTemplateId,
                            ct: CancellationToken.None);
                        assistantTranscriptPersisted = true;

                        // Update projection cursor: messages now reflect events up to current head.
                        var projectedSeq = await _eventReader.GetHeadAsync(command.SessionId, CancellationToken.None);
                        await _projectionStore.SetProjectedCursorAsync(command.SessionId, projectedSeq);
                    }
                }

                framesWritten++;
            }

            renewCts!.Cancel();
            await renewTask!;

            await _commandStore.CompleteAsync(command.CommandId, command.FenceToken!, "succeeded");
            _logger.LogInformation(
                "[ChatWorker] Completed command={CommandId} turn={TurnId} frames={Frames}",
                command.CommandId, command.TurnId, framesWritten);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            renewCts?.Cancel();
            if (renewTask is not null)
                try { await renewTask; } catch { }
            await _commandStore.ReleaseLeaseAsync(command.CommandId, command.FenceToken!, CancellationToken.None);
            _logger.LogWarning("[ChatWorker] Released lease command={CommandId} (shutdown)", command.CommandId);
        }
        catch (Exception ex)
        {
            renewCts?.Cancel();
            if (renewTask is not null)
                try { await renewTask; } catch { }
            _logger.LogError(ex,
                "[ChatWorker] Failed command={CommandId} turn={TurnId}",
                command.CommandId, command.TurnId);

            try
            {
                var errorFrame = ServerSentEventFrame.Json("error", new
                {
                    messageId = streamMessageId ?? command.MessageId,
                    turnId = command.TurnId,
                    message = ex.Message,
                    error = ex.GetType().Name,
                    stackTrace = ex.ToString(),
                });
                await _ssm.AppendAsync(command.SessionId, command.WorkspaceId, errorFrame, CancellationToken.None);
                await _ssm.AppendAsync(command.SessionId, command.WorkspaceId,
                    ServerSentEventFrame.Json("turn.failed", new
                    {
                        turnId = command.TurnId,
                        messageId = command.MessageId,
                        reason = ex.GetType().Name,
                    }), CancellationToken.None);
            }
            catch (Exception ssmEx)
            {
                _logger.LogError(ssmEx, "[ChatWorker] Failed to append error frames");
            }

            await _commandStore.CompleteAsync(command.CommandId, command.FenceToken!, "failed", ex.Message);
        }
    }

    private static ChatExecutionPayload DeserializePayload(string json) =>
        JsonSerializer.Deserialize<ChatExecutionPayload>(json, JsonOpts) ?? new ChatExecutionPayload();

    private static bool TryReadProperty(string json, string propertyName, out string? value)
    {
        value = null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty(propertyName, out var prop))
            {
                value = prop.GetString();
                return true;
            }
        }
        catch (JsonException) { }
        return false;
    }

    private static string? TryGetUsageJson(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("usage", out var usage))
                return usage.GetRawText();
        }
        catch (JsonException) { }
        return null;
    }

    /// <summary>
    /// 定期续租（每 LeaseDurationMs/2），防止长 LLM 调用（多轮 tool call）
    /// 期间其他 Worker 因租约过期而抢占命令。
    /// 续租失败（fence token 不匹配）仅记录 Warning，不抛异常。
    /// </summary>
    private async Task RenewLeasePeriodicallyAsync(ChatCommandRecord command, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(command.FenceToken)) return;
        var intervalMs = LeaseDurationMs / 2;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay((int)intervalMs, ct);
                var renewed = await _commandStore.RenewLeaseAsync(
                    command.CommandId, command.FenceToken, LeaseDurationMs, CancellationToken.None);
                if (!renewed)
                {
                    _logger.LogWarning(
                        "[ChatWorker] Lease renewal failed (fence mismatch?) command={CommandId}",
                        command.CommandId);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Normal shutdown — stream completed or worker stopping.
        }
    }

    private sealed class ChatExecutionPayload
    {
        public string ChannelId { get; set; } = string.Empty;
        public string UserExternalId { get; set; } = string.Empty;
        public string MessageText { get; set; } = string.Empty;
        public LlmConfig? LlmConfig { get; set; }
        public CapabilityPolicy? CapabilityPolicy { get; set; }
        public IReadOnlyList<LlmToolDefinition>? ToolDefinitions { get; set; }
        public IReadOnlyList<SkillPackageInfo>? SkillPackages { get; set; }
        public Dictionary<string, string>? Metadata { get; set; }
    }
}
