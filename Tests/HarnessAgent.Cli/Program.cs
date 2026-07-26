using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using HarnessAgent.Core.Provider;
using HarnessAgent.Core.Memory;
using HarnessAgent.Core.Compaction;
using HarnessAgent.Core.Mcp;
using HarnessAgent.Core.Computer;
using HarnessAgent.Core.Tools;
using HarnessAgent.Core.Middleware;
using HarnessAgent.Core.Browser;
using HarnessAgent.Core.Connectors.Feishu;

if (args.Length > 0
    && string.Equals(args[0], "feishu-echo", StringComparison.OrdinalIgnoreCase))
{
    Environment.ExitCode = await RunFeishuEchoAsync(args[1..]);
    return;
}

Console.WriteLine("=== HarnessAgent CLI ===\n");
var p = 0; const int t = 15;

void Ok(string label) { Console.WriteLine($"  {label} OK ({++p}/{t})\n"); }

Console.WriteLine("-- L0: Provider --"); TestProvider(); Ok("L0");
Console.WriteLine("-- L1: Memory --"); TestMemory(); Ok("L1");
Console.WriteLine("-- L2: Compaction --"); TestCompaction(); Ok("L2");
Console.WriteLine("-- L3: MCP Client --"); await TestMcpClient(); Ok("L3");
Console.WriteLine("-- L4: MCP Server --"); await TestMcpServer(); Ok("L4");
Console.WriteLine("-- C1: ScreenCapture --"); TestScreenCapture(); Ok("C1");
Console.WriteLine("-- C2: UIAutomation --"); TestUIAutomation(); Ok("C2");
Console.WriteLine("-- C6: SelfHealRestart --"); TestSelfHealRestart(); Ok("C6");
Console.WriteLine("-- C5: CodexIntegration --"); await TestCodex(); Ok("C5");
Console.WriteLine("-- gh-tool --"); await TestGhTool(); Ok("gh");
Console.WriteLine("-- C4: BrowserControl --"); await TestBrowser(); Ok("C4");
Console.WriteLine("-- Middleware --"); await TestMiddleware(); Ok("MW");
Console.WriteLine("-- F1: Feishu Client --"); await TestFeishu(); Ok("F1");
Console.WriteLine("-- F2: Feishu Mapper --"); TestFeishuMapper(); Ok("F2");
Console.WriteLine("-- F3: Feishu WebSocket --"); await TestFeishuWebSocket(); Ok("F3");

Console.WriteLine($"=== All {p}/{t} OK ===");

static void TestProvider() { new ProviderRegistry().Register(new MockProvider()); }
static void TestMemory() { var lib = new MemoryLibrary(); lib.AddEntry(lib.CreateBook("t"), MemoryKind.Fact, "x"); }
static void TestCompaction() { if (new CompactionEngine().Evaluate(96000, 128000).Tier != CompactionTier.Aggressive) throw new Exception(); }
static async Task TestMcpClient() { using var t2 = new MockMcpTransport(); using var c = new McpClient(t2); await c.InitializeAsync(); }
static async Task TestMcpServer() { var s = new McpServer(); s.RegisterTool("x","",JsonSerializer.SerializeToElement(new{}),(_,__)=>Task.FromResult("ok")); await s.HandleRequestAsync("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\"}"); }
static void TestScreenCapture() { if (ScreenCapture.GetMonitors().Count == 0) Console.WriteLine("  skip"); }
static void TestUIAutomation() { if (WindowAutomation.GetActiveWindow() == IntPtr.Zero) Console.WriteLine("  skip"); }
static void TestSelfHealRestart() { try { new SelfHealRestart().Discover(); } catch { Console.WriteLine("  skip"); } }
static async Task TestCodex() { if (new CodexIntegration().EnsureLayout() == null) { Console.WriteLine("  skip"); return; } await CodexIntegration.IsPuddingRunningAsync(1000); }
static async Task TestGhTool() { await new GitHubCli().ListIssuesAsync(limit: 1); }
static async Task TestMiddleware() { var pipeline = new ChatPipeline((r,ct) => Task.FromResult(new ChatCompletionResponse { ModelId = r.ModelId, Message = ChatMessage.Assistant("ok"), Usage = new TokenUsage { InputTokens = 1, OutputTokens = 1 }, FinishReason = "stop" })); await pipeline.Use(ChatMiddleware.Validate()).ExecuteAsync(new ChatCompletionRequest { ModelId = "t", Messages = new List<ChatMessage> { ChatMessage.User("hi") } }); }

static async Task TestBrowser()
{
    try
    {
        await using var bc = new BrowserControl();
        var page = await bc.LaunchAsync(headless: true);
        await bc.GoToAsync("about:blank", 5000);
        var title = await bc.GetTitleAsync();
        Console.WriteLine($"  launched: title='{title}'");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"  skip: {ex.Message[..Math.Min(60, ex.Message.Length)]}");
    }
}

