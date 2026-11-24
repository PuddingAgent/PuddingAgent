using System.Security.Cryptography;
using Microsoft.Extensions.Logging.Abstractions;
using PuddingCode.Configuration;
using PuddingPlatform.Data.Dtos;
using PuddingPlatform.Services;

namespace PuddingPlatformTests.Services;

[TestClass]
public sealed class LlmProviderFileServiceTests
{
    [TestMethod]
    public async Task ProviderLimits_ShouldRoundTripThroughConfigFile()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "pudding-llm-provider-tests",
            Guid.NewGuid().ToString("N"));

        try
        {
            Directory.CreateDirectory(root);
            var paths = PuddingDataPaths.FromRoot(root);
            await AtomicFileWriter.WriteJsonAsync(
                paths.SystemConfigFile("llm.providers.json"),
                new PuddingLlmProvidersConfig
                {
                    Providers =
                    [
                        new PuddingLlmProviderConfig
                        {
                            ProviderId = "baseline",
                            Name = "Baseline",
                            BaseUrl = "https://example.invalid/v1",
                            Models =
                            [
                                new PuddingLlmModelConfig
                                {
                                    ModelId = "baseline-model",
                                    Name = "Baseline Model",
                                    Protocol = "openai",
                                },
                            ],
                        },
                    ],
                    Profiles = new Dictionary<string, PuddingLlmProfileConfig>
                    {
                        ["baseline"] = new()
                        {
                            ProviderId = "baseline",
                            ModelId = "baseline-model",
                        },
                    },
                    Roles = new PuddingLlmRoleConfig
                    {
                        Conscious = "baseline",
                        Subconscious = "baseline",
                    },
                });
            var service = new LlmProviderFileService(
                paths,
                NullLogger<LlmProviderFileService>.Instance);

            var created = await service.CreateProviderAsync(new UpsertLlmProviderRequest(
                ProviderId: "moonshot",
                Name: "Moonshot（Kimi，按量付费）",
                BaseUrl: "https://api.moonshot.cn/v1",
                ApiKey: null,
                Description: "Moonshot Kimi K3 按量付费",
                IsEnabled: true,
                MaxConcurrentRequests: 50,
                TokensPerMinute: 2_000_000,
                RequestsPerMinute: 200));

            var listed = (await service.ListProvidersAsync()).Single(p => p.ProviderId == "moonshot");
            var detailed = await service.GetProviderAsync("moonshot");
            var config = await service.LoadAsync();
            var persisted = config.Providers.Single(p => p.ProviderId == "moonshot");

            Assert.AreEqual(50, created.MaxConcurrentRequests);
            Assert.AreEqual(2_000_000, listed.TokensPerMinute);
            Assert.AreEqual(200, detailed!.RequestsPerMinute);
            Assert.AreEqual("Moonshot Kimi K3 按量付费", persisted.Description);
            Assert.AreEqual(50, persisted.MaxConcurrentRequests);
            Assert.AreEqual(2_000_000, persisted.TokensPerMinute);
            Assert.AreEqual(200, persisted.RequestsPerMinute);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    // ── 保存路径的窗口算术校验 ────────────────────────────────────────────────
    // 为何要在这条路径上单独测：SaveConfigAsync 与启动加载共用
    // PuddingFileConfigLoader.ValidateLlmProviders。加载侧的用例（PuddingCoreTests）
    // 只证明「读到坏配置会失败」，证明不了「写进去之前就被拦住」——而后者才是
    // 能担保产品下次还能启动的那一道。若有人删掉 SaveConfigAsync 里的 throw
    // 并改为「记日志后继续写」，加载侧的全部用例仍会通过。

    [TestMethod]
    public async Task CreateModel_WindowCannotCoverReservedOutput_IsRejectedAndFileIsNotWritten()
    {
        var (root, service, configPath) = await CreateServiceAsync();

        try
        {
            var before = Sha256(configPath);

            // 1000 − 64000 − 1024 < 1：窗口连自己的单轮预留都装不下。
            // 运行期会把输入上限夹到 1，该模型此后再也发不出任何请求。
            var ex = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
                service.CreateModelAsync("baseline", ModelRequest(
                    modelId: "degenerate-model",
                    maxContextTokens: 1_000,
                    maxOutputTokens: 64_000)));

            StringAssert.Contains(ex.Message, "maxContextTokens");
            Assert.AreEqual(before, Sha256(configPath), "校验失败时不得改写配置文件");

            var reloaded = await service.LoadAsync();
            Assert.IsFalse(
                reloaded.Providers.SelectMany(p => p.Models).Any(m => m.ModelId == "degenerate-model"),
                "被拒绝的模型不得残留在配置里");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task CreateModel_ExactlyCoversReservedOutput_IsAccepted()
    {
        // 正向对照：65025 − 64000 − 1024 = 1，恰好留出 1 个 token。
        // 没有这一条，一个「拒绝一切写入」的实现同样能让上面的负向用例通过。
        var (root, service, configPath) = await CreateServiceAsync();

        try
        {
            await service.CreateModelAsync("baseline", ModelRequest(
                modelId: "tight-but-valid",
                maxContextTokens: 65_025,
                maxOutputTokens: 64_000));

            var reloaded = await service.LoadAsync();
            Assert.IsTrue(
                reloaded.Providers.SelectMany(p => p.Models).Any(m => m.ModelId == "tight-but-valid"),
                "恰好留 1 token 属于合法边界，必须落盘");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task CreateModel_OneTokenShortOfReservedOutput_IsRejected()
    {
        // 边界另一侧：65024 − 64000 − 1024 = 0，判据是「不足 1」。
        // 与上一条成对，确保比较符 `< 1` 被钉住，而不是被写成 `< 0` 或 `<= 1`。
        var (root, service, _) = await CreateServiceAsync();

        try
        {
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
                service.CreateModelAsync("baseline", ModelRequest(
                    modelId: "one-token-short",
                    maxContextTokens: 65_024,
                    maxOutputTokens: 64_000)));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static UpsertLlmModelRequest ModelRequest(
        string modelId,
        int maxContextTokens,
        int maxOutputTokens)
        => new(
            ModelId: modelId,
            Name: modelId,
            Protocol: "openai",
            Description: null,
            MaxContextTokens: maxContextTokens,
            MaxOutputTokens: maxOutputTokens,
            InputPricePer1MTokens: 0m,
            OutputPricePer1MTokens: 0m,
            CacheHitPricePer1MTokens: 0m,
            CapabilityTags: null,
            IsDeprecated: false,
            IsDefault: false,
            IsEmbedding: false,
            SortOrder: 0,
            MaxConcurrentRequests: null);

    private static async Task<(string Root, LlmProviderFileService Service, string ConfigPath)> CreateServiceAsync()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "pudding-llm-provider-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        var paths = PuddingDataPaths.FromRoot(root);
        var configPath = paths.SystemConfigFile("llm.providers.json");
        await AtomicFileWriter.WriteJsonAsync(
            configPath,
            new PuddingLlmProvidersConfig
            {
                Providers =
                [
                    new PuddingLlmProviderConfig
                    {
                        ProviderId = "baseline",
                        Name = "Baseline",
                        BaseUrl = "https://example.invalid/v1",
                        Models = [],
                    },
                ],
            });

        var service = new LlmProviderFileService(
            paths,
            NullLogger<LlmProviderFileService>.Instance);
        return (root, service, configPath);
    }

    private static string Sha256(string path)
        => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
}
