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

        var result = await loader.LoadLlmProvidersAsync();

        Assert.IsTrue(result.Success);
        var config = result.Config!;
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

        var result = await loader.LoadLlmProvidersAsync();

        Assert.IsFalse(result.Success);
        Assert.IsTrue(result.Errors.Any(e =>
            e.Contains("roles.subconscious") && e.Contains("missing-subconscious")));
    }

    [TestMethod]
    public async Task LoadLlmProvidersAsync_Fails_When_Duplicate_Provider()
    {
        using var temp = new TempDirectory();
        var paths = PuddingDataPaths.FromRoot(temp.Path);
        Directory.CreateDirectory(paths.ConfigRoot);
        await File.WriteAllTextAsync(paths.SystemConfigFile("llm.providers.json"), """
            {
              "providers": [
                {
                  "providerId": "dup",
                  "name": "Provider A",
                  "protocol": "openai",
                  "baseUrl": "https://a.example.com/v1",
                  "models": [
                    { "modelId": "model-a", "name": "Model A" }
                  ]
                },
                {
                  "providerId": "dup",
                  "name": "Provider B",
                  "protocol": "openai",
                  "baseUrl": "https://b.example.com/v1",
                  "models": [
                    { "modelId": "model-b", "name": "Model B" }
                  ]
                }
              ],
              "profiles": {
                "default-conscious": {
                  "providerId": "dup",
                  "modelId": "model-a"
                }
              },
              "roles": {
                "conscious": "default-conscious",
                "subconscious": "default-conscious"
              }
            }
            """);

        var loader = new PuddingFileConfigLoader(paths);

        var result = await loader.LoadLlmProvidersAsync();

        Assert.IsFalse(result.Success);
        Assert.IsTrue(result.Errors.Any(e => e.Contains("duplicate providerId")));
    }

    [TestMethod]
    public async Task LoadSystemAsync_Loads_Valid_System_Config()
    {
        using var temp = new TempDirectory();
        var paths = PuddingDataPaths.FromRoot(temp.Path);
        Directory.CreateDirectory(paths.ConfigRoot);
        await File.WriteAllTextAsync(paths.SystemConfigFile("system.json"), """
            {
              "environment": "development",
              "http": { "port": 5000 },
              "logging": { "level": "Debug" },
              "runtime": { "maxAgentRounds": 100 }
            }
            """);

        var loader = new PuddingFileConfigLoader(paths);

        var result = await loader.LoadSystemAsync();

        Assert.IsTrue(result.Success);
        Assert.AreEqual("development", result.Config!.Environment);
        Assert.AreEqual(5000, result.Config.Http.Port);
    }

    [TestMethod]
    public async Task LoadSecurityAsync_Loads_Valid_Security_Config()
    {
        using var temp = new TempDirectory();
        var paths = PuddingDataPaths.FromRoot(temp.Path);
        Directory.CreateDirectory(paths.ConfigRoot);
        await File.WriteAllTextAsync(paths.SystemConfigFile("security.json"), """
            {
              "jwt": {
                "issuer": "pudding-platform",
                "audience": "pudding-admin",
                "expiryHours": 8,
                "key": "test-key-32bytes-long-minimum!"
              },
              "keyVault": {
                "mode": "local-file",
                "masterKeyRef": "local"
              }
            }
            """);

        var loader = new PuddingFileConfigLoader(paths);

        var result = await loader.LoadSecurityAsync();

        Assert.IsTrue(result.Success);
        Assert.AreEqual("pudding-platform", result.Config!.Jwt.Issuer);
    }

    [TestMethod]
    public async Task LoadConnectorsAsync_Loads_Valid_Connectors_Config()
    {
        using var temp = new TempDirectory();
        var paths = PuddingDataPaths.FromRoot(temp.Path);
        Directory.CreateDirectory(paths.ConfigRoot);
        await File.WriteAllTextAsync(paths.SystemConfigFile("connectors.json"), """
            {
              "http": { "enabled": true },
              "websocket": { "enabled": true },
              "mqtt": { "enabled": false },
              "p2p": { "enabled": true, "port": 9527 }
            }
            """);

        var loader = new PuddingFileConfigLoader(paths);

        var result = await loader.LoadConnectorsAsync();

        Assert.IsTrue(result.Success);
        Assert.IsTrue(result.Config!.Http.Enabled);
        Assert.IsFalse(result.Config.Mqtt.Enabled);
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
