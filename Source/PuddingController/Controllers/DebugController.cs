using Microsoft.AspNetCore.Mvc;
using PuddingCode.Platform;
using PuddingController.Services;

namespace PuddingController.Controllers;

/// <summary>调试诊断 API——查询路由决策、适配器状态等。</summary>
[ApiController]
[Route("api/[controller]")]
public class DebugController : ControllerBase
{
    private readonly InMemoryRouteDecisionStore _routeStore;
    private readonly InMemoryAuditEventStore _auditStore;
    private readonly InMemorySessionRepository _sessionRepo;
    private readonly InMemoryApprovalService _approvalService;
    private readonly RuntimeRegistryService _runtimeRegistry;
    private readonly InMemoryWorkspaceCatalog _workspaceCatalog;

    public DebugController(
        InMemoryRouteDecisionStore routeStore,
        InMemoryAuditEventStore auditStore,
        InMemorySessionRepository sessionRepo,
        InMemoryApprovalService approvalService,
        RuntimeRegistryService runtimeRegistry,
        InMemoryWorkspaceCatalog workspaceCatalog)
    {
        _routeStore = routeStore;
        _auditStore = auditStore;
        _sessionRepo = sessionRepo;
        _approvalService = approvalService;
        _runtimeRegistry = runtimeRegistry;
        _workspaceCatalog = workspaceCatalog;
    }

    /// <summary>按消息 ID 查询路由决策记录。</summary>
    [HttpGet("route/{messageId}")]
    public async Task<ActionResult<RouteDecisionRecord>> GetRouteDecision(string messageId, CancellationToken ct)
    {
        var record = await _routeStore.GetByMessageAsync(messageId, ct);
        return record is null ? NotFound() : Ok(record);
    }

    /// <summary>获取 Controller 运行概况。</summary>
    [HttpGet("summary")]
    public async Task<ActionResult> Summary(CancellationToken ct)
    {
        var recentAudit = await _auditStore.QueryAsync(limit: 10, ct: ct);
        return Ok(new
        {
            utcNow = DateTimeOffset.UtcNow,
            recentAuditCount = recentAudit.Count,
            recentAuditEvents = recentAudit.Select(e => new
            {
                e.EventId,
                e.EventType,
                e.MessageId,
                e.SessionId,
                e.Timestamp
            })
        });
    }

    /// <summary>最小运行指标面板（请求量/会话/审批/节点）。</summary>
    [HttpGet("metrics")]
    public async Task<ActionResult> Metrics(CancellationToken ct)
    {
        var allSessions = await _sessionRepo.QueryAsync(ct: ct);
        var activeSessions = allSessions.Count(s => s.Status == SessionStatus.Active);
        var pendingApprovals = await _approvalService.QueryPendingAsync(ct);
        var runtimeNodes = _runtimeRegistry.GetAll();
        var workspaces = _workspaceCatalog.GetAll();
        var recentAudit = await _auditStore.QueryAsync(limit: 500, ct: ct);

        var auditByType = recentAudit
            .GroupBy(x => x.EventType.ToString())
            .ToDictionary(g => g.Key, g => g.Count());

        return Ok(new
        {
            utcNow = DateTimeOffset.UtcNow,
            session = new
            {
                total = allSessions.Count,
                active = activeSessions,
                closed = allSessions.Count - activeSessions
            },
            approval = new
            {
                pending = pendingApprovals.Count
            },
            runtime = new
            {
                totalNodes = runtimeNodes.Count,
                onlineNodes = runtimeNodes.Count(n => n.Status == RuntimeNodeStatus.Online),
                totalActiveSessionLoad = runtimeNodes.Sum(n => n.ActiveSessionCount)
            },
            workspace = new
            {
                total = workspaces.Count,
                enabled = workspaces.Count(w => w.IsEnabled),
                frozen = workspaces.Count(w => w.IsFrozen)
            },
            audit = new
            {
                recentCount = recentAudit.Count,
                byType = auditByType
            }
        });
    }

