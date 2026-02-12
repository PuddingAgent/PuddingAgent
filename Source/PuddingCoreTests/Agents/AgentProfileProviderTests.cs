using PuddingCode.Agents;
using PuddingCode.Configuration;

namespace PuddingCoreTests.Agents;

[TestClass]
public sealed class AgentProfileProviderTests
{
    [TestMethod]
    public async Task LoadAsync_Reads_Template_Instance_And_Instance_Llm_Config()
    {
        using var temp = new TempDirectory();
        var paths = PuddingDataPaths.FromRoot(temp.Path);
        var templateDir = paths.AgentTemplateRoot("general-assistant");
        var instanceDir = paths.AgentInstanceRoot("default.general-assistant-001");
        Directory.CreateDirectory(templateDir);
        Directory.CreateDirectory(paths.AgentInstanceConfigRoot("default.general-assistant-001"));

        await File.WriteAllTextAsync(Path.Combine(templateDir, "manifest.json"), """
            {
              "templateId": "general-assistant",
              "name": "General Assistant",
              "role": "Service",
              "defaultLlmProfiles": {
                "conscious": "default-conscious",
                "subconscious": "default-subconscious"
              },
              "isBuiltIn": true,
              "isEnabled": true
            }
            """);
        await File.WriteAllTextAsync(Path.Combine(templateDir, "SOUL.md"), "You are Pudding.");
        await File.WriteAllTextAsync(Path.Combine(templateDir, "AGENTS.md"), "Follow workspace rules.");
        await File.WriteAllTextAsync(Path.Combine(instanceDir, "manifest.json"), """
            {
              "agentInstanceId": "default.general-assistant-001",
              "templateId": "general-assistant",
              "workspaceId": "default",
              "displayName": "Pudding"
            }
            """);
        await File.WriteAllTextAsync(paths.AgentInstanceConfigFile("default.general-assistant-001", "llm.json"), """
            {
              "conscious": {
                "profileId": "default-conscious",
                "providerId": "mimo",
                "modelId": "mimo-v2.5-pro"
              },
              "subconscious": {
                "profileId": "default-subconscious",
                "providerId": "mimo",
                "modelId": "mimo-v2.5"
              }
            }
            """);

        var provider = new AgentProfileProvider(paths);

        var profile = await provider.LoadAsync("default.general-assistant-001");

        Assert.AreEqual("default.general-assistant-001", profile.Instance.AgentInstanceId);
        Assert.AreEqual("general-assistant", profile.Template.TemplateId);
        Assert.AreEqual("You are Pudding.", profile.Markdown.Soul);
        Assert.AreEqual("Follow workspace rules.", profile.Markdown.Agents);
        Assert.AreEqual("mimo-v2.5-pro", profile.LlmConfig.Conscious?.ModelId);
        Assert.AreEqual(Path.Combine(templateDir, "SOUL.md"), profile.SourcePaths["template.SOUL.md"]);
        Assert.AreEqual(paths.AgentInstanceConfigFile("default.general-assistant-001", "llm.json"), profile.SourcePaths["instance.config.llm"]);
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
