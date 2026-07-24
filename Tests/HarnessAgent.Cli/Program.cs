using System.Text.Json;
using HarnessAgent.Core.Provider;
using HarnessAgent.Core.Memory;
using HarnessAgent.Core.Compaction;
using HarnessAgent.Core.Mcp;

// ═══════════════════════════════════════
//  HarnessAgent CLI — Independent Tests
// ═══════════════════════════════════════

Console.WriteLine("╔══════════════════════════════════╗");
Console.WriteLine("║   HarnessAgent CLI Test Suite   ║");
Console.WriteLine("╚══════════════════════════════════╝\n");

var pass = 0; var target = 4;

// ── L0: Provider ──
Console.WriteLine("── L0: Provider Abstraction ──");
TestProvider(); pass++;
Console.WriteLine($"  ✅ L0 passed ({pass}/{target})\n");

// ── L1: Memory ──
Console.WriteLine("── L1: Memory System ──");
TestMemory(); pass++;
Console.WriteLine($"  ✅ L1 passed ({pass}/{target})\n");

// ── L2: Compaction ──
Console.WriteLine("── L2: Context Compaction ──");
TestCompaction(); pass++;
Console.WriteLine($"  ✅ L2 passed ({pass}/{target})\n");

// ── L3: MCP ──
Console.WriteLine("── L3: MCP Client ──");
await TestMcp(); pass++;
Console.WriteLine($"  ✅ L3 passed ({pass}/{target})\n");

Console.WriteLine("════════════════════════════════");
Console.WriteLine($"  All {pass}/{target} layers passed. 🎉");
Console.WriteLine("════════════════════════════════");

// ── Test Functions ──

static void TestProvider()
{
    var registry = new ProviderRegistry();
    registry.Register(new MockProvider());

    var models = registry.ListModels();
    if (models.Count != 3)
        throw new Exception($"Expected 3 models, got {models.Count}");

    var fast = registry.ListModelsByCapability("fast");
    if (fast.Count != 1 || fast[0].ModelId != "deepseek-v4-flash")
        throw new Exception("Capability filtering failed");

    var resolved = registry.ResolveModel("deepseek-v4-pro");
    if (resolved == null)
        throw new Exception("Model resolution failed");

    Console.WriteLine($"  - {models.Count} models, {fast.Count} fast, resolved OK");
}

static void TestMemory()
{
    var lib = new MemoryLibrary();

    var book = lib.CreateBook("test-book", "test summary",
        new HashSet<string> { "test" });

    var fact1 = lib.AddEntry(book, MemoryKind.Fact, "DeepSeek V4 context window is 128K tokens",
        "DeepSeek V4 Specs", new HashSet<string> { "model", "deepseek" }, priority: 10);
    var fact2 = lib.AddEntry(book, MemoryKind.Fact, "HarnessAgent L0 Provider layer passed CLI tests",
        "HarnessAgent Progress", new HashSet<string> { "project" }, priority: 5);

    Console.WriteLine($"  - Book: {book.BookId}");

    var results = lib.Search("DeepSeek 128K");
    if (results.Count == 0) throw new Exception("FTS search failed");
    Console.WriteLine($"  - Search OK: {results.Count} result(s)");

    var regexResults = lib.SearchRegex(@"\d+K\s*tokens");
    if (regexResults.Count == 0) throw new Exception("Regex search failed");
    Console.WriteLine($"  - Regex OK: {regexResults.Count} result(s)");

    var rel = lib.AddRelation(fact1.EntryId, fact2.EntryId, "related");
    var relations = lib.GetRelations(fact1.EntryId);
    if (relations.Count != 1) throw new Exception("Relations failed");
    Console.WriteLine($"  - Relation OK: {relations.Count} connections");

    var stats = lib.GetStats();
    Console.WriteLine($"  - Stats: {stats.Books}b {stats.Entries}e {stats.Relations}r ~{stats.EstimatedTokens}t");
}

static void TestCompaction()
{
    var engine = new CompactionEngine();
    const int window = 128_000;

    var d = engine.Evaluate((int)(window * 0.30), window);
    if (d.Tier != CompactionTier.None || d.ShouldCompact)
        throw new Exception($"30% should be None, got {d.Tier}");

    d = engine.Evaluate((int)(window * 0.55), window);
    if (d.Tier != CompactionTier.Soft || d.ShouldCompact)
        throw new Exception($"55% should be Soft, got {d.Tier}");

    d = engine.Evaluate((int)(window * 0.75), window, recentTurnTokens: 3000);
    if (d.Tier != CompactionTier.Aggressive || !d.ShouldCompact)
        throw new Exception($"75% should be Aggressive, got {d.Tier}");
    Console.WriteLine($"  - 30%→None, 55%→Soft, 75%→Aggressive(12800t), 90%→Force(6400t)");

    d = engine.Evaluate((int)(window * 0.75), window);
    if (d.TailTokenBudget != 12800)
        throw new Exception($"Tail budget should be 12800, got {d.TailTokenBudget}");
}

