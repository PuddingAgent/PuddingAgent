using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace PuddingChat;

/// <summary>Consumes the same authenticated admission and projection APIs as the web client.</summary>
public sealed class HttpChatClient : IChatClient
{
    private readonly HttpClient _http;
    private string? _token;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public HttpChatClient(Uri address, HttpMessageHandler? handler = null)
    {
        if (!address.IsLoopback || address.Scheme != Uri.UriSchemeHttp)
            throw new ArgumentException("Desktop chat requires the current loopback Core address.", nameof(address));
        _http = new HttpClient(handler ?? new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false })
        { BaseAddress = new Uri(address.GetLeftPart(UriPartial.Authority)), Timeout = TimeSpan.FromSeconds(30) };
    }
    private static string E(string value) => Uri.EscapeDataString(value);
    private static string AgentPath(RoleKey role) => $"/api/workspaces/{E(role.WorkspaceId)}/agents/{E(role.AgentId)}";
    private async Task<T?> RequestAsync<T>(HttpMethod method, string path, object? body, CancellationToken ct, string? workspace = null)
    {
        using var request = new HttpRequestMessage(method, path);
        if (_token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
        if (workspace is not null) request.Headers.Add("X-Workspace-Id", workspace);
        if (body is not null) request.Content = JsonContent.Create(body, options: Json);
        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotModified) return default;
        if (response.StatusCode == HttpStatusCode.Unauthorized) throw new UnauthorizedAccessException("登录已失效，请重新登录。");
        if (!response.IsSuccessStatusCode) throw new HttpRequestException($"请求未完成（HTTP {(int)response.StatusCode}）。", null, response.StatusCode);
        if (typeof(T) == typeof(bool)) return (T)(object)true;
        return await response.Content.ReadFromJsonAsync<T>(Json, ct).ConfigureAwait(false)
            ?? throw new InvalidDataException("Core 返回了空响应。");
    }
    public async Task LoginAsync(string user, string password, CancellationToken ct)
    {
        _token = null;
        var result = await RequestAsync<JsonElement>(HttpMethod.Post, "/api/login/account", new { username = user, password, type = "account" }, ct);
        if (!result.TryGetProperty("status", out var status) || status.GetString() != "ok"
            || !result.TryGetProperty("token", out var token) || string.IsNullOrWhiteSpace(token.GetString()))
            throw new UnauthorizedAccessException("账号或密码不正确，或账号已停用。");
        _token = token.GetString();
    }
    public async Task<Workspace[]> GetWorkspacesAsync(CancellationToken ct) =>
        await RequestAsync<Workspace[]>(HttpMethod.Get, "/api/workspaces", null, ct) ?? [];
    public async Task<Agent[]> GetAgentsAsync(string workspace, CancellationToken ct) =>
        await RequestAsync<Agent[]>(HttpMethod.Get, $"/api/workspaces/{E(workspace)}/agents", null, ct) ?? [];
    public async Task<AgentStatus[]> GetStatusesAsync(string workspace, CancellationToken ct) =>
        await RequestAsync<AgentStatus[]>(HttpMethod.Get, $"/api/workspaces/{E(workspace)}/agents/status", null, ct) ?? [];
    public Task<Conversation?> GetConversationAsync(RoleKey role, long? cursor, CancellationToken ct) =>
        RequestAsync<Conversation>(HttpMethod.Get, AgentPath(role) + "/conversation" + (cursor is null ? "" : $"?knownCursor={cursor}"), null, ct);
    public async Task<string> EnsureSessionAsync(RoleKey role, Agent agent, CancellationToken ct)
    {
        var result = await RequestAsync<JsonElement>(HttpMethod.Post, "/api/sessions/main", new
        { workspaceId = role.WorkspaceId, principalKind = "agent", principalId = role.AgentId,
            agentTemplateId = agent.SourceTemplateId ?? $"global:{agent.AgentId}", title = agent.Label }, ct);
        return result.GetProperty("sessionId").GetString() ?? throw new InvalidDataException("缺少主会话标识。");
    }
    public async Task<Acceptance> SendAsync(PendingSend send, CancellationToken ct) =>
        await RequestAsync<Acceptance>(HttpMethod.Post, $"/api/v1/conversations/{E(send.ConversationId)}/turns", new
        { send.ClientRequestId, send.ClientMessageId, recipients = new { type = "agent", agentIds = new[] { send.Role.AgentId } },
            content = new[] { new { type = "text", text = send.Text } } }, ct, send.Role.WorkspaceId)
        ?? throw new InvalidDataException("缺少发送回执。");
    public async Task CancelAsync(string workspace, string conversation, string turn, CancellationToken ct) =>
        await RequestAsync<bool>(HttpMethod.Post, $"/api/v1/conversations/{E(conversation)}/turns/{E(turn)}/cancel", new { }, ct, workspace);
    public async Task<ProcessDetails> GetProcessAsync(RoleKey role, string message, CancellationToken ct) =>
        await RequestAsync<ProcessDetails>(HttpMethod.Get, AgentPath(role) + $"/conversation/messages/{E(message)}/process-items", null, ct)
        ?? throw new InvalidDataException("缺少执行明细。");
    public void Dispose() { _token = null; _http.Dispose(); }
}
