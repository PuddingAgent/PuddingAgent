using System.Text.Json;
using PuddingCode.Abstractions;
using PuddingCode.Platform;
using PuddingCode.Tools;
using PuddingRuntime.Services.Tools;

namespace PuddingRuntimeTests.Tools;

/// <summary>
/// list_llm_providers 工具合同测试：
///   · 路由表条目与 ambiguous_model_ids 与 FileLlmResolver 解析语义一致；
///   · 输出严禁包含 apiKey/baseUrl 等敏感字段；
///   · 输出的 route 字段可直接用于 provider/model 解析（不再撞 multiple providers）。
/// </summary>
[TestClass]
public sealed class ListLlmProvidersToolTests
{
    private const string SecretBaseUrl = "https://secret.example.invalid/v1";

    [TestMethod]
    public async Task DefaultListing_MarksAmbiguousModelIds_AndExcludesFilteredModels()
    {
        var tool = CreateTool(out var config);
        var result = await ExecuteAsync(tool, "{}");

        Assert.IsTrue(result.Success, result.Error);
        using var doc = JsonDocument.Parse(result.Output);
        var root = doc.RootElement;

        var ambiguous = root.GetProperty("ambiguousModelIds").EnumerateArray()
            .Select(e => e.GetString()!)
            .ToArray();
        // deepseek-v4-flash 同时注册在 deepseek 与 opencode（均启用）→ 歧义；
        // glm-5.3 的第二个注册在禁用 provider bigmodel 上 → 不算歧义。
        CollectionAssert.AreEquivalent(new[] { "deepseek-v4-flash" }, ambiguous);

        var models = root.GetProperty("models").EnumerateArray().ToList();
        // 默认过滤：弃用（deepseek-chat-pro）、embedding（embedding-3）、
        // 禁用 provider（bigmodel/glm-5.3）都不出现。
        Assert.AreEqual(5, models.Count);
        Assert.IsTrue(models.All(m => !m.GetProperty("isDeprecated").GetBoolean()));
        Assert.IsTrue(models.All(m => !m.GetProperty("isEmbedding").GetBoolean()));
        Assert.IsTrue(models.All(m => m.GetProperty("isEnabled").GetBoolean()));

        var flash = models.Single(m => m.GetProperty("route").GetString() == "deepseek/deepseek-v4-flash");
        Assert.IsTrue(flash.GetProperty("isAmbiguous").GetBoolean());
        Assert.AreEqual("deepseek/deepseek-v4-flash", flash.GetProperty("route").GetString());
        Assert.AreEqual("deepseek", flash.GetProperty("providerId").GetString());
        Assert.AreEqual("openai", flash.GetProperty("protocol").GetString());

        var glm = models.Single(m => m.GetProperty("modelId").GetString() == "glm-5.3");
        Assert.IsFalse(glm.GetProperty("isAmbiguous").GetBoolean());

        var price = flash.GetProperty("pricePer1MTokens");
        Assert.AreEqual(0.5m, price.GetProperty("input").GetDecimal());
        Assert.AreEqual(2.0m, price.GetProperty("output").GetDecimal());

        var providers = root.GetProperty("providers").EnumerateArray().ToList();
        Assert.AreEqual(2, providers.Count);
        Assert.AreEqual("DeepSeek", providers[0].GetProperty("name").GetString());
    }

    [TestMethod]
    public async Task EveryRoute_ResolvesThroughLlmConfigService()
    {
        var tool = CreateTool(out var config);
        var result = await ExecuteAsync(tool, "{}");

        using var doc = JsonDocument.Parse(result.Output);
        foreach (var model in doc.RootElement.GetProperty("models").EnumerateArray())
        {
            var route = model.GetProperty("route").GetString()!;
            var parts = route.Split('/', 2);
            Assert.IsNotNull(
                config.Resolve(parts[0], parts[1]),
                $"route '{route}' from tool output must resolve like a spawn_sub_agent route");
        }
    }

    [TestMethod]
    public async Task ModelIdFilter_AcceptsBareIdAndFullRoute()
    {
        var tool = CreateTool(out _);

        var bare = await ExecuteAsync(tool, """{"model_id":"glm-5.3"}""");
        using (var doc = JsonDocument.Parse(bare.Output))
        {
            var models = doc.RootElement.GetProperty("models").EnumerateArray().ToList();
            Assert.AreEqual(1, models.Count);
            Assert.AreEqual("opencode/glm-5.3", models[0].GetProperty("route").GetString());
        }

        var fullRoute = await ExecuteAsync(tool, """{"model_id":"deepseek/deepseek-v4-flash"}""");
        using (var doc = JsonDocument.Parse(fullRoute.Output))
        {
            var models = doc.RootElement.GetProperty("models").EnumerateArray().ToList();
            Assert.AreEqual(1, models.Count);
            Assert.AreEqual("deepseek/deepseek-v4-flash", models[0].GetProperty("route").GetString());
        }
    }

