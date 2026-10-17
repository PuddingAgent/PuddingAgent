using PuddingCode.Configuration;
using PuddingDeepSeekWeb;
using PuddingRuntime.Services.DeepSeekWeb;
using PuddingRuntimeTests.Tools;

namespace PuddingRuntimeTests.Services;

/// <summary>
/// <see cref="DeepSeekWebSettingsProvider"/> 的配置解析矩阵：search.providers.json 优先 → llm.providers.json
/// 回退 → 合并错误；disabled 是终态；读取失败带路径不抛异常；错误消息绝不回显 Key。
/// 全部使用临时 data 根（<c>PuddingDataPaths.FromRoot</c>），不读真实 D:\data。
/// </summary>
[TestClass]
public sealed class DeepSeekWebSettingsProviderTests
{
    [TestMethod]
    public void SearchProvidersConfig_IsPreferred_AndMapsConnection()
    {
        using var temp = new DeepSeekTempDirectory();
        temp.WriteSearchProviders("""
            {
              "deepseek_search": {
                "enabled": true,
                "baseUrl": "https://search.deepseek.local",
                "apiKey": "search-config-key",
                "timeoutSeconds": 12
              }
            }
            """);
        // llm.providers.json 也有效：必须被 search.providers.json 压过（断言优先级）。
        temp.WriteLlmProviders(DeepSeekWebTestTools.LlmConfig("llm-config-key"));

        var provider = temp.CreateSettingsProvider();

        var ok = provider.TryResolve(out var settings, out var error);

        Assert.IsTrue(ok, error);
        Assert.AreEqual(DeepSeekWebSettingsProvider.SearchProvidersSource, settings.Source);
        Assert.AreEqual("search-config-key", settings.Connection.ApiKey);
        Assert.AreEqual("https://search.deepseek.local", settings.Connection.BaseUrl);
        Assert.AreEqual(12, settings.Connection.TimeoutSeconds);
    }

    [TestMethod]
    public void SearchProvidersConfig_WithoutBaseUrl_FallsBackToOfficialEndpoint()
    {
        using var temp = new DeepSeekTempDirectory();
        temp.WriteSearchProviders("""
            {
              "deepseek_search": {
                "enabled": true,
                "apiKey": "search-config-key"
              }
            }
            """);

        var ok = temp.CreateSettingsProvider().TryResolve(out var settings, out var error);

        Assert.IsTrue(ok, error);
        Assert.AreEqual(DeepSeekWebConnection.DefaultBaseUrl, settings.Connection.BaseUrl);
        Assert.AreEqual(30, settings.Connection.TimeoutSeconds, "timeoutSeconds 缺省应为 30。");
    }

    [TestMethod]
    public void SearchProvidersConfig_Disabled_DoesNotFallBackToLlmProviders()
    {
        using var temp = new DeepSeekTempDirectory();
        temp.WriteSearchProviders("""
            {
              "deepseek_search": {
                "enabled": false,
                "apiKey": "search-config-key"
              }
            }
            """);
        temp.WriteLlmProviders(DeepSeekWebTestTools.LlmConfig("llm-config-key"));

        var ok = temp.CreateSettingsProvider().TryResolve(out _, out var error);

        Assert.IsFalse(ok, "显式关闭不得回退到 llm.providers.json。");
        Assert.AreEqual(DeepSeekWebSettingsProvider.SearchDisabledError, error);
        StringAssert.Contains(error, "disabled in search.providers.json");
    }

    [TestMethod]
    public void SearchProvidersConfig_WithoutApiKey_FallsBackToLlmProvider()
    {
        using var temp = new DeepSeekTempDirectory();
        temp.WriteSearchProviders("""
            {
              "deepseek_search": {
                "enabled": true,
                "baseUrl": "https://ignored.deepseek.local"
              }
            }
            """);
        temp.WriteLlmProviders(DeepSeekWebTestTools.LlmConfig(
            "llm-config-key", baseUrl: "https://llm.deepseek.local"));

        var ok = temp.CreateSettingsProvider().TryResolve(out var settings, out var error);

        Assert.IsTrue(ok, error);
        Assert.AreEqual(DeepSeekWebSettingsProvider.LlmProvidersSource, settings.Source);
        Assert.AreEqual("llm-config-key", settings.Connection.ApiKey);
        Assert.AreEqual("https://llm.deepseek.local", settings.Connection.BaseUrl);
        Assert.AreEqual(30, settings.Connection.TimeoutSeconds);
    }

