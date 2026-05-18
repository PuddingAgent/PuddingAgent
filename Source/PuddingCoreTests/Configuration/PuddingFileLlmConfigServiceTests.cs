using PuddingCode.Abstractions;
using PuddingCode.Configuration;

namespace PuddingCoreTests.Configuration;

#pragma warning disable CS0618 // Tests verify legacy ApiKey mapping while config migrates to file-backed sources.
[TestClass]
public sealed class PuddingFileLlmConfigServiceTests
{
    [TestMethod]
    public void GetDefault_Uses_Conscious_Role_Profile()
    {
        var service = new PuddingFileLlmConfigService(CreateConfig());

        var config = service.GetDefault();

        Assert.IsNotNull(config);
        Assert.AreEqual("https://token-plan-cn.xiaomimimo.com/v1", config.Endpoint);
        Assert.AreEqual("mimo-key", config.ApiKey);
        Assert.AreEqual("mimo-v2.5-pro", config.ModelId);
        Assert.AreEqual("medium", config.ReasoningEffort);
    }

    [TestMethod]
    public void GetMemoryConfig_Uses_Subconscious_Role_Profile()
    {
        var service = new PuddingFileLlmConfigService(CreateConfig());

        var config = service.GetMemoryConfig();

        Assert.IsNotNull(config);
        Assert.AreEqual("https://token-plan-cn.xiaomimimo.com/v1", config.Endpoint);
        Assert.AreEqual("mimo-key", config.ApiKey);
        Assert.AreEqual("mimo-v2.5", config.ModelId);
        Assert.AreEqual("low", config.ReasoningEffort);
    }

    [TestMethod]
    public void Resolve_Uses_Provider_Default_Model_When_Model_Is_Not_Provided()
    {
        var service = new PuddingFileLlmConfigService(CreateConfig());

        var config = service.Resolve("openai");

        Assert.IsNotNull(config);
        Assert.AreEqual("https://api.openai.com/v1", config.Endpoint);
        Assert.IsNull(config.ApiKey);
        Assert.AreEqual("openai", config.KeyVaultId);
        Assert.AreEqual("gpt-4o-mini", config.ModelId);
    }

    [TestMethod]
    public void GetEnabledProviders_Reports_ApiKey_Or_ApiKeyRef()
    {
        var service = new PuddingFileLlmConfigService(CreateConfig());

        var providers = service.GetEnabledProviders();

        Assert.HasCount(2, providers);
        Assert.IsTrue(providers.All(p => p.HasApiKey));
    }

    private static PuddingLlmProvidersConfig CreateConfig()
    {
        return new PuddingLlmProvidersConfig
        {
            DefaultProviderId = "mimo",
            DefaultModelId = "mimo-v2.5-pro",
            Providers =
            [
                new PuddingLlmProviderConfig
                {
                    ProviderId = "mimo",
                    Name = "Mimo",
                    BaseUrl = "https://token-plan-cn.xiaomimimo.com/v1",
                    ApiKey = "mimo-key",
                    Models =
                    [
                        new PuddingLlmModelConfig
                        {
                            ModelId = "mimo-v2.5-pro",
                            Name = "Mimo v2.5 Pro",
                            MaxContextTokens = 1048576,
                            MaxOutputTokens = 131072,
                            IsDefault = true,
                            SortOrder = 1,
                        },
                        new PuddingLlmModelConfig
                        {
                            ModelId = "mimo-v2.5",
                            Name = "Mimo v2.5",
                            MaxContextTokens = 1048576,
                            MaxOutputTokens = 8192,
                            SortOrder = 2,
                        },
                    ],
                },
                new PuddingLlmProviderConfig
                {
                    ProviderId = "openai",
                    Name = "OpenAI",
                    BaseUrl = "https://api.openai.com/v1",
                    ApiKeyRef = "vault:openai",
                    Models =
                    [
                        new PuddingLlmModelConfig
                        {
                            ModelId = "gpt-4o-mini",
                            Name = "GPT-4o Mini",
                            MaxContextTokens = 128000,
                            MaxOutputTokens = 4096,
                            IsDefault = true,
                            SortOrder = 1,
                        },
                    ],
                },
            ],
            Profiles = new Dictionary<string, PuddingLlmProfileConfig>(StringComparer.OrdinalIgnoreCase)
            {
                ["default-conscious"] = new()
                {
                    ProviderId = "mimo",
                    ModelId = "mimo-v2.5-pro",
                    ReasoningEffort = "medium",
                },
                ["default-subconscious"] = new()
                {
                    ProviderId = "mimo",
                    ModelId = "mimo-v2.5",
                    ReasoningEffort = "low",
                },
            },
            Roles = new PuddingLlmRoleConfig
            {
                Conscious = "default-conscious",
                Subconscious = "default-subconscious",
            },
        };
    }
}
#pragma warning restore CS0618
