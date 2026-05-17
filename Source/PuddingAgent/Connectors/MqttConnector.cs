using System.Text;
using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Protocol;
using PuddingCode.Platform;

namespace PuddingAgent.Connectors;

/// <summary>
/// MQTT 最小连接器：连接 broker、订阅入站 topic、将消息转为 PuddingIngressEnvelope。
/// 连接器只负责消息通道；业务执行交由事件系统 → 会话层 → Agent。
/// </summary>
public sealed class MqttConnector : IPuddingConnector, IAsyncDisposable
{
    private readonly IConfiguration _configuration;
    private readonly ILogger<MqttConnector> _logger;
    private IMqttClient? _client;
    private ConnectorContext? _context;

    private long _messagesReceived;
    private long _messagesSent;
    private long _errors;
    private DateTimeOffset? _lastReceiveTime;
    private DateTimeOffset? _lastErrorTime;
    private string? _lastError;

    private string _host = "localhost";
    private int _port = 1883;
    private string? _username;
    private string? _password;
    private string _ingressTopic = "pudding/inbound/#";
    private string _defaultPublishTopic = "pudding/outbound";
    private string _clientId = $"pudding-agent-{Guid.NewGuid():N}";
    private bool _enabled;

    public ConnectorDescriptor Descriptor { get; } = new()
    {
        ConnectorId = "mqtt-001",
        ConnectorType = "mqtt",
        Protocol = "MQTT",
        Version = "1.0",
        Description = "MQTT 连接器：订阅消息并投递到事件系统，支持向 topic 发布回复",
        Capabilities = ["receive", "send", "stream"],
    };

    public MqttConnector(IConfiguration configuration, ILogger<MqttConnector> logger)
    {
        _configuration = configuration;
        _logger = logger;
        LoadOptions();
    }

    public async Task StartAsync(ConnectorContext context, CancellationToken ct = default)
    {
        _context = context;
        if (!_enabled)
        {
            _logger.LogWarning("[MQTT] Disabled by configuration (Mqtt:Enabled=false)");
            _context.Log("[MQTT] 已禁用（Mqtt:Enabled=false）");
            return;
        }

        var factory = new MqttFactory();
        _client = factory.CreateMqttClient();

        _client.ApplicationMessageReceivedAsync += async e =>
        {
            try
            {
                var payload = e.ApplicationMessage.Payload ?? Array.Empty<byte>();
                var text = Encoding.UTF8.GetString(payload);
                var topic = e.ApplicationMessage.Topic ?? "";

                Interlocked.Increment(ref _messagesReceived);
                _lastReceiveTime = DateTimeOffset.UtcNow;

                if (_context?.OnEventReceived is null)
                    return;

                var envelope = new PuddingIngressEnvelope
                {
                    ChannelId = topic,
                    ChannelType = "mqtt",
                    UserExternalId = "mqtt:broker",
                    MessageText = text,
                    MessageType = InferMessageTypeFromTopic(topic),
                    CorrelationId = TryExtractSessionId(topic),
                    Metadata = new Dictionary<string, string>
                    {
                        ["topic"] = topic,
                        ["qos"] = e.ApplicationMessage.QualityOfServiceLevel.ToString(),
                        ["retain"] = e.ApplicationMessage.Retain.ToString(),
                    },
                };

                await _context.OnEventReceived(envelope, CancellationToken.None);
                _logger.LogInformation("[MQTT] Inbound topic={Topic} len={Len}", topic, text.Length);
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref _errors);
                _lastErrorTime = DateTimeOffset.UtcNow;
                _lastError = ex.Message;
                _logger.LogWarning(ex, "[MQTT] Message receive callback failed");
            }
        };

        var optionsBuilder = new MqttClientOptionsBuilder()
            .WithTcpServer(_host, _port)
            .WithClientId(_clientId);

        if (!string.IsNullOrWhiteSpace(_username))
            optionsBuilder = optionsBuilder.WithCredentials(_username, _password);

        var options = optionsBuilder.Build();
        await _client.ConnectAsync(options, ct);

