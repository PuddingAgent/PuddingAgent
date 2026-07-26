using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HarnessAgent.Core.Connectors.Feishu;

/// <summary>
/// 飞书 HTTP API 客户端 — token 管理、发送消息、文件上传。
/// </summary>
public class FeishuClient : IDisposable
{
    private readonly FeishuConfig _config;
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly JsonSerializerOptions _json;

    private string? _tenantAccessToken;
    private DateTime _tokenExpiresAt = DateTime.MinValue;
    private readonly SemaphoreSlim _tokenLock = new(1, 1);

    private const string BaseUrl = "https://open.feishu.cn/open-apis";

    public FeishuClient(FeishuConfig config, HttpClient? http = null)
    {
        _config = config;
        _http = http ?? new HttpClient();
        _ownsHttp = http is null;
        _json = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };
    }

    /// <summary>
    /// 获取或刷新 tenant_access_token（自动缓存，提前 5 分钟刷新）。
    /// </summary>
    public async Task<string> GetAccessTokenAsync(
        CancellationToken ct = default)
    {
        // 缓存有效
        if (_tenantAccessToken != null && DateTime.UtcNow < _tokenExpiresAt.AddMinutes(-5))
            return _tenantAccessToken;

        await _tokenLock.WaitAsync(ct);
        try
        {
            // 双重检查
            if (_tenantAccessToken != null && DateTime.UtcNow < _tokenExpiresAt.AddMinutes(-5))
                return _tenantAccessToken;

            var body = new { app_id = _config.AppId, app_secret = _config.AppSecret };
            var response = await _http.PostAsJsonAsync(
                $"{BaseUrl}/auth/v3/tenant_access_token/internal",
                body,
                _json,
                ct);

            response.EnsureSuccessStatusCode();
            var result = await response.Content.ReadFromJsonAsync<TenantAccessTokenResponse>(
                _json,
                ct);

            if (result?.Code != 0 || string.IsNullOrWhiteSpace(result.TenantAccessToken))
                throw new InvalidOperationException(
                    $"获取 token 失败: code={result?.Code}, msg={result?.Msg}");

            _tenantAccessToken = result.TenantAccessToken;
            _tokenExpiresAt = DateTime.UtcNow.AddSeconds(result.Expire);
            return _tenantAccessToken;
        }
        finally
        {
            _tokenLock.Release();
        }
    }

    /// <summary>
    /// 发送消息（text/markdown/image/card 等）。
    /// </summary>
    public async Task<SendMessageResponse> SendMessageAsync(
        string receiveId,
        string msgType,
        string content,
        string? uuid = null,
        CancellationToken ct = default)
    {
        var token = await GetAccessTokenAsync(ct);
        var request = new HttpRequestMessage(HttpMethod.Post,
            $"{BaseUrl}/im/v1/messages?receive_id_type=open_id")
        {
            Content = JsonContent.Create(new
            {
                receive_id = receiveId,
                msg_type = msgType,
                content,
                uuid
            }, options: _json)
        };
        request.Headers.Authorization = new("Bearer", token);

        var response = await _http.SendAsync(request, ct);
        var result = await response.Content.ReadFromJsonAsync<SendMessageResponse>(
            _json,
            ct);
        return result ?? new SendMessageResponse { Code = -1, Msg = "empty response" };
    }

    /// <summary>
    /// 获取消息内容。
    /// </summary>
    public async Task<FeishuMessageEvent?> GetMessageAsync(
        string messageId,
        CancellationToken ct = default)
    {
        var token = await GetAccessTokenAsync(ct);
        var request = new HttpRequestMessage(HttpMethod.Get,
            $"{BaseUrl}/im/v1/messages/{messageId}");
        request.Headers.Authorization = new("Bearer", token);

        var response = await _http.SendAsync(request, ct);
        var json = await response.Content.ReadAsStringAsync(ct);

        using var doc = JsonDocument.Parse(json);
        var code = doc.RootElement.GetProperty("code").GetInt32();
        if (code != 0) return null;

        var items = doc.RootElement.GetProperty("data").GetProperty("items");
        if (items.GetArrayLength() == 0) return null;

        return JsonSerializer.Deserialize<FeishuMessageEvent>(
            items[0].GetRawText(), _json);
    }

    /// <summary>
    /// 回复消息（text）。
    /// </summary>
    public async Task<SendMessageResponse> ReplyTextAsync(
        string messageId,
        string text,
        string? uuid = null,
        CancellationToken ct = default)
    {
        var token = await GetAccessTokenAsync(ct);
        var content = JsonSerializer.Serialize(new { text }, _json);
        var request = new HttpRequestMessage(HttpMethod.Post,
            $"{BaseUrl}/im/v1/messages/{messageId}/reply")
        {
            Content = JsonContent.Create(new
            {
                content,
                msg_type = "text",
                uuid
            }, options: _json)
        };
        request.Headers.Authorization = new("Bearer", token);

        var response = await _http.SendAsync(request, ct);
        var result = await response.Content.ReadFromJsonAsync<SendMessageResponse>(
            _json,
            ct);
        return result ?? new SendMessageResponse { Code = -1, Msg = "empty response" };
    }

    /// <summary>
    /// 获取机器人所在的群列表。
    /// </summary>
    public async Task<string> ListChatsAsync(
        CancellationToken ct = default)
    {
        var token = await GetAccessTokenAsync(ct);
        var request = new HttpRequestMessage(HttpMethod.Get,
            $"{BaseUrl}/im/v1/chats?page_size=20");
        request.Headers.Authorization = new("Bearer", token);
        var response = await _http.SendAsync(request, ct);
        return await response.Content.ReadAsStringAsync(ct);
    }

    public void Dispose()
    {
        _tokenLock.Dispose();
        if (_ownsHttp)
            _http.Dispose();
    }
}
