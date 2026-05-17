using System.Security.Cryptography;
using System.Text;
using PuddingCode.Platform;

namespace PuddingAgent.Connectors;

/// <summary>
/// Webhook 连接器 — 通过 HTTP POST 端点接收外部 webhook 事件，
/// 支持 HMAC-SHA256 签名验证，将入站事件转换为内部消息并转发给 Runtime。
/// </summary>
public sealed class WebhookConnector : IPuddingConnector
{
    private readonly ILogger<WebhookConnector> _logger;
    private readonly IHttpClientFactory _httpClientFactory;
    private ConnectorContext? _context;

    private long _messagesReceived;
    private long _messagesSent;
    private long _errors;
    private DateTimeOffset? _lastReceiveTime;
    private DateTimeOffset? _lastErrorTime;
    private string? _lastError;

    /// <summary>Webhook HMAC 签名密钥。</summary>
    private readonly string _signingSecret;

    /// <summary>是否启用签名验证。</summary>
    public bool SignatureVerificationEnabled { get; set; } = true;

    public ConnectorDescriptor Descriptor { get; } = new()
    {
        ConnectorId = "webhook-001",
        ConnectorType = "webhook",
        Protocol = "HTTP",
        Version = "1.0",
        Description = "接收外部 Webhook HTTP POST 事件并转发到 Agent Runtime",
        Capabilities = ["receive", "send"],
    };

    public WebhookConnector(IConfiguration configuration, IHttpClientFactory httpClientFactory, ILogger<WebhookConnector> logger)
    {
        _logger = logger;
        _httpClientFactory = httpClientFactory;
        // 签名密钥从配置读取，默认使用随机生成（仅开发环境）
        _signingSecret = configuration["Webhook:SigningSecret"]
            ?? configuration["Jwt:Key"]
            ?? Guid.NewGuid().ToString("N");
    }

    public Task StartAsync(ConnectorContext context, CancellationToken ct = default)
    {
        _context = context;
        _context.Log("[Webhook] 连接器已启动，等待 webhook 事件...");
        _logger.LogInformation("[Webhook] Connector started. SignatureVerification={Enabled}", SignatureVerificationEnabled);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct = default)
    {
        _context?.Log("[Webhook] 连接器已停止。");
        _logger.LogInformation("[Webhook] Connector stopped. Received={Received} Sent={Sent} Errors={Errors}",
            _messagesReceived, _messagesSent, _errors);
        return Task.CompletedTask;
    }

    public async Task SendAsync(ConnectorMessage message, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(message.Target))
        {
            _logger.LogWarning("[Webhook] SendAsync 缺少 Target URL");
            return;
        }