    [TestMethod]
    public void SearchProvidersConfig_WithoutDeepSeekSection_FallsBackToLlmProvider()
    {
        using var temp = new DeepSeekTempDirectory();
        temp.WriteSearchProviders("""
            {
              "anysearch": { "enabled": true, "apiKey": "unrelated-provider-key" }
            }
            """);
        temp.WriteLlmProviders(DeepSeekWebTestTools.LlmConfig("llm-config-key"));

        var ok = temp.CreateSettingsProvider().TryResolve(out var settings, out var error);

        Assert.IsTrue(ok, error);
        Assert.AreEqual(DeepSeekWebSettingsProvider.LlmProvidersSource, settings.Source);
        Assert.AreEqual("llm-config-key", settings.Connection.ApiKey);
    }

    [TestMethod]
    public void LlmProvider_Disabled_ReturnsDisabledError()
    {
        using var temp = new DeepSeekTempDirectory();
        temp.WriteLlmProviders(DeepSeekWebTestTools.LlmConfig("llm-config-key", isEnabled: false));

        var ok = temp.CreateSettingsProvider().TryResolve(out _, out var error);

        Assert.IsFalse(ok);
        Assert.AreEqual(DeepSeekWebSettingsProvider.LlmProviderDisabledError, error);
        StringAssert.Contains(error, "disabled in llm.providers.json");
    }

    [TestMethod]
    public void LlmProvider_ApiKey_ExpandsEnvironmentPlaceholder()
    {
        var variableName = "PUDDING_DEEPSEEK_TEST_KEY_" + Guid.NewGuid().ToString("N");
        const string expectedKey = "sk-from-environment";
        Environment.SetEnvironmentVariable(variableName, expectedKey);
        try
        {
            using var temp = new DeepSeekTempDirectory();
            temp.WriteLlmProviders(DeepSeekWebTestTools.LlmConfig($"${{{variableName}}}"));

            var ok = temp.CreateSettingsProvider().TryResolve(out var settings, out var error);

            Assert.IsTrue(ok, error);
            Assert.AreEqual(expectedKey, settings.Connection.ApiKey);
        }
        finally
        {
            Environment.SetEnvironmentVariable(variableName, null);
        }
    }

    [TestMethod]
    public void LlmProvider_MissingEnvironmentPlaceholder_IsNotConfigured_WithBothConfigPaths()
    {
        var variableName = "PUDDING_DEEPSEEK_UNSET_" + Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable(variableName, null);

        using var temp = new DeepSeekTempDirectory();
        temp.WriteLlmProviders(DeepSeekWebTestTools.LlmConfig($"${{{variableName}}}"));

        var provider = temp.CreateSettingsProvider();
        var ok = provider.TryResolve(out _, out var error);

        Assert.IsFalse(ok);
        StringAssert.Contains(error, temp.SearchProvidersPath);
        StringAssert.Contains(error, temp.LlmProvidersPath);
        StringAssert.Contains(error, "deepseek_search.apiKey");
        StringAssert.Contains(error, "providerId=\"deepseek\"");
    }

    [TestMethod]
    public void LlmProvider_ApiKeyRef_IsNotResolvable_AndIsTreatedAsNotConfigured()
    {
        using var temp = new DeepSeekTempDirectory();
        temp.WriteLlmProviders(DeepSeekWebTestTools.LlmConfig(
            apiKey: string.Empty,
            apiKeyRef: "vault://deepseek-api-key"));

        var ok = temp.CreateSettingsProvider().TryResolve(out _, out var error);

        Assert.IsFalse(ok);
        StringAssert.Contains(error, temp.SearchProvidersPath);
        StringAssert.Contains(error, temp.LlmProvidersPath);
        Assert.IsFalse(
            error.Contains("vault://deepseek-api-key", StringComparison.Ordinal),
            "错误消息不应回显 apiKeyRef 的具体值。");
    }

