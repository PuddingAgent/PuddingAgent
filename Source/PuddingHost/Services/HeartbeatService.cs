using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PuddingCode.Abstractions;
using PuddingCode.Configuration;
using PuddingCode.Models;
using PuddingCode.Scheduling;
using PuddingPlatform.Services;
using PuddingRuntime;
using PuddingRuntime.Models;
using PuddingRuntime.Services;

namespace PuddingAgent.Services;

/// <summary>
/// 主动心跳协调器 — 连接 IdleDetector、AgentWakeQueue 和 IMessageSystem，
/// 在系统空闲时从队列中依次唤醒 Agent。
///
/// 设计原则：
/// - 默认心跳：所有活跃 Agent 默认每 1 小时心跳一次（无需调用 sleep）
/// - 自定义频率：调用 sleep 工具后可自定义心跳间隔（60~86400 秒）
/// - 尽力模式：不保证精确唤醒时间 — 可能被用户消息覆盖、Agent 忙碌跳过、队列排队延迟
/// - 多 Agent 锁：AgentWakeQueue 确保同一时刻只唤醒一个 Agent
/// - 实例提示词：心跳内容由 Agent 实例 manifest 管理，每个 Agent 独立保存
/// </summary>
public sealed class HeartbeatOrchestrator : IHostedService
{
    private readonly IIdleDetector _idleDetector;
    private readonly AgentWakeQueue _wakeQueue;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly PuddingDataPaths _paths;
    private readonly ILogger<HeartbeatOrchestrator> _logger;
    private readonly string _workspaceId;
    private readonly ConcurrentDictionary<string, int> _heartbeatRetryCounts = new(StringComparer.OrdinalIgnoreCase);
    private readonly string? _defaultHeartbeatPrompt;
    private readonly TimeProvider _clock;
    private readonly SemaphoreSlim _tickGate = new(1, 1);
    private readonly HashSet<string> _knownAgents = new(StringComparer.OrdinalIgnoreCase);
    private DateTimeOffset _nextCatalogReconcile;
    private static readonly TimeSpan CatalogReconcileInterval = TimeSpan.FromMinutes(1);

    public HeartbeatOrchestrator(
        IIdleDetector idleDetector,
        AgentWakeQueue wakeQueue,
        IServiceScopeFactory scopeFactory,
        PuddingDataPaths paths,
        ILogger<HeartbeatOrchestrator> logger,
        IConfiguration configuration,
        TimeProvider? clock = null)
    {
        _idleDetector = idleDetector;
        _wakeQueue = wakeQueue;
        _scopeFactory = scopeFactory;
        _paths = paths;
        _logger = logger;
        _clock = clock ?? TimeProvider.System;
        _workspaceId = string.IsNullOrWhiteSpace(configuration["Agent:DefaultWorkspaceId"])
            ? "default"
            : configuration["Agent:DefaultWorkspaceId"]!.Trim();

        // 心跳提示词唯一默认来源：embedded heartbeatPrompt.md（PuddingPlatform.Prompts）。
        // 实例 heartbeatPrompt.md 缺失且嵌入式资源也缺失时，回退编译期常量并告警。
        _defaultHeartbeatPrompt = WorkspaceAgentFileService.TryReadDefaultHeartbeatPrompt();
        if (string.IsNullOrWhiteSpace(_defaultHeartbeatPrompt))
        {
            _logger.LogWarning(
                "[HeartbeatOrchestrator] Embedded default heartbeat prompt unavailable; runtime fallback will use compiled default");
        }
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await EnsureAllAgentsRegisteredAsync(cancellationToken);

        _idleDetector.OnIdleThresholdReached += OnIdleTickAsync;
        _logger.LogInformation("[HeartbeatOrchestrator] Registered on IdleDetector");
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _idleDetector.OnIdleThresholdReached -= OnIdleTickAsync;
        _logger.LogInformation("[HeartbeatOrchestrator] Unregistered");
        return Task.CompletedTask;
    }

    /// <summary>
    /// Called by IdleDetector every ~5 s while the system is idle.
    /// Reconciles registrations at a lower frequency, then tries one due agent.
    /// Always re-arms the detector, including when no agents exist yet.
    /// </summary>
    private async Task OnIdleTickAsync(TimeSpan idleDuration, CancellationToken ct)
    {
        if (!await _tickGate.WaitAsync(0, ct)) return;
        try { await ProcessIdleTickAsync(idleDuration, ct); }
        finally
        {
            _tickGate.Release();
            // Even an empty catalog must be revisited when an agent is created later.
            _idleDetector.ReArm();
        }
    }

