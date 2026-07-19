using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PuddingCode.Abstractions;
using PuddingCode.Configuration;
using PuddingCode.Models;
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
    private readonly string? _configuredAgentId;
    private readonly string _workspaceId;
        private string? _currentAgentId;
    private readonly ConcurrentDictionary<string, int> _heartbeatRetryCounts = new(StringComparer.OrdinalIgnoreCase);

    public HeartbeatOrchestrator(
        IIdleDetector idleDetector,
        AgentWakeQueue wakeQueue,
        IServiceScopeFactory scopeFactory,
        PuddingDataPaths paths,
        ILogger<HeartbeatOrchestrator> logger,
        IConfiguration configuration)
    {
        _idleDetector = idleDetector;
        _wakeQueue = wakeQueue;
        _scopeFactory = scopeFactory;
        _paths = paths;
        _logger = logger;
        _configuredAgentId = string.IsNullOrWhiteSpace(configuration["Agent:DefaultId"])
            ? null
            : configuration["Agent:DefaultId"]!.Trim();
        _workspaceId = string.IsNullOrWhiteSpace(configuration["Agent:DefaultWorkspaceId"])
            ? "default"
            : configuration["Agent:DefaultWorkspaceId"]!.Trim();
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        Console.WriteLine("[Startup] HeartbeatOrchestrator.StartAsync — resolving agent id...");
        _currentAgentId = await ResolveDefaultAgentIdAsync(cancellationToken);
        Console.WriteLine($"[Startup] HeartbeatOrchestrator — agentId={_currentAgentId ?? "(null)"}");
                // R1 fix: Always subscribe to IdleDetector events.
        // If no agent is configured yet, OnIdleTickAsync will retry resolution on each tick.
        if (string.IsNullOrWhiteSpace(_currentAgentId))
        {
            _logger.LogWarning(
                "[HeartbeatOrchestrator] No enabled workspace agent found for workspace={WorkspaceId}; will retry resolution on each idle tick",
                _workspaceId);
        }
        else
        {
            // 等待从磁盘恢复上次 Agent 设置的心跳频率，避免与 EnsureDefaultAsync 竞态
            await RestoreHeartbeatPreferenceAsync(cancellationToken);
        }

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
    /// Ensures a default heartbeat is queued if empty, then tries to dequeue
    /// the next ready agent; if one is found, sends a heartbeat message.
    /// If more agents remain in the queue, re-arms the idle detector.
    /// </summary>
    private async Task OnIdleTickAsync(TimeSpan idleDuration, CancellationToken ct)
    {
                // ── 队列空时自动为所有启用 Agent 填充默认心跳 ──
        var currentAgentId = _currentAgentId;

        // R1 fix: Retry agent resolution on each tick if none was found at startup.
        // This allows dynamic recovery when agents are added after the service starts.
        if (string.IsNullOrWhiteSpace(currentAgentId))
        {
            currentAgentId = await ResolveDefaultAgentIdAsync(ct);
            if (string.IsNullOrWhiteSpace(currentAgentId))
                return;
            _currentAgentId = currentAgentId;
        }

        var count = await _wakeQueue.CountAsync(ct);
        if (count == 0)
        {
            using var fillScope = _scopeFactory.CreateScope();
            var catalog = fillScope.ServiceProvider.GetService<IWorkspaceAgentCatalog>();
            if (catalog is not null)
            {
                var allAgents = await catalog.ListAgentsAsync(_workspaceId, ct);
                var enabledAgents = allAgents
                    .Where(a => a.IsEnabled && !a.IsFrozen)
                    .ToList();
                foreach (var agent in enabledAgents)
                {
                    await _wakeQueue.EnsureDefaultAsync(agent.AgentId, ct);
                }
                _logger.LogDebug(
                    "[HeartbeatOrchestrator] Queue was empty, default-enqueued {Count} agent(s)",
                    enabledAgents.Count);
            }
            else
            {
                await _wakeQueue.EnsureDefaultAsync(currentAgentId, ct);
                _logger.LogDebug(
                    "[HeartbeatOrchestrator] Queue was empty, default-enqueued agent={Agent} (catalog unavailable)",
                    currentAgentId);
            }
        }

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

        try
        {
            using var scope = _scopeFactory.CreateScope();

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
                _idleDetector.ReArm();
                return;
            }

                        var agentConfig = scope.ServiceProvider.GetRequiredService<WorkspaceAgentFileService>();
            var messageSystem = scope.ServiceProvider.GetRequiredService<IMessageSystem>();
            var heartbeatPrompt = await agentConfig.GetAgentHeartbeatPromptAsync(workspaceId, request.AgentId, ct);
            var queuedSeconds = (int)(DateTime.UtcNow - request.EnqueuedAt).TotalSeconds;
            var heartbeatContent = FormatHeartbeatPrompt(
                heartbeatPrompt,
                request.AgentId,
                queuedSeconds);

            var promptContent = heartbeatContent;
            var heartbeatContentWithPrefix = $"── 系统心跳 ──\n\n{promptContent}";

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
                Audience = MessageAudiences.Broadcast,
                Visibility = MessageVisibilities.Public,
                ContentType = MessageContentTypes.Heartbeat,
                Content = heartbeatContentWithPrefix,
                RoomId = workspaceId,
                Priority = 0,
                Metadata = new Dictionary<string, string>
                {
                    ["source"] = "heartbeat",
                    ["agent_id"] = request.AgentId,
                    ["idle_duration_seconds"] = ((int)(DateTime.UtcNow - request.EnqueuedAt).TotalSeconds).ToString(),
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

            // ── 出队成功后检查队列是否还有待处理 agent ──
            var remaining = await _wakeQueue.CountAsync(ct);
            if (remaining > 0)
            {
                // 有 agent 在排队 → 允许 IdleDetector 继续触发下一轮
                _idleDetector.ReArm();
                _logger.LogDebug("[HeartbeatOrchestrator] ReArmed IdleDetector, remaining={Remaining}", remaining);
            }
        }
                catch (Exception ex)
        {
            _logger.LogError(ex, "[HeartbeatOrchestrator] Failed to send heartbeat to agent={Agent}",
                request.AgentId);

            // 指数退避重试：30s → 60s → 120s，最多 3 次
            var retryCount = _heartbeatRetryCounts.AddOrUpdate(
                request.AgentId, 1, (_, c) => c + 1);
            await _wakeQueue.EnqueueRetryAsync(request.AgentId, retryCount - 1, ct);
        }
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
            return $"当前时间 (UTC): {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}\n"
                + $"空闲时长: {queuedSeconds} 秒\n"
                + $"Agent ID: {agentId}\n\n"
                + "请检查是否有待处理的任务或需要主动执行的操作。";
        }

        try
        {
            return string.Format(prompt, agentId, queuedSeconds);
        }
        catch (FormatException)
        {
            // R6: Log format errors instead of silently swallowing them
            _logger.LogWarning("[HeartbeatOrchestrator] Invalid format in heartbeat prompt for agent={Agent}: {Prompt}", agentId, prompt);
            return prompt;
        }
    }

    /// <summary>
    /// Resolve the heartbeat target from the workspace agent catalog.
    /// Heartbeat delivery is routed through Message Fabric, whose participant
    /// resolver only creates deliveries for real enabled workspace agents.
    /// A hard-coded historical agent id can make the system message visible in
    /// the room while no agent ever claims it, so the configured id is treated
    /// as a preference rather than as an authority.
    /// </summary>
    private async Task<string?> ResolveDefaultAgentIdAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var catalog = scope.ServiceProvider.GetService<IWorkspaceAgentCatalog>();
        if (catalog is null)
            return _configuredAgentId;

        var agents = await catalog.ListAgentsAsync(_workspaceId, ct);
        var enabled = agents
            .Where(agent => agent.IsEnabled && !agent.IsFrozen)
            .ToList();

        if (!string.IsNullOrWhiteSpace(_configuredAgentId))
        {
            var configured = enabled.FirstOrDefault(agent =>
                string.Equals(agent.AgentId, _configuredAgentId, StringComparison.OrdinalIgnoreCase));
            if (configured is not null)
                return configured.AgentId;

            _logger.LogWarning(
                "[HeartbeatOrchestrator] Configured default agent was not found or disabled workspace={WorkspaceId} configuredAgent={AgentId}; falling back to workspace catalog",
                _workspaceId,
                _configuredAgentId);
        }

        var selected = enabled
            .Where(agent => !string.IsNullOrWhiteSpace(agent.MainSessionId))
            .OrderBy(agent => agent.AgentId, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault()
            ?? enabled
                .OrderBy(agent => agent.AgentId, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();

        if (selected is not null)
        {
            _logger.LogInformation(
                "[HeartbeatOrchestrator] Resolved default heartbeat agent workspace={WorkspaceId} agent={AgentId} hasMainSession={HasMainSession}",
                _workspaceId,
                selected.AgentId,
                !string.IsNullOrWhiteSpace(selected.MainSessionId));
        }

        return selected?.AgentId;
    }

    /// <summary>
    /// 从 {AgentInstanceRoot}/heartbeat.json 恢复 Agent 上次设置的心跳频率。
    /// 文件不存在或损坏时静默跳过，使用默认心跳。
    /// </summary>
    private async Task RestoreHeartbeatPreferenceAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_currentAgentId))
            return;

        var filePath = Path.Combine(
            _paths.AgentInstanceRoot(_currentAgentId), "heartbeat.json");

        if (!File.Exists(filePath))
        {
            _logger.LogInformation(
                "[Heartbeat] No persisted config for {Agent}, using default", _currentAgentId);
            return;
        }

        try
        {
            var json = await File.ReadAllTextAsync(filePath, ct);
            var pref = JsonSerializer.Deserialize<HeartbeatPreference>(json);

            if (pref is null || pref.MinIdleSeconds <= 0 || pref.MaxIdleSeconds <= 0)
                return;

            // 安全护栏
            var min = Math.Clamp(pref.MinIdleSeconds, 60, 86400);
            var max = Math.Clamp(pref.MaxIdleSeconds, min, 86400);

            await _wakeQueue.EnqueueAsync(
                _currentAgentId,
                TimeSpan.FromSeconds(min),
                TimeSpan.FromSeconds(max),
                ct);

            _logger.LogInformation(
                "[Heartbeat] Restored config for {Agent}: min={Min}s max={Max}s",
                _currentAgentId, min, max);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "[Heartbeat] Failed to restore config for {Agent}, using default", _currentAgentId);
        }
    }
}

