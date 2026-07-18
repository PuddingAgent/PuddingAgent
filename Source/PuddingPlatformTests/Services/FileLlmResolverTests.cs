using Microsoft.Extensions.Logging.Abstractions;
using PuddingCode.Abstractions;
using PuddingCode.Configuration;
using PuddingCode.Platform;
using PuddingPlatform.Services;

namespace PuddingPlatformTests.Services;

[TestClass]
public sealed class FileLlmResolverTests
{
    [TestMethod]
    public async Task ResolveRouteAsync_ExplicitRoute_ReturnsIdentityAndConfigFromSameSource()
    {
        var resolver = CreateResolver(CreateConfig());

        var route = await resolver.ResolveRouteAsync("provider-a/model-a");

        Assert.AreEqual("provider-a", route.ProviderId);
        Assert.AreEqual("model-a", route.ModelId);
        Assert.AreEqual("model-a", route.Config.ModelId);
    }

    [TestMethod]
    public async Task ResolveRouteAsync_DefaultProfile_PreservesConfiguredProviderIdentity()
    {
        var resolver = CreateResolver(CreateConfig(duplicateDefaultModel: true));

        var route = await resolver.ResolveRouteAsync();

        Assert.AreEqual("provider-b", route.ProviderId);
        Assert.AreEqual("shared-model", route.ModelId);
        Assert.AreEqual("shared-model", route.Config.ModelId);
    }

    [TestMethod]
    public async Task ResolveRouteAsync_PlainDuplicateModel_RequiresExplicitProvider()
    {
        var resolver = CreateResolver(CreateConfig(duplicateDefaultModel: true));

        var error = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => resolver.ResolveRouteAsync("shared-model"));

        StringAssert.Contains(error.Message, "exists under multiple providers");
    }

    [TestMethod]
    public async Task ResolveRouteAsync_CapabilityTags_SelectsConfiguredRoute()
    {
        var resolver = CreateResolver(CreateConfig());

        var route = await resolver.ResolveRouteAsync(
            requiredCapabilityTags: ["reasoning-high"]);

        Assert.AreEqual("provider-b", route.ProviderId);
        Assert.AreEqual("shared-model", route.ModelId);
    }

    [TestMethod]
    public async Task ResolveRouteAsync_RejectsConfigSnapshotWithDifferentModel()
    {
        var resolver = new FileLlmResolver(
            new MismatchingConfigService(),
            NullLogger<FileLlmResolver>.Instance);

        var error = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => resolver.ResolveRouteAsync("provider-a/model-a"));

        StringAssert.Contains(error.Message, "resolved mismatched config model");
    }

    private static FileLlmResolver CreateResolver(PuddingLlmProvidersConfig config)
        => new(
            new PuddingFileLlmConfigService(config),
            NullLogger<FileLlmResolver>.Instance);

    private static PuddingLlmProvidersConfig CreateConfig(bool duplicateDefaultModel = false)
    {
        var providerAModelId = duplicateDefaultModel ? "shared-model" : "model-a";
        return new PuddingLlmProvidersConfig
        {
            DefaultProviderId = "provider-a",
            DefaultModelId = providerAModelId,
            Providers =
            [
                new PuddingLlmProviderConfig
                {
                    ProviderId = "provider-a",
                    Name = "Provider A",
                    BaseUrl = "https://provider-a.invalid/v1",
                    IsEnabled = true,
                    Models =
                    [
                        new PuddingLlmModelConfig
                        {
                            ModelId = providerAModelId,
                            IsDefault = true,
                            CapabilityTags = ["fast"],
                        },
                    ],
                },
                new PuddingLlmProviderConfig
                {
                    ProviderId = "provider-b",
                    Name = "Provider B",
                    BaseUrl = "https://provider-b.invalid/v1",
                    IsEnabled = true,
                    Models =
                    [
                        new PuddingLlmModelConfig
                        {
                            ModelId = "shared-model",
                            IsDefault = true,
                            CapabilityTags = ["reasoning-high"],
                        },
                    ],
                },
            ],
            Profiles = new Dictionary<string, PuddingLlmProfileConfig>(StringComparer.OrdinalIgnoreCase)
            {
                ["default-conscious"] = new()
                {
                    ProviderId = duplicateDefaultModel ? "provider-b" : "provider-a",
                    ModelId = duplicateDefaultModel ? "shared-model" : "model-a",
                },
            },
            Roles = new PuddingLlmRoleConfig
            {
                Conscious = "default-conscious",
            },
        };
    }

    private sealed class MismatchingConfigService : ILlmConfigService
    {
        public IReadOnlyList<LlmProviderInfo> GetEnabledProviders() =>
        [
            new LlmProviderInfo
            {
                ProviderId = "provider-a",
                IsEnabled = true,
            },
        ];

        public IReadOnlyList<LlmModelInfo> GetAllModels() =>
        [
            new LlmModelInfo
            {
                ProviderId = "provider-a",
                ModelId = "model-a",
            },
        ];

        public LlmConfig? Resolve(string providerId, string? modelId = null) => new()
        {
            ModelId = "different-model",
        };

        public LlmProfileInfo? ResolveProfile(string profileId) => null;
        public LlmProfileInfo GetDefaultProfile() => throw new NotImplementedException();
        public LlmConfig GetDefault() => throw new NotImplementedException();
        public LlmConfig? GetMemoryConfig() => null;
        public LlmConfig? GetEmbeddingConfig() => null;
        public LlmProviderStrategy? GetProviderStrategy(string providerId) => null;
        public LlmProviderStrategy? GetModelStrategy(string providerId, string modelId) => null;
        public void Reload(object config) { }
    }
}