    private async Task ProcessIdleTickAsync(TimeSpan idleDuration, CancellationToken ct)
    {
        if (_clock.GetUtcNow() >= _nextCatalogReconcile)
            await EnsureAllAgentsRegisteredAsync(ct);

        // ── 尝试出队 ──
        var request = await _wakeQueue.TryDequeueAsync(ct);
        if (request is null)
        {
            // 队列有条目但还没到唤醒时间 → 继续轮询
            var remaining = await _wakeQueue.CountAsync(ct);
            if (remaining > 0)
            {
                _idleDetector.ReArm();
                _logger.LogDebug("[HeartbeatOrchestrator] ReArmed (queue has {Count} waiting)", remaining);
            }
            return;
        }

        HeartbeatPreference? preference = null;
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var agentConfig = scope.ServiceProvider.GetRequiredService<WorkspaceAgentFileService>();
            var agent = await agentConfig.GetAgentAsync(_workspaceId, request.AgentId, ct);
            if (agent is null || !agent.IsEnabled || agent.IsFrozen || string.IsNullOrWhiteSpace(agent.MainSessionId))
            {
                await _wakeQueue.RemoveAsync(request.AgentId, ct);
                _heartbeatRetryCounts.TryRemove(request.AgentId, out _);
                return;
            }
            preference = await TryReadHeartbeatPreferenceAsync(request.AgentId, ct);
            if (preference?.Enabled == false)
            {
                await _wakeQueue.RemoveAsync(request.AgentId, ct);
                _heartbeatRetryCounts.TryRemove(request.AgentId, out _);
                _logger.LogInformation("[HeartbeatOrchestrator] Removed ineligible heartbeat agent={Agent}", request.AgentId);
                return;
            }

            // 发心跳前检查 Agent 是否正在忙（例如正在生成长文本响应）
            // 如果忙则跳过本次心跳，避免打断用户会话
            var workspaceId = _workspaceId;
            var availabilityProvider = scope.ServiceProvider.GetService<IAgentExecutionAvailabilityProvider>();
            var availability = availabilityProvider is not null
                ? await availabilityProvider.GetAsync(workspaceId, request.AgentId, ct)
                : scope.ServiceProvider
                    .GetRequiredService<IAgentExecutionStateRegistry>()
                    .Get(workspaceId, request.AgentId);

            if (!availability.CanStartMessageDelivery)
            {
                _logger.LogInformation(
                    "[HeartbeatOrchestrator] Skip heartbeat agent={Agent} (busy: {Status})",
                    request.AgentId, availability.Status);
                await RequeueSkippedHeartbeatAsync(request, ct);
                return;
            }

            // “Runtime 当前没在生成”不等于 Agent 没有在工作。Agent 可能正在等待
            // 子代理、持有 Task/Goal，或已有持久投递/自动工作租约。心跳必须同时通过
            // 持久事实投影；投影缺失/过期/重建失败均 fail closed，避免打断长任务节奏。
            var durableAvailability = scope.ServiceProvider
                .GetService<IAgentAvailabilityProjectionStore>();
            if (durableAvailability is not null)
            {
                AgentAvailabilitySnapshot durableSnapshot;
                try
                {
                    durableSnapshot = await durableAvailability.RebuildAsync(
                        workspaceId,
                        request.AgentId,
                        ct);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(
                        ex,
                        "[HeartbeatOrchestrator] Skip heartbeat agent={Agent} (durable availability rebuild failed)",
                        request.AgentId);
                    await RequeueSkippedHeartbeatAsync(request, ct);
                    return;
                }

                if (!durableSnapshot.CanAcceptAutomaticTask(DateTimeOffset.UtcNow))
                {
                    _logger.LogInformation(
                        "[HeartbeatOrchestrator] Skip heartbeat agent={Agent} (durable state={State} reason={Reason} task={TaskId} goal={GoalRunId} child={SubAgentRunId})",
                        request.AgentId,
                        durableSnapshot.State,
                        durableSnapshot.ActivityReason,
                        durableSnapshot.ActiveTaskId,
                        durableSnapshot.ActiveGoalRunId,
                        durableSnapshot.ActiveSubAgentRunId);
                    await RequeueSkippedHeartbeatAsync(request, ct);
                    return;
                }
            }

            // ── 检查是否有未 ack 的 pending 心跳投递 ──
            // 若该 agent 已有未确认的心跳 delivery (queued/delivering/retrying)，
            // 则跳过本次心跳，避免重复投递形成风暴。
            var inbox = scope.ServiceProvider.GetService<IMessageInbox>();
            if (inbox is not null)
            {
                var pendingQuery = new MessageInboxQuery
                {
                    Endpoint = new MessageAddress
                    {
                        Kind = MessageEndpointKinds.Agent,
                        Id = request.AgentId,
                        WorkspaceId = workspaceId,
                    },
                    WorkspaceId = workspaceId,
                    Limit = 50,
                    IncludeDelivered = false,
                };
                var pending = await inbox.ListAsync(pendingQuery, ct);
                var hasPendingHeartbeat = pending.Any(item =>
                    string.Equals(item.From.Kind, MessageEndpointKinds.System, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(item.From.Id, "heartbeat", StringComparison.OrdinalIgnoreCase));
                if (hasPendingHeartbeat)
                {
                    _logger.LogInformation(
                        "[HeartbeatOrchestrator] Skip heartbeat agent={Agent} (pending heartbeat delivery exists)",
                        request.AgentId);
                    await RequeueSkippedHeartbeatAsync(request, ct);
                    return;
                }
            }

            var messageSystem = scope.ServiceProvider.GetRequiredService<IMessageSystem>();
            var heartbeatPrompt = await agentConfig.GetAgentHeartbeatPromptAsync(workspaceId, request.AgentId, ct);
            if (string.IsNullOrWhiteSpace(heartbeatPrompt))
            {
                // 兜底：实例 heartbeatPrompt.md 缺失/空白时，使用全局默认（embedded heartbeatPrompt.md）；
                // 嵌入式资源也缺失时回退编译期默认，避免空提示词导致 Agent 心跳无指令。
                heartbeatPrompt = _defaultHeartbeatPrompt;
                if (string.IsNullOrWhiteSpace(heartbeatPrompt))
                    heartbeatPrompt = WorkspaceAgentFileService.DefaultHeartbeatPrompt;
                _logger.LogWarning(
                    "[HeartbeatOrchestrator] Agent={Agent} has empty heartbeat prompt; using default source",
                    request.AgentId);
            }
            var queuedSeconds = (int)(_clock.GetUtcNow().UtcDateTime - request.EnqueuedAt).TotalSeconds;
            var heartbeatContent = FormatHeartbeatPrompt(
                heartbeatPrompt,
                request.AgentId,
                queuedSeconds);

            var heartbeatContentWithPrefix = $"── 系统心跳 ──\n\n{heartbeatContent}";

            var envelope = new MessageEnvelope
            {
                From = new MessageAddress
                {
                    Kind = MessageEndpointKinds.System,
                    Id = "heartbeat",
                    WorkspaceId = workspaceId,
                },
                To = new[]
                {
                    new MessageAddress
                    {
                        Kind = MessageEndpointKinds.Agent,
                        Id = request.AgentId,
                        WorkspaceId = workspaceId,
                    },
                },
                // 心跳内容是目标 Agent 专属提示词渲染：Broadcast 会把 A 的提示词投给 B，
                // 造成上下文污染（曾出现 audit-agent.001 的心跳内容被投给 6a8 的事故）。
                // 必须 Direct 定向投递（MessageRouter 非 Broadcast 分支按 envelope.To 精确投递）。
                Audience = MessageAudiences.Direct,
                Visibility = MessageVisibilities.Public,
                ContentType = MessageContentTypes.Heartbeat,
                Content = heartbeatContentWithPrefix,
                RoomId = workspaceId,
                Priority = 0,
                Metadata = new Dictionary<string, string>
                {
                    ["source"] = "heartbeat",
                    ["agent_id"] = request.AgentId,
                    ["idle_duration_seconds"] = ((int)idleDuration.TotalSeconds).ToString(),
                    ["queue_wait_seconds"] = queuedSeconds.ToString(),
                    ["min_idle_seconds"] = ((int)request.MinIdle.TotalSeconds).ToString(),
                    ["max_idle_seconds"] = ((int)request.MaxIdle.TotalSeconds).ToString(),
                },
            };

            var result = await messageSystem.SendAsync(envelope, ct);
            _logger.LogInformation(
                "[HeartbeatOrchestrator] Heartbeat sent to agent={Agent} messageId={MsgId} idle={Idle}s deliveries={Dlv}",
                request.AgentId, result.MessageId,
                ((int)idleDuration.TotalSeconds).ToString(),
                string.Join(",", result.DeliveryIds));

            // 成功发送 → 重置失败重试计数
            _heartbeatRetryCounts.TryRemove(request.AgentId, out _);
            var (min, max) = GetInterval(preference);
            await _wakeQueue.ScheduleNextAsync(request.AgentId, min, max, ct);

            // ── 出队成功后检查队列是否还有待处理 agent ──
            var remaining = await _wakeQueue.CountAsync(ct);
            if (remaining > 0)
            {
                // 有 agent 在排队 → 允许 IdleDetector 继续触发下一轮
                _idleDetector.ReArm();
                _logger.LogDebug("[HeartbeatOrchestrator] ReArmed IdleDetector, remaining={Remaining}", remaining);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The durable due entry remains recoverable by the next process.
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[HeartbeatOrchestrator] Failed to send heartbeat to agent={Agent}",
                request.AgentId);

            // 指数退避重试：30s → 60s → 120s，最多 3 次
            var retryCount = _heartbeatRetryCounts.AddOrUpdate(
                request.AgentId, 1, (_, c) => c + 1);
            if (!await _wakeQueue.EnqueueRetryAsync(request.AgentId, retryCount - 1, ct))
            {
                _heartbeatRetryCounts.TryRemove(request.AgentId, out _);
                var (min, max) = GetInterval(preference);
                await _wakeQueue.ScheduleNextAsync(request.AgentId, min, max, ct);
            }
        }
    }

