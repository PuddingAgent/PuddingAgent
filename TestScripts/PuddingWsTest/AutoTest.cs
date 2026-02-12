using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace PuddingWsTest;

public static class AutoTest
{
    public static async Task<int> RunAsync(string[] args)
    {
        var url = args.Length > 0 ? args[0] : "ws://localhost:5000/ws/connect";
        var message = args.Length > 1 ? args[1] : "你好，Pudding！请用一句话介绍你自己。";

        Console.WriteLine($"=== WebSocket E2E Test ===");
        Console.WriteLine($"URL: {url}");
        Console.WriteLine($"Message: {message}");
        Console.WriteLine();

        using var ws = new ClientWebSocket();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        try
        {
            // Step 1: Connect
            Console.WriteLine("[1/4] Connecting...");
            await ws.ConnectAsync(new Uri(url), cts.Token);
            Console.WriteLine("  ✅ Connected");

            // Step 2: Send chat message
            Console.WriteLine("[2/4] Sending message...");
            var payload = new { type = "chat", content = message, source = "ws-e2e-test" };
            var json = JsonSerializer.Serialize(payload);
            await ws.SendAsync(
                new ArraySegment<byte>(Encoding.UTF8.GetBytes(json)),
                WebSocketMessageType.Text, true, cts.Token);
            Console.WriteLine("  ✅ Sent");

            // Step 3: Wait for reply (up to 20s)
            Console.WriteLine("[3/4] Waiting for reply...");
            var buffer = new byte[8192];
            var rcvd = new StringBuilder();
            var replyCount = 0;
            var sawDone = false;
            var sawError = false;
            var sawCancelled = false;

            while (ws.State == WebSocketState.Open && !cts.Token.IsCancellationRequested)
            {
                var result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), cts.Token);

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    Console.WriteLine($"  Server closed: {result.CloseStatus}");
                    break;
                }

                if (result.MessageType == WebSocketMessageType.Text)
                {
                    var msg = Encoding.UTF8.GetString(buffer, 0, result.Count);
                    rcvd.Append(msg);
                    replyCount++;

                    try
                    {
                        using var doc = JsonDocument.Parse(msg);
                        if (doc.RootElement.TryGetProperty("event", out var evtEl))
                        {
                            var evtName = evtEl.GetString();
                            if (string.Equals(evtName, "done", StringComparison.OrdinalIgnoreCase)) sawDone = true;
                            if (string.Equals(evtName, "error", StringComparison.OrdinalIgnoreCase)) sawError = true;
                            if (string.Equals(evtName, "cancelled", StringComparison.OrdinalIgnoreCase)) sawCancelled = true;
                        }
                    }
                    catch
                    {
                        // ignore parse errors, keep receiving frames
                    }

                    // Print short preview
                    var preview = msg.Length > 80 ? msg[..80] + "..." : msg;
                    Console.WriteLine($"  Frame #{replyCount}: {preview}");
                }

                // Stop after terminal stream event
                if (sawDone || sawError || sawCancelled)
                    break;
            }

            // Step 4: Verify
            Console.WriteLine("[4/4] Verifying...");
            var totalText = rcvd.ToString();
            if (replyCount > 0 && sawDone && !sawError && !sawCancelled)
            {
                Console.WriteLine($"  ✅ Received {replyCount} frame(s), {totalText.Length} chars total");
                Console.WriteLine($"  Preview: {totalText[..Math.Min(totalText.Length, 200)]}");
                Console.WriteLine();
                Console.WriteLine("=== TEST PASSED ===");
                return 0;
            }
            else
            {
                Console.WriteLine($"  ❌ Stream not completed normally (frames={replyCount}, done={sawDone}, error={sawError}, cancelled={sawCancelled})");
                Console.WriteLine("=== TEST FAILED ===");
                return 1;
            }
        }
        catch (WebSocketException ex)
        {
            Console.WriteLine($"  ❌ WebSocket error: {ex.Message}");
            Console.WriteLine("=== TEST FAILED ===");
            return 1;
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine("  ❌ Timeout (30s)");
            Console.WriteLine("=== TEST FAILED ===");
            return 1;
        }
    }
}
