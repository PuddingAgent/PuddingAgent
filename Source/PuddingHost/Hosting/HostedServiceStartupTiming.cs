using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace PuddingHost.Hosting;

/// <summary>
/// 给每个 <see cref="IHostedService"/> 的 <c>StartAsync</c> 记一个阶段（<c>host.start.&lt;类型名&gt;</c>），
/// 用于把 <c>app.StartAsync()</c> 那个"总时长"拆成"哪个服务花了多少"。它**不改行为、不改注册顺序、不改生命周期**：
/// 包装只替换 <c>IHostedService</c> 的描述符，实例仍由容器解析，异常与返回值原样透传。
/// <para>
/// **只在容器里已经注册了 <see cref="IStartupPhaseSink"/> 时才包装**——Console/生产路径没有 sink，
/// 于是 DI 图与引入本类之前完全一致（这是"埋点不得改变被测对象"的硬要求）。
/// </para>
/// <para>
/// 名字取自**实例类型**（而不是描述符），因为部分服务是"先注册具体单例、再由 <c>IHostedService</c> 派生"
/// 的写法，描述符里看不到真实类型。
/// </para>
/// </summary>
public static class HostedServiceStartupTiming
{
    /// <summary>阶段名前缀。报告侧（Foundation）持有同值常量。</summary>
    public const string PhasePrefix = "host.start.";

    /// <summary>有 sink 时把每个 hosted service 包一层计时；无 sink 时**什么都不做**。</summary>
    public static void WrapWhenEvidenceRequested(IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        if (FindSink(services) is null) return;

        for (var index = 0; index < services.Count; index++)
        {
            var descriptor = services[index];
            if (descriptor.ServiceType != typeof(IHostedService)) continue;
            services[index] = ServiceDescriptor.Singleton<IHostedService>(provider =>
                new TimedHostedService(
                    Resolve(descriptor, provider),
                    provider.GetService<IStartupPhaseSink>()));
        }
    }

    /// <summary>
    /// 取容器里已注册的 sink 实例。只认"实例注册"（Desktop 组合根就是这么注册的）；
    /// 工厂/类型注册在这里不解析，避免为了埋点在 Build 之前构造一个 DI 作用域。
    /// </summary>
    internal static IStartupPhaseSink? FindSink(IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        for (var index = services.Count - 1; index >= 0; index--)
        {
            var descriptor = services[index];
            if (descriptor.ServiceType == typeof(IStartupPhaseSink) && descriptor.ImplementationInstance is IStartupPhaseSink sink)
                return sink;
        }
        return null;
    }

    private static IHostedService Resolve(ServiceDescriptor descriptor, IServiceProvider provider)
    {
        if (descriptor.ImplementationInstance is IHostedService instance) return instance;
        if (descriptor.ImplementationFactory is { } factory) return (IHostedService)factory(provider);
        if (descriptor.ImplementationType is { } type)
            return (IHostedService)ActivatorUtilities.CreateInstance(provider, type);
        throw new InvalidOperationException($"无法解析 hosted service 描述符：{descriptor}");
    }

    private sealed class TimedHostedService(IHostedService inner, IStartupPhaseSink? sink) : IHostedService
    {
        public async Task StartAsync(CancellationToken cancellationToken)
        {
            using var phase = sink?.Phase(PhasePrefix + inner.GetType().Name);
            await inner.StartAsync(cancellationToken).ConfigureAwait(false);
            phase?.Complete();
        }

        public Task StopAsync(CancellationToken cancellationToken) => inner.StopAsync(cancellationToken);
    }
}
