using System.Text.Json;
using HarnessAgent.Core.Provider;
using HarnessAgent.Core.Memory;
using HarnessAgent.Core.Compaction;
using HarnessAgent.Core.Mcp;
using HarnessAgent.Core.Computer;

Console.WriteLine("=== HarnessAgent CLI Test Suite ===\n");
var pass = 0; const int target = 6;

Console.WriteLine("── L0: Provider ──");
TestProvider(); pass++;
Console.WriteLine($"  OK ({pass}/{target})\n");

Console.WriteLine("── L1: Memory ──");
TestMemory(); pass++;
Console.WriteLine($"  OK ({pass}/{target})\n");

Console.WriteLine("── L2: Compaction ──");
TestCompaction(); pass++;
Console.WriteLine($"  OK ({pass}/{target})\n");

Console.WriteLine("── L3: MCP Client ──");
await TestMcpClient(); pass++;
Console.WriteLine($"  OK ({pass}/{target})\n");

Console.WriteLine("── L4: MCP Server ──");
await TestMcpServer(); pass++;
Console.WriteLine($"  OK ({pass}/{target})\n");

Console.WriteLine("── C1: ScreenCapture ──");
TestScreenCapture(); pass++;
Console.WriteLine($"  OK ({pass}/{target})\n");

Console.WriteLine($"=== All {pass}/{target} ✅ ===");

static void TestProvider()
{
    var r = new ProviderRegistry();
    r.Register(new MockProvider());
    if (r.ListModels().Count != 3) throw new Exception("count");
}

static void TestMemory()
{
    var lib = new MemoryLibrary();
    var book = lib.CreateBook("test");
    lib.AddEntry(book, MemoryKind.Fact, "test content");
    if (lib.Search("test").Count == 0) throw new Exception("search");
}

static void TestCompaction()
{
    var e = new CompactionEngine();
    if (e.Evaluate(38400, 128000).Tier != CompactionTier.None) throw new Exception("30%");
    if (e.Evaluate(96000, 128000).Tier != CompactionTier.Aggressive) throw new Exception("75%");
}

static async Task TestMcpClient()
{
    var (r, _, _, _) = JsonRpc.Parse("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/list\"}");
    if (r?.Method != "tools/list") throw new Exception("parse");
    using var t = new MockMcpTransport();
    using var c = new McpClient(t);
    await c.InitializeAsync();
    await c.ListToolsAsync();
}

static async Task TestMcpServer()
{
    var s = new McpServer();
    s.RegisterTool("add", "add", JsonSerializer.SerializeToElement(new { }),
        (args, ct) => Task.FromResult("7"));
    await s.HandleRequestAsync("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\"}");
    var call = await s.HandleRequestAsync("{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/call\",\"params\":{\"name\":\"add\",\"arguments\":{}}}");
    if (JsonRpc.Parse(call).Response == null) throw new Exception("call");
}

static void TestScreenCapture()
{
    var monitors = ScreenCapture.GetMonitors();
    if (monitors.Count == 0) { Console.WriteLine("  - No monitors (headless env) — skipped"); return; }
    var primary = monitors.First(m => m.IsPrimary);
    Console.WriteLine($"  - {monitors.Count} monitor(s), primary: {primary.Bounds.Width}x{primary.Bounds.Height}");
}

sealed class MockMcpTransport : IMcpTransport
{
    public Task<JsonRpc.Response> SendRequestAsync(JsonRpc.Request req, CancellationToken ct)
    {
        using var doc = req.Method switch
        {
            "initialize" => JsonDocument.Parse("{\"capabilities\":{\"tools\":{}},\"serverInfo\":{\"name\":\"mock\",\"version\":\"1\"}}"),
            "tools/list" => JsonDocument.Parse("{\"tools\":[{\"name\":\"t\",\"inputSchema\":{}}]}"),
            _ => throw new Exception(req.Method),
        };
        return Task.FromResult(new JsonRpc.Response { Id = req.Id, Result = doc.RootElement.Clone() });
    }
    public Task SendNotificationAsync(JsonRpc.Notification n, CancellationToken ct) => Task.CompletedTask;
    public void Dispose() { }
}

sealed class MockProvider : IModelProvider
{
    public string ProviderId => "mock";
    public IReadOnlyList<ModelDescriptor> Models { get; } = new List<ModelDescriptor>
    {
        new() { ModelId = "a", ContextWindowTokens = 1000, MaxOutputTokens = 100, SupportsToolCalling = true },
        new() { ModelId = "b", ContextWindowTokens = 2000, MaxOutputTokens = 200, CapabilityTags = new HashSet<string> { "fast" } },
        new() { ModelId = "c", ContextWindowTokens = 3000, MaxOutputTokens = 300 },
    };
    public Task<ChatCompletionResponse> CompleteAsync(ChatCompletionRequest r, CancellationToken ct) =>
        Task.FromResult(new ChatCompletionResponse { ModelId = r.ModelId, Message = ChatMessage.Assistant("OK"), Usage = new TokenUsage { InputTokens = 1, OutputTokens = 1 }, FinishReason = "stop" });
    public IAsyncEnumerable<ChatCompletionChunk> StreamAsync(ChatCompletionRequest r, CancellationToken ct) => throw new NotSupportedException();
}