    private async Task RequeueSkippedHeartbeatAsync(
        WakeRequest request,
        CancellationToken ct)
    {
        await _wakeQueue.ScheduleNextAsync(request.AgentId, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30), ct);
        _idleDetector.ReArm();
    }

    /// <summary>
    /// 对实例提示词做轻量格式化。
    ///
    /// 兼容旧 prompt 中可能存在的 string.Format 占位符：{0}=AgentId，{1}=排队等待秒数。
    /// 格式错误时直接使用原文，避免单个 Agent 配置错误导致心跳调度整体失败。
        /// </summary>
    private string FormatHeartbeatPrompt(string? prompt, string agentId, int queuedSeconds)
    {
        if (string.IsNullOrWhiteSpace(prompt))
        {
            var fallbackPrompt = $"当前时间 (UTC): {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}\n"
                + $"空闲时长: {queuedSeconds} 秒\n"
                + $"Agent ID: {agentId}\n\n"
                + "请检查是否有待处理的任务或需要主动执行的操作。";
            return HeartbeatPromptComposer.AppendAutonomousExecutionContract(fallbackPrompt);
        }

        try
        {
            return HeartbeatPromptComposer.AppendAutonomousExecutionContract(
                string.Format(prompt, agentId, queuedSeconds));
        }
        catch (FormatException)
        {
            // R6: Log format errors instead of silently swallowing them
            _logger.LogWarning("[HeartbeatOrchestrator] Invalid format in heartbeat prompt for agent={Agent}: {Prompt}", agentId, prompt);
            return HeartbeatPromptComposer.AppendAutonomousExecutionContract(prompt);
        }
    }

    /// <summary>Reconcile the complete catalog at most once per minute; due checks stay cheap.</summary>
    private async Task EnsureAllAgentsRegisteredAsync(CancellationToken ct)
    {
        _nextCatalogReconcile = _clock.GetUtcNow().Add(CatalogReconcileInterval);
        using var scope = _scopeFactory.CreateScope();
        var catalog = scope.ServiceProvider.GetService<IWorkspaceAgentCatalog>();
        if (catalog is null) return;
        try
        {
            var agents = await catalog.ListAgentsAsync(_workspaceId, ct);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var registered = 0;
            foreach (var agent in agents)
            {
                seen.Add(agent.AgentId);
                try
                {
                    var eligible = agent.IsEnabled && !agent.IsFrozen && !string.IsNullOrWhiteSpace(agent.MainSessionId);
                    var preference = eligible ? await TryReadHeartbeatPreferenceAsync(agent.AgentId, ct) : null;
                    if (!eligible || preference?.Enabled == false)
                    {
                        await _wakeQueue.RemoveAsync(agent.AgentId, ct);
                        _heartbeatRetryCounts.TryRemove(agent.AgentId, out _);
                        continue;
                    }
                    if (await _wakeQueue.IsInQueueAsync(agent.AgentId, ct)) continue;
                    var (min, max) = GetInterval(preference);
                    await _wakeQueue.EnsureScheduledAsync(agent.AgentId, min, max, ct);
                    registered++;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[HeartbeatOrchestrator] Registration failed agent={Agent}; retry on next reconciliation", agent.AgentId);
                }
            }
            foreach (var removed in _knownAgents.Except(seen).ToArray())
                await _wakeQueue.RemoveAsync(removed, ct);
            _knownAgents.Clear();
            _knownAgents.UnionWith(seen);
            if (registered > 0)
                _logger.LogInformation("[HeartbeatOrchestrator] Heartbeat top-up registered {Count} agent(s)", registered);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[HeartbeatOrchestrator] Heartbeat top-up failed workspace={WorkspaceId}", _workspaceId);
        }
    }

    private static (TimeSpan Min, TimeSpan Max) GetInterval(HeartbeatPreference? preference)
    {
        var min = Math.Clamp(preference?.MinIdleSeconds > 0 ? preference.MinIdleSeconds : 3600, 60, 86400);
        var max = Math.Clamp(preference?.MaxIdleSeconds > 0 ? preference.MaxIdleSeconds : min, min, 86400);
        return (TimeSpan.FromSeconds(min), TimeSpan.FromSeconds(max));
    }

    /// <summary>
    /// 读取 {AgentInstanceRoot}/heartbeat.json（由 sleep 工具写入）。
    /// 文件不存在时使用默认心跳；损坏/不可读时拒绝本次登记或发送，避免误启用。
    /// 区间由 GetInterval 与 sleep 工具保持一致地钳制。
    /// </summary>
    private async Task<HeartbeatPreference?> TryReadHeartbeatPreferenceAsync(string agentId, CancellationToken ct)
    {
        var filePath = Path.Combine(_paths.AgentInstanceRoot(agentId), "heartbeat.json");
        if (!File.Exists(filePath)) return null;
        // Invalid/unreadable preferences fail this registration closed; never silently re-enable an opt-out.
        var json = await File.ReadAllTextAsync(filePath, ct);
        return JsonSerializer.Deserialize<HeartbeatPreference>(json)
            ?? throw new JsonException("Heartbeat preference must be an object");
    }

}

