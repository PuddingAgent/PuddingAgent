using System.Net.Http.Json;
using PuddingCode.Platform;

namespace PuddingPlatform.Services;

/// <summary>
/// 调用 PuddingController HTTP API 的客户端。
/// Platform 管理界面通过此客户端操作 Controller。
/// </summary>
public sealed class PlatformApiClient
{
    private readonly HttpClient _http;

    public PlatformApiClient(HttpClient http) => _http = http;

    // ── Workspace ──────────────────────────────────────

    public async Task<List<WorkspaceDefinition>> GetWorkspacesAsync(CancellationToken ct = default)
    {
        var list = await _http.GetFromJsonAsync<List<WorkspaceDefinition>>("/api/workspace", ct);
        return list ?? [];
    }

    public async Task<WorkspaceDefinition?> GetWorkspaceAsync(string id, CancellationToken ct = default)
    {
        var resp = await _http.GetAsync($"/api/workspace/{Uri.EscapeDataString(id)}", ct);
        if (!resp.IsSuccessStatusCode) return null;
        return await resp.Content.ReadFromJsonAsync<WorkspaceDefinition>(ct);
    }

    public async Task<WorkspaceDefinition?> UpsertWorkspaceAsync(WorkspaceDefinition workspace, CancellationToken ct = default)
    {
        var resp = await _http.PutAsJsonAsync(
            $"/api/workspace/{Uri.EscapeDataString(workspace.WorkspaceId)}", workspace, ct);
        if (!resp.IsSuccessStatusCode) return null;
        return await resp.Content.ReadFromJsonAsync<WorkspaceDefinition>(ct);
    }

    public async Task<bool> DeleteWorkspaceAsync(string workspaceId, CancellationToken ct = default)
    {
        var resp = await _http.DeleteAsync($"/api/workspace/{Uri.EscapeDataString(workspaceId)}", ct);
        return resp.IsSuccessStatusCode;
    }

    public async Task<bool> FreezeWorkspaceAsync(string workspaceId, CancellationToken ct = default)
    {
        var resp = await _http.PostAsync(
            $"/api/workspace/{Uri.EscapeDataString(workspaceId)}/freeze", null, ct);
        return resp.IsSuccessStatusCode;
    }

    public async Task<bool> UnfreezeWorkspaceAsync(string workspaceId, CancellationToken ct = default)
    {
        var resp = await _http.PostAsync(
            $"/api/workspace/{Uri.EscapeDataString(workspaceId)}/unfreeze", null, ct);
        return resp.IsSuccessStatusCode;
    }

    // ── AgentTemplate ─────────────────────────────────

    public async Task<List<AgentTemplateDefinition>> GetAgentTemplatesAsync(CancellationToken ct = default)
    {
        var list = await _http.GetFromJsonAsync<List<AgentTemplateDefinition>>("/api/agenttemplate", ct);
        return list ?? [];
    }

    public async Task<AgentTemplateDefinition?> GetAgentTemplateAsync(string templateId, CancellationToken ct = default)
    {
        var resp = await _http.GetAsync($"/api/agenttemplate/{Uri.EscapeDataString(templateId)}", ct);
        if (!resp.IsSuccessStatusCode) return null;
        return await resp.Content.ReadFromJsonAsync<AgentTemplateDefinition>(ct);
    }

    // ── Session ────────────────────────────────────────

    public async Task<List<SessionRecord>> GetSessionsAsync(string? workspaceId = null, CancellationToken ct = default)
    {
        var url = string.IsNullOrEmpty(workspaceId)
            ? "/api/session"
            : $"/api/session/workspace/{Uri.EscapeDataString(workspaceId)}";
        var list = await _http.GetFromJsonAsync<List<SessionRecord>>(url, ct);
        return list ?? [];
    }

    public async Task<SessionRecord?> GetSessionAsync(string sessionId, CancellationToken ct = default)
    {
        var resp = await _http.GetAsync($"/api/session/{Uri.EscapeDataString(sessionId)}", ct);
        if (!resp.IsSuccessStatusCode) return null;
        return await resp.Content.ReadFromJsonAsync<SessionRecord>(ct);
    }

