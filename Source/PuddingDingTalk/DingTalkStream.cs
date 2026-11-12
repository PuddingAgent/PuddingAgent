using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text.Json;

namespace PuddingDingTalk;

public sealed class DingTalkStream : IDisposable
{
    private readonly HttpClient http = new(new HttpClientHandler { AllowAutoRedirect = false })
        { Timeout = TimeSpan.FromSeconds(12) };

    public async Task ListenAsync(string clientId, string clientSecret, DingTalkGroupHandler handler,
        Action<string> status, CancellationToken ct, Func<CancellationToken, Task<bool>>? ownerActive = null)
    {
        var subscription = new { type = "CALLBACK", topic = GroupIngress.Topic };
        using var opened = await http.PostAsJsonAsync("https://api.dingtalk.com/v1.0/gateway/connections/open",
            new { clientId, clientSecret, subscriptions = new[] { subscription }, ua = "PuddingAgent/1" }, ct);
        opened.EnsureSuccessStatusCode();
        var json = await opened.Content.ReadFromJsonAsync<JsonElement>(ct);
        var ticket = GroupIngress.String(json, "ticket");
        if (!Uri.TryCreate(GroupIngress.String(json, "endpoint"), UriKind.Absolute, out var endpoint)
            || endpoint.Scheme != "wss" || endpoint.Host != "wss-open-connection.dingtalk.com"
            || endpoint.UserInfo.Length != 0 || !endpoint.IsDefaultPort || string.IsNullOrWhiteSpace(ticket))
            throw new InvalidDataException("Unexpected DingTalk discovery response.");
        var address = new UriBuilder(endpoint) { Query = "ticket=" + Uri.EscapeDataString(ticket) }.Uri;
        using var ws = new ClientWebSocket();
        using (var connect = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            connect.CancelAfter(TimeSpan.FromSeconds(15));
            await ws.ConnectAsync(address, connect.Token);
        }
        status("connected");
        var chunk = new byte[8192];
        while (!ct.IsCancellationRequested)
        {
            using var frame = new MemoryStream();
            WebSocketReceiveResult part;
            do
            {
                part = await ws.ReceiveAsync(new ArraySegment<byte>(chunk), ct);
                if (part.MessageType == WebSocketMessageType.Close) return;
                if (part.MessageType != WebSocketMessageType.Text || frame.Length + part.Count > 64 * 1024)
                    throw new InvalidDataException("Invalid DingTalk frame.");
                frame.Write(chunk, 0, part.Count);
            } while (!part.EndOfMessage);
            using var parsed = JsonDocument.Parse(frame.ToArray());
            var root = parsed.RootElement;
            var headers = root.GetProperty("headers");
            var mid = GroupIngress.String(headers, "messageId");
            var topic = GroupIngress.String(headers, "topic");
            var type = GroupIngress.String(root, "type");
            var data = GroupIngress.String(root, "data") ?? "{}";
            if (type == "SYSTEM" && topic == "disconnect") return;
            if (string.IsNullOrWhiteSpace(mid)) continue;
            var code = 200;
            if (type == "CALLBACK" && topic == GroupIngress.Topic)
            {
                if (ownerActive is not null && !await ownerActive(ct)) return;
                try
                {
                    using var body = JsonDocument.Parse(data);
                    status(await handler.HandleCallbackAsync(topic, body.RootElement, async (url, source, result, token) =>
                    {
                        var media = new DingTalkMediaClient(http, clientId, clientSecret);
                        foreach (var image in result.Images ?? [])
                        {
                            try
                            {
                                if (await media.SendImageAsync(source.ConversationId, image, token)) continue;
                            }
                            catch (Exception) when (!token.IsCancellationRequested) { }
                            await ReplyAsync(url, "本次图片发送未取得钉钉成功回执。", token);
                            return false;
                        }
                        return string.IsNullOrWhiteSpace(result.Text) || await ReplyAsync(url, result.Text, token);
                    }, ct));
                }
                catch (JsonException) { code = 400; }
                catch (Exception) when (!ct.IsCancellationRequested) { code = 500; status("processing_failed"); }
            }
            var ack = JsonSerializer.SerializeToUtf8Bytes(new { code, headers = new { messageId = mid, contentType = "application/json" },
                message = code == 200 ? "OK" : "failed", data = type == "SYSTEM" && topic == "ping" ? data : "{}" });
            await ws.SendAsync(new ArraySegment<byte>(ack), WebSocketMessageType.Text, true, ct);
        }
    }

    private async Task<bool> ReplyAsync(Uri url, string content, CancellationToken ct)
    {
        using var response = await http.PostAsJsonAsync(url, new { msgtype = "text", text = new { content } }, ct);
        if (!response.IsSuccessStatusCode) return false;
        var result = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
        return result.TryGetProperty("errcode", out var code) && code.ValueKind == JsonValueKind.Number
            && code.TryGetInt32(out var n) && n == 0;
    }

    public void Dispose() => http.Dispose();
}
