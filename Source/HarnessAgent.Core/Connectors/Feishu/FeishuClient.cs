using System.Net.Http.Json;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HarnessAgent.Core.Connectors.Feishu;

/// <summary>
/// 飞书 HTTP API 客户端 — token 管理、发送消息、文件上传。
/// </summary>
public class FeishuClient : IDisposable
{
    private const int MaxDownloadedMessageResourceBytes = 50 * 1024 * 1024;
    private const int MaxUploadedImageBytes = 10 * 1024 * 1024;
    private const int MaxUploadFileBytes = 30 * 1024 * 1024;
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
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
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
    /// <param name="receiveId">接收方 ID（open_id 或 chat_id，由 receiveIdType 决定）。</param>
    /// <param name="msgType">消息类型。</param>
    /// <param name="content">消息内容。</param>
    /// <param name="uuid">幂等键。</param>
    /// <param name="ct">取消令牌。</param>
    /// <param name="receiveIdType">
    /// 接收方 ID 类型：open_id（私聊）或 chat_id（群聊）。
    /// 群聊兜底时必须传 chat_id，否则 chat_id 会被误当 open_id 使用而发送失败。
    /// </param>
    public async Task<SendMessageResponse> SendMessageAsync(
        string receiveId,
        string msgType,
        string content,
        string? uuid = null,
        CancellationToken ct = default,
        string receiveIdType = "open_id")
    {
        var token = await GetAccessTokenAsync(ct);
        var request = new HttpRequestMessage(HttpMethod.Post,
            $"{BaseUrl}/im/v1/messages?receive_id_type={Uri.EscapeDataString(receiveIdType)}")
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
    /// Downloads one image/file resource attached to a received message. The
    /// response is bounded before it crosses into Pudding artifact storage.
    /// </summary>
    public async Task<FeishuMessageResource> DownloadMessageResourceAsync(
        string messageId,
        string fileKey,
        string resourceType,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(messageId);
        ArgumentException.ThrowIfNullOrWhiteSpace(fileKey);
        if (!string.Equals(resourceType, "image", StringComparison.Ordinal)
            && !string.Equals(resourceType, "file", StringComparison.Ordinal))
        {
            throw new ArgumentOutOfRangeException(
                nameof(resourceType),
                resourceType,
                "Feishu message resource type must be image or file.");
        }

        var token = await GetAccessTokenAsync(ct);
        var encodedMessageId = Uri.EscapeDataString(messageId);
        var encodedFileKey = Uri.EscapeDataString(fileKey);
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"{BaseUrl}/im/v1/messages/{encodedMessageId}/resources/{encodedFileKey}?type={resourceType}");
        request.Headers.Authorization = new("Bearer", token);

        using var response = await _http.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            ct);
        response.EnsureSuccessStatusCode();

        if (response.Content.Headers.ContentLength is > MaxDownloadedMessageResourceBytes)
        {
            throw new InvalidOperationException(
                $"Feishu message resource exceeds {MaxDownloadedMessageResourceBytes} bytes.");
        }

        await using var source = await response.Content.ReadAsStreamAsync(ct);
        using var destination = new MemoryStream();
        var buffer = new byte[81_920];
        while (true)
        {
            var read = await source.ReadAsync(buffer, ct);
            if (read == 0)
                break;
            if (destination.Length + read > MaxDownloadedMessageResourceBytes)
            {
                throw new InvalidOperationException(
                    $"Feishu message resource exceeds {MaxDownloadedMessageResourceBytes} bytes.");
            }

            await destination.WriteAsync(buffer.AsMemory(0, read), ct);
        }

        if (destination.Length == 0)
            throw new InvalidOperationException("Feishu message resource is empty.");

        return new FeishuMessageResource(
            destination.ToArray(),
            response.Content.Headers.ContentType?.MediaType,
            response.Content.Headers.ContentDisposition?.FileNameStar
                ?? response.Content.Headers.ContentDisposition?.FileName);
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

