using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace HarnessAgent.Core.Connectors.Feishu;

/// <summary>
/// Feishu WebSocket long-connection client following the official SDK wire
/// contract: endpoint discovery, pbbp2 protobuf frames, fragment merge,
/// event acknowledgement, ping and reconnect.
/// </summary>
public sealed class FeishuWebSocket : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
    };
    private static readonly JsonSerializerOptions EndpointJsonOptions = new();

    private readonly FeishuConfig _config;
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly ConcurrentDictionary<string, FragmentSet> _fragments = new();
    private ClientWebSocket? _ws;
    private CancellationTokenSource? _cts;
    private Task? _receiveLoop;
    private Task? _pingLoop;
    private int _serviceId;
    private int _pingIntervalSeconds = 120;
    private bool _disposed;

    private const string Domain = "https://open.feishu.cn";
    private const string UserAgent = "PuddingAgent/1.0 FeishuConnector/1.0";

    // 端点发现走默认 HttpClient.Timeout=100s：外网黑洞（被墙/代理挂起）会把
    // 连接器卡在 Starting/Faulted-retry 长达 100 秒。显式收紧到 15s。
    private static readonly TimeSpan EndpointTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan SocketConnectTimeout = TimeSpan.FromSeconds(15);

    /// <summary>Async event handler; completion is acknowledged to Feishu.</summary>
    public event Func<FeishuEvent, Task>? OnEvent;
    /// <summary>Text-only convenience callback used by the manual harness.</summary>
    public event Func<string, string, string, string, Task>? OnTextMessage;
    public event Action<bool>? OnConnectionChanged;
    /// <summary>Credential-free protocol diagnostics for smoke tests.</summary>
    public event Action<string>? OnDiagnostic;

    public bool IsConnected => _ws?.State == WebSocketState.Open;

    public FeishuWebSocket(FeishuConfig config, HttpClient? http = null)
    {
        _config = config;
        _http = http ?? new HttpClient { Timeout = EndpointTimeout };
        _ownsHttp = http is null;
    }

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (IsConnected)
            return;

        _cts?.Dispose();
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        await ConnectSocketAsync(_cts.Token);
        _receiveLoop = Task.Run(() => ReceiveLoopAsync(_cts.Token));
        _pingLoop = Task.Run(() => PingLoopAsync(_cts.Token));
    }

    private async Task ConnectSocketAsync(CancellationToken ct)
    {
        var wsUrl = await GetConnectionUrlAsync(ct);
        var socket = new ClientWebSocket();
        try
        {
            using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            connectCts.CancelAfter(SocketConnectTimeout);
            await socket.ConnectAsync(new Uri(wsUrl), connectCts.Token);
        }
        catch
        {
            socket.Dispose();
            throw;
        }

        var previous = Interlocked.Exchange(ref _ws, socket);
        previous?.Dispose();
        try
        {
            // The official SDK sends the first ping immediately after WebSocket
            // open. Waiting for the periodic interval leaves the socket open but
            // not ready to receive events during short-lived clients/smoke tests.
            await SendFrameAsync(ProtobufFrame.NewPing(_serviceId), ct);
            Trace(
                $"initial ping sent service_id={_serviceId} interval_seconds={_pingIntervalSeconds}");
        }
        catch
        {
            MarkDisconnected();
            throw;
        }
        OnConnectionChanged?.Invoke(true);
    }

    private async Task<string> GetConnectionUrlAsync(CancellationToken ct)
    {
        using var request = CreateEndpointRequest(_config);

        using var response = await _http.SendAsync(request, ct);
        var json = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"Feishu WS endpoint HTTP {(int)response.StatusCode}: {json.Truncate(200)}");
        }

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var code = root.GetProperty("code").GetInt32();
        if (code != 0)
        {
            var message = root.TryGetProperty("msg", out var msg)
                ? msg.GetString()
                : "unknown";
            throw new InvalidOperationException(
                $"Feishu WS endpoint code={code} msg={message}");
        }

        var data = root.GetProperty("data");
        var url = data.GetProperty("URL").GetString()
            ?? throw new InvalidOperationException(
                "Feishu WS endpoint returned no URL.");
        if (data.TryGetProperty("ClientConfig", out var clientConfig)
            && clientConfig.TryGetProperty("PingInterval", out var pingInterval))
        {
            _pingIntervalSeconds = Math.Max(5, pingInterval.GetInt32());
        }

        var uri = new Uri(url);
        foreach (var pair in uri.Query.TrimStart('?').Split(
                     '&',
                     StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = pair.Split('=', 2);
            if (parts.Length == 2
                && string.Equals(
                    parts[0],
                    "service_id",
                    StringComparison.Ordinal)
                && int.TryParse(
                    Uri.UnescapeDataString(parts[1]),
                    out var serviceId))
            {
                _serviceId = serviceId;
            }
        }

        Trace(
            $"endpoint discovered service_id={_serviceId} interval_seconds={_pingIntervalSeconds}");

        return url;
    }

    internal static HttpRequestMessage CreateEndpointRequest(
        FeishuConfig config)
    {
        var body = new { AppID = config.AppId, AppSecret = config.AppSecret };
        var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"{Domain}/callback/ws/endpoint")
        {
            Content = JsonContent.Create(body, options: EndpointJsonOptions),
        };
        request.Headers.Add("locale", "zh");
        request.Headers.UserAgent.ParseAdd(UserAgent);
        return request;
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        var reconnectAttempt = 0;
        while (!ct.IsCancellationRequested)
        {
            if (!IsConnected)
            {
                reconnectAttempt++;
                var delay = TimeSpan.FromSeconds(
                    Math.Min(30, Math.Pow(2, Math.Min(5, reconnectAttempt - 1))));
                try
                {
                    await Task.Delay(delay, ct);
                    await ConnectSocketAsync(ct);
                    reconnectAttempt = 0;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch
                {
                    continue;
                }
            }

            try
            {
                var message = await ReceiveMessageAsync(ct);
                if (message is null)
                {
                    MarkDisconnected();
                    continue;
                }

                if (message.Type == WebSocketMessageType.Binary)
                {
                    var frame = ProtobufFrame.Parse(message.Payload);
                    Trace(
                        $"frame received method={frame.Method} service={frame.Service} type={frame.GetHeader("type") ?? "-"} message_id={frame.GetHeader("message_id") ?? "-"} bytes={frame.Payload.Length}");
                    await HandleFrameAsync(frame, ct);
                }
                else
                {
                    Trace(
                        $"ignored websocket message type={message.Type} bytes={message.Payload.Length}");
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (WebSocketException)
            {
                MarkDisconnected();
            }
            catch (InvalidDataException ex)
            {
                // A malformed frame is isolated; the connection remains usable.
                Trace($"invalid protobuf frame: {ex.Message}");
            }
        }

        MarkDisconnected();
    }

    private async Task<ReceivedMessage?> ReceiveMessageAsync(CancellationToken ct)
    {
        var socket = _ws;
        if (socket is null || socket.State != WebSocketState.Open)
            return null;

        using var buffer = new MemoryStream();
        var chunk = new byte[64 * 1024];
        WebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(chunk, ct);
            if (result.MessageType == WebSocketMessageType.Close)
                return null;
            if (result.Count > 0)
                buffer.Write(chunk, 0, result.Count);
        }
        while (!result.EndOfMessage);

        return new ReceivedMessage(result.MessageType, buffer.ToArray());
    }

    private async Task PingLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(
                    TimeSpan.FromSeconds(_pingIntervalSeconds),
                    ct);
                if (IsConnected)
                    await SendFrameAsync(ProtobufFrame.NewPing(_serviceId), ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (WebSocketException)
            {
                MarkDisconnected();
            }
        }
    }

    private async Task HandleFrameAsync(
        ProtobufFrame frame,
        CancellationToken ct)
    {
        if (frame.Method == ProtobufFrame.Control)
        {
            if (string.Equals(
                    frame.GetHeader("type"),
                    "pong",
                    StringComparison.Ordinal)
                && frame.Payload.Length > 0)
            {
                TryUpdateClientConfig(frame.Payload);
            }
            return;
        }

        if (frame.Method != ProtobufFrame.Data
            || !string.Equals(
                frame.GetHeader("type"),
                "event",
                StringComparison.Ordinal)
            || frame.Payload.Length == 0)
        {
            return;
        }

        var payload = MergePayload(frame);
        if (payload is null)
            return;

        var timer = Stopwatch.StartNew();
        var responseCode = 200;
        try
        {
            var evt = JsonSerializer.Deserialize<FeishuEvent>(
                payload,
                JsonOptions);
            if (evt is not null)
            {
                Trace(
                    $"event decoded event_type={evt.Header?.EventType ?? "-"} message_type={evt.Event?.Message?.MessageType ?? "-"} message_id={evt.ExtractMessageId() ?? "-"}");
                await InvokeAsync(OnEvent, evt);
                await DispatchTextMessageAsync(evt);
            }
        }
        catch (Exception ex)
        {
            responseCode = 500;
            Trace(
                $"event handling failed message_id={frame.GetHeader("message_id") ?? "-"} error={ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            timer.Stop();
            var response = frame.WithResponse(
                JsonSerializer.SerializeToUtf8Bytes(
                    new { code = responseCode }),
                timer.ElapsedMilliseconds);
            await SendFrameAsync(response, ct);
        }
    }

    private byte[]? MergePayload(ProtobufFrame frame)
    {
        var messageId = frame.GetHeader("message_id");
        var sum = ParsePositiveInt(frame.GetHeader("sum"), 1);
        var sequence = ParsePositiveInt(frame.GetHeader("seq"), 0);
        if (sum <= 1 || string.IsNullOrWhiteSpace(messageId))
            return frame.Payload;
        if (sequence < 0 || sequence >= sum || sum > 1024)
            throw new InvalidDataException("Invalid Feishu frame fragment.");

        CleanupExpiredFragments();
        var set = _fragments.GetOrAdd(
            messageId,
            _ => new FragmentSet(sum));
        if (set.Parts.Length != sum)
            throw new InvalidDataException("Inconsistent Feishu fragment count.");

        set.Parts[sequence] = frame.Payload;
        if (set.Parts.Any(part => part is null))
            return null;

        _fragments.TryRemove(messageId, out _);
        var length = set.Parts.Sum(part => part!.Length);
        var merged = new byte[length];
        var offset = 0;
        foreach (var part in set.Parts)
        {
            Buffer.BlockCopy(part!, 0, merged, offset, part!.Length);
            offset += part.Length;
        }
        return merged;
    }

    private void CleanupExpiredFragments()
    {
        var threshold = DateTimeOffset.UtcNow.AddSeconds(-10);
        foreach (var pair in _fragments)
        {
            if (pair.Value.CreatedAt < threshold)
                _fragments.TryRemove(pair.Key, out _);
        }
    }

    private void TryUpdateClientConfig(byte[] payload)
    {
        try
        {
            using var document = JsonDocument.Parse(payload);
            if (document.RootElement.TryGetProperty(
                    "PingInterval",
                    out var pingInterval))
            {
                _pingIntervalSeconds = Math.Max(5, pingInterval.GetInt32());
            }
        }
        catch (JsonException)
        {
            // Keep the last valid client config.
        }
    }

    private async Task DispatchTextMessageAsync(FeishuEvent evt)
    {
        var message = evt.Event?.Message;
        if (message is null
            || !string.Equals(
                message.MessageType,
                "text",
                StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var text = evt.ExtractText();
        if (string.IsNullOrWhiteSpace(text))
            return;

        await InvokeAsync(
            OnTextMessage,
            message.MessageId ?? "",
            message.ChatId ?? "",
            evt.ExtractSenderId() ?? "",
            text);
    }

    private async Task SendFrameAsync(
        ProtobufFrame frame,
        CancellationToken ct)
    {
        var socket = _ws;
        if (socket is null || socket.State != WebSocketState.Open)
            throw new WebSocketException("Feishu WebSocket is not open.");

        var payload = frame.Encode();
        await _sendLock.WaitAsync(ct);
        try
        {
            await socket.SendAsync(
                payload,
                WebSocketMessageType.Binary,
                endOfMessage: true,
                ct);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private static async Task InvokeAsync<T>(
        Func<T, Task>? handlers,
        T arg)
    {
        if (handlers is null)
            return;
        foreach (Func<T, Task> handler in handlers.GetInvocationList())
            await handler(arg);
    }

    private static async Task InvokeAsync<T1, T2, T3, T4>(
        Func<T1, T2, T3, T4, Task>? handlers,
        T1 arg1,
        T2 arg2,
        T3 arg3,
        T4 arg4)
    {
        if (handlers is null)
            return;
        foreach (Func<T1, T2, T3, T4, Task> handler in handlers.GetInvocationList())
            await handler(arg1, arg2, arg3, arg4);
    }

    private void MarkDisconnected()
    {
        var socket = Interlocked.Exchange(ref _ws, null);
        if (socket is null)
            return;
        socket.Dispose();
        OnConnectionChanged?.Invoke(false);
    }

    private void Trace(string message)
    {
        try
        {
            OnDiagnostic?.Invoke(message);
        }
        catch
        {
            // Diagnostics must never change protocol behavior.
        }
    }

    public async Task DisconnectAsync()
    {
        if (_cts is null)
            return;

        await _cts.CancelAsync();
        var socket = _ws;
        if (socket?.State == WebSocketState.Open)
        {
            try
            {
                await socket.CloseAsync(
                    WebSocketCloseStatus.NormalClosure,
                    "",
                    CancellationToken.None);
            }
            catch
            {
                // Best-effort shutdown.
            }
        }

        try { await (_receiveLoop ?? Task.CompletedTask); } catch { }
        try { await (_pingLoop ?? Task.CompletedTask); } catch { }
        MarkDisconnected();
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _cts?.Cancel();
        _ws?.Dispose();
        _cts?.Dispose();
        _sendLock.Dispose();
        if (_ownsHttp)
            _http.Dispose();
    }

    private static int ParsePositiveInt(string? value, int fallback)
        => int.TryParse(value, out var result) ? result : fallback;

    private sealed record ReceivedMessage(
        WebSocketMessageType Type,
        byte[] Payload);

    private sealed class FragmentSet(int count)
    {
        public byte[]?[] Parts { get; } = new byte[count][];
        public DateTimeOffset CreatedAt { get; } = DateTimeOffset.UtcNow;
    }
}

/// <summary>
/// Minimal pbbp2.Frame codec compatible with the official Feishu SDK schema:
/// SeqID(1), LogID(2), service(3), method(4), headers(5),
/// payloadEncoding(6), payloadType(7), payload(8), LogIDNew(9).
/// </summary>
public sealed record ProtobufFrame
{
    public const int Control = 0;
    public const int Data = 1;

    public ulong SeqId { get; init; }
    public ulong LogId { get; init; }
    public int Service { get; init; }
    public int Method { get; init; }
    public Dictionary<string, string> Headers { get; init; } =
        new(StringComparer.Ordinal);
    public string? PayloadEncoding { get; init; }
    public string? PayloadType { get; init; }
    public byte[] Payload { get; init; } = [];
    public string? LogIdNew { get; init; }

    public string? GetHeader(string key)
        => Headers.TryGetValue(key, out var value) ? value : null;

    public static ProtobufFrame NewPing(int serviceId)
        => new()
        {
            SeqId = 0,
            LogId = 0,
            Service = serviceId,
            Method = Control,
            Headers = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["type"] = "ping",
            },
        };

    public ProtobufFrame WithResponse(byte[] payload, long elapsedMilliseconds)
    {
        var headers = new Dictionary<string, string>(
            Headers,
            StringComparer.Ordinal)
        {
            ["biz_rt"] = elapsedMilliseconds.ToString(
                System.Globalization.CultureInfo.InvariantCulture),
        };
        return this with { Headers = headers, Payload = payload };
    }

    public static ProtobufFrame Parse(ReadOnlySpan<byte> data)
    {
        ulong seqId = 0;
        ulong logId = 0;
        var service = 0;
        var method = 0;
        var headers = new Dictionary<string, string>(StringComparer.Ordinal);
        string? payloadEncoding = null;
        string? payloadType = null;
        byte[] payload = [];
        string? logIdNew = null;

        var position = 0;
        while (position < data.Length)
        {
            var tag = ReadVarint(data, ref position);
            var field = (int)(tag >> 3);
            var wireType = (int)(tag & 7);
            switch (field)
            {
                case 1 when wireType == 0:
                    seqId = ReadVarint(data, ref position);
                    break;
                case 2 when wireType == 0:
                    logId = ReadVarint(data, ref position);
                    break;
                case 3 when wireType == 0:
                    service = unchecked((int)ReadVarint(data, ref position));
                    break;
                case 4 when wireType == 0:
                    method = unchecked((int)ReadVarint(data, ref position));
                    break;
                case 5 when wireType == 2:
                {
                    var headerBytes = ReadLengthDelimited(data, ref position);
                    var (key, value) = ParseHeader(headerBytes);
                    headers[key] = value;
                    break;
                }
                case 6 when wireType == 2:
                    payloadEncoding = Encoding.UTF8.GetString(
                        ReadLengthDelimited(data, ref position));
                    break;
                case 7 when wireType == 2:
                    payloadType = Encoding.UTF8.GetString(
                        ReadLengthDelimited(data, ref position));
                    break;
                case 8 when wireType == 2:
                    payload = ReadLengthDelimited(data, ref position).ToArray();
                    break;
                case 9 when wireType == 2:
                    logIdNew = Encoding.UTF8.GetString(
                        ReadLengthDelimited(data, ref position));
                    break;
                default:
                    Skip(data, ref position, wireType);
                    break;
            }
        }

        return new ProtobufFrame
        {
            SeqId = seqId,
            LogId = logId,
            Service = service,
            Method = method,
            Headers = headers,
            PayloadEncoding = payloadEncoding,
            PayloadType = payloadType,
            Payload = payload,
            LogIdNew = logIdNew,
        };
    }

    public byte[] Encode()
    {
        using var stream = new MemoryStream();
        WriteVarintField(stream, 1, SeqId);
        WriteVarintField(stream, 2, LogId);
        WriteVarintField(stream, 3, unchecked((ulong)Service));
        WriteVarintField(stream, 4, unchecked((ulong)Method));
        foreach (var header in Headers)
        {
            using var nested = new MemoryStream();
            WriteBytesField(nested, 1, Encoding.UTF8.GetBytes(header.Key));
            WriteBytesField(nested, 2, Encoding.UTF8.GetBytes(header.Value));
            WriteBytesField(stream, 5, nested.ToArray());
        }
        if (PayloadEncoding is not null)
            WriteBytesField(
                stream,
                6,
                Encoding.UTF8.GetBytes(PayloadEncoding));
        if (PayloadType is not null)
            WriteBytesField(stream, 7, Encoding.UTF8.GetBytes(PayloadType));
        if (Payload.Length > 0)
            WriteBytesField(stream, 8, Payload);
        if (LogIdNew is not null)
            WriteBytesField(stream, 9, Encoding.UTF8.GetBytes(LogIdNew));
        return stream.ToArray();
    }

    private static (string Key, string Value) ParseHeader(
        ReadOnlySpan<byte> data)
    {
        var position = 0;
        string? key = null;
        string? value = null;
        while (position < data.Length)
        {
            var tag = ReadVarint(data, ref position);
            var field = (int)(tag >> 3);
            var wireType = (int)(tag & 7);
            if (field == 1 && wireType == 2)
                key = Encoding.UTF8.GetString(
                    ReadLengthDelimited(data, ref position));
            else if (field == 2 && wireType == 2)
                value = Encoding.UTF8.GetString(
                    ReadLengthDelimited(data, ref position));
            else
                Skip(data, ref position, wireType);
        }

        if (key is null || value is null)
            throw new InvalidDataException("Invalid Feishu protobuf header.");
        return (key, value);
    }

    private static ReadOnlySpan<byte> ReadLengthDelimited(
        ReadOnlySpan<byte> data,
        ref int position)
    {
        var length = checked((int)ReadVarint(data, ref position));
        if (length < 0 || position + length > data.Length)
            throw new InvalidDataException("Invalid protobuf length.");
        var result = data.Slice(position, length);
        position += length;
        return result;
    }

    private static ulong ReadVarint(
        ReadOnlySpan<byte> data,
        ref int position)
    {
        ulong value = 0;
        for (var shift = 0; shift < 64; shift += 7)
        {
            if (position >= data.Length)
                throw new InvalidDataException("Truncated protobuf varint.");
            var current = data[position++];
            value |= (ulong)(current & 0x7f) << shift;
            if ((current & 0x80) == 0)
                return value;
        }
        throw new InvalidDataException("Invalid protobuf varint.");
    }

    private static void Skip(
        ReadOnlySpan<byte> data,
        ref int position,
        int wireType)
    {
        switch (wireType)
        {
            case 0:
                _ = ReadVarint(data, ref position);
                break;
            case 1:
                position = checked(position + 8);
                break;
            case 2:
                _ = ReadLengthDelimited(data, ref position);
                break;
            case 5:
                position = checked(position + 4);
                break;
            default:
                throw new InvalidDataException(
                    $"Unsupported protobuf wire type {wireType}.");
        }
        if (position > data.Length)
            throw new InvalidDataException("Truncated protobuf field.");
    }

    private static void WriteVarintField(
        Stream stream,
        int field,
        ulong value)
    {
        WriteVarint(stream, (ulong)field << 3);
        WriteVarint(stream, value);
    }

    private static void WriteBytesField(
        Stream stream,
        int field,
        ReadOnlySpan<byte> value)
    {
        WriteVarint(stream, ((ulong)field << 3) | 2);
        WriteVarint(stream, (ulong)value.Length);
        stream.Write(value);
    }

    private static void WriteVarint(Stream stream, ulong value)
    {
        while (value >= 0x80)
        {
            stream.WriteByte((byte)((value & 0x7f) | 0x80));
            value >>= 7;
        }
        stream.WriteByte((byte)value);
    }
}

internal static class StringExtensions
{
    public static string Truncate(this string value, int maxLength)
        => value.Length <= maxLength
            ? value
            : value[..maxLength] + "...";
}