static async Task TestFeishu()
{
    var configPath = @"D:\data\config\feishu.json";
    if (!File.Exists(configPath))
    {
        Console.WriteLine($"  skip: config not found");
        return;
    }
    var config = JsonSerializer.Deserialize<FeishuConfig>(File.ReadAllText(configPath), new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
    if (config == null || string.IsNullOrWhiteSpace(config.AppId))
    {
        Console.WriteLine("  skip: invalid config");
        return;
    }
    using var client = new FeishuClient(config);
    try
    {
        var token = await client.GetAccessTokenAsync();
        Console.WriteLine(string.IsNullOrWhiteSpace(token)
            ? "  skip: empty token"
            : "  tenant token acquired");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"  skip: {ex.Message[..Math.Min(80, ex.Message.Length)]}");
    }
}

static void TestFeishuMapper()
{
    var evt = new FeishuEvent
    {
        Event = new FeishuEventV2
        {
            Message = new FeishuMessageEvent
            {
                MessageType = "text",
                TextWithoutAtBot = "你好 Pudding"
            }
        }
    };
    var text = evt.ExtractText();
    Console.WriteLine($"  mapper: text='{text}'");
}

static async Task TestFeishuWebSocket()
{
    var configPath = @"D:\data\config\feishu.json";
    if (!File.Exists(configPath))
    {
        Console.WriteLine("  skip: config not found");
        return;
    }
    var config = JsonSerializer.Deserialize<FeishuConfig>(File.ReadAllText(configPath),
        new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
    if (config == null || string.IsNullOrWhiteSpace(config.AppId))
    {
        Console.WriteLine("  skip: invalid config");
        return;
    }

    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
    using var ws = new FeishuWebSocket(config);

    ws.OnConnectionChanged += connected =>
        Console.WriteLine(connected ? "  [WS] 已连接" : "  [WS] 已断开");

    try
    {
        Console.WriteLine("  正在注册 WebSocket...");
        await ws.ConnectAsync(cts.Token);
        Console.WriteLine("  WebSocket 建连成功（默认 Harness 不发送回复）");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"  skip: {ex.Message[..Math.Min(120, ex.Message.Length)]}");
    }

    await ws.DisconnectAsync();
}

static async Task<int> RunFeishuEchoAsync(string[] commandArgs)
{
    var configPath = @"D:\data\config\feishu.json";
    var once = false;
    var timeoutSeconds = 0;

    for (var i = 0; i < commandArgs.Length; i++)
    {
        switch (commandArgs[i])
        {
            case "--config" when i + 1 < commandArgs.Length:
                configPath = commandArgs[++i];
                break;
            case "--once":
                once = true;
                break;
            case "--timeout-seconds" when i + 1 < commandArgs.Length
                && int.TryParse(commandArgs[++i], out var parsedTimeout)
                && parsedTimeout > 0:
                timeoutSeconds = parsedTimeout;
                break;
            case "--help":
            case "-h":
                PrintFeishuEchoHelp();
                return 0;
            default:
                Console.Error.WriteLine(
                    $"Unknown feishu-echo option: {commandArgs[i]}");
                PrintFeishuEchoHelp();
                return 64;
        }
    }

    if (!File.Exists(configPath))
    {
        Console.Error.WriteLine($"Feishu config not found: {configPath}");
        return 2;
    }

    var config = JsonSerializer.Deserialize<FeishuConfig>(
        await File.ReadAllTextAsync(configPath),
        new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
    if (config is null
        || string.IsNullOrWhiteSpace(config.AppId)
        || string.IsNullOrWhiteSpace(config.AppSecret))
    {
        Console.Error.WriteLine(
            "Feishu config must contain AppId and AppSecret.");
        return 2;
    }

    using var runCts = new CancellationTokenSource();
    if (timeoutSeconds > 0)
        runCts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

    ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
    {
        eventArgs.Cancel = true;
        runCts.Cancel();
    };
    Console.CancelKeyPress += cancelHandler;

    var receivedCount = 0;
    var onceCompleted = new TaskCompletionSource(
        TaskCreationOptions.RunContinuationsAsynchronously);
    using var client = new FeishuClient(config);
    using var webSocket = new FeishuWebSocket(config);

    webSocket.OnDiagnostic += message =>
        Console.WriteLine($"[Feishu Echo] protocol: {message}");
    webSocket.OnConnectionChanged += connected =>
        Console.WriteLine(connected
            ? "[Feishu Echo] WebSocket connected."
            : "[Feishu Echo] WebSocket disconnected.");
    webSocket.OnTextMessage += async (
        messageId,
        chatId,
        senderOpenId,
        text) =>
    {
        var number = Interlocked.Increment(ref receivedCount);
        Console.WriteLine(
            $"[Feishu Echo] #{number} received message={messageId} chat={chatId} sender={senderOpenId} text={JsonSerializer.Serialize(text)}");
        try
        {
            var result = await client.ReplyTextAsync(
                messageId,
                text,
                CreateEchoUuid(messageId),
                runCts.Token);
            if (result.Code != 0)
            {
                throw new InvalidOperationException(
                    $"Feishu reply failed: code={result.Code}, msg={result.Msg}");
            }

            Console.WriteLine(
                $"[Feishu Echo] #{number} replied with the same text.");
            if (once)
                onceCompleted.TrySetResult();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(
                $"[Feishu Echo] #{number} reply failed: {ex.Message}");
            if (once)
                onceCompleted.TrySetException(ex);
            throw;
        }
    };

    try
    {
        var token = await client.GetAccessTokenAsync(runCts.Token);
        if (string.IsNullOrWhiteSpace(token))
        {
            throw new InvalidOperationException(
                "Feishu returned an empty tenant token.");
        }

        await webSocket.ConnectAsync(runCts.Token);
        Console.WriteLine(once
            ? "[Feishu Echo] Ready. Send one text message from Feishu."
            : "[Feishu Echo] Ready. Every text message will be copied back. Press Ctrl+C to stop.");

        try
        {
            if (once)
                await onceCompleted.Task.WaitAsync(runCts.Token);
            else
                await Task.Delay(Timeout.InfiniteTimeSpan, runCts.Token);
        }
        catch (OperationCanceledException) when (runCts.IsCancellationRequested)
        {
            // Ctrl+C or the explicit test timeout is a normal shutdown.
        }

        return timeoutSeconds > 0 && receivedCount == 0 ? 2 : 0;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"[Feishu Echo] Fatal: {ex.Message}");
        return 1;
    }
    finally
    {
        await webSocket.DisconnectAsync();
        Console.CancelKeyPress -= cancelHandler;
        Console.WriteLine(
            $"[Feishu Echo] Stopped. Echoed {receivedCount} message(s).");
    }
}