    // ── Audit ──────────────────────────────────────────

    public async Task<List<AuditEventRecord>> GetAuditEventsAsync(
        string? sessionId = null, string? workspaceId = null, int limit = 50,
        CancellationToken ct = default)
    {
        var query = new List<string>();
        if (!string.IsNullOrEmpty(sessionId))
            query.Add($"sessionId={Uri.EscapeDataString(sessionId)}");
        if (!string.IsNullOrEmpty(workspaceId))
            query.Add($"workspaceId={Uri.EscapeDataString(workspaceId)}");
        query.Add($"limit={limit}");
        var url = "/api/audit?" + string.Join("&", query);
        var list = await _http.GetFromJsonAsync<List<AuditEventRecord>>(url, ct);
        return list ?? [];
    }

    // ── Approval ───────────────────────────────────────

    public async Task<List<ApprovalRecord>> GetPendingApprovalsAsync(CancellationToken ct = default)
    {
        var list = await _http.GetFromJsonAsync<List<ApprovalRecord>>("/api/approval/pending", ct);
        return list ?? [];
    }

    public async Task<bool> ConfirmApprovalAsync(string approvalId, string confirmationCode, string confirmedBy, CancellationToken ct = default)
    {
        var body = new { confirmationCode, confirmedBy };
        var resp = await _http.PostAsJsonAsync($"/api/approval/{Uri.EscapeDataString(approvalId)}/confirm", body, ct);
        return resp.IsSuccessStatusCode;
    }

    public async Task<bool> RejectApprovalAsync(string approvalId, string rejectedBy, CancellationToken ct = default)
    {
        var body = new { rejectedBy };
        var resp = await _http.PostAsJsonAsync($"/api/approval/{Uri.EscapeDataString(approvalId)}/reject", body, ct);
        return resp.IsSuccessStatusCode;
    }

    // ── Message Ingress ────────────────────────────────

    public async Task<MessageIngressResponse?> SendMessageAsync(
        string channelId, string userExternalId, string messageText,
        string? workspaceId = null,
        string? sessionId = null,
        LlmConfig? llmConfig = null,
        string? agentTemplateId = null,
        CapabilityPolicy? capabilityPolicy = null,
        IReadOnlyList<LlmToolDefinition>? toolDefinitions = null,
        CancellationToken ct = default)
    {
        var request = new MessageIngressRequest
        {
            ChannelId = channelId,
            UserExternalId = userExternalId,
            MessageText = messageText,
            WorkspaceId = workspaceId,
            SessionId = sessionId,
            AgentTemplateId = agentTemplateId,
            LlmConfig = llmConfig,
            CapabilityPolicy = capabilityPolicy,
            ToolDefinitions = toolDefinitions,
        };

        var resp = await _http.PostAsJsonAsync("/api/messageingress", request, ct);
        return await resp.Content.ReadFromJsonAsync<MessageIngressResponse>(ct);
    }

    // ── Knowledge Base ─────────────────────────────────

    public async Task<List<KnowledgeDocument>> GetKnowledgeDocumentsAsync(string workspaceId, CancellationToken ct = default)
    {
        var list = await _http.GetFromJsonAsync<List<KnowledgeDocument>>(
            $"/api/knowledge/{Uri.EscapeDataString(workspaceId)}/documents", ct);
        return list ?? [];
    }

    public async Task<List<KnowledgeSearchResult>> SearchKnowledgeAsync(string workspaceId, string query, int topK = 5, CancellationToken ct = default)
    {
        var req = new { Query = query, TopK = topK };
        var resp = await _http.PostAsJsonAsync(
            $"/api/knowledge/{Uri.EscapeDataString(workspaceId)}/search", req, ct);
        if (!resp.IsSuccessStatusCode) return [];
        var results = await resp.Content.ReadFromJsonAsync<List<KnowledgeSearchResult>>(ct);
        return results ?? [];
    }

