using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PuddingCode.Abstractions;
using PuddingCode.Events;
using PuddingCode.Models;
using PuddingCode.Observability;
using PuddingCode.Platform;
using PuddingCode.SubAgents;

namespace PuddingPlatform.Services;

/// <summary>
/// SubAgentManager — ISubAgentManager 的实现。
/// 编排 SessionStateManager（追踪）+ AgentExecutionService（执行）+ IInternalEventBus（通知）。
/// 
/// Singleton 生命周期。使用 IServiceScopeFactory 解决 Singleton/Scoped 冲突。
/// </summary>
public sealed class SubAgentManager : ISubAgentManager
{
    private readonly ISessionStateManager _ssm;
    private readonly IServiceProvider _services;
    private readonly IInternalEventBus _eventBus;
    private readonly ISubAgentRunStore _runStore;
    private readonly ILogger<SubAgentManager> _logger;
    private readonly IRuntimeActivitySink _activitySink;
    private readonly IRuntimeTraceAccessor _traceAccessor;

    public SubAgentManager(
        ISessionStateManager ssm,
        IServiceProvider services,
        IInternalEventBus eventBus,
        ISubAgentRunStore runStore,
        ILogger<SubAgentManager> logger,
        IRuntimeActivitySink activitySink,
        IRuntimeTraceAccessor traceAccessor)
    {
        _ssm = ssm;
        _services = services;
        _eventBus = eventBus;
        _runStore = runStore;
        _logger = logger;
        _activitySink = activitySink;
        _traceAccessor = traceAccessor;
    }

    /// <summary>子代理 subSessionId → runId 的内存映射（供异步完成回调使用）。</summary>
    private readonly ConcurrentDictionary<string, string> _runIdMap = new();

    // ════════════════════════════════════════════════════════
    // 子代理执行
    // ════════════════════════════════════════════════════════

