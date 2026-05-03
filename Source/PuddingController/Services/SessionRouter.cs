using PuddingCode.Platform;
using System.Runtime.CompilerServices;

namespace PuddingController.Services;

/// <summary>
/// Session 路由器——收到 Ingress 消息后做 Workspace 匹配 → 权限校验 → Session 管理 → Runtime 调度。
/// </summary>
public sealed class SessionRouter
{
    private readonly InMemoryWorkspaceCatalog _workspaceCatalog;
    private readonly InMemorySessionRepository _sessionRepo;
    private readonly InMemoryAuditEventStore _auditStore;
    private readonly InMemoryRouteDecisionStore _routeStore;
    private readonly AuthorizationService _authService;
    private readonly RuntimeDispatcher _dispatcher;
    private readonly GatewayEgressService _egressService;
    private readonly ILogger<SessionRouter> _logger;

    public SessionRouter(
        InMemoryWorkspaceCatalog workspaceCatalog,
        InMemorySessionRepository sessionRepo,
        InMemoryAuditEventStore auditStore,
        InMemoryRouteDecisionStore routeStore,
        AuthorizationService authService,
        RuntimeDispatcher dispatcher,
        GatewayEgressService egressService,
        ILogger<SessionRouter> logger)
    {
        _workspaceCatalog = workspaceCatalog;
        _sessionRepo = sessionRepo;
        _auditStore = auditStore;
        _routeStore = routeStore;
        _authService = authService;
        _dispatcher = dispatcher;
        _egressService = egressService;
        _logger = logger;
    }

