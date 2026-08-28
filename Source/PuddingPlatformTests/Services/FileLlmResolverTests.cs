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
    public async Task ResolveRouteAsync_WithoutRoute_RejectsDefaultSelection()
    {
        var resolver = CreateResolver(CreateConfig(duplicateDefaultModel: true));

        var error = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => resolver.ResolveRouteAsync());

        StringAssert.Contains(error.Message, "explicit LLM route is required");
        StringAssert.Contains(error.Message, "does not select defaults");
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
    public async Task ResolveRouteAsync_PlainDuplicateModel_ListsAllCandidateRoutes()
    {
        var resolver = CreateResolver(CreateConfig(duplicateDefaultModel: true));

        var error = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => resolver.ResolveRouteAsync("shared-model"));

        StringAssert.Contains(error.Message, "provider-a/shared-model");
        StringAssert.Contains(error.Message, "provider-b/shared-model");
    }

    [TestMethod]
    public async Task ResolveRouteAsync_UnregisteredModel_ErrorsWithSuggestedAlternatives()
    {
        var resolver = CreateResolver(CreateConfig());

        var error = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => resolver.ResolveRouteAsync("provider-a/model-x"));

        StringAssert.Contains(error.Message, "provider-a/model-x");
        StringAssert.Contains(error.Message, "not registered");
        StringAssert.Contains(error.Message, "Suggested alternatives under provider 'provider-a': provider-a/model-a");
    }

    [TestMethod]
    public async Task ResolveRouteAsync_DeprecatedModel_ErrorsWithDisabledModelDiagnostics()
    {
        var resolver = CreateResolver(CreateConfig(withDeprecatedModel: true));

        var error = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => resolver.ResolveRouteAsync("provider-a/legacy-model"));

        StringAssert.Contains(error.Message, "provider-a/legacy-model");
        StringAssert.Contains(error.Message, "is disabled (isDeprecated=true)");
        StringAssert.Contains(error.Message, "Suggested alternatives under provider 'provider-a': provider-a/model-a");
    }

    [TestMethod]
    public async Task ResolveRouteAsync_ModelOnlyUnderOtherProvider_ErrorsWithAvailableRoutes()
    {
        // 复现真实事故形态：provider 启用但其模型列表无该模型，同 modelId 挂在其他 provider 下。
        var resolver = CreateResolver(CreateConfig());

        var error = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => resolver.ResolveRouteAsync("provider-a/shared-model"));

        StringAssert.Contains(error.Message, "not registered under enabled provider 'provider-a'");
        StringAssert.Contains(error.Message, "provider-b/shared-model");
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
    public async Task ResolveRouteAsync_MissingRequiredCapability_RejectsDefaultFallback()
    {
        var resolver = CreateResolver(CreateConfig());

        var error = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => resolver.ResolveRouteAsync(requiredCapabilityTags: ["vision"]));

        StringAssert.Contains(error.Message, "No enabled LLM model matches required capabilities");
        StringAssert.Contains(error.Message, "vision");
    }

    [TestMethod]
    public async Task ResolveRouteAsync_ExplicitRoute_MustSatisfyRequiredCapability()
    {
        var resolver = CreateResolver(CreateConfig());

        var error = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => resolver.ResolveRouteAsync(
                "provider-a/model-a",
                requiredCapabilityTags: ["vision"]));

        StringAssert.Contains(error.Message, "provider-a/model-a");
        StringAssert.Contains(error.Message, "does not satisfy required capabilities");
        StringAssert.Contains(error.Message, "vision");
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

    private static PuddingLlmProvidersConfig CreateConfig(
        bool duplicateDefaultModel = false,
        bool withDeprecatedModel = false)
    {
        var providerAModelId = duplicateDefaultModel ? "shared-model" : "model-a";
        var providerA = new PuddingLlmProviderConfig
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
        };
        if (withDeprecatedModel)
        {
            providerA.Models.Add(new PuddingLlmModelConfig
            {
                ModelId = "legacy-model",
                IsDeprecated = true,
                CapabilityTags = ["fast"],
            });
        }

        return new PuddingLlmProvidersConfig
        {
            Providers =
            [
                providerA,
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

        public LlmConfig? Resolve(string providerId, string modelId) => new()
        {
            ModelId = "different-model",
        };

        public LlmProfileInfo? ResolveProfile(string profileId) => null;
        public LlmConfig? GetMemoryConfig() => null;
        public LlmConfig? GetEmbeddingConfig() => null;
        public LlmProviderStrategy? GetProviderStrategy(string providerId) => null;
        public LlmProviderStrategy? GetModelStrategy(string providerId, string modelId) => null;
        public void Reload(object config) { }
    }
}
