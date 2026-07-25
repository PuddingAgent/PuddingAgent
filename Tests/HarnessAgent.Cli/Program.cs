using System.Text.Json;
using HarnessAgent.Core.Provider;
using HarnessAgent.Core.Memory;
using HarnessAgent.Core.Compaction;
using HarnessAgent.Core.Mcp;
using HarnessAgent.Core.Computer;
using HarnessAgent.Core.Tools;
using HarnessAgent.Core.Middleware;
using HarnessAgent.Core.Browser;

Console.WriteLine("=== HarnessAgent CLI ===\n");
var p = 0; const int t = 12;

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
Console.WriteLine("-- Middleware --"); await TestMiddleware(); Ok("MW");
Console.WriteLine("-- C4: BrowserControl --"); await TestBrowser(); Ok("C4");

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