    [TestMethod]
    public async Task CapabilityFilter_RequiresAllTags()
    {
        var tool = CreateTool(out _);

        var single = await ExecuteAsync(tool, """{"capability":"fast"}""");
        using (var doc = JsonDocument.Parse(single.Output))
        {
            // 'fast' 同时命中 deepseek 与 opencode 两个注册——歧义 modelId
            // 按能力过滤时应看到全部可用路由。
            var models = doc.RootElement.GetProperty("models").EnumerateArray().ToList();
            Assert.AreEqual(2, models.Count);
            Assert.IsTrue(models.All(m => m.GetProperty("modelId").GetString() == "deepseek-v4-flash"));
            Assert.IsTrue(models.All(m => m.GetProperty("isAmbiguous").GetBoolean()));
        }

        var combined = await ExecuteAsync(tool, """{"capability":"fast,nonexistent-tag"}""");
        using (var doc = JsonDocument.Parse(combined.Output))
        {
            Assert.AreEqual(0, doc.RootElement.GetProperty("models").EnumerateArray().Count());
            Assert.AreEqual(ToolResultStatuses.NoMatch, combined.Status);
        }
    }

    [TestMethod]
    public async Task IncludeFlags_SurfaceDeprecatedEmbeddingAndDisabledProviders()
    {
        var tool = CreateTool(out _);
        var result = await ExecuteAsync(
            tool,
            """{"include_disabled":true,"include_deprecated":true,"include_embeddings":true}""");

        Assert.IsTrue(result.Success, result.Error);
        using var doc = JsonDocument.Parse(result.Output);
        var models = doc.RootElement.GetProperty("models").EnumerateArray().ToList();
        Assert.AreEqual(8, models.Count);

        var bigmodelGlm = models.Single(m =>
            m.GetProperty("providerId").GetString() == "bigmodel"
            && m.GetProperty("modelId").GetString() == "glm-5.3");
        Assert.IsFalse(bigmodelGlm.GetProperty("isEnabled").GetBoolean());
        // 禁用 provider 上的重复注册不构成歧义（与 FileLlmResolver 语义一致）。
        Assert.IsFalse(bigmodelGlm.GetProperty("isAmbiguous").GetBoolean());

        Assert.IsTrue(models.Any(m => m.GetProperty("isDeprecated").GetBoolean()));
        Assert.IsTrue(models.Any(m => m.GetProperty("isEmbedding").GetBoolean()));
    }

    [TestMethod]
    public async Task Output_NeverContainsBaseUrlOrApiKeyFields()
    {
        var tool = CreateTool(out _);
        var result = await ExecuteAsync(
            tool,
            """{"include_disabled":true,"include_deprecated":true,"include_embeddings":true}""");

        Assert.IsTrue(result.Success, result.Error);
        Assert.DoesNotContain(SecretBaseUrl, result.Output);
        Assert.DoesNotContain("baseUrl", result.Output);
        Assert.DoesNotContain("apiKey", result.Output);
    }

    [TestMethod]
    public async Task MissingConfigService_FailsWithStableError()
    {
        var tool = new ListLlmProvidersTool(llmConfigService: null);
        var result = await ExecuteAsync(tool, "{}");

        Assert.IsFalse(result.Success);
        StringAssert.Contains(result.Error!, "LLM config service is not available");
    }

    private static ListLlmProvidersTool CreateTool(out FakeLlmConfigService config)
    {
        config = new FakeLlmConfigService();
        return new ListLlmProvidersTool(config);
    }

    private static Task<ToolExecutionResult> ExecuteAsync(ListLlmProvidersTool tool, string argumentsJson)
        => tool.ExecuteAsync(new ToolExecutionRequest
        {
            ToolCallId = "test-call",
            ArgumentsJson = argumentsJson,
            Context = new ToolExecutionContext
            {
                WorkspaceId = "default",
                SessionId = "session-1",
                AgentInstanceId = "agent-a",
            },
        });