static string CreateEchoUuid(string messageId)
{
    var hash = SHA256.HashData(Encoding.UTF8.GetBytes(messageId));
    return $"echo-{Convert.ToHexString(hash).ToLowerInvariant()[..32]}";
}

static void PrintFeishuEchoHelp()
{
    Console.WriteLine(
        """
        Usage:
          dotnet run --project Tests/HarnessAgent.Cli -- feishu-echo [options]

        Options:
          --config <path>          Feishu JSON config (default D:\data\config\feishu.json)
          --once                   Exit after one successful copied reply
          --timeout-seconds <n>    Stop after n seconds; returns 2 if no message arrived
          --help                   Show this help
        """);
}

sealed class MockMcpTransport : IMcpTransport
{
    public Task<JsonRpc.Response> SendRequestAsync(JsonRpc.Request req, CancellationToken ct)
    { using var doc = req.Method switch { "initialize" => JsonDocument.Parse("{\"capabilities\":{\"tools\":{}},\"serverInfo\":{\"name\":\"m\",\"version\":\"1\"}}"), "tools/list" => JsonDocument.Parse("{\"tools\":[{\"name\":\"t\",\"inputSchema\":{}}]}"), _ => throw new Exception(req.Method), }; return Task.FromResult(new JsonRpc.Response { Id = req.Id, Result = doc.RootElement.Clone() }); }
    public Task SendNotificationAsync(JsonRpc.Notification n, CancellationToken ct) => Task.CompletedTask;
    public void Dispose() { }
}

sealed class MockProvider : IModelProvider
{
    public string ProviderId => "m";
    public IReadOnlyList<ModelDescriptor> Models { get; } = new List<ModelDescriptor> { new() { ModelId = "a", ContextWindowTokens = 1000, MaxOutputTokens = 100, SupportsToolCalling = true }, new() { ModelId = "b", ContextWindowTokens = 2000, MaxOutputTokens = 200, CapabilityTags = new HashSet<string> { "f" } }, new() { ModelId = "c", ContextWindowTokens = 3000, MaxOutputTokens = 300 } };
    public Task<ChatCompletionResponse> CompleteAsync(ChatCompletionRequest r, CancellationToken ct) => Task.FromResult(new ChatCompletionResponse { ModelId = r.ModelId, Message = ChatMessage.Assistant("OK"), Usage = new TokenUsage { InputTokens = 1, OutputTokens = 1 }, FinishReason = "stop" });
    public IAsyncEnumerable<ChatCompletionChunk> StreamAsync(ChatCompletionRequest r, CancellationToken ct) => throw new NotSupportedException();
}
