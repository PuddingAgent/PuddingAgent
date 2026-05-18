using PuddingCode.Configuration;

namespace PuddingCoreTests.Configuration;

[TestClass]
public sealed class LlmProfileResolverTests
{
    [TestMethod]
    public void Resolve_Uses_Agent_Instance_Overrides_For_Conscious_And_Subconscious()
    {
        var llm = CreateLlmConfig();
        var instance = new AgentInstanceLlmConfig
        {
            Conscious = new AgentLlmBinding
            {
                ProfileId = "default-conscious",
                ProviderId = "mimo",
                ModelId = "mimo-v2.5-pro",
                ReasoningEffort = "high",
            },
            Subconscious = new AgentLlmBinding
            {
                ProfileId = "default-subconscious",
                ProviderId = "openai",
                ModelId = "gpt-4o-mini",
                ReasoningEffort = "low",
                ThinkingMode = "disabled",
            },
        };

        var resolved = LlmProfileResolver.Resolve(llm, template: null, instance);

        Assert.AreEqual("mimo", resolved.Conscious.ProviderId);
        Assert.AreEqual("mimo-v2.5-pro", resolved.Conscious.ModelId);
        Assert.AreEqual("https://token-plan-cn.xiaomimimo.com/v1", resolved.Conscious.Endpoint);
        Assert.AreEqual("high", resolved.Conscious.ReasoningEffort);
        Assert.AreEqual("openai", resolved.Subconscious.ProviderId);
        Assert.AreEqual("gpt-4o-mini", resolved.Subconscious.ModelId);
        Assert.AreEqual("https://api.openai.com/v1", resolved.Subconscious.Endpoint);
    }

    [TestMethod]
    public void Resolve_Uses_Template_Defaults_When_Instance_Config_Is_Missing()
    {
        var llm = CreateLlmConfig();
        var template = new AgentTemplateManifest
        {
            TemplateId = "general-assistant",
            DefaultLlmProfiles = new AgentDefaultLlmProfiles
            {
                Conscious = "default-conscious",
                Subconscious = "default-subconscious",
            },
        };

        var resolved = LlmProfileResolver.Resolve(llm, template, instance: null);

        Assert.AreEqual("mimo-v2.5-pro", resolved.Conscious.ModelId);
        Assert.AreEqual("mimo-v2.5", resolved.Subconscious.ModelId);
    }

    [TestMethod]
    public void Resolve_Fails_When_Selected_Model_Does_Not_Belong_To_Provider()
    {
        var llm = CreateLlmConfig();
        var instance = new AgentInstanceLlmConfig
        {
            Conscious = new AgentLlmBinding
            {
                ProfileId = "default-conscious",
                ProviderId = "openai",
                ModelId = "mimo-v2.5-pro",
            },
        };

        InvalidOperationException? ex = null;
        try
        {
            LlmProfileResolver.Resolve(llm, template: null, instance);
        }
        catch (InvalidOperationException caught)
        {
            ex = caught;
        }

        Assert.IsNotNull(ex);
        StringAssert.Contains(ex.Message, "openai/mimo-v2.5-pro");
    }

    private static PuddingLlmProvidersConfig CreateLlmConfig()
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
                        },
                        new PuddingLlmModelConfig
                        {
                            ModelId = "mimo-v2.5",
                            Name = "Mimo v2.5",
                            MaxContextTokens = 1048576,
                            MaxOutputTokens = 8192,
                        },
                    ],
                },
                new PuddingLlmProviderConfig
                {
                    ProviderId = "openai",
                    Name = "OpenAI",
                    BaseUrl = "https://api.openai.com/v1",
                    ApiKey = "openai-key",
                    Models =
                    [
                        new PuddingLlmModelConfig
                        {
                            ModelId = "gpt-4o-mini",
                            Name = "GPT-4o Mini",
                            MaxContextTokens = 128000,
                            MaxOutputTokens = 4096,
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
                    ThinkingMode = "auto",
                },
                ["default-subconscious"] = new()
                {
                    ProviderId = "mimo",
                    ModelId = "mimo-v2.5",
                    ReasoningEffort = "low",
                    ThinkingMode = "disabled",
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
