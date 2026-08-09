using PuddingCode.Abstractions;
using PuddingCode.Configuration;

namespace PuddingCoreTests.Configuration;

#pragma warning disable CS0618 // Tests verify legacy ApiKey mapping while config migrates to file-backed sources.
[TestClass]
public sealed class PuddingFileLlmConfigServiceTests
{
    [TestMethod]
    public void Resolve_Uses_Exact_Provider_And_Model()
    {
        var service = new PuddingFileLlmConfigService(CreateConfig());

        var config = service.Resolve("mimo", "mimo-v2.5-pro");

        Assert.IsNotNull(config);
        Assert.AreEqual("https://token-plan-cn.xiaomimimo.com/v1", config.Endpoint);
        Assert.AreEqual("mimo-key", config.ApiKey);
        Assert.AreEqual("mimo-v2.5-pro", config.ModelId);
        Assert.AreEqual("responses", config.Protocol);
        Assert.IsNull(config.ReasoningEffort);
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
        Assert.AreEqual("openai", config.Protocol);
        Assert.AreEqual("low", config.ReasoningEffort);
    }

    [TestMethod]
    public void Resolve_Uses_Model_Specific_Protocol_Within_Same_Provider()
    {
        var service = new PuddingFileLlmConfigService(CreateConfig());

        var responsesModel = service.Resolve("mimo", "mimo-v2.5-pro");
        var chatCompletionsModel = service.Resolve("mimo", "mimo-v2.5");
        var messagesModel = service.Resolve("mimo", "qwen3.8-max");

        Assert.AreEqual("responses", responsesModel!.Protocol);
        Assert.AreEqual("openai", chatCompletionsModel!.Protocol);
        Assert.AreEqual("anthropic", messagesModel!.Protocol);
    }

    [TestMethod]
    public void ResolveProfile_Returns_Profile_Metadata_And_Config()
    {
        var service = new PuddingFileLlmConfigService(CreateConfig());

        var profile = service.ResolveProfile("default-conscious");

        Assert.IsNotNull(profile);
        Assert.AreEqual("default-conscious", profile.ProfileId);
        Assert.AreEqual("mimo", profile.ProviderId);
        Assert.AreEqual("mimo-v2.5-pro", profile.ModelId);
        Assert.AreEqual("https://token-plan-cn.xiaomimimo.com/v1", profile.Config.Endpoint);
        Assert.AreEqual("mimo-key", profile.Config.ApiKey);
        Assert.AreEqual("responses", profile.Config.Protocol);
        Assert.AreEqual("medium", profile.Config.ReasoningEffort);
    }

    [TestMethod]
    public void ResolveProfile_Returns_Null_For_Missing_Profile()
    {
        var service = new PuddingFileLlmConfigService(CreateConfig());

        var profile = service.ResolveProfile("missing-profile");

        Assert.IsNull(profile);
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
                            Protocol = "responses",
                            MaxContextTokens = 1048576,
                            MaxOutputTokens = 131072,
                            IsDefault = true,
                            SortOrder = 1,
                        },
                        new PuddingLlmModelConfig
                        {
                            ModelId = "mimo-v2.5",
                            Name = "Mimo v2.5",
                            Protocol = "openai",
                            MaxContextTokens = 1048576,
                            MaxOutputTokens = 8192,
                            SortOrder = 2,
                        },
                        new PuddingLlmModelConfig
                        {
                            ModelId = "qwen3.8-max",
                            Name = "Qwen3.8 Max",
                            Protocol = "anthropic",
                            MaxContextTokens = 1000000,
                            MaxOutputTokens = 131072,
                            SortOrder = 3,
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
                            Protocol = "openai",
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
