using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PuddingCode.Configuration;
using PuddingDeepSeekWeb;

namespace PuddingRuntime.Services.DeepSeekWeb;

/// <summary>
/// DeepSeek Search / Fetch 组件在 PuddingAgent 组合根中的装配入口（组件化交付规程 S5）。
/// <para>
/// 注册内容：<see cref="DeepSeekWebSettingsProvider"/>（配置解析，含无 <see cref="PuddingDataPaths"/> 的测试组合回退）、
/// <see cref="IDeepSeekWebTransport"/> → <see cref="WebClientDeepSeekTransport"/>（复用既有 IWebClient）、
/// <see cref="IDeepSeekWebService"/> → 组件实现。全部使用 <c>TryAdd</c> 语义：宿主或测试已注册的实现不被覆盖。
/// </para>
/// <para>
/// 由 <c>AddPuddingToolRegistry</c> 在 <c>IWebClient</c> 注册之后调用一次。两个工具类**不在**这里单独登记：
/// 它们由 <c>AddPuddingToolsFromAssembly</c> 的 <c>[Tool]</c> 扫描统一纳入注册表（与其它内建工具同一条路径），
/// 工具所需的配置解析器/协议服务/传输适配器由本方法提供。
/// </para>
/// </summary>
public static class DeepSeekWebServiceCollectionExtensions
{
    /// <summary>注册 DeepSeek Search / Fetch 的配置解析、传输适配与协议服务。</summary>
    public static IServiceCollection AddPuddingDeepSeekWeb(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // 配置解析器：容器注册了 PuddingDataPaths 就用宿主的 data root；否则回退默认 data 路径。
        // logger 也允许缺席（部分测试组合不注册 logging），避免 ValidateOnBuild/解析失败。
        services.TryAddSingleton<DeepSeekWebSettingsProvider>(sp =>
            new DeepSeekWebSettingsProvider(
                sp.GetService<PuddingDataPaths>() ?? DeepSeekWebSettingsProvider.ResolveDefaultDataPaths(),
                sp.GetService<ILogger<DeepSeekWebSettingsProvider>>()
                    ?? NullLogger<DeepSeekWebSettingsProvider>.Instance));

        services.TryAddSingleton<IDeepSeekWebTransport, WebClientDeepSeekTransport>();

        services.TryAddSingleton<IDeepSeekWebService>(sp =>
            new DeepSeekWebService(sp.GetRequiredService<IDeepSeekWebTransport>()));

        return services;
    }
}
