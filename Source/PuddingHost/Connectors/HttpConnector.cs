using PuddingCode.Platform;

namespace PuddingAgent.Connectors;

/// <summary>
/// HTTP 最小连接器：由 Controller 调用 HandleIncomingAsync 投递入站消息。
/// 该连接器不直接承载监听 socket，职责是协议边界转换与诊断计数。
/// </summary>
public sealed class HttpConnector : IPuddingConnector
{
    private readonly ILogger<HttpConnector> _logger;
    private ConnectorContext? _context;

    private long _messagesReceived;
    private long _messagesSent;
    private long _errors;
    private DateTimeOffset? _lastReceiveTime;
    private DateTimeOffset? _lastErrorTime;
    private string? _lastError;

    public ConnectorDescriptor Descriptor { get; } = new()
    {
        ConnectorId = "http-001",
        ConnectorType = "http",
        Protocol = "HTTP",
        Version = "1.0",
        Description = "HTTP 入站连接器（Controller 调用）",
        Capabilities = ["receive"],
    };

    public HttpConnector(ILogger<HttpConnector> logger)
    {
        _logger = logger;
    }

    public Task StartAsync(ConnectorContext context, CancellationToken ct = default)
    {
        _context = context;
        _context.Log("[HTTP] Connector started");
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct = default)
    {
        _context?.Log("[HTTP] Connector stopped");
        return Task.CompletedTask;
    }

    public Task SendAsync(ConnectorMessage message, CancellationToken ct = default)
    {
        // 最小实现阶段：HTTP connector 仅处理 ingress，不承担主动出站。
        _logger.LogDebug("[HTTP] Send is not implemented yet target={Target}", message.Target);
        return Task.CompletedTask;
    }

    public Task<ConnectorOperationResult> OperateAsync(string operation, Dictionary<string, string>? parameters = null, CancellationToken ct = default)
    {
        return Task.FromResult(new ConnectorOperationResult
        {
            Success = false,
            Error = $"Unknown operation: {operation}",
        });
    }

    public Task<ConnectorDiagnostics> GetDiagnosticsAsync(CancellationToken ct = default)
    {
        return Task.FromResult(new ConnectorDiagnostics
        {
            Status = _context is null ? "stopped" : "running",
            MessagesReceived = _messagesReceived,
            MessagesSent = _messagesSent,
            Errors = _errors,
            LastReceiveTime = _lastReceiveTime,
            LastErrorTime = _lastErrorTime,
            LastError = _lastError,
        });
    }

    public async Task<string> HandleIncomingAsync(
        string channelId,
        string userExternalId,
        string messageText,
        string? sessionId,
        string? messageType,
        Dictionary<string, string>? metadata,
        CancellationToken ct = default)
    {
        if (_context is null)
            throw new InvalidOperationException("HTTP connector not started");

        var resolvedSessionId = string.IsNullOrWhiteSpace(sessionId)
            ? $"http-session-{Guid.NewGuid():N}"[..22]
            : sessionId;

        var envelope = new PuddingIngressEnvelope
        {
            ChannelId = channelId,
            ChannelType = "http",
            UserExternalId = string.IsNullOrWhiteSpace(userExternalId) ? "http:anonymous" : userExternalId,
            MessageText = messageText,
            MessageType = string.IsNullOrWhiteSpace(messageType) ? "chat" : messageType,
            CorrelationId = resolvedSessionId,
            Metadata = metadata ?? new Dictionary<string, string>(),
        };

        try
        {
            await _context.OnEventReceived(envelope, ct);
            Interlocked.Increment(ref _messagesReceived);
            _lastReceiveTime = DateTimeOffset.UtcNow;
            _logger.LogInformation("[HTTP] Inbound accepted channelId={ChannelId} sessionId={SessionId}", channelId, resolvedSessionId);
            return resolvedSessionId;
        }
        catch (Exception ex)
        {
            Interlocked.Increment(ref _errors);
            _lastErrorTime = DateTimeOffset.UtcNow;
            _lastError = ex.Message;
            _logger.LogWarning(ex, "[HTTP] Inbound failed channelId={ChannelId} sessionId={SessionId}", channelId, resolvedSessionId);
            throw;
        }
    }
}