        var subscribeOptions = new MqttClientSubscribeOptionsBuilder()
            .WithTopicFilter(f =>
            {
                f.WithTopic(_ingressTopic);
                f.WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce);
            })
            .Build();

        await _client.SubscribeAsync(subscribeOptions, ct);
        _context.Log($"[MQTT] 已连接 {_host}:{_port}，订阅 topic={_ingressTopic}");
        _logger.LogInformation("[MQTT] Started host={Host}:{Port} ingressTopic={Topic}", _host, _port, _ingressTopic);
    }

    public async Task StopAsync(CancellationToken ct = default)
    {
        if (_client is not null)
        {
            try
            {
                if (_client.IsConnected)
                    await _client.DisconnectAsync();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[MQTT] Disconnect failed");
            }
        }

        _context?.Log("[MQTT] 连接器已停止。");
        _logger.LogInformation("[MQTT] Stopped. Received={Received} Sent={Sent} Errors={Errors}",
            _messagesReceived, _messagesSent, _errors);
    }

    public async Task SendAsync(ConnectorMessage message, CancellationToken ct = default)
    {
        if (_client is null || !_client.IsConnected)
        {
            _logger.LogWarning("[MQTT] Send ignored: client not connected");
            return;
        }

        var topic = string.IsNullOrWhiteSpace(message.Target) ? _defaultPublishTopic : message.Target;
        var qos = MqttQualityOfServiceLevel.AtLeastOnce;
        if (message.Metadata.TryGetValue("qos", out var qosText)
            && int.TryParse(qosText, out var qosNum))
        {
            qos = qosNum switch
            {
                <= 0 => MqttQualityOfServiceLevel.AtMostOnce,
                1 => MqttQualityOfServiceLevel.AtLeastOnce,
                _ => MqttQualityOfServiceLevel.ExactlyOnce,
            };
        }

        var mqttMessage = new MqttApplicationMessageBuilder()
            .WithTopic(topic)
            .WithPayload(message.Content)
            .WithQualityOfServiceLevel(qos)
            .Build();

        try
        {
            await _client.PublishAsync(mqttMessage, ct);
            Interlocked.Increment(ref _messagesSent);
        }
        catch (Exception ex)
        {
            Interlocked.Increment(ref _errors);
            _lastErrorTime = DateTimeOffset.UtcNow;
            _lastError = ex.Message;
            _logger.LogWarning(ex, "[MQTT] Publish failed topic={Topic}", topic);
        }
    }

    public Task<ConnectorOperationResult> OperateAsync(string operation, Dictionary<string, string>? parameters = null, CancellationToken ct = default)
    {
        return operation switch
        {
            "reload" => Task.FromResult(ReloadOptions()),
            _ => Task.FromResult(new ConnectorOperationResult { Success = false, Error = $"Unknown operation: {operation}" })
        };
    }

    public Task<ConnectorDiagnostics> GetDiagnosticsAsync(CancellationToken ct = default)
    {
        var status = _client?.IsConnected == true ? "running" : (_enabled ? "disconnected" : "disabled");
        return Task.FromResult(new ConnectorDiagnostics
        {
            Status = status,
            MessagesReceived = _messagesReceived,
            MessagesSent = _messagesSent,
            Errors = _errors,
            LastReceiveTime = _lastReceiveTime,
            LastErrorTime = _lastErrorTime,
            LastError = _lastError,
        });
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _client?.Dispose();
    }

    private void LoadOptions()
    {
        _enabled = _configuration.GetValue<bool>("Mqtt:Enabled");
        _host = _configuration["Mqtt:Host"] ?? "localhost";
        _port = _configuration.GetValue<int?>("Mqtt:Port") ?? 1883;
        _username = _configuration["Mqtt:Username"];
        _password = _configuration["Mqtt:Password"];
        _ingressTopic = _configuration["Mqtt:IngressTopic"] ?? "pudding/inbound/#";
        _defaultPublishTopic = _configuration["Mqtt:EgressTopic"] ?? "pudding/outbound";
        _clientId = _configuration["Mqtt:ClientId"] ?? _clientId;
    }

    private ConnectorOperationResult ReloadOptions()
    {
        LoadOptions();
        return new ConnectorOperationResult
        {
            Success = true,
            Data = $"Reloaded MQTT options host={_host}:{_port} ingress={_ingressTopic}",
        };
    }

    private static string InferMessageTypeFromTopic(string topic)
    {
        if (string.IsNullOrWhiteSpace(topic)) return "message";
        var segments = topic.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Length == 0 ? "message" : segments[^1];
    }

    private static string? TryExtractSessionId(string topic)
    {
        if (string.IsNullOrWhiteSpace(topic)) return null;
        // 约定：.../session/{sessionId}/...
        var seg = topic.Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < seg.Length - 1; i++)
        {
            if (string.Equals(seg[i], "session", StringComparison.OrdinalIgnoreCase))
                return seg[i + 1];
        }
        return null;
    }
}
