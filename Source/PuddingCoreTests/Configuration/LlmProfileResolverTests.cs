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
    public void Resolve_Ignores_Instance_Binding_And_Returns_Null_When_Template_Missing()
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

        // 71f187f（refactor(llm): remove default-conscious/subconscious profiles and
        // DefaultProviderId/DefaultModelId）起：instance 绑定不再参与解析，全局 roles 回退已删除；
        // Resolve 只认模板 DefaultLlmProfiles ⇒ 无 template 时该角色为 null。
        var resolved = LlmProfileResolver.Resolve(llm, template: null, instance);

        Assert.IsNull(resolved.Conscious);
        Assert.IsNull(resolved.Subconscious);
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
    public void Resolve_Returns_Null_When_Template_Missing_Even_If_Global_Roles_Configured()
    {
        var llm = CreateLlmConfig();

        // 71f187f 起：全局 roles（roles.conscious/subconscious）不再作为回退
        // （LlmProfileResolver.cs 类注释 [Obsolete] 段 + ResolveRole 注释「不再回退到全局 roles」；
        // PuddingLlmRoleConfig 已标 [Obsolete]）。无 template ⇒ 两个角色均为 null。
        var resolved = LlmProfileResolver.Resolve(llm, template: null, instance: null);

        Assert.IsNull(resolved.Conscious);
        Assert.IsNull(resolved.Subconscious);
    }

    [TestMethod]
    public void Resolve_Ignores_Instance_Profile_And_ReasoningEffort_When_Template_Missing()
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

        // 71f187f 起：Resolve 的 instance 参数被完全忽略（解析链仅 template.DefaultLlmProfiles），
        // 不存在「回退到 roles 默认值并带出 medium ReasoningEffort」的路径；无 template ⇒ null。
        var resolved = LlmProfileResolver.Resolve(llm, template: null, instance);

        Assert.IsNull(resolved.Conscious);
    }

    [TestMethod]
    public void Resolve_OutputLimitComesFromModel_NotLegacyProfile()
    {
        var config = CreateLlmConfig();
        config.Profiles["default-conscious"] = System.Text.Json.JsonSerializer.Deserialize<PuddingLlmProfileConfig>("""
            {"providerId":"mimo","modelId":"mimo-v2.5-pro","maxReplyTokens":4096}
            """, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))!;
        var resolved = LlmProfileResolver.Resolve(config, new AgentTemplateManifest
        {
            DefaultLlmProfiles = new AgentDefaultLlmProfiles { Conscious = "default-conscious" },
        }, null);
        Assert.AreEqual(131072, resolved.Conscious!.MaxOutputTokens);
    }

    private static PuddingLlmProvidersConfig CreateLlmConfig()
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