    private sealed class FakeLlmConfigService : ILlmConfigService
    {
        private readonly List<LlmProviderInfo> _providers =
        [
            new()
            {
                ProviderId = "deepseek",
                Name = "DeepSeek",
                BaseUrl = SecretBaseUrl,
                IsEnabled = true,
                HasApiKey = true,
            },
            new()
            {
                ProviderId = "opencode",
                Name = "OpenCode",
                BaseUrl = SecretBaseUrl,
                IsEnabled = true,
                HasApiKey = true,
            },
            new()
            {
                ProviderId = "bigmodel",
                Name = "智谱",
                BaseUrl = SecretBaseUrl,
                IsEnabled = false,
                HasApiKey = true,
            },
            new()
            {
                ProviderId = "bigmodel-embeddings",
                Name = "智谱 Embedding",
                BaseUrl = SecretBaseUrl,
                IsEnabled = true,
                HasApiKey = true,
            },
        ];

        private readonly List<LlmModelInfo> _models =
        [
            Model("deepseek", "deepseek-v4-flash", protocol: "openai", tags: ["fast", "cheap"],
                inputPrice: 0.5m, outputPrice: 2.0m, cacheHitPrice: 0.1m, sortOrder: 10),
            Model("deepseek", "deepseek-v4-pro", protocol: "openai", tags: ["reasoning-high", "expensive"],
                inputPrice: 4.0m, outputPrice: 16.0m, cacheHitPrice: 1.0m, sortOrder: 11),
            Model("deepseek", "deepseek-chat-pro", protocol: "openai", tags: ["fast"],
                inputPrice: 1.0m, outputPrice: 4.0m, isDeprecated: true, sortOrder: 12),
            Model("opencode", "deepseek-v4-flash", protocol: "openai", tags: ["fast"],
                inputPrice: 0.6m, outputPrice: 2.2m, sortOrder: 20),
            Model("opencode", "glm-5.3", protocol: "openai", tags: ["code", "cheap"],
                inputPrice: 0.3m, outputPrice: 1.2m, sortOrder: 21),
            Model("opencode", "kimi-k3", protocol: "openai", tags: ["long-context"],
                inputPrice: 2.0m, outputPrice: 8.0m, sortOrder: 22),
            Model("bigmodel", "glm-5.3", protocol: "openai", tags: ["code"],
                inputPrice: 0.3m, outputPrice: 1.2m, sortOrder: 30),
            Model("bigmodel-embeddings", "embedding-3", protocol: "openai", tags: ["embedding"],
                inputPrice: 0.5m, outputPrice: 0m, isEmbedding: true, sortOrder: 31),
        ];

        public IReadOnlyList<LlmProviderInfo> GetEnabledProviders()
            => _providers.Where(p => p.IsEnabled).ToList();

        public IReadOnlyList<LlmModelInfo> GetAllModels() => _models;

        // 与 FileLlmResolver 的解析边界一致：启用 provider、非弃用模型才可解析。
        public LlmConfig? Resolve(string providerId, string modelId)
        {
            var provider = _providers.FirstOrDefault(p =>
                string.Equals(p.ProviderId, providerId, StringComparison.OrdinalIgnoreCase));
            if (provider is null || !provider.IsEnabled)
                return null;

            var model = _models.FirstOrDefault(m =>
                string.Equals(m.ProviderId, providerId, StringComparison.OrdinalIgnoreCase)
                && string.Equals(m.ModelId, modelId, StringComparison.OrdinalIgnoreCase));
            if (model is null || model.IsDeprecated)
                return null;

            return new LlmConfig
            {
                Endpoint = SecretBaseUrl,
                ModelId = model.ModelId,
                Protocol = model.Protocol,
            };
        }

        public LlmProfileInfo? ResolveProfile(string profileId) => null;
        public LlmConfig? GetMemoryConfig() => null;
        public LlmConfig? GetEmbeddingConfig() => null;
        public LlmProviderStrategy? GetProviderStrategy(string providerId) => null;
        public LlmProviderStrategy? GetModelStrategy(string providerId, string modelId) => null;
        public void Reload(object config) { }

        private static LlmModelInfo Model(
            string providerId,
            string modelId,
            string protocol,
            string[] tags,
            decimal inputPrice,
            decimal outputPrice,
            decimal cacheHitPrice = 0m,
            bool isDeprecated = false,
            bool isEmbedding = false,
            int sortOrder = 100)
            => new()
            {
                ProviderId = providerId,
                ModelId = modelId,
                Name = modelId,
                Protocol = protocol,
                CapabilityTags = [.. tags],
                InputPricePer1MTokens = inputPrice,
                OutputPricePer1MTokens = outputPrice,
                CacheHitPricePer1MTokens = cacheHitPrice,
                IsDeprecated = isDeprecated,
                IsEmbedding = isEmbedding,
                SortOrder = sortOrder,
            };
    }
}
