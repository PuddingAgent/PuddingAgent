using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using PuddingCode.Platform;

namespace PuddingAgent.Connectors;

/// <summary>
/// WebSocket 连接器 — 通过 WebSocket 协议接收和发送消息。
/// 
/// 能力：Receive + Send + Stream
/// 入站：客户端发送 {"type":"chat","content":"Hello","source":"ws-user-xxx"}
/// 出站：通过 ConnectorHost 转发 SSE 帧到对应 WS 连接
/// 
/// 鉴权：首条消息携带认证帧 {"type":"auth","signature":"...","timestamp":...}
///       SM2 签名验证在 GatewayAuthService 完成。
/// </summary>
public sealed class WebSocketConnector : IPuddingConnector, IDisposable
{
    private readonly ILogger<WebSocketConnector> _logger;
    private readonly ConcurrentDictionary<string, WebSocketConnection> _connections = new();
    private ConnectorContext? _context;

    private long _messagesReceived;
    private long _messagesSent;
    private long _errors;
    private DateTimeOffset? _lastReceiveTime;
    private DateTimeOffset? _lastErrorTime;
    private string? _lastError;

    public ConnectorDescriptor Descriptor { get; } = new()
    {
        ConnectorId = "websocket-001",
        ConnectorType = "websocket",
        Protocol = "WebSocket",
        Version = "1.0",
        Description = "WebSocket 双向通道：接收客户端消息并推送 Agent 回复",
        Capabilities = ["receive", "send", "stream"],
    };

    public WebSocketConnector(ILogger<WebSocketConnector> logger)
    {
        _logger = logger;
    }

    public Task StartAsync(ConnectorContext context, CancellationToken ct = default)
    {
        _context = context;
        _context.Log("[WebSocket] 连接器已启动，等待 WebSocket 连接...");
        _logger.LogInformation("[WebSocket] Connector started");
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken ct = default)
    {
        // 关闭所有活跃连接
        foreach (var (id, conn) in _connections)
        {
            try
            {
                await conn.Socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Server shutting down", ct);
            }
            catch { /* ignore */ }
        }
        _connections.Clear();
        _context?.Log("[WebSocket] 连接器已停止。");
        _logger.LogInformation("[WebSocket] Connector stopped. Received={Received} Sent={Sent} Errors={Errors}",
            _messagesReceived, _messagesSent, _errors);
    }

    /// <summary>
    /// 接受并管理一个 WebSocket 连接。
    /// 由 ASP.NET Core 中间件（或 WebSocketController）调用。
    /// </summary>
    public async Task AcceptAsync(WebSocket socket, string connectionId, string? authenticatedUser, CancellationToken ct = default)
    {
        var conn = new WebSocketConnection(socket, connectionId, authenticatedUser);
        _connections[connectionId] = conn;
        _logger.LogWarning("[WebSocket] Connection accepted: {Id} user={User}", connectionId, authenticatedUser ?? "(anonymous)");

        try
        {
            var buffer = new byte[4096];
            while (socket.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                var result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), ct);

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Bye", ct);
                    break;
                }

