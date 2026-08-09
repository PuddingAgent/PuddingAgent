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
                  "baseUrl": "https://api.openai.com/v1",
                  "apiKey": "openai-key",
                  "isEnabled": true,
                  "models": [
                    {
                      "modelId": "gpt-4o-mini",
                      "name": "GPT-4o Mini",
                      "protocol": "openai",
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
                  "baseUrl": "https://token-plan-cn.xiaomimimo.com/v1",
                  "apiKey": "mimo-key",
                  "isEnabled": true,
                  "models": [
                    {
                      "modelId": "mimo-v2.5-pro",
                      "name": "Mimo v2.5 Pro",
                      "protocol": "responses",
                      "maxContextTokens": 1048576,
                      "maxOutputTokens": 131072,
                      "capabilityTags": ["text", "function-calling", "streaming"],
                      "isDefault": true,
                      "sortOrder": 1
                    },
                    {
                      "modelId": "mimo-v2.5",
                      "name": "Mimo v2.5",
                      "protocol": "openai",
                      "maxContextTokens": 1048576,
                      "maxOutputTokens": 8192,
                      "capabilityTags": ["text", "streaming"],
                      "isDefault": false,
                      "sortOrder": 2
                    },
                    {
                      "modelId": "qwen3.8-max",
                      "name": "Qwen3.8 Max",
                      "protocol": "anthropic",
                      "maxContextTokens": 1000000,
                      "maxOutputTokens": 131072,
                      "capabilityTags": ["text", "function-calling", "streaming"],
                      "isDefault": false,
                      "sortOrder": 3
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
        var mimoModels = config.Providers.Single(provider => provider.ProviderId == "mimo").Models;
        Assert.AreEqual("responses", mimoModels.Single(model => model.ModelId == "mimo-v2.5-pro").Protocol);
        Assert.AreEqual("openai", mimoModels.Single(model => model.ModelId == "mimo-v2.5").Protocol);
        Assert.AreEqual("anthropic", mimoModels.Single(model => model.ModelId == "qwen3.8-max").Protocol);
        Assert.AreEqual("mimo", config.Profiles["default-conscious"].ProviderId);
        Assert.AreEqual("mimo-v2.5", config.Profiles["default-subconscious"].ModelId);
        Assert.AreEqual("default-conscious", config.Roles.Conscious);
        Assert.AreEqual("default-subconscious", config.Roles.Subconscious);
    }

    [TestMethod]
    public async Task LoadLlmProvidersAsync_Fails_When_Model_Protocol_Is_Missing_Or_Unsupported()
    {
        using var temp = new TempDirectory();
        var paths = PuddingDataPaths.FromRoot(temp.Path);
        Directory.CreateDirectory(paths.ConfigRoot);
        await File.WriteAllTextAsync(paths.SystemConfigFile("llm.providers.json"), """
            {
              "providers": [
                {
                  "providerId": "mixed",
                  "name": "Mixed Protocol Provider",
                  "baseUrl": "https://example.invalid/v1",
                  "models": [
                    { "modelId": "missing", "name": "Missing Protocol" },
                    { "modelId": "unsupported", "name": "Unsupported Protocol", "protocol": "legacy" }
                  ]
                }
              ]
            }
            """);

        var result = await new PuddingFileConfigLoader(paths).LoadLlmProvidersAsync();

        Assert.IsFalse(result.Success);
        Assert.IsTrue(result.Errors.Any(error => error.Contains("model 'missing' protocol", StringComparison.Ordinal)));
        Assert.IsTrue(result.Errors.Any(error => error.Contains("model 'unsupported' protocol", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task LoadLlmProvidersAsync_Ignores_Legacy_Role_Aliases()
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
                  "baseUrl": "http://localhost:5000/__fake_llm/v1",
                  "apiKey": "local-dev-only",
                  "isEnabled": true,
                  "models": [
                    {
                      "modelId": "fake-chat",
                      "name": "Fake Chat",
                      "protocol": "openai",
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

        Assert.IsTrue(result.Success);
        Assert.IsEmpty(result.Errors);
    }

    [TestMethod]
    public async Task LoadLlmProvidersAsync_Allows_ProviderRegistry_Without_Profiles()
    {
        using var temp = new TempDirectory();
        var paths = PuddingDataPaths.FromRoot(temp.Path);
        Directory.CreateDirectory(paths.ConfigRoot);
        await File.WriteAllTextAsync(paths.SystemConfigFile("llm.providers.json"), """
            {
              "providers": [
                {
                  "providerId": "qwen",
                  "name": "Qwen",
                  "baseUrl": "https://example.invalid/v1",
                  "apiKey": "test-only",
                  "isEnabled": true,
                  "models": [
                    {
                      "modelId": "qwen-max",
                      "name": "Qwen Max",
                      "protocol": "openai",
                      "isDeprecated": false
                    }
                  ]
                }
              ],
              "profiles": {},
              "roles": {
                "conscious": "removed-default",
                "subconscious": "removed-memory-default"
              }
            }
            """);

        var loader = new PuddingFileConfigLoader(paths);

        var result = await loader.LoadLlmProvidersAsync();

        Assert.IsTrue(result.Success);
        Assert.IsEmpty(result.Errors);
        Assert.HasCount(1, result.Config!.Providers);
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
                  "baseUrl": "https://a.example.com/v1",
                  "models": [
                    { "modelId": "model-a", "name": "Model A", "protocol": "openai" }
                  ]
                },
                {
                  "providerId": "dup",
                  "name": "Provider B",
                  "baseUrl": "https://b.example.com/v1",
                  "models": [
                    { "modelId": "model-b", "name": "Model B", "protocol": "openai" }
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
