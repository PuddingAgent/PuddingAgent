using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using PuddingCode.Abstractions;
using PuddingCode.Platform;
using PuddingRuntime.Services;

namespace PuddingRuntimeTests.Services;

/// <summary>
/// <see cref="JevDecisionOptionsProvider"/> 的「资源池接线」契约测试。
/// <para>
/// 覆盖：① 池优先 —— 端点/模型取自 llm.providers.json 的 provider <c>jev</c>，密钥经
/// <c>apiKeyRef → LlmConfig.KeyVaultId</c> 由 KeyVault 解析，显式配置节被池压过；
/// ② 池中无 jev 条目时回退配置节/环境变量；③ KeyVault 不可用时回退配置密钥；
/// ④ 池内模型按 sortOrder 选首个未废弃模型；⑤ 均未配置时 fail-closed 抛 jev.not_configured。
/// </para>
/// 全部离线（内存配置 + 手写 fake），不访问网络与真实 KeyVault。
/// </summary>
[TestClass]
public sealed class JevDecisionOptionsProviderPoolTests
{
    private const string PoolBaseUrl = "https://jev.pool.test";
    private const string ConfigBaseUrl = "https://config.example.test";

    private static IConfiguration Config(params (string Key, string? Value)[] entries)
        => new ConfigurationBuilder()
            .AddInMemoryCollection(entries.ToDictionary(entry => entry.Key, entry => entry.Value))
            .Build();

    private static JevDecisionOptionsProvider Provider(
        IConfiguration configuration,
        FakeLlmConfigService? llm = null,
        FakeKeyVaultService? vault = null)
        => new(
            configuration,
            llm,
            NullLogger<JevDecisionOptionsProvider>.Instance,
            vault);

    [TestMethod]
    public async Task GetOptionsAsync_PoolEntryWins_EndpointModelAndVaultKey()
    {
        var configuration = Config(
            ("Jev:BaseUrl", ConfigBaseUrl),
            ("Jev:ApiKey", "config-key"));
        var llm = FakeLlmConfigService.WithJev(PoolBaseUrl, "jev-latest", keyVaultId: "jev-key");
        var vault = new FakeKeyVaultService { Value = "pool-secret" };

        var options = await Provider(configuration, llm, vault).GetOptionsAsync();

        Assert.AreEqual(PoolBaseUrl, options.BaseUrl, "端点应取自资源池 provider.baseUrl");
        Assert.AreEqual("jev-latest", options.ModelId, "模型应取自资源池");
        Assert.AreEqual("pool-secret", options.ApiKey, "密钥应经池 apiKeyRef → KeyVaultId 解析");
    }

    [TestMethod]
    public async Task GetOptionsAsync_AdditionalProviderId_StillResolvesFromPool()
    {
        var configuration = Config(
            ("Jev:ProviderId", "jev-shadow"),
            ("Jev:BaseUrl", ConfigBaseUrl),
            ("Jev:ApiKey", "config-key"));
        var llm = FakeLlmConfigService.WithJev(
            PoolBaseUrl, "jev-latest", keyVaultId: null, providerId: "jev-shadow");

        var options = await Provider(configuration, llm).GetOptionsAsync();

        Assert.AreEqual(PoolBaseUrl, options.BaseUrl);
    }

    [TestMethod]
    public async Task GetOptionsAsync_NoPoolEntry_FallsBackToConfigurationSection()
    {
        var configuration = Config(
            ("Jev:BaseUrl", ConfigBaseUrl),
            ("Jev:ApiKey", "config-key"));

        var options = await Provider(configuration, FakeLlmConfigService.WithJev(
            PoolBaseUrl, "jev-latest", keyVaultId: null, providerId: "other-provider")).GetOptionsAsync();

        Assert.AreEqual(ConfigBaseUrl, options.BaseUrl);
        Assert.AreEqual("config-key", options.ApiKey);
    }

    [TestMethod]
    public async Task GetOptionsAsync_PoolVaultKeyUnavailable_FallsBackToConfiguredKey()
    {
        var configuration = Config(
            ("Jev:BaseUrl", ConfigBaseUrl),
            ("Jev:ApiKey", "config-key"));
        var llm = FakeLlmConfigService.WithJev(PoolBaseUrl, "jev-latest", keyVaultId: "jev-key");

        // vault 为 null：无法解析池密钥 ⇒ 回退到配置密钥，但端点/模型仍取池。
        var options = await Provider(configuration, llm).GetOptionsAsync();

        Assert.AreEqual(PoolBaseUrl, options.BaseUrl, "端点仍应来自资源池");
        Assert.AreEqual("config-key", options.ApiKey, "池密钥不可解析时回退配置密钥");
    }

    [TestMethod]
    public async Task GetOptionsAsync_PoolModels_SelectsLowestSortOrder()
    {
        var configuration = Config(("Jev:ApiKey", "config-key"));
        var llm = FakeLlmConfigService.WithJev(
            PoolBaseUrl,
            models: [("jev-latest", 20), ("jev-1.13.0", 1)],
            keyVaultId: null);

        var options = await Provider(configuration, llm).GetOptionsAsync();

        Assert.AreEqual("jev-1.13.0", options.ModelId, "应按 sortOrder 选首个未废弃模型");
    }

    [TestMethod]
    public async Task GetOptionsAsync_NothingConfigured_ThrowsNotConfigured()
    {
        var provider = Provider(Config());

        var exception = await Assert.ThrowsAsync<JevDecisionException>(
            () => provider.GetOptionsAsync());

        Assert.AreEqual(JevDecisionCodes.NotConfigured, exception.Code);
    }

