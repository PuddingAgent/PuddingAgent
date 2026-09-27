using Microsoft.Extensions.Logging.Abstractions;
using PuddingCode.Configuration;
using PuddingPlatform.Data.Dtos;
using PuddingPlatform.Services;

namespace PuddingPlatformTests.Services;

[TestClass]
public sealed class LlmProviderFileServiceTests
{
    [TestMethod]
    public async Task NativePatch_PreservesAdvancedSettings_AndExplicitlyChangesSecrets()
    {
        var root = Path.Combine(Path.GetTempPath(), "pudding-native-config-" + Guid.NewGuid().ToString("N"));
        var paths = PuddingDataPaths.FromRoot(root);
        try
        {
            await AtomicFileWriter.WriteJsonAsync(paths.SystemConfigFile("llm.providers.json"), new PuddingLlmProvidersConfig {
                Providers = [new() { ProviderId = "test", Name = "Old", BaseUrl = "https://example.invalid/v1", ApiKeyRef = "vault:fixture",
                    MaxConcurrentRequests = 7, TokensPerMinute = 12345, Models = [new() { ModelId = "model", Name = "Old", Protocol = "openai",
                        PricePer1MInputTokens = 1.23m, MaxInputTokens = 1000, CapabilityTags = ["tools"] },
                        new() { ModelId = "other", Name = "Other", Protocol = "openai" }] }] });
            var service = new LlmProviderFileService(paths, NullLogger<LlmProviderFileService>.Instance);
            await service.SaveChatModelSettingsAsync("test", "New", "https://example.invalid/v1", true,
                "model", "New model", "responses", 32768, 4096, null, false, CancellationToken.None);
            var provider = (await service.LoadAsync()).Providers.Single();
            Assert.AreEqual("vault:fixture", provider.ApiKeyRef); Assert.AreEqual(7, provider.MaxConcurrentRequests);
            Assert.AreEqual(12345L, provider.TokensPerMinute); Assert.AreEqual(2, provider.Models.Count);
            var model = provider.Models.Single(m => m.ModelId == "model");
            Assert.AreEqual(1.23m, model.PricePer1MInputTokens); Assert.AreEqual(1000, model.MaxInputTokens);
            CollectionAssert.AreEqual(new[] { "tools" }, model.CapabilityTags);
            await service.SaveChatModelSettingsAsync("test", "New", "https://example.invalid/v1", true,
                "model", "New model", "responses", 32768, 4096, "fixture-only", false, CancellationToken.None);
            provider = (await service.LoadAsync()).Providers.Single();
            Assert.AreEqual("fixture-only", provider.ApiKey); Assert.IsNull(provider.ApiKeyRef);
            Assert.IsTrue((await service.ListProvidersAsync()).Single().HasApiKey);
            await service.SaveChatModelSettingsAsync("test", "New", "https://example.invalid/v1", true,
                "model", "New model", "responses", 32768, 4096, null, true, CancellationToken.None);
            Assert.IsFalse((await service.ListProvidersAsync()).Single().HasApiKey);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task ProviderLimits_ShouldRoundTripThroughConfigFile()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "pudding-llm-provider-tests",
            Guid.NewGuid().ToString("N"));

        try
        {
            Directory.CreateDirectory(root);
            var paths = PuddingDataPaths.FromRoot(root);
            await AtomicFileWriter.WriteJsonAsync(
                paths.SystemConfigFile("llm.providers.json"),
                new PuddingLlmProvidersConfig
                {
                    Providers =
                    [
                        new PuddingLlmProviderConfig
                        {
                            ProviderId = "baseline",
                            Name = "Baseline",
                            BaseUrl = "https://example.invalid/v1",
                            Models =
                            [
                                new PuddingLlmModelConfig
                                {
                                    ModelId = "baseline-model",
                                    Name = "Baseline Model",
                                    Protocol = "openai",
                                },
                            ],
                        },
                    ],
                    Profiles = new Dictionary<string, PuddingLlmProfileConfig>
                    {
                        ["baseline"] = new()
                        {
                            ProviderId = "baseline",
                            ModelId = "baseline-model",
                        },
                    },
                    Roles = new PuddingLlmRoleConfig
                    {
                        Conscious = "baseline",
                        Subconscious = "baseline",
                    },
                });
            var service = new LlmProviderFileService(
                paths,
                NullLogger<LlmProviderFileService>.Instance);

            var created = await service.CreateProviderAsync(new UpsertLlmProviderRequest(
                ProviderId: "moonshot",
                Name: "Moonshot（Kimi，按量付费）",
                BaseUrl: "https://api.moonshot.cn/v1",
                ApiKey: null,
                Description: "Moonshot Kimi K3 按量付费",
                IsEnabled: true,
                MaxConcurrentRequests: 50,
                TokensPerMinute: 2_000_000,
                RequestsPerMinute: 200));

            var listed = (await service.ListProvidersAsync()).Single(p => p.ProviderId == "moonshot");
            var detailed = await service.GetProviderAsync("moonshot");
            var config = await service.LoadAsync();
            var persisted = config.Providers.Single(p => p.ProviderId == "moonshot");

            Assert.AreEqual(50, created.MaxConcurrentRequests);
            Assert.AreEqual(2_000_000, listed.TokensPerMinute);
            Assert.AreEqual(200, detailed!.RequestsPerMinute);
            Assert.AreEqual("Moonshot Kimi K3 按量付费", persisted.Description);
            Assert.AreEqual(50, persisted.MaxConcurrentRequests);
            Assert.AreEqual(2_000_000, persisted.TokensPerMinute);
            Assert.AreEqual(200, persisted.RequestsPerMinute);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }
}