    /// <summary>按 Session 查询调试快照（会话 + 最近审计）。</summary>
    [HttpGet("session/{sessionId}")]
    public async Task<ActionResult> SessionDebug(string sessionId, CancellationToken ct)
    {
        var session = await _sessionRepo.GetAsync(sessionId, ct);
        if (session is null) return NotFound();

        var audit = await _auditStore.QueryAsync(sessionId: sessionId, limit: 30, ct: ct);
        return Ok(new
        {
            session,
            auditCount = audit.Count,
            recentAudit = audit.Select(a => new
            {
                a.EventId,
                a.EventType,
                a.MessageId,
                a.Detail,
                a.Timestamp
            })
        });
    }

    /// <summary>按 messageId 查询完整调试链路（路由 + 审计 + 会话）。</summary>
    [HttpGet("message/{messageId}")]
    public async Task<ActionResult> MessageDebug(string messageId, CancellationToken ct)
    {
        var route = await _routeStore.GetByMessageAsync(messageId, ct);
        var audit = await _auditStore.QueryAsync(messageId: messageId, limit: 50, ct: ct);
        var session = route?.SessionId is null ? null : await _sessionRepo.GetAsync(route.SessionId, ct);

        if (route is null && audit.Count == 0)
            return NotFound();

        return Ok(new
        {
            messageId,
            routeDecision = route,
            session,
            auditCount = audit.Count,
            auditTimeline = audit.Select(a => new
            {
                a.EventId,
                a.EventType,
                a.Detail,
                a.Timestamp
            }),
            diagnosis = new
            {
                routeSuccess = route?.IsSuccess,
                routeFailureReason = route?.FailureReason,
                hasPermissionDenied = audit.Any(a => a.EventType == AuditEventType.PermissionDenied),
                hasRuntimeFailure = audit.Any(a => a.EventType == AuditEventType.AgentExecutionFailed)
            }
        });
    }

    /// <summary>按 workspaceId 查询调试快照（会话/审批/路由失败/审计）。</summary>
    [HttpGet("workspace/{workspaceId}")]
    public async Task<ActionResult> WorkspaceDebug(string workspaceId, CancellationToken ct)
    {
        var workspace = _workspaceCatalog.GetWorkspace(workspaceId);
        if (workspace is null) return NotFound();

        var sessions = await _sessionRepo.QueryAsync(workspaceId: workspaceId, ct: ct);
        var pendingApprovals = await _approvalService.QueryPendingAsync(ct);
        var pendingInWorkspace = pendingApprovals.Where(a => a.WorkspaceId == workspaceId).ToList();
        var audit = await _auditStore.QueryAsync(workspaceId: workspaceId, limit: 100, ct: ct);
        var routes = await _routeStore.QueryAsync(workspaceId: workspaceId, limit: 100, ct: ct);

        var failedRoutes = routes.Where(r => !r.IsSuccess).ToList();

        return Ok(new
        {
            workspace = new
            {
                workspace.WorkspaceId,
                workspace.Name,
                workspace.IsEnabled,
                workspace.IsFrozen,
                channelCount = workspace.ChannelBindings.Count,
                agentTemplateCount = workspace.AgentTemplateIds.Count,
                auditAgentCount = workspace.AuditAgentTemplateIds.Count
            },
            session = new
            {
                total = sessions.Count,
                active = sessions.Count(s => s.Status == SessionStatus.Active),
                frozen = sessions.Count(s => s.Status == SessionStatus.Frozen),
                failed = sessions.Count(s => s.Status == SessionStatus.Failed)
            },
            approval = new
            {
                pending = pendingInWorkspace.Count
            },
            routing = new
            {
                recentTotal = routes.Count,
                recentFailures = failedRoutes.Count,
                topFailureReasons = failedRoutes
                    .GroupBy(r => r.FailureReason ?? "unknown")
                    .OrderByDescending(g => g.Count())
                    .Take(5)
                    .Select(g => new { reason = g.Key, count = g.Count() })
            },
            audit = new
            {
                recentCount = audit.Count,
                permissionDeniedCount = audit.Count(a => a.EventType == AuditEventType.PermissionDenied),
                runtimeFailedCount = audit.Count(a => a.EventType == AuditEventType.AgentExecutionFailed)
            },
            workflow = new
            {
                boundWorkflows = workspace.WorkflowBindings.Count,
                potentialBlockerHint = workspace.WorkflowBindings.Count > 0 && failedRoutes.Count > 0
                    ? "存在工作流绑定且近期有路由失败，请检查触发渠道与权限配置"
                    : (string?)null
            }
        });
    }
}