    // ── fakes ─────────────────────────────────────────────────────────────

    [TestMethod]
    public async Task GetOptionsAsync_PoolApiKeyIsVaultReference_IsInjectedBeforeUse()
    {
        // 资源池迁移到 KeyVault 后，apiKey 的标准形态是 {{vault:…}} 引用。
        // 不解析就发出去只能拿到 401，而错误读起来像「密钥配错了」。
        var configuration = Config(("Jev:BaseUrl", ConfigBaseUrl));
        var llm = FakeLlmConfigService.WithJev(
            PoolBaseUrl, "jev-latest", keyVaultId: null, apiKey: "{{vault:jev.apiKey}}");
        var vault = new FakeKeyVaultService { Value = "resolved-plaintext" };

        var options = await Provider(configuration, llm, vault).GetOptionsAsync();

        Assert.AreEqual("resolved-plaintext", options.ApiKey);
        Assert.IsFalse(
            options.ApiKey.Contains("{{vault:", StringComparison.Ordinal),
            "引用必须被解开，不能把占位符当密钥带出去。");
    }

    [TestMethod]
    public async Task GetOptionsAsync_VaultReferenceWithoutVaultService_FailsClosedInsteadOfSendingPlaceholder()
    {
        // 没有 vault 服务时绝不能把占位符当密钥：那会让真实错误被 401 掩盖。
        var configuration = Config(("Jev:BaseUrl", ConfigBaseUrl));
        var llm = FakeLlmConfigService.WithJev(
            PoolBaseUrl, "jev-latest", keyVaultId: null, apiKey: "{{vault:jev.apiKey}}");

        var exception = await Assert.ThrowsExactlyAsync<JevDecisionException>(
            () => Provider(configuration, llm, vault: null).GetOptionsAsync());

        Assert.AreEqual(JevDecisionCodes.NotConfigured, exception.Code);
    }

    private sealed class FakeLlmConfigService : ILlmConfigService
    {
        private readonly List<LlmProviderInfo> _providers = [];
        private readonly List<LlmModelInfo> _models = [];
        private readonly Dictionary<string, LlmConfig> _resolved = new(StringComparer.OrdinalIgnoreCase);

        public static FakeLlmConfigService WithJev(
            string baseUrl,
            string? modelId = null,
            string? keyVaultId = null,
            string providerId = "jev",
            IReadOnlyList<(string ModelId, int SortOrder)>? models = null,
            string? apiKey = null)
        {
            var service = new FakeLlmConfigService();
            service._providers.Add(new LlmProviderInfo
            {
                ProviderId = providerId,
                Name = providerId,
                BaseUrl = baseUrl,
                IsEnabled = true,
                HasApiKey = true,
            });

            var entries = models ?? [(modelId ?? "jev-latest", 1)];
            foreach (var (id, sortOrder) in entries)
            {
                service._models.Add(new LlmModelInfo
                {
                    ProviderId = providerId,
                    ModelId = id,
                    Name = id,
                    IsDefault = sortOrder == 1,
                    SortOrder = sortOrder,
                });
                service._resolved[$"{providerId}/{id}"] = new LlmConfig
                {
                    Endpoint = baseUrl,
                    ModelId = id,
                    KeyVaultId = keyVaultId,
#pragma warning disable CS0618 // 资源池迁移后 apiKey 正常形态是 {{vault:…}} 引用，必须能表达。
                    ApiKey = apiKey,
#pragma warning restore CS0618
                };
            }

            return service;
        }

        public IReadOnlyList<LlmProviderInfo> GetEnabledProviders() => _providers;

        public IReadOnlyList<LlmModelInfo> GetAllModels() => _models;

        public LlmConfig? Resolve(string providerId, string modelId)
            => _resolved.TryGetValue($"{providerId}/{modelId}", out var config) ? config : null;

        public LlmProfileInfo? ResolveProfile(string profileId) => null;

        public LlmConfig? GetMemoryConfig() => null;

        public LlmConfig? GetEmbeddingConfig() => null;

        public LlmProviderStrategy? GetProviderStrategy(string providerId) => null;

        public LlmProviderStrategy? GetModelStrategy(string providerId, string modelId) => null;

        public void Reload(object config)
        {
        }
    }

    private sealed class FakeKeyVaultService : IKeyVaultService
    {
        public string? Value { get; init; }

        public Task<KeyVaultSecretDetail?> GetSecretAsync(
            string keyVaultId,
            bool includePlainText = false,
            CancellationToken ct = default)
            => Task.FromResult<KeyVaultSecretDetail?>(new KeyVaultSecretDetail
            {
                KeyVaultId = keyVaultId,
                Name = keyVaultId,
                Value = Value,
            });

        public Task<string> EncryptAsync(string plainText, CancellationToken ct = default)
            => Task.FromResult(plainText);

        public Task<string> DecryptAsync(string encryptedValue, CancellationToken ct = default)
            => Task.FromResult(encryptedValue);

        public Task<KeyVaultSecretSummary> CreateSecretAsync(
            CreateKeyVaultSecretCommand request,
            CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<KeyVaultSecretSummary?> UpdateSecretAsync(
            string keyVaultId,
            UpdateKeyVaultSecretCommand request,
            CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<KeyVaultSecretSummary>> ListSecretsAsync(CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<bool> DeleteSecretAsync(string keyVaultId, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<string> InjectAsync(string text, CancellationToken ct = default)
            => Task.FromResult(Value ?? text);

        public Task<string> StripAsync(string text, CancellationToken ct = default)
            => Task.FromResult(text);
    }
}