    public async Task<SubAgentSpawnResult> SpawnAsync(
        SubAgentSpawnRequest request,
        CancellationToken ct = default)
    {
        var subSessionId = $"{request.ParentSessionId}-sub-{Guid.NewGuid().ToString("N")[..8]}";
        var spawnedAt = DateTimeOffset.UtcNow;
        var trace = ResolveTrace(request, subSessionId);

        _logger.LogInformation(
            "[SubAgentMgr] Spawn async parent={Parent} sub={Sub} template={Template} task={Task}",
            request.ParentSessionId, subSessionId, request.TemplateId,
            request.TaskDescription.Length > 80 ? request.TaskDescription[..80] + "..." : request.TaskDescription);

        // 1. 追踪创建
        await _ssm.TrackSubAgentStartAsync(request.ParentSessionId, new SubAgentSpawnInfo
        {
            SubSessionId = subSessionId,
            ParentSessionId = request.ParentSessionId,
            ParentAgentId = request.ParentAgentId,
            TemplateId = request.TemplateId,
            ModelId = request.ModelId,
            TaskSummary = request.TaskDescription.Length > 200
                ? request.TaskDescription[..200] + "..."
                : request.TaskDescription,
            SpawnedAt = spawnedAt,
        }, ct);

        // 2. 创建子代理运行归档（ADR-021）
        var runHandle = await _runStore.CreateRunAsync(new SubAgentRunCreateRequest
        {
            ParentSessionId = request.ParentSessionId,
            SubSessionId = subSessionId,
            WorkspaceId = request.WorkspaceId,
            AgentInstanceId = request.ParentAgentId ?? subSessionId,
            TemplateId = request.TemplateId,
            Task = request.TaskDescription,
        }, ct);

        // 将 runId 存入内存映射（供完成回调使用）
        _runIdMap[subSessionId] = runHandle.RunId;

        // 3. 发布内部事件 subagent.run.created（替代旧的 agent.sub_completed styled 事件）
        await _eventBus.PublishAsync(new InternalEvent
        {
            Type = "subagent.run.created",
            SchemaVersion = EventSchemaRegistry.GetSchemaVersion("subagent.run.created"),
            Priority = EventPriorityLevel.Normal,
            Source = new EventSource { SourceType = "subagent", SourceId = subSessionId },
            WorkspaceId = request.WorkspaceId,
            SessionId = request.ParentSessionId,
            AgentId = request.ParentAgentId,
            Payload = new
            {
                sub_agent_id = subSessionId,
                template = request.TemplateId,
                task = request.TaskDescription.Length > 200
                    ? request.TaskDescription[..200] + "..."
                    : request.TaskDescription,
                run_id = runHandle.RunId,
            },
            Metadata = new Dictionary<string, string>
            {
                ["parent_session"] = request.ParentSessionId,
                ["parent_agent"] = request.ParentAgentId ?? "",
                ["run_id"] = runHandle.RunId,
            },
            TraceId = trace.TraceId,
            CorrelationId = trace.CorrelationId,
            Trace = trace,
        }, ct);

        var runId = runHandle.RunId;
        _logger.LogInformation(
            "[SubAgentMgr] Run archive created runId={RunId} sub={Sub} path={Path}",
            runId, subSessionId, runHandle.ArchivePath);

        await RecordActivityAsync(
            trace,
            "spawn",
            RuntimeActivityStatuses.Started,
            $"Spawned sub-agent {subSessionId}",
            ct);

        // 4. 推送 SubAgentSpawned 帧到父会话
        _ = _ssm.AppendAsync(request.ParentSessionId, request.WorkspaceId,
            ServerSentEventFrame.Json(SessionEventTypes.SubAgentSpawned, new
            {
                sub_agent_id = subSessionId,
                template = request.TemplateId,
                model = request.ModelId ?? "默认",
                task_summary = request.TaskDescription.Length > 200
                    ? request.TaskDescription[..200] + "..."
                    : request.TaskDescription,
            }), CancellationToken.None, trace, RuntimeActivityComponents.SubAgent, "sub_agent.spawned");

        // execution service resolved dynamically below

        // 3. Fire-and-forget 异步执行
        _ = Task.Run(async () =>
        {
            try
            {
                var r = await DispatchChildAgentAsync(subSessionId, request);
                var completedAt = DateTimeOffset.UtcNow;
                bool success = r.IsSuccess;
                string? replyText = r.ReplyText;
                string? errorMsg = r.ErrorMessage;
                TokenUsageDto? usage = r.Usage;

                await _ssm.TrackSubAgentCompleteAsync(subSessionId, new SubAgentResult
                {
                    Success = success,
                    Reply = replyText,
                    Error = errorMsg,
                    Usage = usage,
                    CompletedAt = completedAt,
                }, CancellationToken.None);

                // 完成运行归档（ADR-021）
                if (_runIdMap.TryRemove(subSessionId, out var completedRunId))
                {
                    await _runStore.CompleteRunAsync(completedRunId, new SubAgentRunCompletion
                    {
                        Status = success ? "completed" : "failed",
                        Output = replyText,
                        ErrorMessage = errorMsg,
                    }, CancellationToken.None);
                }
                else
                {
                    _logger.LogWarning("[SubAgentMgr] No runId found for sub={Sub}, skip archive completion", subSessionId);
                }

                // SSE 帧保持不变（前端 UI 仍用 subagent.completed）
                _ = _ssm.AppendAsync(request.ParentSessionId, request.WorkspaceId,
                    ServerSentEventFrame.Json(SessionEventTypes.SubAgentCompleted, new
                    {
                        sub_agent_id = subSessionId,
                        success,
                        reply = replyText,
                        error = errorMsg,
                    }), CancellationToken.None);

                // 发布内部事件 subagent.run.completed / subagent.run.failed
                var completedEventType = success ? "subagent.run.completed" : "subagent.run.failed";
                await _eventBus.PublishAsync(new InternalEvent
                {
                    Type = completedEventType,
                    SchemaVersion = EventSchemaRegistry.GetSchemaVersion(completedEventType),
                    Priority = EventPriorityLevel.Normal,
                    Source = new EventSource { SourceType = "subagent", SourceId = subSessionId },
                    WorkspaceId = request.WorkspaceId,
                    SessionId = request.ParentSessionId,
                    AgentId = request.ParentAgentId,
                    Payload = new { sub_agent_id = subSessionId, success, reply = replyText, error = errorMsg },
                    Metadata = new Dictionary<string, string>
                    {
                        ["parent_session"] = request.ParentSessionId,
                        ["parent_agent"] = request.ParentAgentId ?? "",
                        ["run_id"] = completedRunId ?? "",
                    },
                    TraceId = trace.TraceId,
                    CorrelationId = trace.CorrelationId,
                    Trace = trace,
                }, CancellationToken.None);

                await RecordActivityAsync(
                    trace,
                    "complete",
                    success ? RuntimeActivityStatuses.Succeeded : RuntimeActivityStatuses.Failed,
                    success ? $"Sub-agent {subSessionId} completed" : errorMsg,
                    CancellationToken.None);

                _logger.LogInformation("[SubAgentMgr] Async completed sub={Sub} parent={Parent} success={Success}",
                    subSessionId, request.ParentSessionId, success);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[SubAgentMgr] Async failed sub={Sub} parent={Parent}", subSessionId, request.ParentSessionId);

                // 异常路径也完成运行归档
                if (_runIdMap.TryRemove(subSessionId, out var failedRunId))
                {
                    await _runStore.CompleteRunAsync(failedRunId, new SubAgentRunCompletion
                    {
                        Status = "failed",
                        ErrorMessage = ex.Message,
                    }, CancellationToken.None);
                }

                // 发布 subagent.run.failed 事件
                await _eventBus.PublishAsync(new InternalEvent
                {
                    Type = "subagent.run.failed",
                    SchemaVersion = EventSchemaRegistry.GetSchemaVersion("subagent.run.failed"),
                    Priority = EventPriorityLevel.Normal,
                    Source = new EventSource { SourceType = "subagent", SourceId = subSessionId },
                    WorkspaceId = request.WorkspaceId,
                    SessionId = request.ParentSessionId,
                    AgentId = request.ParentAgentId,
                    Payload = new { sub_agent_id = subSessionId, error = ex.Message },
                    Metadata = new Dictionary<string, string>
                    {
                        ["parent_session"] = request.ParentSessionId,
                        ["parent_agent"] = request.ParentAgentId ?? "",
                        ["run_id"] = failedRunId ?? "",
                    },
                    TraceId = trace.TraceId,
                    CorrelationId = trace.CorrelationId,
                    Trace = trace,
                }, CancellationToken.None);

                _ = _ssm.TrackSubAgentCompleteAsync(subSessionId, new SubAgentResult
                { Success = false, Error = ex.Message, CompletedAt = DateTimeOffset.UtcNow }, CancellationToken.None);
                _ = _ssm.AppendAsync(request.ParentSessionId, request.WorkspaceId,
                    ServerSentEventFrame.Json(SessionEventTypes.SubAgentCompleted, new { sub_agent_id = subSessionId, success = false, error = ex.Message }),
                    CancellationToken.None, trace, RuntimeActivityComponents.SubAgent, "sub_agent.failed");
                await RecordActivityAsync(
                    trace,
                    "complete",
                    RuntimeActivityStatuses.Failed,
                    ex.Message,
                    CancellationToken.None);
            }
        }, CancellationToken.None);

        return new SubAgentSpawnResult
        {
            SubSessionId = subSessionId,
            Success = true,
        };
    }

