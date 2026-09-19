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

    // ── V5-T2：模型级 vision 合同节校验（存在即必须完整有效，fail-fast）────────

    [TestMethod]
    public async Task LoadLlmProvidersAsync_VisionContract_Accepts_Complete_Config()
    {
        // C1（配置侧）：合法 vision 合同节通过校验并保留到模型条目。
        using var temp = new TempDirectory();
        var paths = PuddingDataPaths.FromRoot(temp.Path);
        Directory.CreateDirectory(paths.ConfigRoot);
        await File.WriteAllTextAsync(paths.SystemConfigFile("llm.providers.json"), """
            {
              "providers": [
                {
                  "providerId": "deepseek",
                  "name": "DeepSeek",
                  "baseUrl": "https://api.deepseek.com",
                  "apiKey": "key",
                  "isEnabled": true,
                  "models": [
                    {
                      "modelId": "deepseek-flash",
                      "name": "deepseek-flash",
                      "protocol": "responses",
                      "capabilityTags": ["vision"],
                      "isDefault": true,
                      "sortOrder": 1,
                      "vision": {
                        "version": "deepseek-2026-09-12-1024",
                        "maxImagesPerRequest": 8,
                        "inlineMaxBytesPerImage": 2000000,
                        "inlineMaxTotalBytes": 41943040,
                        "estimatedTokensPerImageUpperBound": 1024
                      }
                    }
                  ]
                }
              ]
            }
            """);

        var loader = new PuddingFileConfigLoader(paths);

        var result = await loader.LoadLlmProvidersAsync();

        Assert.IsTrue(result.Success);
        var vision = result.Config!.Providers[0].Models[0].Vision!;
        Assert.AreEqual("deepseek-2026-09-12-1024", vision.Version);
        Assert.AreEqual(8, vision.MaxImagesPerRequest);
        Assert.AreEqual(2000000L, vision.InlineMaxBytesPerImage);
        Assert.AreEqual(1024, vision.EstimatedTokensPerImageUpperBound);
    }

    [TestMethod]
    public async Task LoadLlmProvidersAsync_VisionContract_MissingVersion_Fails()
    {
        // C3：vision 节存在但缺 version = 半成品合同，加载期显式失败（不静默容忍、不伪造生效）。
        using var temp = new TempDirectory();
        var paths = PuddingDataPaths.FromRoot(temp.Path);
        Directory.CreateDirectory(paths.ConfigRoot);
        await File.WriteAllTextAsync(paths.SystemConfigFile("llm.providers.json"), """
            {
              "providers": [
                {
                  "providerId": "deepseek",
                  "name": "DeepSeek",
                  "baseUrl": "https://api.deepseek.com",
                  "apiKey": "key",
                  "isEnabled": true,
                  "models": [
                    {
                      "modelId": "deepseek-flash",
                      "name": "deepseek-flash",
                      "protocol": "responses",
                      "capabilityTags": ["vision"],
                      "isDefault": true,
                      "sortOrder": 1,
                      "vision": { "maxImagesPerRequest": 8 }
                    }
                  ]
                }
              ]
            }
            """);

        var loader = new PuddingFileConfigLoader(paths);

        var result = await loader.LoadLlmProvidersAsync();

        Assert.IsFalse(result.Success);
        Assert.IsTrue(result.Errors.Any(e => e.Contains("vision version is required")));
    }

    [TestMethod]
    public async Task LoadLlmProvidersAsync_VisionContract_NonPositiveLimit_Fails()
    {
        // C3：vision 合同数值非法（≤0）在加载期显式失败。
        using var temp = new TempDirectory();
        var paths = PuddingDataPaths.FromRoot(temp.Path);
        Directory.CreateDirectory(paths.ConfigRoot);
        await File.WriteAllTextAsync(paths.SystemConfigFile("llm.providers.json"), """
            {
              "providers": [
                {
                  "providerId": "deepseek",
                  "name": "DeepSeek",
                  "baseUrl": "https://api.deepseek.com",
                  "apiKey": "key",
                  "isEnabled": true,
                  "models": [
                    {
                      "modelId": "deepseek-flash",
                      "name": "deepseek-flash",
                      "protocol": "responses",
                      "capabilityTags": ["vision"],
                      "isDefault": true,
                      "sortOrder": 1,
                      "vision": { "version": "v1", "maxImagesPerRequest": 0 }
                    }
                  ]
                }
              ]
            }
            """);

        var loader = new PuddingFileConfigLoader(paths);

        var result = await loader.LoadLlmProvidersAsync();

        Assert.IsFalse(result.Success);
        Assert.IsTrue(result.Errors.Any(e => e.Contains("maxImagesPerRequest must be greater than zero")));
    }

    [TestMethod]
    public async Task LoadLlmProvidersAsync_VisionContract_LooseningValue_Is_Rejected_With_Dimension_Value_And_Ceiling()
    {
        // V5-T4（更松）：maxImagesPerRequest=601 超过官方天花板 600 → 加载期显式拒绝；
        // 错误信息必须同时点名 provider/model、维度名、配置值与天花板，并注明只许收紧。
        using var temp = new TempDirectory();
        var paths = PuddingDataPaths.FromRoot(temp.Path);
        Directory.CreateDirectory(paths.ConfigRoot);
        await File.WriteAllTextAsync(paths.SystemConfigFile("llm.providers.json"), """
            {
              "providers": [
                {
                  "providerId": "deepseek",
                  "name": "DeepSeek",
                  "baseUrl": "https://api.deepseek.com",
                  "apiKey": "key",
                  "isEnabled": true,
                  "models": [
                    {
                      "modelId": "deepseek-flash",
                      "name": "deepseek-flash",
                      "protocol": "responses",
                      "capabilityTags": ["vision"],
                      "isDefault": true,
                      "sortOrder": 1,
                      "vision": { "version": "v1", "maxImagesPerRequest": 601 }
                    }
                  ]
                }
              ]
            }
            """);

        var loader = new PuddingFileConfigLoader(paths);

        var result = await loader.LoadLlmProvidersAsync();

        Assert.IsFalse(result.Success);
        var error = result.Errors.FirstOrDefault(e =>
            e.Contains("provider 'deepseek'", StringComparison.Ordinal)
            && e.Contains("model 'deepseek-flash'", StringComparison.Ordinal)
            && e.Contains("maxImagesPerRequest=601", StringComparison.Ordinal)
            && e.Contains("exceeds the product limit 600", StringComparison.Ordinal));
        Assert.IsNotNull(error);
        Assert.IsTrue(error.Contains("may only tighten", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task LoadLlmProvidersAsync_VisionContract_AllSevenLoosenedDimensions_Are_Rejected_Individually()
    {
        // V5-T4：7 个上限维度逐个点名——每个放宽型维度都产生独立错误（维度名 + 配置值 + 天花板三要素）。
        // 口径区分：inline *Bytes = 解码后字节，inlineMaxTotalWireBytes / filesMaxTotalBytes = wire 字节，
        // filesMaxBytesPerImage = 上传原始编码字节。
        using var temp = new TempDirectory();
        var paths = PuddingDataPaths.FromRoot(temp.Path);
        Directory.CreateDirectory(paths.ConfigRoot);
        await File.WriteAllTextAsync(paths.SystemConfigFile("llm.providers.json"), """
            {
              "providers": [
                {
                  "providerId": "deepseek",
                  "name": "DeepSeek",
                  "baseUrl": "https://api.deepseek.com",
                  "apiKey": "key",
                  "isEnabled": true,
                  "models": [
                    {
                      "modelId": "deepseek-flash",
                      "name": "deepseek-flash",
                      "protocol": "responses",
                      "capabilityTags": ["vision"],
                      "isDefault": true,
                      "sortOrder": 1,
                      "vision": {
                        "version": "v1",
                        "maxImagesPerRequest": 601,
                        "inlineMaxBytesPerImage": 67108864,
                        "inlineMaxTotalBytes": 83886080,
                        "inlineMaxTotalWireBytes": 134217728,
                        "filesMaxBytesPerImage": 134217728,
                        "filesMaxTotalBytes": 268435456,
                        "estimatedTokensPerImageUpperBound": 2048
                      }
                    }
                  ]
                }
              ]
            }
            """);

        var loader = new PuddingFileConfigLoader(paths);

        var result = await loader.LoadLlmProvidersAsync();

        Assert.IsFalse(result.Success);
        Assert.HasCount(7, result.Errors);
        var loosened = new (string Dimension, string Value, string Ceiling)[]
        {
            ("maxImagesPerRequest", "601", "600"),
            ("inlineMaxBytesPerImage", "67108864", "33554432"),
            ("inlineMaxTotalBytes", "83886080", "67108864"),
            ("inlineMaxTotalWireBytes", "134217728", "50331648"),
            ("filesMaxBytesPerImage", "134217728", "67108864"),
            ("filesMaxTotalBytes", "268435456", "209715200"),
            ("estimatedTokensPerImageUpperBound", "2048", "1024"),
        };
        foreach (var (dimension, value, ceiling) in loosened)
        {
            Assert.IsTrue(
                result.Errors.Any(e =>
                    e.Contains($"{dimension}={value}", StringComparison.Ordinal)
                    && e.Contains($"exceeds the product limit {ceiling}", StringComparison.Ordinal)
                    && e.Contains("may only tighten", StringComparison.Ordinal)),
                $"expected a loosening rejection naming {dimension}={value} with ceiling {ceiling}.");
        }
    }

    [TestMethod]
    public async Task LoadLlmProvidersAsync_VisionContract_TighteningValues_Are_Accepted()
    {
        // V5-T4（更紧）：合同值全部低于产品天花板 → 正常通过且原值保留（收紧合法）。
        // 相等情形由既有 C1 用例（值 = 产品默认的合法合同）覆盖。
        using var temp = new TempDirectory();
        var paths = PuddingDataPaths.FromRoot(temp.Path);
        Directory.CreateDirectory(paths.ConfigRoot);
        await File.WriteAllTextAsync(paths.SystemConfigFile("llm.providers.json"), """
            {
              "providers": [
                {
                  "providerId": "deepseek",
                  "name": "DeepSeek",
                  "baseUrl": "https://api.deepseek.com",
                  "apiKey": "key",
                  "isEnabled": true,
                  "models": [
                    {
                      "modelId": "deepseek-flash",
                      "name": "deepseek-flash",
                      "protocol": "responses",
                      "capabilityTags": ["vision"],
                      "isDefault": true,
                      "sortOrder": 1,
                      "vision": {
                        "version": "v1",
                        "maxImagesPerRequest": 4,
                        "inlineMaxBytesPerImage": 1000000,
                        "inlineMaxTotalBytes": 20971520,
                        "inlineMaxTotalWireBytes": 33554432,
                        "filesMaxBytesPerImage": 33554432,
                        "filesMaxTotalBytes": 104857600,
                        "estimatedTokensPerImageUpperBound": 512
                      }
                    }
                  ]
                }
              ]
            }
            """);

        var loader = new PuddingFileConfigLoader(paths);

        var result = await loader.LoadLlmProvidersAsync();

        Assert.IsTrue(result.Success);
        var vision = result.Config!.Providers[0].Models[0].Vision!;
        Assert.AreEqual(4, vision.MaxImagesPerRequest);
        Assert.AreEqual(1000000L, vision.InlineMaxBytesPerImage);
        Assert.AreEqual(20971520L, vision.InlineMaxTotalBytes);
        Assert.AreEqual(33554432L, vision.InlineMaxTotalWireBytes);
        Assert.AreEqual(33554432L, vision.FilesMaxBytesPerImage);
        Assert.AreEqual(104857600L, vision.FilesMaxTotalBytes);
        Assert.AreEqual(512, vision.EstimatedTokensPerImageUpperBound);
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
