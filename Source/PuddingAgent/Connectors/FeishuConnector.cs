using System.Text.Json;
using HarnessAgent.Core.Connectors.Feishu;
using PuddingAgent.Services;
using PuddingCode.Platform;

namespace PuddingAgent.Connectors;

/// <summary>
/// Agent-owned Feishu connector. One instance represents exactly one Agent ↔
/// Feishu robot binding from the Agent manifest.
/// </summary>
public sealed class FeishuConnector : IPuddingConnector
{
    private readonly ILogger<FeishuConnector> _logger;
    private readonly FeishuConnectorBinding _binding;
    private readonly FeishuConfig _config;
    private ConnectorContext? _context;
    private FeishuClient? _client;
    private FeishuWebSocket? _webSocket;

    private long _messagesReceived;
    private long _messagesSent;
    private long _errors;
    private DateTimeOffset? _lastReceiveTime;
    private DateTimeOffset? _lastErrorTime;
    private string? _lastError;

    public ConnectorDescriptor Descriptor { get; }

    public FeishuConnector(
        FeishuConnectorBinding binding,
        ILogger<FeishuConnector> logger)
    {
        _binding = binding;
        _logger = logger;
        _config = new FeishuConfig
        {
            AppId = binding.AppId,
            AppSecret = binding.AppSecret,
            Description = binding.Description,
        };
        Descriptor = new ConnectorDescriptor
        {
            ConnectorId = FeishuConnectorIdentity.ForAgent(binding.AgentId),
            ConnectorType = "feishu",
            Protocol = "Feishu OpenAPI",
            Version = "1.0",
            Description = $"飞书机器人（Agent: {binding.AgentId}）",
            Capabilities = ["receive", "send"],
        };
    }

    public async Task StartAsync(
        ConnectorContext context,
        CancellationToken ct = default)
    {
        _context = context;
        _client = new FeishuClient(_config);
        context.Log("[Feishu] Connector starting WebSocket long connection...");

        try
        {
            _webSocket = new FeishuWebSocket(_config);
            _webSocket.OnEvent += async evt =>
            {
                try
                {
                    await HandleIncomingAsync(evt, CancellationToken.None);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(
                        ex,
                        "[Feishu] Error handling incoming event agent={AgentId}",
                        _binding.AgentId);
                    // The WebSocket layer turns handler failures into a non-200
                    // event ACK. Swallowing here would acknowledge a message
                    // that was not durably accepted by the gateway.
                    throw;
                }
            };
            await _webSocket.ConnectAsync(ct);
            context.Log("[Feishu] WebSocket connected successfully");
        }
        catch (Exception ex)
        {
            // 飞书事件没有可用的轮询拉取 API。把无接收能力的连接器伪装成
            // running 会静默丢消息，因此明确进入 faulted。
            _logger.LogError(
                ex,
                "[Feishu] WebSocket connection failed agent={AgentId}",
                _binding.AgentId);
            _webSocket?.Dispose();
            _webSocket = null;
            _client.Dispose();
            _client = null;
            throw;
        }
    }

    public async Task StopAsync(CancellationToken ct = default)
    {
        if (_webSocket is not null)
        {
            await _webSocket.DisconnectAsync();
            _webSocket.Dispose();
            _webSocket = null;
        }

        _client?.Dispose();
        _client = null;
        _context?.Log("[Feishu] Connector stopped");
        _context = null;
    }

    public async Task SendAsync(
        ConnectorMessage message,
        CancellationToken ct = default)
    {
        if (_client is null)
            throw new InvalidOperationException("Feishu connector not started");

        try
        {
            var uuid = message.Metadata.TryGetValue("uuid", out var stableUuid)
                ? stableUuid
                : null;
            if (message.Metadata.TryGetValue("message_id", out var messageId)
                && !string.IsNullOrWhiteSpace(messageId))
            {
                var replyResult = await _client.ReplyTextAsync(
                    messageId,
                    message.Content,
                    uuid,
                    ct);
                if (replyResult.Code != 0)
                {
                    throw new InvalidOperationException(
                        $"Feishu reply failed: code={replyResult.Code}, msg={replyResult.Msg}");
                }
            }
            else
            {
                var openId = message.Metadata.TryGetValue("open_id", out var oid)
                    ? oid
                    : message.Target;
                if (string.IsNullOrWhiteSpace(openId))
                {
                    throw new ArgumentException(
                        "No open_id or target specified for Feishu message");
                }

                var content = JsonSerializer.Serialize(
                    new { text = message.Content });
                var sendResult = await _client.SendMessageAsync(
                    openId,
                    "text",
                    content,
                    uuid,
                    ct);
                if (sendResult.Code != 0)
                {
                    throw new InvalidOperationException(
                        $"Feishu send failed: code={sendResult.Code}, msg={sendResult.Msg}");
                }
            }

            Interlocked.Increment(ref _messagesSent);
            _logger.LogInformation(
                "[Feishu] Message sent connector={ConnectorId} agent={AgentId}",
                Descriptor.ConnectorId,
                _binding.AgentId);
        }
        catch (Exception ex)
        {
            Interlocked.Increment(ref _errors);
            _lastErrorTime = DateTimeOffset.UtcNow;
            _lastError = ex.Message;
            _logger.LogWarning(
                ex,
                "[Feishu] Send failed connector={ConnectorId} agent={AgentId}",
                Descriptor.ConnectorId,
                _binding.AgentId);
            throw;
        }
    }

