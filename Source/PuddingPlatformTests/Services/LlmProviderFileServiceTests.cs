using Microsoft.Extensions.Logging.Abstractions;
using PuddingCode.Configuration;
using PuddingPlatform.Data.Dtos;
using PuddingPlatform.Services;

namespace PuddingPlatformTests.Services;

[TestClass]
public sealed class LlmProviderFileServiceTests
{
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
                Protocol: "openai",
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
