using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace PuddingSlack;

public interface ISlackSocket : IAsyncDisposable
{
    Task ConnectAsync(Uri uri, CancellationToken ct);
    Task<string?> ReceiveAsync(CancellationToken ct);
    Task SendAsync(string text, CancellationToken ct);
}

/// <summary>Only persists the event before ACK; Agent work runs on a separate inbox consumer.</summary>
public sealed class SlackSocketMode(SlackApiClient api, SlackAdmission admission, Action<SlackInboundMessage> persist,
    Action<string> state, Func<ISlackSocket>? createSocket = null)
{
    public async Task RunAsync(CancellationToken ct)
    {
        var failures = 0;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                state("connecting");
                var uri = await api.OpenConnectionAsync(ct);
                await using var socket = (createSocket ?? (() => new SlackWebSocket()))();
                using (var connect = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    connect.CancelAfter(TimeSpan.FromSeconds(15));
                    await socket.ConnectAsync(uri, connect.Token);
                }
                while (await socket.ReceiveAsync(ct) is { } text)
                {
                    if (!await HandleAsync(text, socket, ct)) break;
                    failures = 0;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception) { state("disconnected_retrying"); }
            if (!ct.IsCancellationRequested)
                await Task.Delay(TimeSpan.FromSeconds(Math.Min(30, Math.Pow(2, Math.Min(++failures, 5)))), ct);
        }
        state("stopped");
    }

    public async Task<bool> HandleAsync(string text, ISlackSocket socket, CancellationToken ct)
    {
        using var document = JsonDocument.Parse(text);
        var root = document.RootElement;
        var type = SlackEventParser.String(root, "type");
        if (type == "hello") { state("connected"); return true; }
        if (type == "disconnect") return false;
        var envelopeId = SlackEventParser.String(root, "envelope_id");
        if (envelopeId is null) return true;
        if (type == "events_api" && root.TryGetProperty("payload", out var payload))
        {
            var message = SlackEventParser.Parse(payload, admission);
            if (message is not null) persist(message);
        }
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(2));
        await socket.SendAsync(JsonSerializer.Serialize(new { envelope_id = envelopeId }), deadline.Token);
        return true;
    }
}

public sealed class SlackWebSocket : ISlackSocket
{
    private readonly ClientWebSocket _socket = new();
    public SlackWebSocket()
    {
        _socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
        _socket.Options.KeepAliveTimeout = TimeSpan.FromSeconds(20);
    }
    public Task ConnectAsync(Uri uri, CancellationToken ct) => _socket.ConnectAsync(uri, ct);
    public async Task<string?> ReceiveAsync(CancellationToken ct)
    {
        var buffer = new byte[8192];
        using var stream = new MemoryStream();
        while (true)
        {
            var result = await _socket.ReceiveAsync(buffer.AsMemory(), ct);
            if (result.MessageType == WebSocketMessageType.Close) return null;
            if (result.MessageType != WebSocketMessageType.Text || stream.Length + result.Count > 1024 * 1024)
                throw new SlackProtocolException("invalid_socket_frame");
            stream.Write(buffer, 0, result.Count);
            if (result.EndOfMessage) return Encoding.UTF8.GetString(stream.GetBuffer(), 0, (int)stream.Length);
        }
    }
    public async Task SendAsync(string text, CancellationToken ct) => await _socket.SendAsync(
        Encoding.UTF8.GetBytes(text).AsMemory(), WebSocketMessageType.Text, true, ct);
    public ValueTask DisposeAsync() { _socket.Dispose(); return ValueTask.CompletedTask; }
}