    public async Task<SubAgentExecuteResult> ExecuteSyncAsync(
        SubAgentSpawnRequest request,
        CancellationToken ct = default)
    {
        var subSessionId = $"{request.ParentSessionId}-sub-{Guid.NewGuid().ToString("N")[..8]}";
        var trace = ResolveTrace(request, subSessionId);

        _logger.LogInformation("[SubAgentMgr] Execute sync parent={Parent} sub={Sub} template={Template}",
            request.ParentSessionId, subSessionId, request.TemplateId);

        await RecordActivityAsync(trace, "execute_sync", RuntimeActivityStatuses.Started,
            $"Executing sync sub-agent {subSessionId}", ct);

        var r = await DispatchChildAgentAsync(subSessionId, request);

        await RecordActivityAsync(trace, "execute_sync",
            r.IsSuccess ? RuntimeActivityStatuses.Succeeded : RuntimeActivityStatuses.Failed,
            r.IsSuccess ? $"Sync sub-agent {subSessionId} completed" : r.ErrorMessage,
            ct);

        return new SubAgentExecuteResult
        {
            SubSessionId = subSessionId, Success = r.IsSuccess,
            Reply = r.ReplyText, Error = r.ErrorMessage, Usage = r.Usage,
        };
    }

    public async Task<int> CancelAllAsync(string parentSessionId, CancellationToken ct = default)
    {
        var running = await _ssm.GetSubAgentsAsync(parentSessionId, ct);
        var toCancel = running.Where(s => s.Status == "running").ToList();

        foreach (var sa in toCancel)
        {
            await _ssm.TrackSubAgentCompleteAsync(sa.SubSessionId, new SubAgentResult
            {
                Success = false,
                Error = "Cancelled by parent",
                CompletedAt = DateTimeOffset.UtcNow,
            }, ct);

            // 完成运行归档（ADR-021）
            if (_runIdMap.TryRemove(sa.SubSessionId, out var cancelledRunId))
            {
                await _runStore.CompleteRunAsync(cancelledRunId, new SubAgentRunCompletion
                {
                    Status = "cancelled",
                    ErrorMessage = "Cancelled by parent",
                }, ct);
            }

            // 发布 subagent.run.cancelled 事件
            await _eventBus.PublishAsync(new InternalEvent
            {
                Type = "subagent.run.cancelled",
                SchemaVersion = EventSchemaRegistry.GetSchemaVersion("subagent.run.cancelled"),
                Priority = EventPriorityLevel.Normal,
                Source = new EventSource { SourceType = "subagent", SourceId = sa.SubSessionId },
                WorkspaceId = null,
                SessionId = parentSessionId,
                Payload = new { sub_agent_id = sa.SubSessionId, reason = "Cancelled by parent" },
                Metadata = new Dictionary<string, string>
                {
                    ["parent_session"] = parentSessionId,
                    ["run_id"] = cancelledRunId ?? "",
                },
            }, ct);

            await _activitySink.RecordAsync(new RuntimeActivity
            {
                Trace = RuntimeTraceContext.CreateNew(
                    sessionId: parentSessionId,
                    workspaceId: null,
                    executionId: sa.SubSessionId),
                Component = RuntimeActivityComponents.SubAgent,
                Operation = "cancel",
                Status = RuntimeActivityStatuses.Cancelled,
                Summary = $"Cancelled sub-agent {sa.SubSessionId}",
            }, ct);
        }

        _logger.LogInformation(
            "[SubAgentMgr] Cancelled {Count} sub-agents parent={Parent}",
            toCancel.Count, parentSessionId);

        return toCancel.Count;
    }

