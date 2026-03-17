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

    // ── Session ────────────────────────────────────────

    public async Task<List<SessionRecord>> GetSessionsAsync(string? workspaceId = null, CancellationToken ct = default)
    {
        var url = string.IsNullOrEmpty(workspaceId)
            ? "/api/session"
            : $"/api/session?workspaceId={Uri.EscapeDataString(workspaceId)}";
        var list = await _http.GetFromJsonAsync<List<SessionRecord>>(url, ct);
        return list ?? [];
    }

    public async Task<SessionRecord?> GetSessionAsync(string sessionId, CancellationToken ct = default)
    {
        var resp = await _http.GetAsync($"/api/session/{Uri.EscapeDataString(sessionId)}", ct);
        if (!resp.IsSuccessStatusCode) return null;
        return await resp.Content.ReadFromJsonAsync<SessionRecord>(ct);
    }

    // ── Message Ingress ────────────────────────────────

    public async Task<MessageIngressResponse?> SendMessageAsync(
        string channelId, string userExternalId, string messageText,
        string? workspaceId = null, CancellationToken ct = default)
    {
        var request = new MessageIngressRequest
        {
            ChannelId = channelId,
            UserExternalId = userExternalId,
            MessageText = messageText,
            WorkspaceId = workspaceId,
        };

        var resp = await _http.PostAsJsonAsync("/api/messageingress", request, ct);
        return await resp.Content.ReadFromJsonAsync<MessageIngressResponse>(ct);
    }
}