    // ── Unified Storage ────────────────────────────────

    public async Task<List<StorageObjectMeta>> ListObjectsAsync(string workspaceId, string? prefix = null, CancellationToken ct = default)
    {
        var url = $"/api/storage/{Uri.EscapeDataString(workspaceId)}/objects";
        if (!string.IsNullOrEmpty(prefix)) url += $"?prefix={Uri.EscapeDataString(prefix)}";
        var list = await _http.GetFromJsonAsync<List<StorageObjectMeta>>(url, ct);
        return list ?? [];
    }

    // ── Knowledge Graph ────────────────────────────────

    public async Task<List<GraphEntity>> QueryGraphEntitiesAsync(string workspaceId, GraphQueryRequest req, CancellationToken ct = default)
    {
        var resp = await _http.PostAsJsonAsync(
            $"/api/graph/{Uri.EscapeDataString(workspaceId)}/entities/query", req, ct);
        if (!resp.IsSuccessStatusCode) return [];
        var results = await resp.Content.ReadFromJsonAsync<List<GraphEntity>>(ct);
        return results ?? [];
    }

    public async Task<object?> GetGraphStatsAsync(string workspaceId, CancellationToken ct = default)
    {
        var resp = await _http.GetAsync($"/api/graph/{Uri.EscapeDataString(workspaceId)}/stats", ct);
        if (!resp.IsSuccessStatusCode) return null;
        return await resp.Content.ReadFromJsonAsync<object>(ct);
    }

    // ── Debug / Observability ─────────────────────────

    public async Task<DebugSummaryDto?> GetDebugSummaryAsync(CancellationToken ct = default)
    {
        var resp = await _http.GetAsync("/api/debug/summary", ct);
        if (!resp.IsSuccessStatusCode) return null;
        return await resp.Content.ReadFromJsonAsync<DebugSummaryDto>(ct);
    }

    public async Task<DebugMetricsDto?> GetDebugMetricsAsync(CancellationToken ct = default)
    {
        var resp = await _http.GetAsync("/api/debug/metrics", ct);
        if (!resp.IsSuccessStatusCode) return null;
        return await resp.Content.ReadFromJsonAsync<DebugMetricsDto>(ct);
    }

    public async Task<SessionDebugDto?> GetSessionDebugAsync(string sessionId, CancellationToken ct = default)
    {
        var resp = await _http.GetAsync($"/api/debug/session/{Uri.EscapeDataString(sessionId)}", ct);
        if (!resp.IsSuccessStatusCode) return null;
        return await resp.Content.ReadFromJsonAsync<SessionDebugDto>(ct);
    }

    public async Task<MessageDebugDto?> GetMessageDebugAsync(string messageId, CancellationToken ct = default)
    {
        var resp = await _http.GetAsync($"/api/debug/message/{Uri.EscapeDataString(messageId)}", ct);
        if (!resp.IsSuccessStatusCode) return null;
        return await resp.Content.ReadFromJsonAsync<MessageDebugDto>(ct);
    }

    public async Task<WorkspaceDebugDto?> GetWorkspaceDebugAsync(string workspaceId, CancellationToken ct = default)
    {
        var resp = await _http.GetAsync($"/api/debug/workspace/{Uri.EscapeDataString(workspaceId)}", ct);
        if (!resp.IsSuccessStatusCode) return null;
        return await resp.Content.ReadFromJsonAsync<WorkspaceDebugDto>(ct);
    }
}

public sealed record DebugSummaryDto
{
    public DateTimeOffset UtcNow { get; init; }
    public int RecentAuditCount { get; init; }
    public IReadOnlyList<DebugAuditItemDto> RecentAuditEvents { get; init; } = [];
}

public sealed record DebugAuditItemDto
{
    public string? EventId { get; init; }
    public string? EventType { get; init; }
    public string? MessageId { get; init; }
    public string? SessionId { get; init; }
    public DateTimeOffset Timestamp { get; init; }
    public string? Detail { get; init; }
}

