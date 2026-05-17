using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace PuddingWsTest;

/// <summary>
/// WebSocket 测试客户端 — 用于端到端验证 WebSocket 连接器。
/// 
/// 功能：
///   connect <url>  — 连接 Pudding Agent WS 端点
///   auth <key>    — 发送 SM2 签名认证
///   send <text>   — 发送聊天消息
///   listen        — 持续接收推送
///   disconnect    — 断开连接
///   quit          — 退出程序
/// </summary>
public class Program
{
    private static ClientWebSocket? _ws;
    private static CancellationTokenSource? _cts;

    public static async Task Main(string[] args)
    {
        // Auto test mode
        if (args.Length > 0 && args[0] == "--auto")
        {
            await AutoTest.RunAsync(args.Skip(1).ToArray());
            return;
        }

        Console.OutputEncoding = Encoding.UTF8;
        Console.WriteLine("=== Pudding WebSocket Test Client ===");
        Console.WriteLine("Commands: connect <url> | send <text> | listen | disconnect | quit");
        Console.WriteLine();

        while (true)
        {
            Console.Write("> ");
            var line = Console.ReadLine();
            if (string.IsNullOrWhiteSpace(line)) continue;

            var parts = line.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
            var cmd = parts[0].ToLowerInvariant();
            var arg = parts.Length > 1 ? parts[1] : "";

            try
            {
                switch (cmd)
                {
                    case "connect":
                        await ConnectAsync(arg);
                        break;
                    case "send":
                        await SendAsync(arg);
                        break;
                    case "listen":
                        _ = ListenAsync();
                        break;
                    case "disconnect":
                        await DisconnectAsync();
                        break;
                    case "quit":
                    case "exit":
                        await DisconnectAsync();
                        return;
                    default:
                        Console.WriteLine($"Unknown command: {cmd}");
                        break;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error: {ex.Message}");
            }
        }
    }

    private static async Task ConnectAsync(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
            url = "ws://localhost:5000/ws/connect";

        _ws?.Dispose();
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        _ws = new ClientWebSocket();

        Console.WriteLine($"Connecting to {url}...");
        await _ws.ConnectAsync(new Uri(url), _cts.Token);
        Console.WriteLine("Connected!");

        // Start background listener
        _ = ListenAsync();
    }

    private static async Task SendAsync(string text)
    {
        if (_ws?.State != WebSocketState.Open)
        {
            Console.WriteLine("Not connected. Use 'connect <url>' first.");
            return;
        }

        var payload = new
        {
            type = "chat",
            content = text,
            source = "ws-test-cli",
            sessionId = (string?)null,
        };
        var json = JsonSerializer.Serialize(payload);
        var bytes = Encoding.UTF8.GetBytes(json);

        await _ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, _cts!.Token);
        Console.WriteLine($"Sent: {text}");
    }

    private static async Task ListenAsync()
    {
        if (_ws?.State != WebSocketState.Open)
        {
            Console.WriteLine("Not connected.");
            return;
        }

        Console.WriteLine("Listening for messages...");
        var buffer = new byte[4096];

        try
        {
            while (_ws.State == WebSocketState.Open && !_cts!.Token.IsCancellationRequested)
            {
                var result = await _ws.ReceiveAsync(new ArraySegment<byte>(buffer), _cts.Token);

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    Console.WriteLine($"[WS] Server closed connection: {result.CloseStatus}");
                    break;
                }

                if (result.MessageType == WebSocketMessageType.Text)
                {
                    var message = Encoding.UTF8.GetString(buffer, 0, result.Count);
                    Console.WriteLine($"[WS] Received: {message}");
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException ex)
        {
            Console.WriteLine($"[WS] Connection error: {ex.Message}");
        }
    }

    private static async Task DisconnectAsync()
    {
        if (_ws?.State == WebSocketState.Open)
        {
            await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Client disconnecting", CancellationToken.None);
            Console.WriteLine("Disconnected.");
        }
        _ws?.Dispose();
        _ws = null;
        _cts?.Cancel();
    }
}