        try
        {
            using var httpClient = _httpClientFactory.CreateClient();
            httpClient.Timeout = TimeSpan.FromSeconds(30);
            var content = new StringContent(message.Content, Encoding.UTF8, "application/json");

            // 复制元数据到请求头
            var request = new HttpRequestMessage(HttpMethod.Post, message.Target)
            {
                Content = content
            };
            foreach (var kv in message.Metadata)
                request.Headers.TryAddWithoutValidation(kv.Key, kv.Value);

            var response = await httpClient.SendAsync(request, ct);
            response.EnsureSuccessStatusCode();

            Interlocked.Increment(ref _messagesSent);
            _logger.LogInformation("[Webhook] SendAsync to {Target} → {StatusCode}", message.Target, response.StatusCode);
        }
        catch (Exception ex)
        {
            Interlocked.Increment(ref _errors);
            _lastErrorTime = DateTimeOffset.UtcNow;
            _lastError = ex.Message;
            _logger.LogError(ex, "[Webhook] SendAsync failed to {Target}", message.Target);
        }
    }

    public Task<ConnectorOperationResult> OperateAsync(
        string operation, Dictionary<string, string>? parameters = null, CancellationToken ct = default)
    {
        return operation switch
        {
            "rotate_secret" => Task.FromResult(RotateSecret()),
            _ => Task.FromResult(new ConnectorOperationResult
            {
                Success = false,
                Error = $"Unknown operation: {operation}"
            })
        };
    }

    public Task<ConnectorDiagnostics> GetDiagnosticsAsync(CancellationToken ct = default)
    {
        return Task.FromResult(new ConnectorDiagnostics
        {
            Status = _context != null ? "running" : "stopped",
            MessagesReceived = _messagesReceived,
            MessagesSent = _messagesSent,
            Errors = _errors,
            LastReceiveTime = _lastReceiveTime,
            LastErrorTime = _lastErrorTime,
            LastError = _lastError,
        });
    }

    /// <summary>
    /// 验证 webhook 请求的 HMAC-SHA256 签名。
    /// 签名算法：HMAC-SHA256(requestBody, signingSecret) → hex → "sha256=" + hex
    /// 请求头：X-Webhook-Signature 或 X-Hub-Signature-256
    /// </summary>
    public bool VerifySignature(string requestBody, string signatureHeader)
    {
        if (!SignatureVerificationEnabled)
            return true;

        if (string.IsNullOrWhiteSpace(signatureHeader))
        {
            _logger.LogWarning("[Webhook] 签名头缺失，拒绝请求");
            return false;
        }

        var expectedPrefix = "sha256=";
        var signature = signatureHeader.StartsWith(expectedPrefix, StringComparison.OrdinalIgnoreCase)
            ? signatureHeader[expectedPrefix.Length..]
            : signatureHeader;

        var computed = ComputeHmacSha256(requestBody, _signingSecret);
        var isValid = string.Equals(computed, signature, StringComparison.OrdinalIgnoreCase);

        if (!isValid)
            _logger.LogWarning("[Webhook] 签名验证失败。Expected={Expected} Computed={Computed}", signature, computed);

        return isValid;
    }

    /// <summary>
    /// 处理入站 webhook 事件 — 验证签名后构造 PuddingIngressEnvelope，
    /// 通过 OnEventReceived 回调投递给 Runtime。
    /// </summary>
    public async Task<bool> HandleIncomingAsync(
        string channelId, string body, string? signatureHeader,
        Dictionary<string, string>? headers = null, CancellationToken ct = default)
    {
        // 验证签名
        if (!string.IsNullOrWhiteSpace(signatureHeader) && !VerifySignature(body, signatureHeader))
        {
            Interlocked.Increment(ref _errors);
            _lastErrorTime = DateTimeOffset.UtcNow;
            _lastError = "Signature verification failed";
            return false;
        }

        Interlocked.Increment(ref _messagesReceived);
        _lastReceiveTime = DateTimeOffset.UtcNow;

        var metadata = headers ?? [];
        if (!metadata.ContainsKey("source_type")) metadata["source_type"] = "webhook";
        if (!metadata.ContainsKey("source_id")) metadata["source_id"] = channelId;
        if (!metadata.ContainsKey("source_name")) metadata["source_name"] = "webhook";

        var correlationId = metadata.TryGetValue("X-Session-Id", out var sid) && !string.IsNullOrWhiteSpace(sid)
            ? sid
            : null;

        var envelope = new PuddingIngressEnvelope
        {
            ChannelId = channelId,
            ChannelType = "webhook",
            UserExternalId = $"webhook:{channelId}",
            MessageText = body,
            MessageType = "webhook_event",
            CorrelationId = correlationId,
            Metadata = metadata,
        };

        if (_context?.OnEventReceived != null)
        {
            try
            {
                await _context.OnEventReceived(envelope, ct);
                _logger.LogInformation("[Webhook] 事件已投递。channel={ChannelId} bodyLen={Len}", channelId, body.Length);
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref _errors);
                _lastErrorTime = DateTimeOffset.UtcNow;
                _lastError = ex.Message;
                _logger.LogError(ex, "[Webhook] OnEventReceived 回调异常 channel={ChannelId}", channelId);
                return false;
            }
        }
        else
        {
            _logger.LogWarning("[Webhook] OnEventReceived 未设置，事件丢弃 channel={ChannelId}", channelId);
        }

        return true;
    }

    private ConnectorOperationResult RotateSecret()
    {
        _logger.LogInformation("[Webhook] rotate_secret 操作已请求（需外部更新配置）");
        return new ConnectorOperationResult
        {
            Success = true,
            Data = "Secret rotation acknowledged. Update Webhook:SigningSecret in configuration."
        };
    }

    private static string ComputeHmacSha256(string message, string secret)
    {
        var keyBytes = Encoding.UTF8.GetBytes(secret);
        var messageBytes = Encoding.UTF8.GetBytes(message);
        var hash = HMACSHA256.HashData(keyBytes, messageBytes);
        return Convert.ToHexStringLower(hash);
    }
}
