using Microsoft.Extensions.Logging.Abstractions;
using PuddingCode.Configuration;
using PuddingPlatform.Data.Dtos;
using PuddingPlatform.Services;

namespace PuddingPlatformTests.Services;

/// <summary>
/// LlmProviderFileService.GetBalanceAsync 注册表分发测试：
/// 无适配器 → 「暂不支持」DTO；有适配器 → 委托并传入解析后的 apiKey；
/// provider 不存在 → KeyNotFoundException；有适配器但无 apiKey → InvalidOperationException。
/// </summary>
[TestClass]
public sealed class LlmProviderBalanceDispatchTests
{
    private sealed class StubBalanceProvider : ILlmBalanceProvider
    {
        private readonly Func<PuddingLlmProviderConfig, bool> _canHandle;

        public StubBalanceProvider(Func<PuddingLlmProviderConfig, bool> canHandle)
            => _canHandle = canHandle;

        public PuddingLlmProviderConfig? LastProvider { get; private set; }
        public string? LastApiKey { get; private set; }

        public bool CanHandle(PuddingLlmProviderConfig provider) => _canHandle(provider);

        public Task<LlmProviderBalanceDto> QueryAsync(
            PuddingLlmProviderConfig provider, string apiKey, CancellationToken ct = default)
        {
            LastProvider = provider;
            LastApiKey = apiKey;
            return Task.FromResult(new LlmProviderBalanceDto(
                provider.ProviderId, "stub://endpoint",
                IsAvailable: true,
                [new LlmBalanceInfoDto("CNY", 42.00m, 2.00m, 40.00m)],
                Error: null, QueriedAt: DateTimeOffset.UtcNow));
        }
    }

    private static async Task<LlmProviderFileService> CreateServiceAsync(
        PuddingDataPaths paths,
        string providerId,
        string? apiKey,
        ILlmBalanceProvider[] adapters)
    {
        await AtomicFileWriter.WriteJsonAsync(
            paths.SystemConfigFile("llm.providers.json"),
            new PuddingLlmProvidersConfig
            {
                Providers =
                [
                    new PuddingLlmProviderConfig
                    {
                        ProviderId = providerId,
                        Name = providerId,
                        BaseUrl = "https://example.invalid",
                        ApiKey = apiKey,
                    },
                ],
            });
        return new LlmProviderFileService(
            paths,
            NullLogger<LlmProviderFileService>.Instance,
            balanceProviders: adapters);
    }

    private static string CreateTempRoot() =>
        Path.Combine(Path.GetTempPath(), "pudding-llm-balance-tests", Guid.NewGuid().ToString("N"));

    [TestMethod]
    public async Task GetBalance_NoMatchingAdapter_ReturnsNotSupportedDto()
    {
        var root = CreateTempRoot();
        try
        {
            Directory.CreateDirectory(root);
            var service = await CreateServiceAsync(
                PuddingDataPaths.FromRoot(root), "moonshot", "sk-test", adapters: []);

            var dto = await service.GetBalanceAsync("moonshot");

            Assert.IsFalse(dto.IsAvailable);
            Assert.AreEqual(0, dto.BalanceInfos.Count);
            Assert.IsTrue(dto.Error?.Contains("暂不支持") == true);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task GetBalance_MatchingAdapter_ReceivesProviderAndResolvedApiKey()
    {
        var root = CreateTempRoot();
        try
        {
            Directory.CreateDirectory(root);
            var stub = new StubBalanceProvider(p => p.ProviderId.Contains("deepseek", StringComparison.OrdinalIgnoreCase));
            var service = await CreateServiceAsync(
                PuddingDataPaths.FromRoot(root), "deepseek", "sk-plain", [stub]);

            var dto = await service.GetBalanceAsync("deepseek");

            Assert.IsTrue(dto.IsAvailable);
            Assert.AreEqual(42.00m, dto.BalanceInfos[0].TotalBalance);
            Assert.AreEqual("deepseek", stub.LastProvider?.ProviderId);
            Assert.AreEqual("sk-plain", stub.LastApiKey);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task GetBalance_UnknownProvider_ThrowsKeyNotFound()
    {
        var root = CreateTempRoot();
        try
        {
            Directory.CreateDirectory(root);
            var service = await CreateServiceAsync(
                PuddingDataPaths.FromRoot(root), "deepseek", "sk-test", [new StubBalanceProvider(_ => true)]);

            await Assert.ThrowsExactlyAsync<KeyNotFoundException>(
                () => service.GetBalanceAsync("missing"));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task GetBalance_AdapterWithoutApiKey_ThrowsInvalidOperation()
    {
        var root = CreateTempRoot();
        try
        {
            Directory.CreateDirectory(root);
            var stub = new StubBalanceProvider(_ => true);
            var service = await CreateServiceAsync(
                PuddingDataPaths.FromRoot(root), "deepseek", apiKey: null, [stub]);

            await Assert.ThrowsExactlyAsync<InvalidOperationException>(
                () => service.GetBalanceAsync("deepseek"));
            Assert.IsNull(stub.LastApiKey);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
