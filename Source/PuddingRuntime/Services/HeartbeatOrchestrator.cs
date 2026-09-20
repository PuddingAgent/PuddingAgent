using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PuddingCode.Abstractions;
using PuddingCode.Configuration;
using PuddingCode.Models;
using PuddingRuntime.Models;

namespace PuddingRuntime.Services;

/// <summary>
/// 心跳编排器：把「空闲信号 → 唤醒队列 → 消息投递」串成闭环。
///
/// 背景（本次修复的缺陷）：<see cref="IdleDetector"/> 只负责判空闲并发事件，
/// <c>AgentWakeQueue</c> 只负责维护唤醒计划，而真正「把到期 Agent 唤醒一次」的执行体
/// 此前并不存在 —— 队列注释中引用的 <c>HeartbeatOrchestrator</c> 没有实现。后果：
///   · <c>AgentWakeQueue.TryDequeueAsync</c> / <c>EnsureDefaultAsync</c> 在生产代码中零调用；
///   · <see cref="IIdleDetector.OnIdleThresholdReached"/> 在生产代码中零订阅者；
///   · 心跳从未真正投递过，<c>agent_status</c> 展示的心跳只是读 heartbeat.json 的意图。
///
/// 本类补齐这一环，并承担 <see cref="HeartbeatPreference"/> 文档所述的
/// 「启动时读取恢复」职责：启动时按 heartbeat.json 恢复自定义间隔，
/// 未自定义的 Agent 走队列自带的默认间隔（1 小时）。
/// </summary>
public sealed class HeartbeatOrchestrator : IHostedService, IDisposable
{
    /// <summary>
    /// 兜底排空周期。「是否到期」由 <c>AgentWakeQueue</c> 内部按 EarliestWakeAt 判定，
    /// 本周期只决定「多久看一眼」。
    ///
    /// 为什么不用 <see cref="IIdleDetector.ReArm"/> 做节拍：ReArm 会解除空闲窗口的
    /// 「本窗口已触发」标记，而 detector 每次触发都记一条 LogInformation；以 5 秒级
    /// 粒度持续 ReArm 会写成千上万条日志，把本就只能回溯约 45 分钟的日志缓冲冲垮。
    /// </summary>
    private static readonly TimeSpan DrainInterval = TimeSpan.FromSeconds(60);

    /// <summary>
    /// 单个排空周期内最多唤醒的 Agent 数。与队列注释「ensures only one agent is woken
    /// per idle cycle, avoiding concurrent LLM calls」一致。
    /// </summary>
    private const int MaxWakeupsPerCycle = 1;

    /// <summary>心跳发送者标识。<c>MessageDeliveryDispatcher.IsHeartbeat</c> 依据 From 判定。</summary>
    private const string HeartbeatSenderId = "heartbeat";

    private readonly IIdleDetector _idleDetector;
    private readonly AgentWakeQueue _wakeQueue;
    private readonly IMessageSystem _messageSystem;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly PuddingDataPaths _paths;
    private readonly ILogger<HeartbeatOrchestrator> _logger;
    private readonly string _workspaceId;

    private readonly SemaphoreSlim _drainGate = new(1, 1);
    private readonly ConcurrentDictionary<string, int> _consecutiveFailures = new(StringComparer.OrdinalIgnoreCase);

    private static readonly JsonSerializerOptions HeartbeatJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private CancellationTokenSource? _loopCts;
    private Task? _loopTask;
    private int _started;