internal static class HeartbeatPromptComposer
{
    internal const string AutonomousExecutionContract = """
## 系统级自主执行契约（优先于上方实例提示词）

- 心跳是自主执行轮次，不是咨询轮次。不得询问用户“要做什么”“是否继续”或让用户选择下一步。
- 先调用 `goal_read`，再用 `query_session_logs(exclude_heartbeat=true)` 恢复最近未完成、且已由用户授权的工作；不要因为 goal.md 为空就忽略最近对话中的未完成任务。
- 找到可执行工作后，本轮必须立即完成一个具体、安全、可回滚的推进步骤。需要并行或编码时可调用 `spawn_sub_agent`/Smart 工作流，并在后续心跳用 `query_sub_agents` 继续验收。
- 不得扩大用户授权范围。若某一步需要额外审批、破坏性操作或外部协调，记录阻塞并改做另一个安全步骤；只有确实无路可走时才汇报阻塞。
- 只有在已完成一个推进步骤，或有证据确认不存在可推进事项后，才调用 `sleep`。不要以问题结束心跳回复。
- 心跳结束之后，通过调用 `send_message` 汇报进展到飞书（模板：本周期摘要/目标进展/健康指标/下一步），失败时写入 work_summary 下轮补报，不阻塞 sleep。
""";

    internal static string AppendAutonomousExecutionContract(string prompt)
        => $"{prompt.Trim()}\n\n{AutonomousExecutionContract}";
}