public sealed record DebugMetricsDto
{
    public DateTimeOffset UtcNow { get; init; }
    public DebugSessionMetricsDto Session { get; init; } = new();
    public DebugApprovalMetricsDto Approval { get; init; } = new();
    public DebugRuntimeMetricsDto Runtime { get; init; } = new();
    public DebugWorkspaceMetricsDto Workspace { get; init; } = new();
    public DebugAuditMetricsDto Audit { get; init; } = new();
}

public sealed record DebugSessionMetricsDto
{
    public int Total { get; init; }
    public int Active { get; init; }
    public int Closed { get; init; }
}

public sealed record DebugApprovalMetricsDto
{
    public int Pending { get; init; }
}

public sealed record DebugRuntimeMetricsDto
{
    public int TotalNodes { get; init; }
    public int OnlineNodes { get; init; }
    public int TotalActiveSessionLoad { get; init; }
}

public sealed record DebugWorkspaceMetricsDto
{
    public int Total { get; init; }
    public int Enabled { get; init; }
    public int Frozen { get; init; }
}

public sealed record DebugAuditMetricsDto
{
    public int RecentCount { get; init; }
    public Dictionary<string, int> ByType { get; init; } = [];
}

public sealed record SessionDebugDto
{
    public SessionRecord? Session { get; init; }
    public int AuditCount { get; init; }
    public IReadOnlyList<DebugAuditItemDto> RecentAudit { get; init; } = [];
}

public sealed record MessageDebugDto
{
    public string? MessageId { get; init; }
    public RouteDecisionRecord? RouteDecision { get; init; }
    public SessionRecord? Session { get; init; }
    public int AuditCount { get; init; }
    public IReadOnlyList<DebugAuditItemDto> AuditTimeline { get; init; } = [];
    public MessageDiagnosisDto Diagnosis { get; init; } = new();
}

public sealed record MessageDiagnosisDto
{
    public bool? RouteSuccess { get; init; }
    public string? RouteFailureReason { get; init; }
    public bool HasPermissionDenied { get; init; }
    public bool HasRuntimeFailure { get; init; }
}

public sealed record WorkspaceDebugDto
{
    public WorkspaceDebugInfoDto Workspace { get; init; } = new();
    public WorkspaceSessionSummaryDto Session { get; init; } = new();
    public WorkspaceApprovalSummaryDto Approval { get; init; } = new();
    public WorkspaceRoutingSummaryDto Routing { get; init; } = new();
    public WorkspaceAuditSummaryDto Audit { get; init; } = new();
    public WorkspaceWorkflowSummaryDto Workflow { get; init; } = new();
}

public sealed record WorkspaceDebugInfoDto
{
    public string? WorkspaceId { get; init; }
    public string? Name { get; init; }
    public bool IsEnabled { get; init; }
    public bool IsFrozen { get; init; }
    public int ChannelCount { get; init; }
    public int AgentTemplateCount { get; init; }
    public int AuditAgentCount { get; init; }
}

public sealed record WorkspaceSessionSummaryDto
{
    public int Total { get; init; }
    public int Active { get; init; }
    public int Frozen { get; init; }
    public int Failed { get; init; }
}

public sealed record WorkspaceApprovalSummaryDto
{
    public int Pending { get; init; }
}

public sealed record WorkspaceRoutingSummaryDto
{
    public int RecentTotal { get; init; }
    public int RecentFailures { get; init; }
    public IReadOnlyList<WorkspaceFailureReasonDto> TopFailureReasons { get; init; } = [];
}

public sealed record WorkspaceFailureReasonDto
{
    public string? Reason { get; init; }
    public int Count { get; init; }
}

public sealed record WorkspaceAuditSummaryDto
{
    public int RecentCount { get; init; }
    public int PermissionDeniedCount { get; init; }
    public int RuntimeFailedCount { get; init; }
}

public sealed record WorkspaceWorkflowSummaryDto
{
    public int BoundWorkflows { get; init; }
    public string? PotentialBlockerHint { get; init; }
}