    public HeartbeatOrchestrator(
        IIdleDetector idleDetector,
        AgentWakeQueue wakeQueue,
        IMessageSystem messageSystem,
        IServiceScopeFactory scopeFactory,
        PuddingDataPaths paths,
        ILogger<HeartbeatOrchestrator> logger,
        IConfiguration? configuration = null)
    {
        _idleDetector = idleDetector;
        _wakeQueue = wakeQueue;
        _messageSystem = messageSystem;
        _scopeFactory = scopeFactory;
        _paths = paths;
        _logger = logger;

        // 与 AgentStatusTool / Subconscious 的默认工作区口径保持一致。
        _workspaceId = configuration?.GetValue<string>("Subconscious:Scheduling:DefaultWorkspaceId")
            ?? "default";
    }

    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.CompareExchange(ref _started, 1, 0) == 1)
        {
            _logger.LogInformation("[HeartbeatOrchestrator] Already started; skipping duplicate StartAsync");
            return;
        }

        _idleDetector.OnIdleThresholdReached += OnIdleThresholdReachedAsync;

        var registered = await RestoreRegistrationsAsync(cancellationToken);

        _loopCts = new CancellationTokenSource();
        _loopTask = RunDrainLoopAsync(_loopCts.Token);

        _logger.LogInformation(
            "[HeartbeatOrchestrator] Started workspace={Workspace} drainInterval={Interval}s maxWakeupsPerCycle={Max} registeredAgents={Registered}",
            _workspaceId,
            (int)DrainInterval.TotalSeconds,
            MaxWakeupsPerCycle,
            registered);
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _idleDetector.OnIdleThresholdReached -= OnIdleThresholdReachedAsync;

        _loopCts?.Cancel();
        if (_loopTask is not null)
        {
            try
            {
                await _loopTask.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                // 停机路径的正常取消。
            }
        }

        _loopCts?.Dispose();
        _loopCts = null;
        _loopTask = null;
        _logger.LogInformation("[HeartbeatOrchestrator] Stopped");
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _loopCts?.Dispose();
        _drainGate.Dispose();
    }

    /// <summary>
    /// 启动注册：按 heartbeat.json 恢复自定义间隔（<c>sleep</c> 工具写入者），
    /// 其余 Agent 走队列自带的默认间隔（1 小时）。只在启动时调用一次 ——
    /// 每周期重复调用 EnqueueAsync 会把 EarliestWakeAt 一直往后推，导致永不触发。
    /// </summary>
    public async Task<int> RestoreRegistrationsAsync(CancellationToken ct)
    {
        var agents = await ListEnabledAgentsAsync(ct);
        var registered = 0;

        foreach (var agentId in agents)
        {
            try
            {
                var preference = ReadHeartbeatPreference(agentId);
                if (preference is { MinIdleSeconds: > 0, MaxIdleSeconds: > 0 }
                    && preference.MaxIdleSeconds >= preference.MinIdleSeconds)
                {
                    await _wakeQueue.EnqueueAsync(
                        agentId,
                        TimeSpan.FromSeconds(preference.MinIdleSeconds),
                        TimeSpan.FromSeconds(preference.MaxIdleSeconds),
                        ct);
                    _logger.LogInformation(
                        "[HeartbeatOrchestrator] Restored custom heartbeat agent={Agent} min={Min}s max={Max}s",
                        agentId,
                        preference.MinIdleSeconds,
                        preference.MaxIdleSeconds);
                }
                else
                {
                    await _wakeQueue.EnsureDefaultAsync(agentId, ct);
                }

                registered++;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[HeartbeatOrchestrator] Register failed agent={Agent}", agentId);
            }
        }

        return registered;
    }

    /// <summary>
    /// 每个排空周期补齐默认注册，覆盖「启动之后才创建的 Agent」。
    /// <c>EnsureDefaultAsync</c> 对已在队列中的 Agent 是幂等的（直接返回），
    /// 因此不会推迟既有条目的 EarliestWakeAt。
    /// </summary>
    public async Task<int> EnsureDefaultsAsync(CancellationToken ct)
    {
        var agents = await ListEnabledAgentsAsync(ct);
        var added = 0;

        foreach (var agentId in agents)
        {
            try
            {
                if (!await _wakeQueue.IsInQueueAsync(agentId, ct))
                {
                    await _wakeQueue.EnsureDefaultAsync(agentId, ct);
                    added++;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[HeartbeatOrchestrator] Ensure default failed agent={Agent}", agentId);
            }
        }

        return added;
    }

    /// <summary>
    /// 执行一次排空：最多取出 <see cref="MaxWakeupsPerCycle"/> 个到期 Agent 并投递心跳；
    /// 投递失败按指数退避重新入队，连续失败达上限后丢弃并报错。
    /// </summary>
    public async Task<int> DrainOnceAsync(CancellationToken ct)
    {
        await _drainGate.WaitAsync(ct);
        try
        {
            var woken = 0;
            var attempts = 0;

            while (attempts < MaxWakeupsPerCycle)
            {
                var request = await _wakeQueue.TryDequeueAsync(ct);
                if (request is null)
                    break;

                // 计入尝试预算：失败也不在同一周期内继续唤醒下一个，
                // 否则投递侧整体故障时会退化成一次唤醒风暴。
                attempts++;

                if (await SendHeartbeatAsync(request, ct))
                {
                    _consecutiveFailures.TryRemove(request.AgentId, out _);
                    woken++;
                    continue;
                }

                var failures = _consecutiveFailures.AddOrUpdate(request.AgentId, 1, (_, current) => current + 1);
                var requeued = await _wakeQueue.EnqueueRetryAsync(request.AgentId, failures - 1, ct);
                if (!requeued)
                {
                    _consecutiveFailures.TryRemove(request.AgentId, out _);
                    _logger.LogError(
                        "[HeartbeatOrchestrator] Heartbeat dropped after {Failures} consecutive failures agent={Agent}",
                        failures,
                        request.AgentId);
                }
            }

            return woken;
        }
        finally
        {
            _drainGate.Release();
        }
    }

    private async Task RunDrainLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(DrainInterval);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (!await timer.WaitForNextTickAsync(ct))
                    break;
            }
            catch (OperationCanceledException)
            {
                break;
            }

            try
            {
                await EnsureDefaultsAsync(ct);
                await DrainOnceAsync(ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[HeartbeatOrchestrator] Drain cycle failed");
            }
        }
    }

    private async Task OnIdleThresholdReachedAsync(TimeSpan idle, CancellationToken ct)
    {
        try
        {
            await DrainOnceAsync(ct);
        }
        catch (OperationCanceledException)
        {
            // 停机路径的正常取消。
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[HeartbeatOrchestrator] Idle-triggered drain failed idle={Idle}s", (int)idle.TotalSeconds);
        }
    }

    private async Task<bool> SendHeartbeatAsync(WakeRequest request, CancellationToken ct)
    {
        try
        {
            var envelope = BuildHeartbeatEnvelope(request);
            var result = await _messageSystem.SendAsync(envelope, ct);
            _logger.LogInformation(
                "[HeartbeatOrchestrator] Heartbeat sent agent={Agent} idle={Idle}s min={Min}s max={Max}s messageId={MessageId}",
                request.AgentId,
                (int)(DateTime.UtcNow - request.EnqueuedAt).TotalSeconds,
                (int)request.MinIdle.TotalSeconds,
                (int)request.MaxIdle.TotalSeconds,
                result.MessageId);
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[HeartbeatOrchestrator] Heartbeat send failed agent={Agent}", request.AgentId);
            return false;
        }
    }

    private MessageEnvelope BuildHeartbeatEnvelope(WakeRequest request)
    {
        var idleSeconds = (int)Math.Max(0, (DateTime.UtcNow - request.EnqueuedAt).TotalSeconds);
        var minSeconds = (int)request.MinIdle.TotalSeconds;
        var maxSeconds = (int)request.MaxIdle.TotalSeconds;

        // 内容必须以 [HEARTBEAT] 开头：ContextPipelineLayers.IsHeartbeatContent 依赖此前缀，
        // 并以 From=system/heartbeat 供 MessageDeliveryDispatcher.IsHeartbeat 判定。
        var content =
            $"[HEARTBEAT] 系统心跳：已空闲 {idleSeconds} 秒（本 Agent 心跳间隔 {minSeconds}~{maxSeconds} 秒）。" +
            "请先读 goal.md 判断是否有待推进事项；若无待办，简短确认即可，不必展开工作。";

        return new MessageEnvelope
        {
            From = new MessageAddress
            {
                Kind = MessageEndpointKinds.System,
                Id = HeartbeatSenderId,
                WorkspaceId = _workspaceId,
            },
            To =
            [
                new MessageAddress
                {
                    Kind = MessageEndpointKinds.Agent,
                    Id = request.AgentId,
                    WorkspaceId = _workspaceId,
                },
            ],
            Audience = MessageAudiences.Direct,
            Visibility = MessageVisibilities.Public,
            ContentType = MessageContentTypes.Text,
            Content = content,
            RoomId = _workspaceId,
            Priority = 0,
            Metadata = new Dictionary<string, string>
            {
                ["source"] = "heartbeat-orchestrator",
                ["agent_id"] = request.AgentId,
                ["idle_seconds"] = idleSeconds.ToString(),
                ["min_idle_seconds"] = minSeconds.ToString(),
                ["max_idle_seconds"] = maxSeconds.ToString(),
            },
        };
    }

    private async Task<IReadOnlyList<string>> ListEnabledAgentsAsync(CancellationToken ct)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var catalog = scope.ServiceProvider.GetService<IWorkspaceAgentQueryService>();
            if (catalog is null)
            {
                _logger.LogWarning("[HeartbeatOrchestrator] IWorkspaceAgentQueryService unavailable; no agents registered");
                return [];
            }

            var agents = await catalog.ListAgentsAsync(_workspaceId, ct);
            return agents
                .Where(a => a.IsEnabled && !string.IsNullOrWhiteSpace(a.AgentId))
                .Select(a => a.AgentId)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[HeartbeatOrchestrator] Agent enumeration failed workspace={Workspace}", _workspaceId);
            return [];
        }
    }

    private HeartbeatPreference? ReadHeartbeatPreference(string agentId)
    {
        try
        {
            var path = Path.Combine(_paths.AgentInstanceRoot(agentId), "heartbeat.json");
            if (!File.Exists(path))
                return null;

            return JsonSerializer.Deserialize<HeartbeatPreference>(File.ReadAllText(path), HeartbeatJsonOptions);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[HeartbeatOrchestrator] heartbeat.json unreadable agent={Agent}", agentId);
            return null;
        }
    }
}