    /// <summary>Uploads an Ogg/Opus audio file and returns its Feishu file_key.</summary>
    public async Task<UploadFileResponse> UploadAudioAsync(
        byte[] content,
        string fileName,
        long? durationMs = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        if (content.Length == 0)
            throw new ArgumentException("Audio file is empty.", nameof(content));
        if (content.Length > MaxUploadFileBytes)
        {
            throw new ArgumentException(
                $"Audio file exceeds {MaxUploadFileBytes} bytes.",
                nameof(content));
        }

        var token = await GetAccessTokenAsync(ct);
        using var multipart = new MultipartFormDataContent();
        multipart.Add(new StringContent("opus"), "file_type");
        multipart.Add(new StringContent(fileName), "file_name");
        if (durationMs is > 0)
        {
            multipart.Add(
                new StringContent(
                    durationMs.Value.ToString(
                        System.Globalization.CultureInfo.InvariantCulture)),
                "duration");
        }
        var audio = new ByteArrayContent(content);
        audio.Headers.ContentType = new("audio/ogg");
        multipart.Add(audio, "file", fileName);

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"{BaseUrl}/im/v1/files")
        {
            Content = multipart,
        };
        request.Headers.Authorization = new("Bearer", token);
        using var response = await _http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<UploadFileResponse>(
            _json,
            ct);
        return result ?? new UploadFileResponse
        {
            Code = -1,
            Msg = "empty response",
        };
    }

    /// <summary>Replies to a Feishu message with an uploaded audio file.</summary>
    public async Task<SendMessageResponse> ReplyAudioAsync(
        string messageId,
        string fileKey,
        string? uuid = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(messageId);
        ArgumentException.ThrowIfNullOrWhiteSpace(fileKey);
        var token = await GetAccessTokenAsync(ct);
        var content = JsonSerializer.Serialize(new { file_key = fileKey }, _json);
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"{BaseUrl}/im/v1/messages/{Uri.EscapeDataString(messageId)}/reply")
        {
            Content = JsonContent.Create(new
            {
                content,
                msg_type = "audio",
                uuid,
            }, options: _json),
        };
        request.Headers.Authorization = new("Bearer", token);
        using var response = await _http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<SendMessageResponse>(
            _json,
            ct);
        return result ?? new SendMessageResponse
        {
            Code = -1,
            Msg = "empty response",
        };
    }

    /// <summary>Uploads an image for use in a Feishu message.</summary>
    public async Task<UploadImageResponse> UploadImageAsync(
        byte[] content,
        string fileName,
        string mimeType,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        ArgumentException.ThrowIfNullOrWhiteSpace(mimeType);
        if (content.Length == 0)
            throw new ArgumentException("Image file is empty.", nameof(content));
        if (content.Length > MaxUploadedImageBytes)
        {
            throw new ArgumentException(
                $"Image file exceeds {MaxUploadedImageBytes} bytes.",
                nameof(content));
        }
        if (mimeType is not ("image/jpeg" or "image/png" or "image/webp"))
        {
            throw new ArgumentOutOfRangeException(
                nameof(mimeType),
                mimeType,
                "Feishu message image must be JPEG, PNG, or WebP.");
        }

        var token = await GetAccessTokenAsync(ct);
        using var multipart = new MultipartFormDataContent();
        multipart.Add(new StringContent("message"), "image_type");
        var image = new ByteArrayContent(content);
        image.Headers.ContentType = new(mimeType);
        multipart.Add(image, "image", fileName);

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"{BaseUrl}/im/v1/images")
        {
            Content = multipart,
        };
        request.Headers.Authorization = new("Bearer", token);
        using var response = await _http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<UploadImageResponse>(
            _json,
            ct);
        return result ?? new UploadImageResponse
        {
            Code = -1,
            Msg = "empty response",
        };
    }

    /// <summary>Replies to a Feishu message with an uploaded image.</summary>
    public async Task<SendMessageResponse> ReplyImageAsync(
        string messageId,
        string imageKey,
        string? uuid = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(messageId);
        ArgumentException.ThrowIfNullOrWhiteSpace(imageKey);
        var token = await GetAccessTokenAsync(ct);
        var content = JsonSerializer.Serialize(new { image_key = imageKey }, _json);
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"{BaseUrl}/im/v1/messages/{Uri.EscapeDataString(messageId)}/reply")
        {
            Content = JsonContent.Create(new
            {
                content,
                msg_type = "image",
                uuid,
            }, options: _json),
        };
        request.Headers.Authorization = new("Bearer", token);
        using var response = await _http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<SendMessageResponse>(
            _json,
            ct);
        return result ?? new SendMessageResponse
        {
            Code = -1,
            Msg = "empty response",
        };
    }

    /// <summary>Create a CardKit v1 card entity from a JSON 2.0 card.</summary>
    public async Task<CardKitResponse> CreateCardAsync(
        string cardJson,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cardJson);
        var token = await GetAccessTokenAsync(ct);
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"{BaseUrl}/cardkit/v1/cards")
        {
            Content = JsonContent.Create(new
            {
                type = "card_json",
                data = cardJson,
            }, options: _json),
        };
        request.Headers.Authorization = new("Bearer", token);

        using var response = await _http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<CardKitResponse>(
            _json,
            ct);
        return result ?? new CardKitResponse { Code = -1, Msg = "empty response" };
    }

    /// <summary>Reply to a Feishu message with a reference to a CardKit entity.</summary>
    public async Task<SendMessageResponse> ReplyCardAsync(
        string messageId,
        string cardId,
        string? uuid = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(messageId);
        ArgumentException.ThrowIfNullOrWhiteSpace(cardId);
        var token = await GetAccessTokenAsync(ct);
        var content = JsonSerializer.Serialize(new
        {
            type = "card",
            data = new { card_id = cardId },
        }, _json);
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"{BaseUrl}/im/v1/messages/{messageId}/reply")
        {
            Content = JsonContent.Create(new
            {
                content,
                msg_type = "interactive",
                uuid,
            }, options: _json),
        };
        request.Headers.Authorization = new("Bearer", token);

        using var response = await _http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<SendMessageResponse>(
            _json,
            ct);
        return result ?? new SendMessageResponse { Code = -1, Msg = "empty response" };
    }

    /// <summary>
    /// Replace one CardKit text element's cumulative content. Feishu renders the
    /// newly appended suffix with its native typewriter animation.
    /// </summary>
    public async Task<CardKitResponse> UpdateCardElementContentAsync(
        string cardId,
        string elementId,
        string content,
        int sequence,
        string? uuid = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cardId);
        ArgumentException.ThrowIfNullOrWhiteSpace(elementId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sequence);
        var token = await GetAccessTokenAsync(ct);
        using var request = new HttpRequestMessage(
            HttpMethod.Put,
            $"{BaseUrl}/cardkit/v1/cards/{cardId}/elements/{elementId}/content")
        {
            Content = JsonContent.Create(new
            {
                content,
                sequence,
                uuid,
            }, options: _json),
        };
        request.Headers.Authorization = new("Bearer", token);

        using var response = await _http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<CardKitResponse>(
            _json,
            ct);
        return result ?? new CardKitResponse { Code = -1, Msg = "empty response" };
    }

    /// <summary>
    /// Replace a CardKit entity with a complete JSON 2.0 card. This atomically
    /// closes native streaming mode while setting final content and summary;
    /// the settings endpoint does not accept every card config field.
    /// </summary>
    public async Task<CardKitResponse> UpdateCardAsync(
        string cardId,
        string cardJson,
        int sequence,
        string? uuid = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cardId);
        ArgumentException.ThrowIfNullOrWhiteSpace(cardJson);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sequence);
        var token = await GetAccessTokenAsync(ct);
        using var request = new HttpRequestMessage(
            HttpMethod.Put,
            $"{BaseUrl}/cardkit/v1/cards/{cardId}")
        {
            Content = JsonContent.Create(new
            {
                card = new
                {
                    type = "card_json",
                    data = cardJson,
                },
                sequence,
                uuid,
            }, options: _json),
        };
        request.Headers.Authorization = new("Bearer", token);

        using var response = await _http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<CardKitResponse>(
            _json,
            ct);
        return result ?? new CardKitResponse { Code = -1, Msg = "empty response" };
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
