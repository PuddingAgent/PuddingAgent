using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PuddingCode.Abstractions;
using PuddingCode.Platform;
using PuddingCode.Tools;
using PuddingRuntime.Services.Tools;

namespace PuddingRuntimeTests.Tools;

/// <summary>
/// B1 修复回归锁：StrictConfiguredToolApprovalLlmProfileResolver 分支 B 放宽语义：
/// ① ProviderId+ModelId 齐备（无 ProfileId）→ 解析成功，ProfileId 为确定性合成值；
/// ② 缺 ProviderId 或 ModelId → 仍 null（依赖等待语义不变）；
/// ③ 显式 ProfileId 且可解析 → 行为与修复前完全一致（分支 A 回归锁）；
/// ④ 路由未注册（Resolve 返回 null）→ 仍 null，绝不回退默认模型（ADR-091）。
/// </summary>
[TestClass]
public sealed class StrictConfiguredToolApprovalLlmProfileResolverB1Tests
{
    private static readonly ToolApprovalTicketRequest Ticket = new() { ToolId = "shell" };

    private static readonly ToolApprovalIdentity Identity = new()
    {
        WorkspaceId = "ws",
        SessionId = "session",
        AgentInstanceId = "agent",
        UserId = "user",
    };

    private static readonly ToolDescriptor Descriptor = new()
    {
        ToolId = "shell",
        Name = "Shell",
        Description = "Shell tool descriptor for resolver tests.",
    };

    private sealed class B1StubLlmConfigService : ILlmConfigService
    {
        private readonly Dictionary<string, LlmConfig> _routes;
        private readonly Dictionary<string, LlmProfileInfo> _profiles;

        public B1StubLlmConfigService(
            Dictionary<string, LlmConfig>? routes = null,
            Dictionary<string, LlmProfileInfo>? profiles = null)
        {
            _routes = routes ?? new Dictionary<string, LlmConfig>();
            _profiles = profiles ?? new Dictionary<string, LlmProfileInfo>();
        }

        public int ResolveRouteCalls { get; private set; }

        public IReadOnlyList<LlmProviderInfo> GetEnabledProviders() => Array.Empty<LlmProviderInfo>();

        public IReadOnlyList<LlmModelInfo> GetAllModels() => Array.Empty<LlmModelInfo>();

        public LlmConfig? Resolve(string providerId, string modelId)
        {
            ResolveRouteCalls++;
            return _routes.TryGetValue($"{providerId}/{modelId}", out var config) ? config : null;
        }

        public LlmProfileInfo? ResolveProfile(string profileId)
            => _profiles.TryGetValue(profileId, out var profile) ? profile : null;

        public LlmConfig? GetMemoryConfig() => null;

        public LlmConfig? GetEmbeddingConfig() => null;

        public LlmProviderStrategy? GetProviderStrategy(string providerId) => null;

        public LlmProviderStrategy? GetModelStrategy(string providerId, string modelId) => null;

        public void Reload(object config)
        {
        }
    }

    private static LlmConfig DeepSeekConfig() => new()
    {
        Endpoint = "https://api.deepseek.com/v1",
        ModelId = "deepseek-flash",
    };

    private static ToolApprovalLlmProfile? Resolve(ToolApprovalLlmOptions options, ILlmConfigService? service = null)
    {
        var resolver = new StrictConfiguredToolApprovalLlmProfileResolver(Options.Create(options), service);
        return resolver.ResolveAsync(Ticket, Identity, Descriptor).GetAwaiter().GetResult();
    }