    // ════════════════════════════════════════════════════════
    // 状态查询
    // ════════════════════════════════════════════════════════

    public Task<IReadOnlyList<SubAgentStatus>> GetSubAgentsAsync(
        string parentSessionId,
        SubAgentQueryFilter? filter = null,
        CancellationToken ct = default)
    {
        return _ssm.GetSubAgentsAsync(parentSessionId, ct);
    }

    public Task<int> GetRunningCountAsync(string parentSessionId, CancellationToken ct = default)
    {
        return _ssm.GetRunningSubAgentCountAsync(parentSessionId, ct);
    }

    public async Task<SubAgentStatus?> GetStatusAsync(string subSessionId, CancellationToken ct = default)
    {
        // 通过子会话ID查找状态（遍历父会话的所有子代理）
        // 由于子会话ID包含父会话ID前缀，我们从SSM查询父会话的所有子代理然后过滤
        var parts = subSessionId.Split("-sub-");
        if (parts.Length < 2) return null;
        var parentId = parts[0];
        var all = await _ssm.GetSubAgentsAsync(parentId, ct);
        return all.FirstOrDefault(s => s.SubSessionId == subSessionId);
    }

    // ════════════════════════════════════════════════════════
    // 诊断查询
    // ════════════════════════════════════════════════════════

    public async Task<IReadOnlyList<SubAgentStatus>> GrepAsync(
        string parentSessionId, string keyword,
        CancellationToken ct = default)
    {
        var all = await _ssm.GetSubAgentsAsync(parentSessionId, ct);
        return all.Where(s =>
            (s.TaskSummary?.Contains(keyword, StringComparison.OrdinalIgnoreCase) == true) ||
            (s.ResultSummary?.Contains(keyword, StringComparison.OrdinalIgnoreCase) == true)
        ).ToList();
    }

