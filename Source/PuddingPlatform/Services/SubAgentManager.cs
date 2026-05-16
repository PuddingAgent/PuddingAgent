using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PuddingCode.Abstractions;
using PuddingCode.Models;
using PuddingCode.Platform;

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
    private readonly ILogger<SubAgentManager> _logger;

    public SubAgentManager(
        ISessionStateManager ssm,
        IServiceProvider services,
        IInternalEventBus eventBus,
        ILogger<SubAgentManager> logger)
    {
        _ssm = ssm;
        _services = services;
        _eventBus = eventBus;
        _logger = logger;
    }

    // ════════════════════════════════════════════════════════
    // 子代理执行
    // ════════════════════════════════════════════════════════

    public async Task<SubAgentSpawnResult> SpawnAsync(
        SubAgentSpawnRequest request,
        CancellationToken ct = default)
    {
        var subSessionId = $"{request.ParentSessionId}-sub-{Guid.NewGuid().ToString("N")[..8]}";
        var spawnedAt = DateTimeOffset.UtcNow;

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

        // 2. 推送 SubAgentSpawned 帧到父会话
        _ = _ssm.AppendAsync(request.ParentSessionId, request.WorkspaceId,
            ServerSentEventFrame.Json(SessionEventTypes.SubAgentSpawned, new
            {
                sub_agent_id = subSessionId,
                template = request.TemplateId,
                model = request.ModelId ?? "默认",
                task_summary = request.TaskDescription.Length > 200
                    ? request.TaskDescription[..200] + "..."
                    : request.TaskDescription,
            }), CancellationToken.None);

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

                _ = _ssm.AppendAsync(request.ParentSessionId, request.WorkspaceId,
                    ServerSentEventFrame.Json(SessionEventTypes.SubAgentCompleted, new
                    {
                        sub_agent_id = subSessionId,
                        success,
                        reply = replyText,
                        error = errorMsg,
                    }), CancellationToken.None);

                await _eventBus.PublishAsync(new InternalEvent
                {
                    Type = "agent.sub_completed", Priority = EventPriorityLevel.Normal,
                    Source = new EventSource { SourceType = "subagent", SourceId = subSessionId },
                    WorkspaceId = request.WorkspaceId,
                    SessionId = request.ParentSessionId,
                    AgentId = request.ParentAgentId,
                    Payload = new { sub_agent_id = subSessionId, success, reply = replyText, error = errorMsg },
                    Metadata = new Dictionary<string, string> { ["parent_session"] = request.ParentSessionId, ["parent_agent"] = request.ParentAgentId ?? "" },
                }, CancellationToken.None);

                _logger.LogInformation("[SubAgentMgr] Async completed sub={Sub} parent={Parent} success={Success}",
                    subSessionId, request.ParentSessionId, success);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[SubAgentMgr] Async failed sub={Sub} parent={Parent}", subSessionId, request.ParentSessionId);
                _ = _ssm.TrackSubAgentCompleteAsync(subSessionId, new SubAgentResult
                { Success = false, Error = ex.Message, CompletedAt = DateTimeOffset.UtcNow }, CancellationToken.None);
                _ = _ssm.AppendAsync(request.ParentSessionId, request.WorkspaceId,
                    ServerSentEventFrame.Json(SessionEventTypes.SubAgentCompleted, new { sub_agent_id = subSessionId, success = false, error = ex.Message }),
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

        _logger.LogInformation("[SubAgentMgr] Execute sync parent={Parent} sub={Sub} template={Template}",
            request.ParentSessionId, subSessionId, request.TemplateId);

        var r = await DispatchChildAgentAsync(subSessionId, request);

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
        reqType.GetProperty("MaxRounds")?.SetValue(childReq, request.MaxRounds);

        var execType = Type.GetType("PuddingRuntime.Services.AgentExecutionService, PuddingRuntime")
            ?? throw new InvalidOperationException("Cannot find AgentExecutionService");
        var execMethod = execType.GetMethod("ExecuteAsync")
            ?? throw new InvalidOperationException("Cannot find ExecuteAsync");
        var execSvc = services.GetService(execType)
            ?? throw new InvalidOperationException("AgentExecutionService not registered");

        return (Task<dynamic>)execMethod.Invoke(execSvc, [childReq, CancellationToken.None])!;
    }
}