                if (result.MessageType == WebSocketMessageType.Text)
                {
                    conn.ReceiveBuffer.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));

                    if (result.EndOfMessage)
                    {
                        var message = conn.ReceiveBuffer.ToString();
                        conn.ReceiveBuffer.Clear();
                        await HandleIncomingMessageAsync(conn, message, ct);
                    }
                }
            }
        }
        catch (WebSocketException ex)
        {
            _logger.LogWarning(ex, "[WebSocket] Connection error: {Id}", connectionId);
        }
        catch (OperationCanceledException) { /* shutdown */ }
        finally
        {
            _connections.TryRemove(connectionId, out _);
            _logger.LogInformation("[WebSocket] Connection closed: {Id}", connectionId);
        }
    }

    private async Task HandleIncomingMessageAsync(WebSocketConnection conn, string raw, CancellationToken ct)
    {
        try
        {
            Interlocked.Increment(ref _messagesReceived);
            _lastReceiveTime = DateTimeOffset.UtcNow;

            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;
            var msgType = root.TryGetProperty("type", out var t) ? t.GetString() : "chat";

            if (msgType == "auth")
            {
                // 认证帧：GatewayAuthService 已在连接握手阶段处理，此处仅记录
                var user = root.TryGetProperty("user", out var u) ? u.GetString() : "?";
                conn.AuthenticatedUser = user;
                _logger.LogInformation("[WebSocket] Auth success: {Id} user={User}", conn.ConnectionId, user);
                return;
            }

            // 业务消息 → PuddingIngressEnvelope → OnEventReceived
            var content = root.TryGetProperty("content", out var c) ? c.GetString() : raw;
            var sourceId = root.TryGetProperty("source", out var s) ? s.GetString() : conn.ConnectionId;
            var sessionId = root.TryGetProperty("sessionId", out var sid) ? sid.GetString() : null;

            var envelope = new PuddingIngressEnvelope
            {
                ChannelId = conn.ConnectionId,
                ChannelType = "websocket",
                UserExternalId = conn.AuthenticatedUser ?? "anonymous",
                MessageText = content,
                MessageType = msgType,
                CorrelationId = sessionId,
                Timestamp = DateTimeOffset.UtcNow,
                Metadata = new Dictionary<string, string>
                {
                    ["connectionId"] = conn.ConnectionId,
                    ["authenticatedUser"] = conn.AuthenticatedUser ?? "",
                    ["sourceId"] = sourceId,
                },
            };

            if (_context?.OnEventReceived is not null)
                await _context.OnEventReceived(envelope, ct);

            _logger.LogWarning("[WebSocket] Message from {Id}: {Content}", conn.ConnectionId,
                content.Length > 80 ? content[..80] + "..." : content);
        }
        catch (JsonException ex)
        {
            Interlocked.Increment(ref _errors);
            _lastErrorTime = DateTimeOffset.UtcNow;
            _lastError = ex.Message;
            _logger.LogWarning(ex, "[WebSocket] Invalid JSON from {Id}", conn.ConnectionId);
        }
    }

    /// <summary>向指定 WebSocket 连接发送消息。</summary>
    public async Task SendAsync(ConnectorMessage message, CancellationToken ct = default)
    {
        var connectionId = message.Target; // Target = connectionId
        if (!_connections.TryGetValue(connectionId, out var conn))
        {
            _logger.LogWarning("[WebSocket] Send target not found: {Target}", connectionId);
            return;
        }

        if (conn.Socket.State != WebSocketState.Open) return;

        try
        {
            var bytes = Encoding.UTF8.GetBytes(message.Content);
            await conn.Socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, ct);
            Interlocked.Increment(ref _messagesSent);
        }
        catch (Exception ex)
        {
            Interlocked.Increment(ref _errors);
            _lastErrorTime = DateTimeOffset.UtcNow;
            _lastError = ex.Message;
            _logger.LogWarning(ex, "[WebSocket] Send failed to {Id}", connectionId);
        }
    }

    /// <summary>广播消息到所有连接。</summary>
    public async Task BroadcastAsync(string content, CancellationToken ct = default)
    {
        var tasks = _connections.Keys.Select(id =>
            SendAsync(new ConnectorMessage { Target = id, Content = content }, ct));
        await Task.WhenAll(tasks);
    }

    public Task<ConnectorOperationResult> OperateAsync(string operation, Dictionary<string, string>? parameters, CancellationToken ct = default)
    {
        return operation switch
        {
            "list_connections" => Task.FromResult(new ConnectorOperationResult
            {
                Success = true,
                Data = JsonSerializer.Serialize(_connections.Select(kv => new
                {
                    connectionId = kv.Key,
                    user = kv.Value.AuthenticatedUser,
                    state = kv.Value.Socket.State.ToString(),
                })),
            }),
            _ => Task.FromResult(new ConnectorOperationResult { Success = false, Error = $"Unknown operation: {operation}" }),
        };
    }

    public Task<ConnectorDiagnostics> GetDiagnosticsAsync(CancellationToken ct = default)
    {
        return Task.FromResult(new ConnectorDiagnostics
        {
            Status = "running",
            MessagesReceived = _messagesReceived,
            MessagesSent = _messagesSent,
            Errors = _errors,
            LastReceiveTime = _lastReceiveTime,
            LastErrorTime = _lastErrorTime,
            LastError = _lastError,
        });
    }

    public void Dispose()
    {
        foreach (var (_, conn) in _connections)
            conn.Socket.Dispose();
        _connections.Clear();
    }

    private sealed class WebSocketConnection
    {
        public WebSocket Socket { get; }
        public string ConnectionId { get; }
        public string? AuthenticatedUser { get; set; }
        public StringBuilder ReceiveBuffer { get; } = new();

        public WebSocketConnection(WebSocket socket, string connectionId, string? authenticatedUser)
        {
            Socket = socket;
            ConnectionId = connectionId;
            AuthenticatedUser = authenticatedUser;
        }
    }
}