    [TestMethod]
    public void ProviderAndModelWithoutProfile_Resolves_WithSyntheticProfileId()
    {
        // 服务存在且路由已注册
        var service = new B1StubLlmConfigService(new Dictionary<string, LlmConfig>
        {
            ["deepseek/deepseek-flash"] = DeepSeekConfig(),
        });

        var profile = Resolve(new ToolApprovalLlmOptions
        {
            ProviderId = "deepseek",
            ModelId = "deepseek-flash",
        }, service);

        Assert.IsNotNull(profile);
        Assert.AreEqual("deepseek", profile!.ProviderId);
        Assert.AreEqual("deepseek-flash", profile.ModelId);
        Assert.AreEqual("deepseek/deepseek-flash", profile.ProfileId);
        Assert.IsNull(profile.AgentInstanceId);
        Assert.AreEqual(1, service.ResolveRouteCalls);

        // 服务缺席：ProviderId+ModelId 齐备同样可解析（_llmConfigService == null 分支）
        var withoutService = Resolve(new ToolApprovalLlmOptions
        {
            ProviderId = "deepseek",
            ModelId = "deepseek-flash",
        });

        Assert.IsNotNull(withoutService);
        Assert.AreEqual("deepseek", withoutService!.ProviderId);
        Assert.AreEqual("deepseek/deepseek-flash", withoutService.ProfileId);
        Assert.AreEqual("deepseek-flash", withoutService.ModelId);
    }

    [TestMethod]
    public void MissingProviderOrModel_StillReturnsNull_ForDependencyWait()
    {
        var service = new B1StubLlmConfigService(new Dictionary<string, LlmConfig>
        {
            ["deepseek/deepseek-flash"] = DeepSeekConfig(),
        });

        // 缺 ModelId
        Assert.IsNull(Resolve(new ToolApprovalLlmOptions { ProviderId = "deepseek" }, service));
        // 缺 ProviderId
        Assert.IsNull(Resolve(new ToolApprovalLlmOptions { ModelId = "deepseek-flash" }, service));
        // 全空
        Assert.IsNull(Resolve(new ToolApprovalLlmOptions(), service));
        // 服务缺席时同样保持依赖等待语义
        Assert.IsNull(Resolve(new ToolApprovalLlmOptions { ProviderId = "deepseek" }, null));
        Assert.IsNull(Resolve(new ToolApprovalLlmOptions { ModelId = "deepseek-flash" }, null));
    }

    [TestMethod]
    public void ExplicitProfile_Resolvable_BehaviorUnchanged()
    {
        var service = new B1StubLlmConfigService(profiles: new Dictionary<string, LlmProfileInfo>
        {
            ["audit.main"] = new LlmProfileInfo
            {
                ProfileId = "audit.main",
                ProviderId = "deepseek",
                ModelId = "deepseek-flash",
                Config = DeepSeekConfig(),
            },
        });

        var profile = Resolve(new ToolApprovalLlmOptions
        {
            ProviderId = "deepseek",
            ProfileId = "audit.main",
            ModelId = "deepseek-flash",
            AgentTemplateId = "approval-auditor",
        }, service);

        Assert.IsNotNull(profile);
        Assert.AreEqual("deepseek", profile!.ProviderId);
        Assert.AreEqual("audit.main", profile.ProfileId);
        Assert.AreEqual("deepseek-flash", profile.ModelId);
        Assert.AreEqual("approval-auditor", profile.AgentTemplateId);
        Assert.IsNull(profile.AgentInstanceId);
        // 分支 A 不经过 Resolve(providerId, modelId)
        Assert.AreEqual(0, service.ResolveRouteCalls);

        // 回归锁：分支 A ProviderId 一致性校验（不匹配 → null）不变
        Assert.IsNull(Resolve(new ToolApprovalLlmOptions
        {
            ProviderId = "bigmodel",
            ProfileId = "audit.main",
            ModelId = "deepseek-flash",
        }, service));

        // 回归锁：分支 A ProfileId 无法解析且服务存在 → null，不落入分支 B（不变）
        Assert.IsNull(Resolve(new ToolApprovalLlmOptions
        {
            ProviderId = "deepseek",
            ProfileId = "missing.profile",
            ModelId = "deepseek-flash",
        }, service));
        Assert.AreEqual(0, service.ResolveRouteCalls);
    }

    [TestMethod]
    public void UnregisteredRoute_StillReturnsNull()
    {
        // 空注册表：服务存在但路由未注册 → null（fail-closed，不回退默认模型）
        var service = new B1StubLlmConfigService();

        var profile = Resolve(new ToolApprovalLlmOptions
        {
            ProviderId = "deepseek",
            ModelId = "deepseek-flash",
            // 故意不提供 ProfileId：走分支 B 的新注册校验
        }, service);

        Assert.IsNull(profile);
        Assert.AreEqual(1, service.ResolveRouteCalls);
    }
}
