using PuddingCode.Agents;
using PuddingCode.Configuration;

namespace PuddingCoreTests.Agents;

[TestClass]
public sealed class AgentProfileProviderTests
{
    [TestMethod]
    public async Task LoadAsync_Reads_Instance_Manifest_With_Embedded_Config()
    {
        using var temp = new TempDirectory();
        var paths = PuddingDataPaths.FromRoot(temp.Path);
        var instanceDir = paths.AgentInstanceRoot("default.general-assistant-001");
        Directory.CreateDirectory(instanceDir);
        Directory.CreateDirectory(paths.AgentInstanceConfigRoot("default.general-assistant-001"));

        // Markdown 文件写入实例目录
        await File.WriteAllTextAsync(Path.Combine(instanceDir, "SOUL.md"), "You are Pudding.");
        await File.WriteAllTextAsync(Path.Combine(instanceDir, "AGENTS.md"), "Follow workspace rules.");

        // 实例 manifest 包含全部嵌入的模板配置
        await File.WriteAllTextAsync(Path.Combine(instanceDir, "manifest.json"), """
            {
              "agentInstanceId": "default.general-assistant-001",
              "templateId": "general-assistant",
              "workspaceId": "default",
              "displayName": "Pudding",
              "role": "Service",
              "systemPrompt": "You are a helpful assistant.",
              "memorySearchMode": "deep",
              "maxContextTokens": 65536,
              "maxReplyTokens": 4096,
              "maxRounds": 200,
              "maxElapsedSeconds": 1200,
              "preferredProviderId": "mimo",
              "preferredModelId": "mimo-v2.5-pro",
              "capabilities": {
                "allowedToolIds": ["cap-file-read", "cap-search-memory"]
              },
              "soulMdFile": "SOUL.md",
              "agentsMdFile": "AGENTS.md",
              "isEnabled": true
            }
            """);

        await File.WriteAllTextAsync(paths.AgentInstanceConfigFile("default.general-assistant-001", "llm.json"), """
            {
              "conscious": {
                "profileId": "default-conscious",
                "providerId": "mimo",
                "modelId": "mimo-v2.5-pro"
              }
            }
            """);

        var provider = new AgentProfileProvider(paths);

        var profile = await provider.LoadAsync("default.general-assistant-001");

        // 实例身份
        Assert.AreEqual("default.general-assistant-001", profile.Instance.AgentInstanceId);
        // 模板信息从实例 manifest 构建（不再跨目录读取）
        Assert.AreEqual("general-assistant", profile.Template.TemplateId);
        Assert.AreEqual("mimo", profile.Template.PreferredProviderId);
        Assert.AreEqual("mimo-v2.5-pro", profile.Template.PreferredModelId);
        // Markdown 从实例目录读取
        Assert.AreEqual("You are Pudding.", profile.Markdown.Soul);
        Assert.AreEqual("Follow workspace rules.", profile.Markdown.Agents);
        // LLM 配置（实例级覆盖）
        Assert.AreEqual("mimo-v2.5-pro", profile.LlmConfig.Conscious?.ModelId);
        // Source paths 指向实例目录
        Assert.AreEqual(Path.Combine(instanceDir, "SOUL.md"), profile.SourcePaths["instance.SOUL.md"]);
        Assert.AreEqual(paths.AgentInstanceConfigFile("default.general-assistant-001", "llm.json"), profile.SourcePaths["instance.config.llm"]);
    }

    [TestMethod]
    public async Task LoadAsync_Missing_Markdown_Files_Returns_Null()
    {
        using var temp = new TempDirectory();
        var paths = PuddingDataPaths.FromRoot(temp.Path);
        var instanceDir = paths.AgentInstanceRoot("agent-no-md");
        Directory.CreateDirectory(instanceDir);

        await File.WriteAllTextAsync(Path.Combine(instanceDir, "manifest.json"), """
            {
              "agentInstanceId": "agent-no-md",
              "templateId": "general-assistant",
              "workspaceId": "default",
              "isEnabled": true
            }
            """);

        var provider = new AgentProfileProvider(paths);
        var profile = await provider.LoadAsync("agent-no-md");

        Assert.IsNull(profile.Markdown.Soul);
        Assert.IsNull(profile.Markdown.Agents);
        Assert.AreEqual("general-assistant", profile.Template.TemplateId);
    }

    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "pudding-agent-profile-tests",
            Guid.NewGuid().ToString("N"));

        public TempDirectory()
        {
            Directory.CreateDirectory(Path);
        }

        public void Dispose()
        {
            if (Directory.Exists(Path))
                Directory.Delete(Path, recursive: true);
        }
    }
}