    public Task<ConnectorOperationResult> OperateAsync(
        string operation,
        Dictionary<string, string>? parameters = null,
        CancellationToken ct = default)
        => Task.FromResult(new ConnectorOperationResult
        {
            Success = false,
            Error = $"Unknown operation: {operation}",
        });

    public Task<ConnectorDiagnostics> GetDiagnosticsAsync(
        CancellationToken ct = default)
        => Task.FromResult(new ConnectorDiagnostics
        {
            Status = _context is null
                ? "stopped"
                : (_webSocket?.IsConnected == true
                    ? "connected"
                    : "disconnected"),
            MessagesReceived = _messagesReceived,
            MessagesSent = _messagesSent,
            Errors = _errors,
            LastReceiveTime = _lastReceiveTime,
            LastErrorTime = _lastErrorTime,
            LastError = _lastError,
        });

    /// <summary>
    /// Maps a Feishu event to a typed gateway envelope. Internal connector/channel
    /// identity is deliberately separate from Feishu chat_id/message_id.
    /// </summary>
    public async Task HandleIncomingAsync(
        FeishuEvent evt,
        CancellationToken ct = default)
    {
        if (_context is null)
            throw new InvalidOperationException("Feishu connector not started");

        if (!string.IsNullOrWhiteSpace(evt.Header?.EventType)
            && !string.Equals(
                evt.Header.EventType,
                "im.message.receive_v1",
                StringComparison.Ordinal))
        {
            _logger.LogDebug(
                "[Feishu] Ignored unsupported event connector={ConnectorId} eventType={EventType}",
                Descriptor.ConnectorId,
                evt.Header.EventType);
            return;
        }

        var text = evt.ExtractText();
        var senderId = evt.ExtractSenderId() ?? "feishu:anonymous";
        var chatId = evt.ExtractChatId();
        var messageId = evt.ExtractMessageId();

        if (string.IsNullOrWhiteSpace(chatId))
            throw new InvalidOperationException(
                "Feishu inbound message is missing chat_id.");
        if (string.IsNullOrWhiteSpace(messageId))
            throw new InvalidOperationException(
                "Feishu inbound message is missing message_id.");

        var envelope = new PuddingIngressEnvelope
        {
            ConnectorId = Descriptor.ConnectorId,
            WorkspaceId = _binding.WorkspaceId,
            AgentId = _binding.AgentId,
            ChannelId = "feishu",
            ChannelType = "feishu",
            UserExternalId = senderId,
            MessageText = text,
            MessageType = "chat",
            ExternalConversationId = chatId,
            ExternalMessageId = messageId,
            CorrelationId = chatId,
            Metadata = new Dictionary<string, string>
            {
                ["source"] = "feishu",
                ["message_id"] = messageId,
                ["chat_id"] = chatId,
                ["sender_id"] = senderId,
            },
        };

        try
        {
            await _context.OnEventReceived(envelope, ct);
            Interlocked.Increment(ref _messagesReceived);
            _lastReceiveTime = DateTimeOffset.UtcNow;
            _logger.LogInformation(
                "[Feishu] Inbound accepted connector={ConnectorId} agent={AgentId} chat={ChatId} message={MessageId}",
                Descriptor.ConnectorId,
                _binding.AgentId,
                chatId,
                messageId);
        }
        catch (Exception ex)
        {
            Interlocked.Increment(ref _errors);
            _lastErrorTime = DateTimeOffset.UtcNow;
            _lastError = ex.Message;
            _logger.LogWarning(
                ex,
                "[Feishu] Inbound failed connector={ConnectorId} agent={AgentId} chat={ChatId} message={MessageId}",
                Descriptor.ConnectorId,
                _binding.AgentId,
                chatId,
                messageId);
            throw;
        }
    }
}

public sealed record FeishuConnectorBinding(
    string AgentId,
    string WorkspaceId,
    string AppId,
    string AppSecret,
    string? Description);
