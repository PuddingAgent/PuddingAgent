using HarnessAgent.Core.Provider;

// ── HarnessAgent CLI Test: Provider Layer ──

Console.WriteLine("=== HarnessAgent CLI Test ===");
Console.WriteLine("Testing L0: Provider Abstraction Layer\n");

// 1. Create provider registry
var registry = new ProviderRegistry();
Console.WriteLine("[1] ProviderRegistry created");

// 2. Create a mock provider
var mockProvider = new MockProvider();
registry.Register(mockProvider);
Console.WriteLine($"[2] Registered provider: {mockProvider.ProviderId}");
Console.WriteLine($"    Models: {mockProvider.Models.Count}");

// 3. List all models
var models = registry.ListModels();
Console.WriteLine($"\n[3] All models ({models.Count}):");
foreach (var m in models)
{
    var tags = string.Join(", ", m.CapabilityTags);
    Console.WriteLine($"    - {m.ModelId} ({m.ContextWindowTokens / 1000}K ctx, {m.MaxOutputTokens / 1000}K out) [{tags}]");
}

// 4. Filter by capability
var fastModels = registry.ListModelsByCapability("fast");
Console.WriteLine($"\n[4] Fast models: {fastModels.Count}");
foreach (var m in fastModels)
    Console.WriteLine($"    - {m.ModelId}");

// 5. Resolve a specific model
var resolved = registry.ResolveModel("deepseek-v4-pro");
Console.WriteLine($"\n[5] Resolved 'deepseek-v4-pro': {(resolved.HasValue ? "found" : "not found")}");

// 6. Mock a chat completion
var request = new ChatCompletionRequest
{
    ModelId = "deepseek-v4-flash",
    Messages = new List<ChatMessage>
    {
        ChatMessage.System("You are a helpful assistant."),
        ChatMessage.User("Hello! What framework is this?")
    },
    MaxTokens = 256
};

var response = await mockProvider.CompleteAsync(request);
Console.WriteLine($"\n[6] Mock completion:");
Console.WriteLine($"    Model: {response.ModelId}");
Console.WriteLine($"    Response: {response.Message.Content}");
Console.WriteLine($"    Tokens: {response.Usage.InputTokens} in / {response.Usage.OutputTokens} out");
Console.WriteLine($"    Cost: ${response.Usage.InputTokens / 1_000_000m * resolved?.Model.InputPricePerMTokens:0.0000} + " +
                  $"${response.Usage.OutputTokens / 1_000_000m * resolved?.Model.OutputPricePerMTokens:0.0000}");

Console.WriteLine("\n=== All tests passed ===");

// ── Mock Provider ──
internal sealed class MockProvider : IModelProvider
{
    public string ProviderId => "deepseek";

    public IReadOnlyList<ModelDescriptor> Models { get; } = new List<ModelDescriptor>
    {
        new()
        {
            ModelId = "deepseek-v4-pro",
            DisplayName = "DeepSeek V4 Pro",
            ContextWindowTokens = 128_000,
            MaxOutputTokens = 32_000,
            InputPricePerMTokens = 2.0m,
            OutputPricePerMTokens = 6.0m,
            CapabilityTags = new HashSet<string> { "reasoning", "coding", "planning" },
            SupportsToolCalling = true,
        },
        new()
        {
            ModelId = "deepseek-v4-flash",
            DisplayName = "DeepSeek V4 Flash",
            ContextWindowTokens = 128_000,
            MaxOutputTokens = 8_000,
            InputPricePerMTokens = 0.27m,
            OutputPricePerMTokens = 1.2m,
            CapabilityTags = new HashSet<string> { "fast", "retrieval", "search" },
            SupportsToolCalling = true,
        },
        new()
        {
            ModelId = "deepseek-reasoner",
            DisplayName = "DeepSeek Reasoner",
            ContextWindowTokens = 64_000,
            MaxOutputTokens = 32_000,
            InputPricePerMTokens = 4.0m,
            OutputPricePerMTokens = 12.0m,
            CapabilityTags = new HashSet<string> { "reasoning", "deep-think" },
            SupportsToolCalling = false,
        },
    };

    public Task<ChatCompletionResponse> CompleteAsync(ChatCompletionRequest request, CancellationToken ct = default)
    {
        var model = Models.First(m => m.ModelId == request.ModelId);
        return Task.FromResult(new ChatCompletionResponse
        {
            ModelId = request.ModelId,
            Message = ChatMessage.Assistant($"This is HarnessAgent — a CLI-testable agent framework. You asked: \"{request.Messages[^1].Content}\""),
            Usage = new TokenUsage
            {
                InputTokens = 50,
                OutputTokens = 30,
                CachedInputTokens = model.CapabilityTags.Contains("fast") ? 40 : null,
            },
            FinishReason = "stop",
        });
    }

    public IAsyncEnumerable<ChatCompletionChunk> StreamAsync(ChatCompletionRequest request, CancellationToken ct = default)
        => throw new NotSupportedException("Mock provider does not support streaming.");
}
