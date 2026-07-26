using System.Text.Encodings.Web;
using System.Text.Json;
using HarnessAgent.Core.Connectors.Feishu;
using PuddingAgent.Services;
using PuddingCode.Platform;

namespace PuddingAgent.Connectors;

/// <summary>
/// Channel-owned Feishu connector. Agent routing is resolved by the stable
/// channel reference stored in the Agent manifest.
/// </summary>
public sealed class FeishuConnector : IPuddingConnector
{
    private static readonly JsonSerializerOptions CardJsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly ILogger<FeishuConnector> _logger;
    private readonly FeishuConnectorBinding _binding;
    private readonly FeishuInboundMessageMapper _inboundMessageMapper;
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
        ILogger<FeishuConnector> logger,
        FeishuInboundMessageMapper inboundMessageMapper)
    {
        _binding = binding;
        _logger = logger;
        _inboundMessageMapper = inboundMessageMapper;
        _config = new FeishuConfig
        {
            AppId = binding.AppId,
            AppSecret = binding.AppSecret,
            Description = binding.Description,
        };
        Descriptor = new ConnectorDescriptor
        {
            ConnectorId = FeishuConnectorIdentity.ForChannel(
                binding.ChannelId ?? binding.AgentId),
            ConnectorType = "feishu",
            Protocol = "Feishu OpenAPI",
            Version = "1.1",
            Description = $"飞书机器人（Agent: {binding.AgentId}）",
            Capabilities = ["receive", "send", "stream"],
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
            if (string.Equals(
                    Get(message.Metadata, ConnectorStreamMetadata.ReplyMode),
                    ConnectorStreamMetadata.FinalizeReplyMode,
                    StringComparison.Ordinal))
            {
                await FinalizeStreamReplyAsync(message, uuid, ct);
            }
            else if (message.Metadata.TryGetValue("message_id", out var messageId)
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

    public async Task<ConnectorOperationResult> OperateAsync(
        string operation,
        Dictionary<string, string>? parameters = null,
        CancellationToken ct = default)
    {
        if (_client is null)
            throw new InvalidOperationException("Feishu connector not started");

        try
        {
            return operation switch
            {
                ConnectorStreamOperations.Create => await CreateStreamAsync(
                    parameters,
                    ct),
                ConnectorStreamOperations.Publish => await PublishStreamAsync(
                    parameters,
                    ct),
                ConnectorStreamOperations.Update => await UpdateStreamAsync(
                    parameters,
                    ct),
                ConnectorStreamOperations.Finish => await FinishStreamAsync(
                    parameters,
                    ct),
                _ => new ConnectorOperationResult
                {
                    Success = false,
                    Error = $"Unknown operation: {operation}",
                },
            };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Interlocked.Increment(ref _errors);
            _lastErrorTime = DateTimeOffset.UtcNow;
            _lastError = ex.Message;
            _logger.LogWarning(
                ex,
                "[FeishuStream] Operation failed connector={ConnectorId} operation={Operation}",
                Descriptor.ConnectorId,
                operation);
            return new ConnectorOperationResult
            {
                Success = false,
                Error = ex.Message,
            };
        }
    }

    private async Task<ConnectorOperationResult> CreateStreamAsync(
        IReadOnlyDictionary<string, string>? parameters,
        CancellationToken ct)
    {
        var initial = Get(parameters, ConnectorStreamParameters.Content);
        var card = BuildStreamingCard(initial ?? "正在思考…");
        var result = await _client!.CreateCardAsync(card, ct);
        if (result.Code != 0 || string.IsNullOrWhiteSpace(result.Data?.CardId))
        {
            return Failed(
                $"Feishu CardKit create failed: code={result.Code}, msg={result.Msg}");
        }

        return new ConnectorOperationResult
        {
            Success = true,
            Data = result.Data.CardId,
        };
    }

    private async Task<ConnectorOperationResult> PublishStreamAsync(
        IReadOnlyDictionary<string, string>? parameters,
        CancellationToken ct)
    {
        var externalMessageId = Require(
            parameters,
            ConnectorStreamParameters.ExternalMessageId);
        var cardId = Require(parameters, ConnectorStreamParameters.ResourceId);
        var uuid = Get(parameters, ConnectorStreamParameters.Uuid);
        var result = await _client!.ReplyCardAsync(
            externalMessageId,
            cardId,
            uuid,
            ct);
        if (result.Code != 0 || string.IsNullOrWhiteSpace(result.Data?.MessageId))
        {
            return Failed(
                $"Feishu CardKit publish failed: code={result.Code}, msg={result.Msg}");
        }

        Interlocked.Increment(ref _messagesSent);
        return new ConnectorOperationResult
        {
            Success = true,
            Data = result.Data.MessageId,
        };
    }

    private async Task<ConnectorOperationResult> UpdateStreamAsync(
        IReadOnlyDictionary<string, string>? parameters,
        CancellationToken ct)
    {
        var result = await _client!.UpdateCardElementContentAsync(
            Require(parameters, ConnectorStreamParameters.ResourceId),
            Require(parameters, ConnectorStreamParameters.ElementId),
            Get(parameters, ConnectorStreamParameters.Content) ?? "...",
            ParseSequence(parameters),
            Get(parameters, ConnectorStreamParameters.Uuid),
            ct);
        return result.Code == 0
            ? new ConnectorOperationResult { Success = true }
            : Failed(
                $"Feishu CardKit update failed: code={result.Code}, msg={result.Msg}");
    }

    private async Task<ConnectorOperationResult> FinishStreamAsync(
        IReadOnlyDictionary<string, string>? parameters,
        CancellationToken ct)
    {
        var content = Get(parameters, ConnectorStreamParameters.Content) ?? "...";
        var result = await _client!.UpdateCardAsync(
            Require(parameters, ConnectorStreamParameters.ResourceId),
            BuildCompletedCard(
                content,
                Get(parameters, ConnectorStreamParameters.Summary)
                ?? BuildSummary(content)),
            sequence: ParseSequence(parameters),
            uuid: Get(parameters, ConnectorStreamParameters.Uuid),
            ct);
        return result.Code == 0
            ? new ConnectorOperationResult { Success = true }
            : Failed(
                $"Feishu CardKit finish failed: code={result.Code}, msg={result.Msg}");
    }

    private async Task FinalizeStreamReplyAsync(
        ConnectorMessage message,
        string? uuid,
        CancellationToken ct)
    {
        var cardId = Require(
            message.Metadata,
            ConnectorStreamMetadata.ResourceId);
        var elementId = Require(
            message.Metadata,
            ConnectorStreamMetadata.ElementId);
        var contentSequence = ParsePositiveInt(
            Require(message.Metadata, ConnectorStreamMetadata.ContentSequence),
            ConnectorStreamMetadata.ContentSequence);
        var finishSequence = ParsePositiveInt(
            Require(message.Metadata, ConnectorStreamMetadata.FinishSequence),
            ConnectorStreamMetadata.FinishSequence);

        try
        {
            var update = await _client!.UpdateCardElementContentAsync(
                cardId,
                elementId,
                message.Content,
                contentSequence,
                SuffixUuid(uuid, "content"),
                ct);
            if (update.Code != 0)
            {
                throw new InvalidOperationException(
                    $"Feishu CardKit final content failed: code={update.Code}, msg={update.Msg}");
            }

            var finish = await _client.UpdateCardAsync(
                cardId,
                BuildCompletedCard(
                    message.Content,
                    BuildSummary(message.Content)),
                sequence: finishSequence,
                uuid: SuffixUuid(uuid, "finish"),
                ct);
            if (finish.Code != 0)
            {
                throw new InvalidOperationException(
                    $"Feishu CardKit final settings failed: code={finish.Code}, msg={finish.Msg}");
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception cardError)
        {
            var sourceMessageId = Get(message.Metadata, "message_id")
                ?? throw new InvalidOperationException(
                    "Feishu CardKit finalization failed and no source message is available for text fallback.",
                    cardError);
            _logger.LogWarning(
                cardError,
                "[FeishuStream] Final card update failed; falling back to text connector={ConnectorId}",
                Descriptor.ConnectorId);
            var fallback = await _client!.ReplyTextAsync(
                sourceMessageId,
                message.Content,
                uuid,
                ct);
            if (fallback.Code != 0)
            {
                throw new InvalidOperationException(
                    $"Feishu stream text fallback failed: code={fallback.Code}, msg={fallback.Msg}",
                    cardError);
            }
        }
    }

    private static string BuildStreamingCard(string initialText)
        => JsonSerializer.Serialize(new
        {
            schema = "2.0",
            config = new
            {
                streaming_mode = true,
                update_multi = true,
                summary = new { content = "正在生成回复…" },
                streaming_config = new
                {
                    print_frequency_ms = new { @default = 70 },
                    print_step = new { @default = 1 },
                    print_strategy = "fast",
                },
            },
            body = new
            {
                elements = new object[]
                {
                    new
                    {
                        tag = "markdown",
                        element_id = "stream_md",
                        content = initialText,
                    },
                },
            },
        }, CardJsonOptions);

    private static string BuildCompletedCard(string content, string summary)
        => JsonSerializer.Serialize(new
        {
            schema = "2.0",
            config = new
            {
                streaming_mode = false,
                update_multi = true,
                summary = new { content = summary },
            },
            body = new
            {
                elements = new object[]
                {
                    new
                    {
                        tag = "markdown",
                        element_id = "stream_md",
                        content,
                    },
                },
            },
        }, CardJsonOptions);

    private static string BuildSummary(string content)
    {
        var oneLine = string.Join(
            ' ',
            content.Split(
                (char[]?)null,
                StringSplitOptions.RemoveEmptyEntries));
        return oneLine.Length <= 50 ? oneLine : $"{oneLine[..49]}…";
    }

    private static string? SuffixUuid(string? value, string suffix)
        => string.IsNullOrWhiteSpace(value)
            ? null
            : $"{value}-{suffix}";

    private static ConnectorOperationResult Failed(string error)
        => new()
        {
            Success = false,
            Error = error,
        };

    private static int ParseSequence(
        IReadOnlyDictionary<string, string>? parameters)
        => ParsePositiveInt(
            Require(parameters, ConnectorStreamParameters.Sequence),
            ConnectorStreamParameters.Sequence);

    private static int ParsePositiveInt(string value, string name)
        => int.TryParse(value, out var parsed) && parsed > 0
            ? parsed
            : throw new ArgumentException($"Invalid positive integer '{name}'.");

    private static string Require(
        IReadOnlyDictionary<string, string>? parameters,
        string name)
        => Get(parameters, name)
           ?? throw new ArgumentException($"Missing connector parameter '{name}'.");

    private static string? Get(
        IReadOnlyDictionary<string, string>? parameters,
        string name)
        => parameters is not null
           && parameters.TryGetValue(name, out var value)
           && !string.IsNullOrWhiteSpace(value)
            ? value
            : null;

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

        if (_client is null)
            throw new InvalidOperationException("Feishu connector not started");

        var envelope = await _inboundMessageMapper.MapAsync(
            _binding,
            Descriptor.ConnectorId,
            evt,
            _client,
            ct);
        var chatId = envelope.ExternalConversationId!;
        var messageId = envelope.ExternalMessageId!;

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
    string? Description,
    string? ChannelId = null);