    /// <summary>
    /// 核心路由——接收 MessageIngressRequest 并返回结果。
    /// 链路: Workspace 解析 → 权限校验 → Session 查找/创建 → Runtime 调度 → 出站回写 → 审计。
    /// </summary>
    public async Task<MessageIngressResponse> RouteMessageAsync(MessageIngressRequest request, CancellationToken ct = default)
    {
        var messageId = Guid.NewGuid().ToString("N");
        var workspaceId = request.WorkspaceId ?? "default";
        var userId = request.UserExternalId;

        // 1. 解析 Workspace（优先精确 ID，其次按渠道查找）
        var workspace = _workspaceCatalog.GetWorkspace(workspaceId)
                        ?? _workspaceCatalog.FindByChannel(request.ChannelId);
        if (workspace is null)
        {
            var failRoute = new RouteDecisionRecord
            {
                MessageId = messageId,
                ChannelId = request.ChannelId,
                IsSuccess = false,
                FailureReason = $"Workspace not found: {workspaceId}",
            };
            await _routeStore.SaveAsync(failRoute, ct);

            return new MessageIngressResponse
            {
                MessageId = messageId,
                SessionId = request.SessionId ?? "",
                RouteDecisionId = failRoute.RouteDecisionId,
                IsSuccess = false,
                ErrorMessage = failRoute.FailureReason,
            };
        }

        workspaceId = workspace.WorkspaceId;

        // 2. 审计：消息入站
        await _auditStore.RecordAsync(new AuditEventRecord
        {
            EventType = AuditEventType.MessageReceived,
            MessageId = messageId,
            WorkspaceId = workspace.WorkspaceId,
            Detail = $"channel={request.ChannelId}, user={userId}, text length={request.MessageText.Length}"
        }, ct);

        // 3. 选择 Agent 模板
        var templateId = request.AgentTemplateId
                 ?? ResolveAgentTemplate(workspace, request.ChannelId);

        // 4. 权限校验
        var authDecision = _authService.Authorize(
            request.ChannelId, userId, workspaceId, templateId, request.SessionId);

        if (!authDecision.IsAllowed)
        {
            _logger.LogWarning("[Router] Permission denied: user={User} workspace={Ws} reason={Reason}",
                userId, workspaceId, authDecision.DenialReason);

            await _auditStore.RecordAsync(new AuditEventRecord
            {
                EventType = AuditEventType.PermissionDenied,
                MessageId = messageId,
                WorkspaceId = workspaceId,
                Detail = authDecision.DenialReason,
            }, ct);

            var denyRoute = new RouteDecisionRecord
            {
                MessageId = messageId,
                ChannelId = request.ChannelId,
                WorkspaceId = workspaceId,
                AgentTemplateId = templateId,
                IsSuccess = false,
                FailureReason = $"Permission denied: {authDecision.DenialReason}",
            };
            await _routeStore.SaveAsync(denyRoute, ct);

            return new MessageIngressResponse
            {
                MessageId = messageId,
                SessionId = request.SessionId ?? "",
                RouteDecisionId = denyRoute.RouteDecisionId,
                IsSuccess = false,
                ErrorMessage = $"Permission denied: {authDecision.DenialReason}",
            };
        }

        // 5. Session 查找/创建
        SessionRecord? session = null;

        if (request.SessionId is not null)
        {
            session = await _sessionRepo.GetAsync(request.SessionId, ct);
        }

        session ??= await _sessionRepo.FindActiveAsync(
            request.ChannelId, userId, workspace.WorkspaceId, templateId, ct);

        if (session is null)
        {
            session = new SessionRecord
            {
                SessionId = Guid.NewGuid().ToString("N"),
                WorkspaceId = workspace.WorkspaceId,
                AgentTemplateId = templateId,
                SessionType = SessionType.ServiceSession,
                Status = SessionStatus.Active,
                ChannelId = request.ChannelId,
                OwnerUserId = userId,
            };
            await _sessionRepo.CreateAsync(session, ct);

            await _auditStore.RecordAsync(new AuditEventRecord
            {
                EventType = AuditEventType.SessionCreated,
                SessionId = session.SessionId,
                WorkspaceId = workspace.WorkspaceId,
                AgentTemplateId = templateId,
            }, ct);

            _logger.LogInformation("[Router] New session: {SessionId} -> template={Template}",
                session.SessionId, templateId);
        }

        // 6. 记录路由决策
        var routeDecision = new RouteDecisionRecord
        {
            MessageId = messageId,
            ChannelId = request.ChannelId,
            WorkspaceId = workspaceId,
            AgentTemplateId = templateId,
            SessionId = session.SessionId,
            IsSuccess = true,
        };
        await _routeStore.SaveAsync(routeDecision, ct);

        await _auditStore.RecordAsync(new AuditEventRecord
        {
            EventType = AuditEventType.RouteDecision,
            SessionId = session.SessionId,
            MessageId = messageId,
            WorkspaceId = workspace.WorkspaceId,
            AgentTemplateId = templateId,
            Detail = $"routeDecisionId={routeDecision.RouteDecisionId}"
        }, ct);

        // 7. 构建 Runtime 请求并调度
        var dispatchRequest = new RuntimeDispatchRequest
        {
            SessionId = session.SessionId,
            WorkspaceId = workspace.WorkspaceId,
            AgentTemplateId = session.AgentTemplateId,
            MessageText = request.MessageText,
            LlmConfig = request.LlmConfig,
            CapabilityPolicy = request.CapabilityPolicy,
            ToolDefinitions = request.ToolDefinitions,
            SkillPackages = request.SkillPackages,
        };

        _logger.LogInformation(
            "[Router] DISPATCH msgId={MsgId} session={Session} ws={Ws} template={Template} hasLlmConfig={HasConfig}",
            messageId, session.SessionId, workspace.WorkspaceId, session.AgentTemplateId,
            request.LlmConfig is not null);

        await _auditStore.RecordAsync(new AuditEventRecord
        {
            EventType = AuditEventType.RuntimeDispatched,
            SessionId = session.SessionId,
            MessageId = messageId,
            WorkspaceId = workspace.WorkspaceId,
        }, ct);

        var dispatchResult = await _dispatcher.DispatchAsync(dispatchRequest, ct);

        if (dispatchResult.IsSuccess)
            _logger.LogInformation(
                "[Router] RESULT ok msgId={MsgId} session={Session} replyLen={Len}",
                messageId, session.SessionId, dispatchResult.ReplyText?.Length ?? 0);
        else
            _logger.LogWarning(
                "[Router] RESULT failed msgId={MsgId} session={Session} error={Error}",
                messageId, session.SessionId, dispatchResult.ErrorMessage);

        // 8. 审计：Runtime 响应
        await _auditStore.RecordAsync(new AuditEventRecord
        {
            EventType = dispatchResult.IsSuccess
                ? AuditEventType.RuntimeReplyReceived
                : AuditEventType.AgentExecutionFailed,
            SessionId = session.SessionId,
            MessageId = messageId,
            WorkspaceId = workspace.WorkspaceId,
            AgentTemplateId = session.AgentTemplateId,
            Detail = dispatchResult.IsSuccess
                ? $"reply length={dispatchResult.ReplyText?.Length ?? 0}"
                : $"error={dispatchResult.ErrorMessage}"
        }, ct);

        // 9. 出站回写（非阻塞，失败仅记录日志）
        if (dispatchResult.IsSuccess && dispatchResult.ReplyText is not null)
        {
            await _egressService.PublishReplyAsync(
                request.ChannelId, userId, session.SessionId, messageId, dispatchResult.ReplyText, ct);
        }

        return new MessageIngressResponse
        {
            MessageId = messageId,
            SessionId = session.SessionId,
            RouteDecisionId = routeDecision.RouteDecisionId,
            Reply = dispatchResult.ReplyText,
            IsSuccess = dispatchResult.IsSuccess,
            ErrorMessage = dispatchResult.ErrorMessage,
            Usage = dispatchResult.Usage,
            TurnSteps = dispatchResult.TurnSteps,
        };
    }

