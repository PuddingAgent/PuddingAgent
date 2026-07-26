using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using HarnessAgent.Core.Connectors.Feishu;

namespace HarnessAgent.Core.Tests.Feishu;

[TestClass]
public sealed class FeishuWebSocketInitialPingTests
{
    [TestMethod]
    public async Task ConnectAsync_SendsOfficialInitialPingImmediately()
    {
        var port = ReserveTcpPort();
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();

        using var timeout =
            new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var receivedFrame = ReceiveFirstFrameAsync(listener, timeout.Token);
        var webSocketUrl = $"ws://127.0.0.1:{port}/ws?service_id=42";
        using var http = new HttpClient(
            new EndpointDiscoveryHandler(webSocketUrl));
        using var client = new FeishuWebSocket(
            new FeishuConfig
            {
                AppId = "cli_test",
                AppSecret = "secret_test",
            },
            http);

        await client.ConnectAsync(timeout.Token);
        var ping = await receivedFrame;

        Assert.AreEqual(ProtobufFrame.Control, ping.Method);
        Assert.AreEqual(42, ping.Service);
        Assert.AreEqual("ping", ping.GetHeader("type"));
    }

    [TestMethod]
    public async Task ConnectAsync_ReceivesEventAndReturnsSuccessfulAck()
    {
        var port = ReserveTcpPort();
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();

        using var timeout =
            new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var exchange = RunEventExchangeAsync(listener, timeout.Token);
        var webSocketUrl = $"ws://127.0.0.1:{port}/ws?service_id=42";
        using var http = new HttpClient(
            new EndpointDiscoveryHandler(webSocketUrl));
        using var client = new FeishuWebSocket(
            new FeishuConfig
            {
                AppId = "cli_test",
                AppSecret = "secret_test",
            },
            http);
        var receivedText = new TaskCompletionSource<string>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        client.OnTextMessage += (_, _, _, text) =>
        {
            receivedText.TrySetResult(text);
            return Task.CompletedTask;
        };

        await client.ConnectAsync(timeout.Token);
        var ack = await exchange;

        Assert.AreEqual("hello from fake lark", await receivedText.Task);
        Assert.AreEqual(ProtobufFrame.Data, ack.Method);
        Assert.AreEqual("event", ack.GetHeader("type"));
        Assert.AreEqual("om_local_event", ack.GetHeader("message_id"));
        Assert.IsFalse(string.IsNullOrWhiteSpace(ack.GetHeader("biz_rt")));
        using var response = JsonDocument.Parse(ack.Payload);
        Assert.AreEqual(200, response.RootElement.GetProperty("code").GetInt32());
    }

    private static async Task<ProtobufFrame> ReceiveFirstFrameAsync(
        HttpListener listener,
        CancellationToken ct)
    {
        var context = await listener.GetContextAsync().WaitAsync(ct);
        var upgrade = await context.AcceptWebSocketAsync(null);
        using var socket = upgrade.WebSocket;
        using var payload = new MemoryStream();
        var buffer = new byte[4096];
        WebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(buffer, ct);
            if (result.Count > 0)
                payload.Write(buffer, 0, result.Count);
        }
        while (!result.EndOfMessage);

        Assert.AreEqual(WebSocketMessageType.Binary, result.MessageType);
        return ProtobufFrame.Parse(payload.ToArray());
    }

    private static async Task<ProtobufFrame> RunEventExchangeAsync(
        HttpListener listener,
        CancellationToken ct)
    {
        var context = await listener.GetContextAsync().WaitAsync(ct);
        var upgrade = await context.AcceptWebSocketAsync(null);
        using var socket = upgrade.WebSocket;

        var ping = await ReceiveFrameAsync(socket, ct);
        Assert.AreEqual("ping", ping.GetHeader("type"));

        var eventFrame = new ProtobufFrame
        {
            Service = 42,
            Method = ProtobufFrame.Data,
            Headers = new Dictionary<string, string>
            {
                ["type"] = "event",
                ["message_id"] = "om_local_event",
                ["sum"] = "1",
                ["seq"] = "0",
                ["trace_id"] = "trace-local",
            },
            PayloadEncoding = "json",
            PayloadType = "application/json",
            Payload = Encoding.UTF8.GetBytes(
                """
                {
                  "schema": "2.0",
                  "header": {
                    "event_id": "evt_local",
                    "event_type": "im.message.receive_v1"
                  },
                  "event": {
                    "sender": {
                      "sender_id": {
                        "open_id": "ou_local_sender"
                      }
                    },
                    "message": {
                      "message_id": "om_local_event",
                      "chat_id": "oc_local_chat",
                      "message_type": "text",
                      "content": "{\"text\":\"hello from fake lark\"}"
                    }
                  }
                }
                """),
        };
        await socket.SendAsync(
            eventFrame.Encode(),
            WebSocketMessageType.Binary,
            endOfMessage: true,
            ct);

        return await ReceiveFrameAsync(socket, ct);
    }

    private static async Task<ProtobufFrame> ReceiveFrameAsync(
        WebSocket socket,
        CancellationToken ct)
    {
        using var payload = new MemoryStream();
        var buffer = new byte[4096];
        WebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(buffer, ct);
            if (result.Count > 0)
                payload.Write(buffer, 0, result.Count);
        }
        while (!result.EndOfMessage);

        Assert.AreEqual(WebSocketMessageType.Binary, result.MessageType);
        return ProtobufFrame.Parse(payload.ToArray());
    }

    private static int ReserveTcpPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }

    private sealed class EndpointDiscoveryHandler(string webSocketUrl)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var json =
                $$"""
                {
                  "code": 0,
                  "msg": "ok",
                  "data": {
                    "URL": "{{webSocketUrl}}",
                    "ClientConfig": {
                      "PingInterval": 120,
                      "ReconnectCount": 3,
                      "ReconnectInterval": 1,
                      "ReconnectNonce": 1
                    }
                  }
                }
                """;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    json,
                    Encoding.UTF8,
                    "application/json"),
            });
        }
    }
}
