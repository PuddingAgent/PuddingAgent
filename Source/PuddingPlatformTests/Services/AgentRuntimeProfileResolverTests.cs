using PuddingCode.Abstractions;
using PuddingCode.Agents;
using PuddingCode.Configuration;
using PuddingCode.Platform;
using PuddingPlatform.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace PuddingPlatformTests.Services;

[TestClass]
public sealed class AgentRuntimeProfileResolverTests
{
    [TestMethod]
    public async Task ResolveRoleAsync_UsesAgentInstanceBindings_ForSubconsciousAndDeveloper()
    {
        using var temp = new TempDirectory();
        var paths = PuddingDataPaths.FromRoot(temp.Path);
        var agentRoot = paths.AgentInstanceRoot("agent-1");
        Directory.CreateDirectory(agentRoot);
        await File.WriteAllTextAsync(Path.Combine(agentRoot, "manifest.json"), """
            {
              "agentInstanceId": "agent-1",
              "templateId": "general-assistant",
              "workspaceId": "workspace-1",
              "memorySearchMode": "deep",
              "memoryLlmProviderId": "deepseek",
              "memoryLlmModelId": "deepseek-v4-flash",
              "developerModel": "qwen/qwen3.8-max-preview"
            }
            """);

        var resolver = new AgentLLMConfigResolver(
            templateFileService: null!,
            new AgentProfileProvider(paths),
            CreateRoleConfigService(),
            NullLogger<AgentLLMConfigResolver>.Instance);

        var subconscious = await resolver.ResolveRoleAsync(
            "workspace-1",
            "agent-1",
            AgentLlmRoleIds.Subconscious);
        var developer = await resolver.ResolveRoleAsync(
            "workspace-1",
            "agent-1",
            AgentLlmRoleIds.Developer);

        Assert.AreEqual("deepseek", subconscious.ProviderId);
        Assert.AreEqual("deepseek-v4-flash", subconscious.ModelId);
        Assert.AreEqual("deepseek-v4-flash", subconscious.Config.ModelId);
        Assert.AreEqual("agent:agent-1:subconscious", subconscious.ProfileId);
        Assert.AreEqual("qwen", developer.ProviderId);
        Assert.AreEqual("qwen3.8-max-preview", developer.ModelId);
    }

    [TestMethod]
    public async Task ResolveRoleAsync_MissingSubconsciousBinding_FailsWithoutFallback()
    {
        using var temp = new TempDirectory();
        var paths = PuddingDataPaths.FromRoot(temp.Path);
        var agentRoot = paths.AgentInstanceRoot("agent-1");
        Directory.CreateDirectory(agentRoot);
        await File.WriteAllTextAsync(Path.Combine(agentRoot, "manifest.json"), """
            {
              "agentInstanceId": "agent-1",
              "templateId": "general-assistant",
              "workspaceId": "workspace-1",
              "preferredProviderId": "deepseek",
              "preferredModelId": "deepseek-v4-pro"
            }
            """);

        var resolver = new AgentLLMConfigResolver(
            templateFileService: null!,
            new AgentProfileProvider(paths),
            CreateRoleConfigService(),
            NullLogger<AgentLLMConfigResolver>.Instance);

        var error = await Assert.ThrowsExactlyAsync<AgentConfigurationException>(() =>
            resolver.ResolveRoleAsync("workspace-1", "agent-1", AgentLlmRoleIds.Subconscious));

        StringAssert.Contains(error.Message, "missing the provider/model binding");
        StringAssert.Contains(error.Message, "subconscious");
    }

    [TestMethod]
    public void ResolveConsciousLlm_Uses_Manifest_Provider_Model_Pair()
    {
        var manifest = new AgentInstanceManifest
        {
            AgentInstanceId = "agent-1",
            PreferredProviderId = "qwen",
            PreferredModelId = "qwen-max",
            ReasoningEffort = "high",
        };

        var route = AgentRuntimeProfileResolver.ResolveConsciousLlm(
            manifest,
            manifest.AgentInstanceId,
            @"D:\data\agents\agent-1\manifest.json",
            CreateConfigService());

        Assert.AreEqual("qwen", route.ProviderId);
        Assert.AreEqual("qwen-max", route.ModelId);
        Assert.AreEqual("qwen-max", route.Config.ModelId);
        Assert.AreEqual("high", route.Config.ReasoningEffort);
        Assert.IsNull(route.ProfileId);
    }

