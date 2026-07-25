using System.Text.Json;
using HarnessAgent.Core.Provider;
using HarnessAgent.Core.Memory;
using HarnessAgent.Core.Compaction;
using HarnessAgent.Core.Mcp;

Console.WriteLine("=== HarnessAgent CLI Test Suite ===\n");
var pass = 0; const int target = 5;

// L0
Console.WriteLine("── L0: Provider ──");
TestProvider(); pass++;
Console.WriteLine($"  OK ({pass}/{target})\n");

// L1
Console.WriteLine("── L1: Memory ──");
TestMemory(); pass++;
Console.WriteLine($"  OK ({pass}/{target})\n");

// L2
Console.WriteLine("── L2: Compaction ──");
TestCompaction(); pass++;
Console.WriteLine($"  OK ({pass}/{target})\n");

// L3
Console.WriteLine("── L3: MCP Client ──");
await TestMcpClient(); pass++;
Console.WriteLine($"  OK ({pass}/{target})\n");

// L4
Console.WriteLine("── L4: MCP Server ──");
await TestMcpServer(); pass++;
Console.WriteLine($"  OK ({pass}/{target})\n");

Console.WriteLine($"=== All {pass}/{target} ✅ ===");

// ──── Tests ────

static void TestProvider()
{
    var r = new ProviderRegistry();
    r.Register(new MockProvider());
    if (r.ListModels().Count != 3) throw new Exception("count");
    if (r.ListModelsByCapability("fast").Count != 1) throw new Exception("capability");
    if (r.ResolveModel("deepseek-v4-pro") == null) throw new Exception("resolve");
}

static void TestMemory()
{
    var lib = new MemoryLibrary();
    var book = lib.CreateBook("test");
    var e1 = lib.AddEntry(book, MemoryKind.Fact, "DeepSeek V4 has 128K tokens");
    lib.AddEntry(book, MemoryKind.Fact, "HarnessAgent progress");
    if (lib.Search("DeepSeek 128K").Count == 0) throw new Exception("search");
    if (lib.SearchRegex(@"\d+K\s*tokens").Count == 0) throw new Exception("regex");
    lib.AddRelation(e1.EntryId, e1.EntryId, "self-test");
}

static void TestCompaction()
{
    var e = new CompactionEngine();
    if (e.Evaluate(38400, 128000).Tier != CompactionTier.None) throw new Exception("30%");
    if (e.Evaluate(70400, 128000).Tier != CompactionTier.Soft) throw new Exception("55%");
    var d = e.Evaluate(96000, 128000);
    if (d.Tier != CompactionTier.Aggressive || d.TailTokenBudget != 12800) throw new Exception("75%");
    if (e.Evaluate(115200, 128000).Tier != CompactionTier.Force) throw new Exception("90%");
}

static async Task TestMcpClient()
{
    var (r, _, _, _) = JsonRpc.Parse("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/list\"}");
    if (r?.Method != "tools/list") throw new Exception("parse");

    using var t = new MockMcpTransport();
    using var c = new McpClient(t);
    if (!(await c.InitializeAsync()).SupportsTools) throw new Exception("init");
    if ((await c.ListToolsAsync()).Count != 2) throw new Exception("list");
    if ((await c.CallToolAsync("gh_issue_create", JsonSerializer.SerializeToElement(new { }))).IsError)
        throw new Exception("call");
}

static async Task TestMcpServer()
{
    var s = new McpServer();
    s.RegisterTool("add", "Add two numbers",
        JsonSerializer.SerializeToElement(new { type = "object", properties = new { a = new { type = "number" }, b = new { type = "number" } } }),
        (args, ct) => Task.FromResult((args.GetProperty("a").GetInt32() + args.GetProperty("b").GetInt32()).ToString()));

    var init = await s.HandleRequestAsync("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\"}");
    if (JsonRpc.Parse(init).Response == null) throw new Exception("init");

    var tl = await s.HandleRequestAsync("{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/list\"}");
    if (JsonRpc.Parse(tl).Response!.Result.GetProperty("tools").GetArrayLength() != 1) throw new Exception("list");

    var call = await s.HandleRequestAsync("{\"jsonrpc\":\"2.0\",\"id\":3,\"method\":\"tools/call\",\"params\":{\"name\":\"add\",\"arguments\":{\"a\":3,\"b\":4}}}");
    var text = JsonRpc.Parse(call).Response!.Result.GetProperty("content")[0].GetProperty("text").GetString();
    if (text != "7") throw new Exception($"3+4 should be 7, got {text}");
}

// ──── Mocks ────

sealed class MockMcpTransport : IMcpTransport
{
    public Task<JsonRpc.Response> SendRequestAsync(JsonRpc.Request req, CancellationToken ct)
    {
        using var doc = req.Method switch
        {
            "initialize" => JsonDocument.Parse("{\"capabilities\":{\"tools\":{}},\"serverInfo\":{\"name\":\"mock\",\"version\":\"1.0\"}}"),
            "tools/list" => JsonDocument.Parse("{\"tools\":[{\"name\":\"gh_issue_create\",\"inputSchema\":{}},{\"name\":\"gh_pr_list\",\"inputSchema\":{}}]}"),
            "tools/call" => JsonDocument.Parse("{\"content\":[{\"type\":\"text\",\"text\":\"OK\"}]}"),
            _ => throw new Exception(req.Method),
        };
        return Task.FromResult(new JsonRpc.Response { Id = req.Id, Result = doc.RootElement.Clone() });
    }
    public Task SendNotificationAsync(JsonRpc.Notification n, CancellationToken ct) => Task.CompletedTask;
    public void Dispose() { }
}

sealed class MockProvider : IModelProvider
{
    public string ProviderId => "deepseek";
    public IReadOnlyList<ModelDescriptor> Models { get; } = new List<ModelDescriptor>
    {
        new() { ModelId = "deepseek-v4-pro", ContextWindowTokens = 128000, MaxOutputTokens = 32000, SupportsToolCalling = true },
        new() { ModelId = "deepseek-v4-flash", ContextWindowTokens = 128000, MaxOutputTokens = 8000, SupportsToolCalling = true, CapabilityTags = new HashSet<string> { "fast" } },
        new() { ModelId = "deepseek-reasoner", ContextWindowTokens = 64000, MaxOutputTokens = 32000 },
    };
    public Task<ChatCompletionResponse> CompleteAsync(ChatCompletionRequest r, CancellationToken ct) =>
        Task.FromResult(new ChatCompletionResponse { ModelId = r.ModelId, Message = ChatMessage.Assistant("OK"), Usage = new TokenUsage { InputTokens = 10, OutputTokens = 10 }, FinishReason = "stop" });
    public IAsyncEnumerable<ChatCompletionChunk> StreamAsync(ChatCompletionRequest r, CancellationToken ct) => throw new NotSupportedException();
}
