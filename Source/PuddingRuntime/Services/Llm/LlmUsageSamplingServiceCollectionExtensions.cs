using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using PuddingCode.Configuration;
using PuddingLlmUsage;
using PuddingLlmUsage.Contracts;
using PuddingLlmUsage.Storage;
using PuddingRuntime.Services.Tools;

namespace PuddingRuntime.Services.Llm;

/// <summary>
/// 余额采样的装配入口。
/// <para>
/// <b>必须由产品组合根调用</b>：本仓库存在两条装配路径，<c>PuddingHost</c> 拥有产品组合根
/// 且<b>不</b>调用 <c>RuntimeServiceExtensions.AddPuddingRuntime</c>（见
/// <c>PuddingHost/Extensions/PuddingServiceCollectionExtensions.Platform.cs</c> 的注释）。
/// 只在这里注册而产品不调用，采样就永远不跑 —— 那是最难发现的一种"半交付"。
/// </para>
/// </summary>
public static class LlmUsageSamplingServiceCollectionExtensions
{
    /// <summary>库文件名（位于 <c>&lt;DataRoot&gt;/databases/</c>）。</summary>
    public const string DatabaseFileName = "llm_usage.db";

    /// <summary>
    /// 注册余额采样的全部依赖并挂上宿主服务。
    /// </summary>
    /// <param name="options">为 <c>null</c> 时用 <see cref="LlmUsageSamplingOptions.Default"/>。</param>
    public static IServiceCollection AddPuddingLlmUsageSampling(
        this IServiceCollection services,
        LlmUsageSamplingOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var effective = (options ?? LlmUsageSamplingOptions.Default).Validate();

        services.TryAddSingleton(effective);
        services.TryAddSingleton(TimeProvider.System);

        services.TryAddSingleton<ILlmUsageStore>(sp =>
        {
            var paths = sp.GetRequiredService<PuddingDataPaths>();
            return new SqliteLlmUsageStore(Path.Combine(paths.DatabasesRoot, DatabaseFileName));
        });

        // 复用工具已有的 HttpClient：两条路径共用一个连接池，
        // 既避免 DNS/连接反复重建，也让测试可以只替换 handler 就控制住网络。
        services.TryAddSingleton<ILlmBalanceProbe>(sp =>
            new LlmProviderBalanceProbe(
                sp.GetRequiredService<PuddingDataPaths>(),
                ProviderBalanceTool.SharedHttp));

        services.TryAddSingleton<LlmBalanceSampler>();
        services.AddHostedService<LlmBalanceSamplingService>();

        return services;
    }
}
