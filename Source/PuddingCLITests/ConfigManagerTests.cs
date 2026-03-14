namespace PuddingCodeCLITests;

using PuddingCode.Core;

[TestClass]
public sealed class ConfigManagerTests
{
    private string _testConfigPath = null!;
    private string _testConfigDir = null!;

    [TestInitialize]
    public void Setup()
    {
        _testConfigDir = Path.Combine(
            Path.GetTempPath(), 
            $"pudding_config_test_{Guid.NewGuid()}");
        Directory.CreateDirectory(_testConfigDir);
        _testConfigPath = Path.Combine(_testConfigDir, "config.json");
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_testConfigDir))
        {
            try { Directory.Delete(_testConfigDir, true); } catch { }
        }
    }

    // ──── Load Tests ────

    [TestMethod]
    public void Load_NonExistentFile_ReturnsEmptyConfig()
    {
        // Act
        var config = ConfigManager.Load(_testConfigPath);

        // Assert
        Assert.IsNotNull(config);
        Assert.AreEqual(0, config.Providers.Count);
        Assert.IsNull(config.ActiveProvider);
    }

    [TestMethod]
    public void Load_ValidConfigFile_ReturnsConfig()
    {
        // Arrange
        var testConfig = new PuddingCliConfig
        {
            ActiveProvider = "test",
            Providers = 
            [
                new ProviderEntry
                {
                    Id = "test",
                    Name = "Test Provider",
                    Endpoint = "https://api.test.com",
                    ApiKey = "test-key",
                    Model = "test-model"
                }
            ]
        };
        ConfigManager.Save(_testConfigPath, testConfig);

        // Act
        var config = ConfigManager.Load(_testConfigPath);

        // Assert
        Assert.AreEqual("test", config.ActiveProvider);
        Assert.AreEqual(1, config.Providers.Count);
        Assert.AreEqual("Test Provider", config.Providers[0].Name);
    }

    [TestMethod]
    public void Load_MigratedV010Format_ConvertsToNewFormat()
    {
        // Arrange - Old v0.1.0 format (single provider at root)
        var oldFormatJson = """
        {
          "apiKey": "old-key",
          "endpoint": "https://api.old.com",
          "model": "old-model"
        }
        """;
        File.WriteAllText(_testConfigPath, oldFormatJson);

        // Act
        var config = ConfigManager.Load(_testConfigPath);

        // Assert
        Assert.AreEqual("default", config.ActiveProvider);
        Assert.AreEqual(1, config.Providers.Count);
        Assert.AreEqual("default", config.Providers[0].Id);
        Assert.AreEqual("Migrated", config.Providers[0].Name);
        Assert.AreEqual("old-key", config.Providers[0].ApiKey);
        Assert.AreEqual("https://api.old.com", config.Providers[0].Endpoint);
        Assert.AreEqual("old-model", config.Providers[0].Model);
    }

    [TestMethod]
    public void Load_CorruptedFile_ReturnsEmptyConfig()
    {
        // Arrange
        File.WriteAllText(_testConfigPath, "not valid json {{{");

        // Act
        var config = ConfigManager.Load(_testConfigPath);

        // Assert
        Assert.IsNotNull(config);
        Assert.AreEqual(0, config.Providers.Count);
    }

    [TestMethod]
    public void Load_InvalidJsonStructure_ReturnsEmptyConfig()
    {
        // Arrange
        File.WriteAllText(_testConfigPath, """{"wrong": "structure"}""");

        // Act
        var config = ConfigManager.Load(_testConfigPath);

        // Assert
        Assert.IsNotNull(config);
        Assert.AreEqual(0, config.Providers.Count);
    }

    // ──── Save Tests ────

    [TestMethod]
    public void Save_CreatesDirectoryIfNotExists()
    {
        // Arrange
        var nestedPath = Path.Combine(_testConfigDir, "nested", "path", "config.json");

        // Act
        var config = new PuddingCliConfig();
        ConfigManager.Save(nestedPath, config);

        // Assert
        Assert.IsTrue(File.Exists(nestedPath));
    }

    [TestMethod]
    public void Save_WritesValidJson()
    {
        // Arrange
        var config = new PuddingCliConfig
        {
            ActiveProvider = "test",
            Providers = 
            [
                new ProviderEntry
                {
                    Id = "test",
                    Name = "Test",
                    Endpoint = "https://test.com",
                    ApiKey = "key",
                    Model = "model",
                    Temperature = 0.7,
                    MaxTokens = 1000
                }
            ]
        };

        // Act
        ConfigManager.Save(_testConfigPath, config);

        // Assert
        var content = File.ReadAllText(_testConfigPath);
        StringAssert.Contains(content, "\"activeProvider\": \"test\"");
        StringAssert.Contains(content, "\"id\": \"test\"");
        StringAssert.Contains(content, "\"temperature\": 0.7");
        StringAssert.Contains(content, "\"maxTokens\": 1000");
    }

    [TestMethod]
    public void Save_RoundTrip_PreservesData()
    {
        // Arrange
        var original = new PuddingCliConfig
        {
            ActiveProvider = "my-provider",
            Providers = 
            [
                new ProviderEntry
                {
                    Id = "my-provider",
                    Name = "My Provider",
                    Endpoint = "https://api.myprovider.com",
                    ApiKey = "secret-key",
                    Model = "gpt-4",
                    Temperature = 0.5,
                    MaxTokens = 2048
                }
            ]
        };

        // Act
        ConfigManager.Save(_testConfigPath, original);
        var loaded = ConfigManager.Load(_testConfigPath);

        // Assert
        Assert.AreEqual(original.ActiveProvider, loaded.ActiveProvider);
        Assert.AreEqual(1, loaded.Providers.Count);
        Assert.AreEqual(original.Providers[0].Id, loaded.Providers[0].Id);
        Assert.AreEqual(original.Providers[0].Name, loaded.Providers[0].Name);
        Assert.AreEqual(original.Providers[0].Endpoint, loaded.Providers[0].Endpoint);
        Assert.AreEqual(original.Providers[0].ApiKey, loaded.Providers[0].ApiKey);
        Assert.AreEqual(original.Providers[0].Model, loaded.Providers[0].Model);
        Assert.AreEqual(original.Providers[0].Temperature, loaded.Providers[0].Temperature);
        Assert.AreEqual(original.Providers[0].MaxTokens, loaded.Providers[0].MaxTokens);
    }

    [TestMethod]
    public void Save_NullValues_NotWrittenToJson()
    {
        // Arrange
        var config = new PuddingCliConfig
        {
            ActiveProvider = "test",
            Providers = 
            [
                new ProviderEntry
                {
                    Id = "test",
                    Name = "Test",
                    Endpoint = "https://test.com",
                    ApiKey = "key",
                    Model = "model"
                    // Temperature and MaxTokens are null
                }
            ]
        };

        // Act
        ConfigManager.Save(_testConfigPath, config);
        var content = File.ReadAllText(_testConfigPath);

        // Assert
        Assert.IsFalse(content.Contains("temperature"));
        Assert.IsFalse(content.Contains("maxTokens"));
    }

    // ──── DefaultPath Tests ────

    [TestMethod]
    public void DefaultPath_ReturnsValidPathInUserProfile()
    {
        // Act
        var path = ConfigManager.DefaultPath;

        // Assert
        Assert.IsNotNull(path);
        StringAssert.Contains(path, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        StringAssert.Contains(path, ".pudding");
        StringAssert.Contains(path, "config.json");
    }

    // ──── Multiple Providers Tests ────

    [TestMethod]
    public void Load_MultipleProviders_LoadsAll()
    {
        // Arrange
        var config = new PuddingCliConfig
        {
            ActiveProvider = "provider2",
            Providers = 
            [
                new ProviderEntry { Id = "provider1", Name = "First", Endpoint = "https://api1.com", ApiKey = "key1", Model = "model1" },
                new ProviderEntry { Id = "provider2", Name = "Second", Endpoint = "https://api2.com", ApiKey = "key2", Model = "model2" },
                new ProviderEntry { Id = "provider3", Name = "Third", Endpoint = "https://api3.com", ApiKey = "key3", Model = "model3" }
            ]
        };
        ConfigManager.Save(_testConfigPath, config);

        // Act
        var loaded = ConfigManager.Load(_testConfigPath);

        // Assert
        Assert.AreEqual(3, loaded.Providers.Count);
        Assert.AreEqual("provider2", loaded.ActiveProvider);
    }

    // ──── Edge Cases ────

    [TestMethod]
    public void Load_EmptyJsonFile_ReturnsEmptyConfig()
    {
        // Arrange
        File.WriteAllText(_testConfigPath, "{}");

        // Act
        var config = ConfigManager.Load(_testConfigPath);

        // Assert
        Assert.IsNotNull(config);
        Assert.AreEqual(0, config.Providers.Count);
    }

    [TestMethod]
    public void Save_EmptyConfig_WritesValidJson()
    {
        // Arrange
        var config = new PuddingCliConfig();

        // Act
        ConfigManager.Save(_testConfigPath, config);

        // Assert
        var content = File.ReadAllText(_testConfigPath);
        Assert.IsNotNull(content);
        // Should be valid JSON
        var loaded = ConfigManager.Load(_testConfigPath);
        Assert.IsNotNull(loaded);
    }

    [TestMethod]
    public void Load_CamelCasePropertyNames_DeserializesCorrectly()
    {
        // Arrange
        var json = """
        {
          "activeProvider": "test",
          "providers": [
            {
              "id": "test",
              "name": "Test",
              "endpoint": "https://test.com",
              "apiKey": "key",
              "model": "model"
            }
          ]
        }
        """;
        File.WriteAllText(_testConfigPath, json);

        // Act
        var config = ConfigManager.Load(_testConfigPath);

        // Assert
        Assert.AreEqual("test", config.ActiveProvider);
        Assert.AreEqual(1, config.Providers.Count);
        Assert.AreEqual("test", config.Providers[0].Id);
    }
}