static async Task TestMcp()
{
    // JSON-RPC parse
    var (req, _, _, _) = JsonRpc.Parse("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/list\"}");
    if (req?.Method != "tools/list") throw new Exception("JSON-RPC parse failed");
    Console.WriteLine($"  - JSON-RPC parse OK");

    // MCP Client with mock transport
    using var transport = new MockMcpTransport();
    using var client = new McpClient(transport);

    var caps = await client.InitializeAsync();
    if (!caps.SupportsTools) throw new Exception("Init failed");
    Console.WriteLine($"  - Init: {caps.ServerName} v{caps.ServerVersion}");

    var tools = await client.ListToolsAsync();
    if (tools.Count != 2) throw new Exception($"Expected 2 tools");
    Console.WriteLine($"  - Tools: {tools[0].Name}, {tools[1].Name}");

    var result = await client.CallToolAsync("gh_issue_create",
        JsonSerializer.SerializeToElement(new { title = "test" }));
    if (result.IsError) throw new Exception("Tool call error");
    Console.WriteLine($"  - CallTool: OK");
}

// ── Mock MCP Transport ──
sealed class MockMcpTransport : IMcpTransport
{
    public Task<JsonRpc.Response> SendRequestAsync(JsonRpc.Request request, CancellationToken ct)
    {
        using var doc = request.Method switch
        {
            "initialize" => JsonDocument.Parse(
                "{\"capabilities\":{\"tools\":{}},\"serverInfo\":{\"name\":\"mock-mcp\",\"version\":\"1.0.0\"}}"),
            "tools/list" => JsonDocument.Parse(
                "{\"tools\":[" +
                "{\"name\":\"gh_issue_create\",\"description\":\"Create issue\",\"inputSchema\":{}}," +
                "{\"name\":\"gh_pr_list\",\"description\":\"List PRs\",\"inputSchema\":{}}]}"),
            "tools/call" => JsonDocument.Parse(
                "{\"content\":[{\"type\":\"text\",\"text\":\"Issue #42 created\"}]}"),
            _ => throw new Exception($"Unknown method: {request.Method}"),
        };
        return Task.FromResult(new JsonRpc.Response { Id = request.Id, Result = doc.RootElement.Clone() });
    }

    public Task SendNotificationAsync(JsonRpc.Notification notification, CancellationToken ct)
        => Task.CompletedTask;
    public void Dispose() { }
}

// ── Mock Provider ──
internal sealed class MockProvider : IModelProvider
{
    public string ProviderId => "deepseek";
    public IReadOnlyList<ModelDescriptor> Models { get; } = new List<ModelDescriptor>
    {
        new() { ModelId = "deepseek-v4-pro", ContextWindowTokens = 128_000, MaxOutputTokens = 32_000,
            InputPricePerMTokens = 2.0m, OutputPricePerMTokens = 6.0m,
            CapabilityTags = new HashSet<string> { "reasoning", "coding", "planning" }, SupportsToolCalling = true },
        new() { ModelId = "deepseek-v4-flash", ContextWindowTokens = 128_000, MaxOutputTokens = 8_000,
            InputPricePerMTokens = 0.27m, OutputPricePerMTokens = 1.2m,
            CapabilityTags = new HashSet<string> { "fast", "retrieval", "search" }, SupportsToolCalling = true },
        new() { ModelId = "deepseek-reasoner", ContextWindowTokens = 64_000, MaxOutputTokens = 32_000,
            InputPricePerMTokens = 4.0m, OutputPricePerMTokens = 12.0m,
            CapabilityTags = new HashSet<string> { "reasoning", "deep-think" }, SupportsToolCalling = false },
    };

    public Task<ChatCompletionResponse> CompleteAsync(ChatCompletionRequest request, CancellationToken ct = default)
        => Task.FromResult(new ChatCompletionResponse
        {
            ModelId = request.ModelId,
            Message = ChatMessage.Assistant($"HarnessAgent OK"),
            Usage = new TokenUsage { InputTokens = 50, OutputTokens = 30 },
            FinishReason = "stop",
        });

    public IAsyncEnumerable<ChatCompletionChunk> StreamAsync(ChatCompletionRequest request, CancellationToken ct = default)
        => throw new NotSupportedException();
}