    /// <summary>
    /// 流式路由：复用同步路径的 Workspace/权限/Session 决策，然后将 Runtime SSE 帧逐层透传。
    /// </summary>
    public async IAsyncEnumerable<ServerSentEventFrame> RouteMessageStreamAsync(
        MessageIngressRequest request,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var messageId = Guid.NewGuid().ToString("N");
        var workspaceId = request.WorkspaceId ?? "default";
        var userId = request.UserExternalId;

        var workspace = _workspaceCatalog.GetWorkspace(workspaceId)
                        ?? _workspaceCatalog.FindByChannel(request.ChannelId);
        if (workspace is null)
        {
            var failure = $"Workspace not found: {workspaceId}";
            yield return ServerSentEventFrame.Json("error", new { message = failure });
            yield break;
        }

        workspaceId = workspace.WorkspaceId;

        await _auditStore.RecordAsync(new AuditEventRecord
        {
            EventType = AuditEventType.MessageReceived,
            MessageId = messageId,
            WorkspaceId = workspace.WorkspaceId,
            Detail = $"stream channel={request.ChannelId}, user={userId}, text length={request.MessageText.Length}"
        }, ct);

        var templateId = request.AgentTemplateId
                 ?? ResolveAgentTemplate(workspace, request.ChannelId);

        var authDecision = _authService.Authorize(
            request.ChannelId, userId, workspaceId, templateId, request.SessionId);

        if (!authDecision.IsAllowed)
        {
            _logger.LogWarning("[Router] Stream permission denied: user={User} workspace={Ws} reason={Reason}",
                userId, workspaceId, authDecision.DenialReason);

            await _auditStore.RecordAsync(new AuditEventRecord
            {
                EventType = AuditEventType.PermissionDenied,
                MessageId = messageId,
                WorkspaceId = workspaceId,
                Detail = authDecision.DenialReason,
            }, ct);

            yield return ServerSentEventFrame.Json("error", new { message = $"Permission denied: {authDecision.DenialReason}" });
            yield break;
        }

        SessionRecord? session = null;

        if (request.SessionId is not null)
            session = await _sessionRepo.GetAsync(request.SessionId, ct);

        session ??= await _sessionRepo.FindActiveAsync(
            request.ChannelId, userId, workspace.WorkspaceId, templateId, ct);

        if (session is null)
        {
            session = new SessionRecord
            {
                SessionId = Guid.NewGuid().ToString("N"),
                WorkspaceId = workspace.WorkspaceId,
                AgentTemplateId = templateId,
                SessionType = SessionType.ServiceSession,
                Status = SessionStatus.Active,
                ChannelId = request.ChannelId,
                OwnerUserId = userId,
            };
            await _sessionRepo.CreateAsync(session, ct);

            await _auditStore.RecordAsync(new AuditEventRecord
            {
                EventType = AuditEventType.SessionCreated,
                SessionId = session.SessionId,
                WorkspaceId = workspace.WorkspaceId,
                AgentTemplateId = templateId,
            }, ct);
        }

        var routeDecision = new RouteDecisionRecord
        {
            MessageId = messageId,
            ChannelId = request.ChannelId,
            WorkspaceId = workspaceId,
            AgentTemplateId = templateId,
            SessionId = session.SessionId,
            IsSuccess = true,
        };
        await _routeStore.SaveAsync(routeDecision, ct);

        await _auditStore.RecordAsync(new AuditEventRecord
        {
            EventType = AuditEventType.RouteDecision,
            SessionId = session.SessionId,
            MessageId = messageId,
            WorkspaceId = workspace.WorkspaceId,
            AgentTemplateId = templateId,
            Detail = $"stream routeDecisionId={routeDecision.RouteDecisionId}"
        }, ct);

        yield return ServerSentEventFrame.Json("metadata", new
        {
            messageId,
            sessionId = session.SessionId,
            routeDecisionId = routeDecision.RouteDecisionId,
        });

        var dispatchRequest = new RuntimeDispatchRequest
        {
            SessionId = session.SessionId,
            WorkspaceId = workspace.WorkspaceId,
            AgentTemplateId = session.AgentTemplateId,
            MessageText = request.MessageText,
            LlmConfig = request.LlmConfig,
            CapabilityPolicy = request.CapabilityPolicy,
            ToolDefinitions = request.ToolDefinitions,
            SkillPackages = request.SkillPackages,
        };

        _logger.LogInformation(
            "[Router] STREAM dispatch msgId={MsgId} session={Session} ws={Ws} template={Template} hasLlmConfig={HasConfig}",
            messageId, session.SessionId, workspace.WorkspaceId, session.AgentTemplateId,
            request.LlmConfig is not null);

        await _auditStore.RecordAsync(new AuditEventRecord
        {
            EventType = AuditEventType.RuntimeDispatched,
            SessionId = session.SessionId,
            MessageId = messageId,
            WorkspaceId = workspace.WorkspaceId,
            Detail = "stream",
        }, ct);

        await foreach (var frame in _dispatcher.DispatchStreamAsync(dispatchRequest, ct))
            yield return frame;

        await _auditStore.RecordAsync(new AuditEventRecord
        {
            EventType = AuditEventType.RuntimeReplyReceived,
            SessionId = session.SessionId,
            MessageId = messageId,
            WorkspaceId = workspace.WorkspaceId,
            AgentTemplateId = session.AgentTemplateId,
            Detail = "stream completed"
        }, ct);
    }

    /// <summary>根据 Workspace 配置和渠道选择默认 Agent 模板。</summary>
    private static string ResolveAgentTemplate(WorkspaceDefinition workspace, string channelId)
    {
        var binding = workspace.ChannelBindings.FirstOrDefault(cb => cb.ChannelId == channelId);
        if (binding?.DefaultAgentTemplateId is not null)
            return binding.DefaultAgentTemplateId;

        return workspace.AgentTemplateIds.FirstOrDefault()
               ?? BuiltInAgentTemplates.WorkspaceServiceAgent.TemplateId;
    }
}
