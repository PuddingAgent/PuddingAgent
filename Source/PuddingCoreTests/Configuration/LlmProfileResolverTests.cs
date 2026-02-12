using PuddingCode.Configuration;

namespace PuddingCoreTests.Configuration;

[TestClass]
public sealed class LlmProfileResolverTests
{
    [TestMethod]
    public void Resolve_Ignores_Agent_Instance_Bindings_And_Uses_Template_Defaults()
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
        var instance = new AgentInstanceLlmConfig
        {
            Conscious = new AgentLlmBinding
            {
                ProfileId = "default-subconscious",
                ProviderId = "openai",
                ModelId = "gpt-4o-mini",
                ReasoningEffort = "high",
            },
            Subconscious = new AgentLlmBinding
            {
                ProviderId = "openai",
                ModelId = "gpt-4o-mini",
                ThinkingMode = "disabled",
            },
        };

        var resolved = LlmProfileResolver.Resolve(llm, template, instance);

        Assert.IsNotNull(resolved.Conscious);
        Assert.AreEqual("mimo", resolved.Conscious.ProviderId);
        Assert.AreEqual("mimo-v2.5-pro", resolved.Conscious.ModelId);
        Assert.AreEqual("https://token-plan-cn.xiaomimimo.com/v1", resolved.Conscious.Endpoint);
        Assert.AreEqual("medium", resolved.Conscious.ReasoningEffort);
        Assert.IsNotNull(resolved.Subconscious);
        Assert.AreEqual("mimo", resolved.Subconscious.ProviderId);
        Assert.AreEqual("mimo-v2.5", resolved.Subconscious.ModelId);
        Assert.AreEqual("https://token-plan-cn.xiaomimimo.com/v1", resolved.Subconscious.Endpoint);
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

        Assert.IsNotNull(resolved.Conscious);
        Assert.AreEqual("mimo-v2.5-pro", resolved.Conscious.ModelId);
        Assert.IsNotNull(resolved.Subconscious);
        Assert.AreEqual("mimo-v2.5", resolved.Subconscious.ModelId);
    }

    [TestMethod]
    public void Resolve_Ignores_Invalid_Instance_Model_And_Falls_Back_To_Global_Role_Default()
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

        var resolved = LlmProfileResolver.Resolve(llm, template: null, instance);

        Assert.IsNotNull(resolved.Conscious);
        Assert.AreEqual("mimo", resolved.Conscious.ProviderId);
        Assert.AreEqual("mimo-v2.5-pro", resolved.Conscious.ModelId);
    }

    [TestMethod]
    public void Resolve_Returns_Null_When_Role_Profile_Is_Missing()
    {
        var llm = CreateLlmConfig();
        // 没有 template，没有 instance，role 指定了不存在的 profile
        llm = llm with { Roles = new PuddingLlmRoleConfig { Conscious = "nonexistent", Subconscious = null } };

        var resolved = LlmProfileResolver.Resolve(llm, template: null, instance: null);

        Assert.IsNull(resolved.Conscious);
        Assert.IsNull(resolved.Subconscious);
    }

    [TestMethod]
    public void Resolve_Falls_Back_To_Global_Role_Defaults()
    {
        var llm = CreateLlmConfig();

        // 无 template，无 instance → 使用全局 roles 默认值
        var resolved = LlmProfileResolver.Resolve(llm, template: null, instance: null);

        Assert.IsNotNull(resolved.Conscious);
        Assert.AreEqual("mimo-v2.5-pro", resolved.Conscious.ModelId);
        Assert.IsNotNull(resolved.Subconscious);
        Assert.AreEqual("mimo-v2.5", resolved.Subconscious.ModelId);
    }

    [TestMethod]
    public void Resolve_Ignores_Instance_Profile_And_Falls_Back_To_Role_Defaults()
    {
        var llm = CreateLlmConfig();
        var instance = new AgentInstanceLlmConfig
        {
            Conscious = new AgentLlmBinding
            {
                ProfileId = "default-subconscious",  // 故意用不同的 profile
                ReasoningEffort = "high",
            },
        };

        var resolved = LlmProfileResolver.Resolve(llm, template: null, instance);

        Assert.IsNotNull(resolved.Conscious);
        Assert.AreEqual("mimo-v2.5-pro", resolved.Conscious.ModelId);
        Assert.AreEqual("medium", resolved.Conscious.ReasoningEffort);
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
