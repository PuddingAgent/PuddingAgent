using PuddingCode.Configuration;

namespace PuddingCoreTests.Configuration;

[TestClass]
public sealed class PuddingFileConfigLoaderTests
{
    [TestMethod]
    public async Task LoadLlmProvidersAsync_Loads_Multiple_Providers_Models_And_Role_Profiles()
    {
        using var temp = new TempDirectory();
        var paths = PuddingDataPaths.FromRoot(temp.Path);
        Directory.CreateDirectory(paths.ConfigRoot);
        await File.WriteAllTextAsync(paths.SystemConfigFile("llm.providers.json"), """
            {
              "defaultProviderId": "openai",
              "defaultModelId": "gpt-4o-mini",
              "providers": [
                {
                  "providerId": "openai",
                  "name": "OpenAI",
                  "protocol": "openai",
                  "baseUrl": "https://api.openai.com/v1",
                  "apiKey": "openai-key",
                  "isEnabled": true,
                  "models": [
                    {
                      "modelId": "gpt-4o-mini",
                      "name": "GPT-4o Mini",
                      "maxContextTokens": 128000,
                      "maxOutputTokens": 4096,
                      "capabilityTags": ["text", "streaming"],
                      "isDefault": true,
                      "sortOrder": 1
                    }
                  ]
                },
                {
                  "providerId": "mimo",
                  "name": "Mimo",
                  "protocol": "openai",
                  "baseUrl": "https://token-plan-cn.xiaomimimo.com/v1",
                  "apiKey": "mimo-key",
                  "isEnabled": true,
                  "models": [
                    {
                      "modelId": "mimo-v2.5-pro",
                      "name": "Mimo v2.5 Pro",
                      "maxContextTokens": 1048576,
                      "maxOutputTokens": 131072,
                      "capabilityTags": ["text", "function-calling", "streaming"],
                      "isDefault": true,
                      "sortOrder": 1
                    },
                    {
                      "modelId": "mimo-v2.5",
                      "name": "Mimo v2.5",
                      "maxContextTokens": 1048576,
                      "maxOutputTokens": 8192,
                      "capabilityTags": ["text", "streaming"],
                      "isDefault": false,
                      "sortOrder": 2
                    }
                  ]
                }
              ],
              "profiles": {
                "default-conscious": {
                  "providerId": "mimo",
                  "modelId": "mimo-v2.5-pro",
                  "reasoningEffort": "medium",
                  "thinkingMode": "auto"
                },
                "default-subconscious": {
                  "providerId": "mimo",
                  "modelId": "mimo-v2.5",
                  "reasoningEffort": "low",
                  "thinkingMode": "disabled"
                }
              },
              "roles": {
                "conscious": "default-conscious",
                "subconscious": "default-subconscious"
              }
            }
            """);

        var loader = new PuddingFileConfigLoader(paths);

        var config = await loader.LoadLlmProvidersAsync();

        Assert.HasCount(2, config.Providers);
        Assert.AreEqual("mimo", config.Profiles["default-conscious"].ProviderId);
        Assert.AreEqual("mimo-v2.5", config.Profiles["default-subconscious"].ModelId);
        Assert.AreEqual("default-conscious", config.Roles.Conscious);
        Assert.AreEqual("default-subconscious", config.Roles.Subconscious);
    }

    [TestMethod]
    public async Task LoadLlmProvidersAsync_Fails_When_Role_Profile_Is_Missing()
    {
        using var temp = new TempDirectory();
        var paths = PuddingDataPaths.FromRoot(temp.Path);
        Directory.CreateDirectory(paths.ConfigRoot);
        await File.WriteAllTextAsync(paths.SystemConfigFile("llm.providers.json"), """
            {
              "providers": [
                {
                  "providerId": "fake",
                  "name": "Fake LLM",
                  "protocol": "openai",
                  "baseUrl": "http://localhost:5000/__fake_llm/v1",
                  "apiKey": "local-dev-only",
                  "isEnabled": true,
                  "models": [
                    {
                      "modelId": "fake-chat",
                      "name": "Fake Chat",
                      "maxContextTokens": 65536,
                      "maxOutputTokens": 4096,
                      "isDefault": true,
                      "sortOrder": 1
                    }
                  ]
                }
              ],
              "profiles": {
                "default-conscious": {
                  "providerId": "fake",
                  "modelId": "fake-chat"
                }
              },
              "roles": {
                "conscious": "default-conscious",
                "subconscious": "missing-subconscious"
              }
            }
            """);

        var loader = new PuddingFileConfigLoader(paths);

        InvalidOperationException? ex = null;
        try
        {
            await loader.LoadLlmProvidersAsync();
        }
        catch (InvalidOperationException caught)
        {
            ex = caught;
        }

        Assert.IsNotNull(ex);
        StringAssert.Contains(ex.Message, "roles.subconscious");
        StringAssert.Contains(ex.Message, "missing-subconscious");
    }

    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "pudding-config-tests",
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