    public async Task<IReadOnlyList<SubAgentStatus>> GetRecentAsync(
        string parentSessionId, int days,
        CancellationToken ct = default)
    {
        var cutoff = DateTimeOffset.UtcNow.AddDays(-days);
        var all = await _ssm.GetSubAgentsAsync(parentSessionId, ct);
        return all.Where(s => s.SpawnedAt >= cutoff).ToList();
    }

    public async Task<SubAgentStats> GetStatsAsync(
        string parentSessionId, CancellationToken ct = default)
    {
        var all = await _ssm.GetSubAgentsAsync(parentSessionId, ct);
        var list = all.ToList();

        return new SubAgentStats
        {
            Total = list.Count,
            Running = list.Count(s => s.Status == "running"),
            Completed = list.Count(s => s.Status == "completed"),
            Failed = list.Count(s => s.Status == "failed"),
            LastCompletedId = list.LastOrDefault(s => s.Status == "completed")?.SubSessionId,
            LastFailedId = list.LastOrDefault(s => s.Status == "failed")?.SubSessionId,
        };
    }

    /// <summary>
    /// 查询 subSessionId 对应的 runId（避免 AgentExecutionService 重复创建 run archive）。
    /// 异步子代理在 SpawnAsync 中已创建 run，同步子代理则返回 null 由调用方创建。
    /// </summary>
    public string? TryGetRunId(string subSessionId)
    {
        _runIdMap.TryGetValue(subSessionId, out var runId);
        return runId;
    }

    // ════════════════════════════════════════════════════════
    // 跨模块调用（反射，PuddingPlatform 不引用 PuddingRuntime）
    // ════════════════════════════════════════════════════════

    private Task<dynamic> DispatchChildAgentAsync(string subSessionId, SubAgentSpawnRequest request)
        => DispatchChildAgentImpl(subSessionId, request, _services);

    private static Task<dynamic> DispatchChildAgentImpl(string subSessionId, SubAgentSpawnRequest request, IServiceProvider services)
    {
        var reqType = Type.GetType("PuddingRuntime.Services.RuntimeDispatchRequest, PuddingRuntime")
            ?? throw new InvalidOperationException("Cannot find RuntimeDispatchRequest");
        var childReq = Activator.CreateInstance(reqType)!;
        reqType.GetProperty("SessionId")?.SetValue(childReq, subSessionId);
        reqType.GetProperty("WorkspaceId")?.SetValue(childReq, request.WorkspaceId);
        reqType.GetProperty("AgentTemplateId")?.SetValue(childReq, request.TemplateId);
        reqType.GetProperty("MessageText")?.SetValue(childReq, request.TaskDescription);
        reqType.GetProperty("CapabilityPolicy")?.SetValue(childReq, request.CapabilityPolicy);
        reqType.GetProperty("LlmConfig")?.SetValue(childReq, request.LlmConfig);
        reqType.GetProperty("MaxRounds")?.SetValue(childReq, request.MaxRounds);

        var execType = Type.GetType("PuddingRuntime.Services.AgentExecutionService, PuddingRuntime")
            ?? throw new InvalidOperationException("Cannot find AgentExecutionService");
        var execMethod = execType.GetMethod("ExecuteAsync")
            ?? throw new InvalidOperationException("Cannot find ExecuteAsync");
        var execSvc = services.GetService(execType)
            ?? throw new InvalidOperationException("AgentExecutionService not registered");

        return (Task<dynamic>)execMethod.Invoke(execSvc, [childReq, CancellationToken.None])!;
    }

    private RuntimeTraceContext ResolveTrace(SubAgentSpawnRequest request, string subSessionId)
    {
        var parent = _traceAccessor.Current
            ?? RuntimeTraceContext.CreateNew(
                sessionId: request.ParentSessionId,
                workspaceId: request.WorkspaceId,
                userId: request.ParentAgentId);

        var trace = parent.CreateChildExecution(
            sessionId: subSessionId,
            executionId: subSessionId,
            subAgentId: subSessionId);

        _traceAccessor.Current = trace;
        return trace;
    }

    private Task RecordActivityAsync(
        RuntimeTraceContext trace,
        string operation,
        string status,
        string? summary,
        CancellationToken ct)
    {
        return _activitySink.RecordAsync(new RuntimeActivity
        {
            Trace = trace,
            Component = RuntimeActivityComponents.SubAgent,
            Operation = operation,
            Status = status,
            Summary = summary,
        }, ct);
    }
}
