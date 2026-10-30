using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using PuddingCode.Configuration;
using PuddingLlmUsage;
using PuddingLlmUsage.Contracts;
using PuddingLlmUsage.Storage;
using PuddingRuntime.Services.Llm;

namespace PuddingRuntimeTests.Services;

/// <summary>
/// 装配契约：<c>AddPuddingLlmUsageSampling</c> 注册的东西必须<b>真能解析出来</b>。
/// <para>
/// 这类"注册了但解析不了"的缺陷只会在宿主启动时才炸 —— 而那时 Core 已经在重启过程中，
/// 排查成本极高。用 <see cref="ServiceProviderOptions.ValidateOnBuild"/> 把它提前到单元测试。
/// </para>
/// <para>
/// 本测试<b>不启动</b>任何宿主服务，也不发网络请求：只验证容器能构造出对象图。
/// </para>
/// </summary>
[TestClass]
public sealed class LlmUsageSamplingRegistrationTests
{
    private string _root = string.Empty;

    [TestInitialize]
    public void Initialize()
    {
        _root = Path.Combine(Path.GetTempPath(), "pudding-llm-usage-di-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    [TestCleanup]
    public void Cleanup()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // 清理失败不影响结论。
        }
    }

    private ServiceProvider BuildProvider(LlmUsageSamplingOptions? options = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(PuddingDataPaths.FromRoot(_root));
        PuddingRuntime.Services.Llm.LlmUsageSamplingServiceCollectionExtensions
            .AddPuddingLlmUsageSampling(services, options);

        return services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true,
        });
    }

    [TestMethod]
    public void Registration_ResolvesTheWholeObjectGraph()
    {
        using var provider = BuildProvider();

        // ValidateOnBuild=true 已保证所有注册可解析；这里再逐个取一次，失败会指向具体类型。
        Assert.IsNotNull(provider.GetRequiredService<LlmBalanceSampler>());
        Assert.IsNotNull(provider.GetRequiredService<ILlmUsageStore>());
        Assert.IsNotNull(provider.GetRequiredService<ILlmBalanceProbe>());
        Assert.IsNotNull(provider.GetRequiredService<LlmUsageSamplingOptions>());
        Assert.IsNotNull(provider.GetRequiredService<TimeProvider>());
    }

    [TestMethod]
    public void Registration_AddsTheHostedServiceExactlyOnce()
    {
        using var provider = BuildProvider();

        var samplers = provider.GetServices<IHostedService>()
            .Where(service => service is LlmBalanceSamplingService)
            .ToArray();

        Assert.AreEqual(1, samplers.Length,
            "采样服务必须注册且只注册一次 —— 两次会让每轮采样重复发请求、重复写库。");
    }

    [TestMethod]
    public void Registration_UsesTheProvidedOptions_NotTheDefault()
    {
        var custom = new LlmUsageSamplingOptions(
            Enabled: false,
            ProviderIds: ["bigmodel"],
            Interval: TimeSpan.FromMinutes(5),
            PerProviderTimeout: TimeSpan.FromSeconds(10));

        using var provider = BuildProvider(custom);
        var resolved = provider.GetRequiredService<LlmUsageSamplingOptions>();

        Assert.IsFalse(resolved.Enabled);
        CollectionAssert.AreEqual(new[] { "bigmodel" }, resolved.ProviderIds.ToArray());
        Assert.AreEqual(TimeSpan.FromMinutes(5), resolved.Interval);
    }

    [TestMethod]
    public void Registration_RejectsInvalidOptions()
    {
        var invalid = new LlmUsageSamplingOptions(
            Enabled: true,
            ProviderIds: [],
            Interval: TimeSpan.FromMinutes(10),
            PerProviderTimeout: TimeSpan.FromSeconds(20));

        Assert.Throws<ArgumentException>(() => BuildProvider(invalid).Dispose());
    }

    [TestMethod]
    public void Registration_StoreUsesTheDataRootDatabasesDirectory()
    {
        using var provider = BuildProvider();

        // 库必须落在 <DataRoot>/databases/ 下，而不是进程工作目录 ——
        // 后者在部署后指向活产物目录，会把用户数据写进 bin。
        var store = provider.GetRequiredService<ILlmUsageStore>();
        Assert.IsInstanceOfType<SqliteLlmUsageStore>(store);

        var expected = Path.Combine(_root, "databases", LlmUsageSamplingServiceCollectionExtensions.DatabaseFileName);
        Assert.IsFalse(File.Exists(expected), "构造阶段不应创建数据库文件（第一次采样时才建）。");

        store.InitializeAsync().GetAwaiter().GetResult();
        Assert.IsTrue(File.Exists(expected), $"数据库应创建在 {expected}。");
    }
}