    [TestMethod]
    public void ResolveConsciousLlm_Missing_Manifest_Provider_FailsClosed()
    {
        var error = Assert.ThrowsExactly<AgentConfigurationException>(() =>
            AgentRuntimeProfileResolver.ResolveConsciousLlm(
                new AgentInstanceManifest
                {
                    AgentInstanceId = "agent-1",
                    PreferredModelId = "qwen-max",
                },
                "agent-1",
                @"D:\data\agents\agent-1\manifest.json",
                CreateConfigService()));

        Assert.AreEqual(TerminalErrorCodes.AgentConfigurationInvalid, error.ErrorCode);
        StringAssert.Contains(error.Message, "preferredProviderId");
        StringAssert.Contains(error.Message, "manifest.json");
    }

    [TestMethod]
    public void ResolveConsciousLlm_Missing_Manifest_Model_FailsClosed()
    {
        var error = Assert.ThrowsExactly<AgentConfigurationException>(() =>
            AgentRuntimeProfileResolver.ResolveConsciousLlm(
                new AgentInstanceManifest
                {
                    AgentInstanceId = "agent-1",
                    PreferredProviderId = "qwen",
                },
                "agent-1",
                @"D:\data\agents\agent-1\manifest.json",
                CreateConfigService()));

        Assert.AreEqual(TerminalErrorCodes.AgentConfigurationInvalid, error.ErrorCode);
        StringAssert.Contains(error.Message, "preferredModelId");
        StringAssert.Contains(error.Message, "manifest.json");
    }

    [TestMethod]
    public void ResolveConsciousLlm_Invalid_Manifest_Model_Does_Not_Fallback()
    {
        var error = Assert.ThrowsExactly<AgentConfigurationException>(() =>
            AgentRuntimeProfileResolver.ResolveConsciousLlm(
                new AgentInstanceManifest
                {
                    AgentInstanceId = "agent-1",
                    PreferredProviderId = "qwen",
                    PreferredModelId = "missing-model",
                },
                "agent-1",
                @"D:\data\agents\agent-1\manifest.json",
                CreateConfigService()));

        Assert.AreEqual(TerminalErrorCodes.AgentConfigurationInvalid, error.ErrorCode);
        StringAssert.Contains(error.Message, "missing-model");
        StringAssert.Contains(error.Message, "No fallback model was selected");
    }

    private static ILlmConfigService CreateConfigService()
        => new PuddingFileLlmConfigService(new PuddingLlmProvidersConfig
        {
            Providers =
            [
                new PuddingLlmProviderConfig
                {
                    ProviderId = "qwen",
                    Name = "Qwen",
                    BaseUrl = "https://example.invalid/v1",
                    IsEnabled = true,
                    Models =
                    [
                        new PuddingLlmModelConfig
                        {
                            ModelId = "qwen-max",
                            Name = "Qwen Max",
                            IsDeprecated = false,
                        },
                    ],
                },
            ],
        });

    private static ILlmConfigService CreateRoleConfigService()
        => new PuddingFileLlmConfigService(new PuddingLlmProvidersConfig
        {
            Providers =
            [
                new PuddingLlmProviderConfig
                {
                    ProviderId = "deepseek",
                    Name = "DeepSeek",
                    BaseUrl = "https://deepseek.invalid/v1",
                    IsEnabled = true,
                    Models =
                    [
                        new PuddingLlmModelConfig { ModelId = "deepseek-v4-flash" },
                        new PuddingLlmModelConfig { ModelId = "deepseek-v4-pro" },
                    ],
                },
                new PuddingLlmProviderConfig
                {
                    ProviderId = "qwen",
                    Name = "Qwen",
                    BaseUrl = "https://qwen.invalid/v1",
                    IsEnabled = true,
                    Models =
                    [
                        new PuddingLlmModelConfig { ModelId = "qwen3.8-max-preview" },
                    ],
                },
            ],
        });

    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "pudding-agent-role-route-tests",
            Guid.NewGuid().ToString("N"));

        public TempDirectory() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            if (Directory.Exists(Path))
                Directory.Delete(Path, recursive: true);
        }
    }
}
