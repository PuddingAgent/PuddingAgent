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

        Assert.IsNotNull(resolved.Conscious);
        Assert.AreEqual("mimo", resolved.Conscious.ProviderId);
        Assert.AreEqual("mimo-v2.5-pro", resolved.Conscious.ModelId);
        Assert.AreEqual("https://token-plan-cn.xiaomimimo.com/v1", resolved.Conscious.Endpoint);
        Assert.AreEqual("high", resolved.Conscious.ReasoningEffort);
        Assert.IsNotNull(resolved.Subconscious);
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

        Assert.IsNotNull(resolved.Conscious);
        Assert.AreEqual("mimo-v2.5-pro", resolved.Conscious.ModelId);
        Assert.IsNotNull(resolved.Subconscious);
        Assert.AreEqual("mimo-v2.5", resolved.Subconscious.ModelId);
    }

    [TestMethod]
    public void Resolve_Returns_Null_When_Selected_Model_Does_Not_Belong_To_Provider()
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

        // 模型不属于该 provider，返回 null（不抛异常）
        Assert.IsNull(resolved.Conscious);
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
    public void Resolve_Instance_Overrides_Role_Defaults()
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
        // instance 覆盖了 role default
        Assert.AreEqual("mimo-v2.5", resolved.Conscious.ModelId);
        Assert.AreEqual("high", resolved.Conscious.ReasoningEffort);
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
