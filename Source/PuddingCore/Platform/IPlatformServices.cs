namespace PuddingCode.Platform;

/// <summary>Workspace 配置目录——加载、缓存和查询 Workspace 定义。</summary>
public interface IWorkspaceCatalog
{
    Task LoadAsync(CancellationToken ct = default);
    Task ReloadAsync(CancellationToken ct = default);
    WorkspaceDefinition? GetWorkspace(string workspaceId);
    WorkspaceDefinition? FindByChannel(string channelId);
    IReadOnlyList<WorkspaceDefinition> GetAll();
}

/// <summary>会话仓库——ServiceSession 的持久化。</summary>
public interface ISessionRepository
{
    Task<SessionRecord> CreateAsync(SessionRecord record, CancellationToken ct = default);
    Task<SessionRecord?> GetAsync(string sessionId, CancellationToken ct = default);
    Task<SessionRecord?> FindActiveAsync(string channelId, string ownerUserId, string workspaceId, string agentTemplateId, CancellationToken ct = default);
    Task<SessionRecord?> FindMainAsync(string workspaceId, string principalKind, string principalId, CancellationToken ct = default);
    Task<SessionRecord> RebindMainAsync(string workspaceId, string principalKind, string principalId, string successorSessionId, CancellationToken ct = default);
    Task<IReadOnlyList<SessionRecord>> QueryAsync(string? channelId = null, string? userId = null, string? workspaceId = null, CancellationToken ct = default);
    Task UpdateAsync(SessionRecord record, CancellationToken ct = default);
    Task DeleteAsync(string sessionId, CancellationToken ct = default);
}

/// <summary>路由决策存储。</summary>
public interface IRouteDecisionStore
{
    Task SaveAsync(RouteDecisionRecord record, CancellationToken ct = default);
    Task<RouteDecisionRecord?> GetAsync(string routeDecisionId, CancellationToken ct = default);
    Task<RouteDecisionRecord?> GetByMessageAsync(string messageId, CancellationToken ct = default);
}

/// <summary>审计事件存储。</summary>
public interface IAuditEventStore
{
    Task RecordAsync(AuditEventRecord record, CancellationToken ct = default);
    Task<AuditEventRecord?> GetAsync(string eventId, CancellationToken ct = default);
    Task<IReadOnlyList<AuditEventRecord>> QueryAsync(
        string? sessionId = null, string? messageId = null, string? workspaceId = null,
        string? approvalId = null, int limit = 50, CancellationToken ct = default);
}

/// <summary>审批服务。</summary>
public interface IApprovalService
{
    Task<ApprovalRecord> RequestApprovalAsync(string sessionId, string workspaceId, string actionDescription, CancellationToken ct = default);
    Task<ApprovalRecord?> GetAsync(string approvalId, CancellationToken ct = default);
    Task<IReadOnlyList<ApprovalRecord>> QueryPendingAsync(CancellationToken ct = default);
    Task<bool> ConfirmAsync(string approvalId, string confirmationCode, string confirmedBy, CancellationToken ct = default);
    Task<bool> RejectAsync(string approvalId, string rejectedBy, CancellationToken ct = default);
}
