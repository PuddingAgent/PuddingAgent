using HarnessAgent.Core.Provider;
using HarnessAgent.Core.Memory;

// ═══════════════════════════════════════
//  HarnessAgent CLI — Independent Tests
// ═══════════════════════════════════════

Console.WriteLine("╔══════════════════════════════════╗");
Console.WriteLine("║   HarnessAgent CLI Test Suite   ║");
Console.WriteLine("╚══════════════════════════════════╝\n");

// ── L0: Provider ──
Console.WriteLine("── L0: Provider Abstraction ──");
TestProvider();
Console.WriteLine("  ✅ L0 passed\n");

// ── L1: Memory ──
Console.WriteLine("── L1: Memory System ──");
TestMemory();
Console.WriteLine("  ✅ L1 passed\n");

Console.WriteLine("════════════════════════════════");
Console.WriteLine("  All layers passed. 🎉");
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

    // 1. Book management
    var book = lib.CreateBook("测试记忆库", "用于验证记忆系统核心功能",
        new HashSet<string> { "test", "harness" });
    Console.WriteLine($"  - Book: {book.BookId} '{book.Title}'");

    // 2. Entry creation
    var fact1 = lib.AddEntry(book, MemoryKind.Fact, "DeepSeek V4 上下文窗口为 128K tokens",
        "DeepSeek V4 规格", new HashSet<string> { "model", "deepseek" }, priority: 10);
    var fact2 = lib.AddEntry(book, MemoryKind.Fact, "HarnessAgent L0 Provider 层已通过 CLI 测试",
        "HarnessAgent 进度", new HashSet<string> { "project", "milestone" }, priority: 5);

    Console.WriteLine($"  - Entries: {fact1.EntryId}, {fact2.EntryId}");

    // 3. Search
    var results = lib.Search("DeepSeek 128K");
    if (results.Count == 0)
        throw new Exception("FTS search failed");
    Console.WriteLine($"  - Search 'DeepSeek 128K': {results.Count} result(s)");

    // 4. Regex search
    var regexResults = lib.SearchRegex(@"\d+K\s*tokens");
    if (regexResults.Count == 0)
        throw new Exception("Regex search failed");
    Console.WriteLine($"  - Regex '\\d+K tokens': {regexResults.Count} result(s)");

    // 5. Relations
    var rel = lib.AddRelation(fact1.EntryId, fact2.EntryId, "related",
        "Both describe HarnessAgent project aspects");
    var relations = lib.GetRelations(fact1.EntryId);
    if (relations.Count != 1)
        throw new Exception("Relations failed");
    Console.WriteLine($"  - Relation: {rel.RelationType} ({relations.Count} connections)");

    // 6. Stats
    var stats = lib.GetStats();
    Console.WriteLine($"  - Stats: {stats.Books} books, {stats.Entries} entries, {stats.Relations} relations, ~{stats.EstimatedTokens} tokens");
}

// ── Mock Provider ──
internal sealed class MockProvider : IModelProvider
{
    public string ProviderId => "deepseek";

    public IReadOnlyList<ModelDescriptor> Models { get; } = new List<ModelDescriptor>
    {
        new()
        {
            ModelId = "deepseek-v4-pro", DisplayName = "DeepSeek V4 Pro",
            ContextWindowTokens = 128_000, MaxOutputTokens = 32_000,
            InputPricePerMTokens = 2.0m, OutputPricePerMTokens = 6.0m,
            CapabilityTags = new HashSet<string> { "reasoning", "coding", "planning" },
            SupportsToolCalling = true,
        },
        new()
        {
            ModelId = "deepseek-v4-flash", DisplayName = "DeepSeek V4 Flash",
            ContextWindowTokens = 128_000, MaxOutputTokens = 8_000,
            InputPricePerMTokens = 0.27m, OutputPricePerMTokens = 1.2m,
            CapabilityTags = new HashSet<string> { "fast", "retrieval", "search" },
            SupportsToolCalling = true,
        },
        new()
        {
            ModelId = "deepseek-reasoner", DisplayName = "DeepSeek Reasoner",
            ContextWindowTokens = 64_000, MaxOutputTokens = 32_000,
            InputPricePerMTokens = 4.0m, OutputPricePerMTokens = 12.0m,
            CapabilityTags = new HashSet<string> { "reasoning", "deep-think" },
            SupportsToolCalling = false,
        },
    };

    public Task<ChatCompletionResponse> CompleteAsync(ChatCompletionRequest request, CancellationToken ct = default)
    {
        return Task.FromResult(new ChatCompletionResponse
        {
            ModelId = request.ModelId,
            Message = ChatMessage.Assistant($"HarnessAgent: you asked \"{request.Messages[^1].Content[..Math.Min(60, request.Messages[^1].Content.Length)]}\""),
            Usage = new TokenUsage { InputTokens = 50, OutputTokens = 30 },
            FinishReason = "stop",
        });
    }

    public IAsyncEnumerable<ChatCompletionChunk> StreamAsync(ChatCompletionRequest request, CancellationToken ct = default)
        => throw new NotSupportedException();
}
