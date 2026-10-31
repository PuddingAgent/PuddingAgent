using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using PuddingRetrieval.Adapters;
using PuddingRetrieval;
using PuddingCodeIndex.Contracts;
using PuddingRetrieval.Contracts;
using PuddingFullTextIndex.Contracts;
using PuddingRuntime.Services;

namespace PuddingRuntime;

/// <summary>
/// ADR-089 检索端口（<see cref="IRetrievalPort"/>）的组合根 —— **单点定义**。
/// <para>
/// <b>为什么必须抽成一个方法</b>：<c>WorkspaceSearchTool</c> 的构造函数参数就是
/// <see cref="IRetrievalPort"/>，而工具是由 <c>AddPuddingToolsFromAssembly</c> 扫描注册的。
/// 产品组合根（PuddingHost）与 <see cref="RuntimeServiceExtensions.AddPuddingRuntime"/> 是
/// 两条不同的装配路径，端口必须由两边共用同一份登记。
/// </para>
/// <para>
/// <b>漏登记的后果是启动期硬失败，不是工具不可用</b>：<c>BuildServiceProvider</c> 会开启
/// ValidateOnBuild，发现 <c>WorkspaceSearchTool</c> 的依赖无法解析就抛
/// <see cref="System.AggregateException"/>，进程以 -532462766 退出，健康检查超时后自动回滚。
/// 2026-10-29 实测：宿主扫描到该工具但没登记端口，连续三次部署全部回滚，根因就在这里。
/// </para>
/// <para>
/// 全部使用 <c>TryAdd</c>：重复调用是幂等的，先注册者胜。
/// </para>
/// </summary>
public static class RetrievalServiceCollectionExtensions
{
    /// <summary>
    /// 登记检索端口及其两个窄依赖（符号面 / 文本面）。依赖 <c>ICodeIndexStore</c>
    /// 必须由调用方先行注册（产品宿主在 Platform 组合根登记，测试用内存实现）。
    /// </summary>
    public static IServiceCollection AddPuddingRetrievalPort(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // 端口自身只依赖窄端口（符号面/文本面）；跨依赖（全文引擎）在这一层解。
        // 文本面用 GetService 取：宿主未注册 IFullTextSearchEngine 时工厂返回 null，
        // 端口据此让 Comment 面显式报 IntentUnsupported —— 缺能力不等于起不来。
        services.TryAddSingleton<IRetrievalScopeDirectories, WorkspaceRootScopeDirectories>();
        services.TryAddSingleton<IRetrievalTextSource>(sp =>
        {
            var engine = sp.GetService<IFullTextSearchEngine>();
            return engine is null
                ? null!
                : new FullTextRetrievalTextSource(
                    engine, sp.GetRequiredService<IRetrievalScopeDirectories>());
        });
        services.TryAddSingleton<IRetrievalPort>(sp => new RetrievalPortV1(
            new CodeIndexStoreSymbolSource(sp.GetRequiredService<ICodeIndexStore>()),
            sp.GetService<IRetrievalTextSource>()));

        return services;
    }
}