    [TestMethod]
    public void NoConfigurationFiles_ReturnsMergedError_MentioningBothAbsolutePaths()
    {
        using var temp = new DeepSeekTempDirectory();

        var provider = temp.CreateSettingsProvider();
        var ok = provider.TryResolve(out _, out var error);

        Assert.IsFalse(ok);
        StringAssert.Contains(error, temp.SearchProvidersPath);
        StringAssert.Contains(error, temp.LlmProvidersPath);
        StringAssert.Contains(error, "deepseek_search.apiKey");
        StringAssert.Contains(error, "providerId=\"deepseek\"");
        Assert.IsFalse(error.Contains("sk-", StringComparison.Ordinal), "错误消息不得包含 Key。");
    }

    [TestMethod]
    public void InvalidSearchProvidersJson_ReturnsReadErrorWithPath_AndDoesNotThrow()
    {
        using var temp = new DeepSeekTempDirectory();
        temp.WriteSearchProviders("{ this is not valid json");

        var ok = temp.CreateSettingsProvider().TryResolve(out _, out var error);

        Assert.IsFalse(ok);
        StringAssert.Contains(error, "Failed to read DeepSeek Search/Fetch config file");
        StringAssert.Contains(error, temp.SearchProvidersPath);
    }

    [TestMethod]
    public void InvalidLlmProvidersJson_ReturnsReadErrorWithPath_AndDoesNotThrow()
    {
        using var temp = new DeepSeekTempDirectory();
        temp.WriteLlmProviders("{ \"providers\": [ }");

        var ok = temp.CreateSettingsProvider().TryResolve(out _, out var error);

        Assert.IsFalse(ok);
        StringAssert.Contains(error, "Failed to read DeepSeek Search/Fetch config file");
        StringAssert.Contains(error, temp.LlmProvidersPath);
    }

    [TestMethod]
    public void TimeoutSeconds_IsClampedToSupportedRange()
    {
        using var temp = new DeepSeekTempDirectory();
        temp.WriteSearchProviders("""
            {
              "deepseek_search": { "enabled": true, "apiKey": "test-key", "timeoutSeconds": 999 }
            }
            """);

        var ok = temp.CreateSettingsProvider().TryResolve(out var settings, out var error);

        Assert.IsTrue(ok, error);
        Assert.AreEqual(120, settings.Connection.TimeoutSeconds);
    }

    [TestMethod]
    public void ResolveDefaultDataPaths_UsesPuddingDataRootEnvironmentVariable()
    {
        var previous = Environment.GetEnvironmentVariable("PUDDING_DATA_ROOT");
        var expectedRoot = Path.Combine(Path.GetTempPath(), "pudding-default-root-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("PUDDING_DATA_ROOT", expectedRoot);
        try
        {
            var paths = DeepSeekWebSettingsProvider.ResolveDefaultDataPaths();

            Assert.AreEqual(
                Path.GetFullPath(expectedRoot).TrimEnd(Path.DirectorySeparatorChar),
                paths.DataRoot);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PUDDING_DATA_ROOT", previous);
        }
    }

    [TestMethod]
    public void ResolveDefaultDataPaths_FallsBackToAppBaseDataDirectory()
    {
        var previous = Environment.GetEnvironmentVariable("PUDDING_DATA_ROOT");
        Environment.SetEnvironmentVariable("PUDDING_DATA_ROOT", null);
        try
        {
            var paths = DeepSeekWebSettingsProvider.ResolveDefaultDataPaths();

            Assert.AreEqual(
                Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "data"))
                    .TrimEnd(Path.DirectorySeparatorChar),
                paths.DataRoot);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PUDDING_DATA_ROOT", previous);
        }
    }
}
