using PuddingFullTextIndex.Contracts;

namespace PuddingHost.Hosting;

/// <summary>
/// Slice S-A（2026-10-01）：宿主侧**供给组合的共享访问器** —— 把「本次进程里唯一的那个
/// <see cref="IFullTextIndexSupplyComposition"/>」暴露给**所有**消费方（预建服务、状态只读出口、后续切片）。
/// <para>
/// 为什么必须有它（真实缺口，不是推测）：<c>IFullTextIndexSupplyCompositionFactory</c> 只是**惰性工厂**，
/// 组合实例此前是在 <c>IndexPrebuildService.PrebuildAsync</c> 里**局部构造**的 —— 于是任何别的消费方
/// （例如 <c>GET /api/admin/index/status</c>）都拿不到那个实例，job 台账**永远为空**。
/// 本类型是「同一个实例被所有人看见」的唯一接缝。
/// </para>
/// <para>
/// 两条硬契约（R4 / R2）：
/// <list type="number">
/// <item><see cref="GetOrCreate"/> **只在真正进入供给路径时**才被调用 —— 配置门（
/// <c>FullTextIndexSupplyResolver.Resolve</c> 的 <c>IsNoOp</c> 早返回）必须仍在它**之前**；
/// 默认关闭（<c>Enabled=false</c>）时进程内**一次都不许**构造组合。</item>
/// <item><see cref="Current"/> 是**纯只读**的：不构造、不解析 scope、不碰索引根 —— 只读状态查询
/// 必须只用它（于是「未构造」这件事被**如实**报告，而不是被查询动作本身掩盖）。</item>
/// </list>
/// </para>
/// </summary>
public interface IFullTextIndexSupplyAccessor
{
    /// <summary>
    /// 惰性构造一次并缓存；进程内所有消费方共用同一实例。
    /// <para>并发调用安全：同一 <paramref name="options"/> 只会触发一次
    /// <see cref="IFullTextIndexSupplyCompositionFactory.Create"/>。</para>
    /// </summary>
    /// <param name="options">供给参数（Data 目录 <c>system.json</c> 的 <c>FullTextIndex</c> 节）。</param>
    IFullTextIndexSupplyComposition GetOrCreate(FullTextIndexSupplyOptions options);

    /// <summary>
    /// 已构造则返回实例，**未构造返回 <c>null</c>** —— 供只读状态查询使用，**不得**触发构造。
    /// </summary>
    IFullTextIndexSupplyComposition? Current { get; }
}

/// <summary>
/// <see cref="IFullTextIndexSupplyAccessor"/> 的生产实现：<c>lock</c> + double-check 的**进程内单例缓存**。
/// <para>
/// 为什么是「一个进程一个组合」而不是「一个 options 值一个组合」：组合一旦建立就绑定了
/// 引擎实例（reader 缓存 / 失效接缝）、协调器（跨进程租约、job 状态机）与暂存构建器 ——
/// 它们都按**进程生命周期**持有资源，重新构造会产生第二个写者（第二个租约持有者 / 第二份 job 台账），
/// 这正是要防止的事故。供给参数只在进程启动时读取一次（<c>IndexPrebuildService</c> 是 hosted service），
/// 运行期不换值。
/// </para>
/// <para>
/// 注意：本类型**故意不暴露任何「触发重建」的能力**（S-A 切片纯只读查询，不得因状态查询产生写入）。
/// </para>
/// </summary>
internal sealed class FullTextIndexSupplyAccessor : IFullTextIndexSupplyAccessor
{
    private readonly IFullTextIndexSupplyCompositionFactory _factory;
    private readonly object _gate = new();

    private IFullTextIndexSupplyComposition? _current;

    /// <summary>
    /// 构造（**必须 public**：DI 的激活器只认公共构造函数，即 <c>ValidateOnBuild</c> 也会检查它）。
    /// 类型自身是 <c>internal</c>，因此可见性仍然只限本程序集（+ 测试程序集）。
    /// </summary>
    /// <param name="factory">供给组合的惰性工厂（真正的构造推迟到首次 <see cref="GetOrCreate"/>）。</param>
    public FullTextIndexSupplyAccessor(IFullTextIndexSupplyCompositionFactory factory)
        => _factory = factory ?? throw new ArgumentNullException(nameof(factory));

    /// <inheritdoc />
    public IFullTextIndexSupplyComposition? Current => Volatile.Read(ref _current);

    /// <inheritdoc />
    public IFullTextIndexSupplyComposition GetOrCreate(FullTextIndexSupplyOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        // 快路径：已构造则直接返回（绝不在热路径上加锁，也绝不重复构造）。
        var existing = Volatile.Read(ref _current);
        if (existing is not null)
            return existing;

        lock (_gate)
        {
            // double-check：并发进入时只有一个线程能构造，其余线程复用其结果。
            _current ??= _factory.Create(options);
            return _current;
        }
    }
}
