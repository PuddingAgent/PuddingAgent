using PuddingFullTextIndex;
using PuddingFullTextIndex.Contracts;
using PuddingFullTextIndex.Infrastructure.Maintenance;

namespace PuddingHost.Hosting;

/// <summary>
/// S5b（2026-09-27）：宿主侧「维护组合」端口 —— 宿主对全文索引**局部维护循环**的唯一句柄。
/// <para>
/// 「供给」管的是启动时**整目录预建 / 原子替换**（<see cref="IFullTextIndexSupplyComposition"/>）；
/// 「维护」管的是运行期**按变更集局部写**（组件 S3a~S3e 已交付）。两条路径写的是同一份 live 索引，
/// 因此都要求与查询侧**共用同一个引擎实例**（见 <see cref="LiveEngine"/>）。
/// </para>
/// <para>
/// 依赖方向：<c>PuddingHost → PuddingFullTextIndex</c>（组合根持有组件；组件不认识宿主）。
/// 本类型只依赖组件的 **public Contracts**，不引用任何 <c>internal</c> 布局实现。
/// </para>
/// </summary>
public interface IFullTextIndexMaintenanceComposition
{
    /// <summary>组件内唯一的维护入口（S3d）：宿主只调用 <c>StartAsync</c> / <c>StopAsync</c> 与只读快照。</summary>
    IFullTextIndexMaintenance Maintenance { get; }

    /// <summary>
    /// 本次生效的 scope 清单（<c>StartAsync</c> 的入参）。键由
    /// <see cref="FullTextIndexMaintenanceOptions.BuildScopes"/> 经组件真源推导，与供给侧逐字符同口径。
    /// </summary>
    IReadOnlyList<FullTextMaintenanceScope> Scopes { get; }

    /// <summary>
    /// **实际生效**的组件侧维护策略（D1）：由维护节绑定值 + 供给同源的四项合并而来
    /// （见 <see cref="FullTextIndexMaintenanceOptions.ApplySingleSource"/>）。
    /// 供日志与断言核对「配置值 == 组件收到的值」，避免两侧各自漂移。
    /// </summary>
    MaintenanceOptions ComponentOptions { get; }

    /// <summary>
    /// 查询侧**同一个** <see cref="FullTextIndexOptions"/> 实例（同一索引根）。
    /// 维护受 <c>IndexRootDirectory</c> 约束：checkpoint 与 live 索引都必须落在查询侧那个根下。
    /// </summary>
    FullTextIndexOptions IndexOptions { get; }

    /// <summary>
    /// 与**查询侧同一个** <see cref="IFullTextIndexRootedEngine"/> 实例（D4）。
    /// <para>
    /// ⚠️ 为什么必须是同一实例而不是「同型新实例」：维护批次 commit 成功后要经
    /// <c>IScopeReaderInvalidation</c> 失效该语料根的 reader / searcher 缓存；失效打在**别的实例**上
    /// 等于没失效 —— 查询会继续命中陈旧 reader，直到进程重启才看到新文档。
    /// </para>
    /// </summary>
    IFullTextIndexRootedEngine LiveEngine { get; }
}

/// <summary>
/// 维护组合的**惰性**工厂（S5b）。
/// <para>
/// 与供给侧同形：工厂对象本身很轻（只持有引擎 / 探针 / 时钟引用，不持有任何索引根句柄），
/// 而维护器、局部写引擎、跨进程租约、reader 失效接缝的实例化被推迟到
/// <c>FullTextIndexMaintenanceHostedService</c> 真正进入维护路径之后 ——
/// 默认关闭（<c>FullTextIndex:Maintenance:Enabled=false</c>）时**永不发生**。
/// </para>
/// </summary>
public interface IFullTextIndexMaintenanceCompositionFactory
{
    /// <summary>按宿主配置构造一次维护组合（调用方负责复用返回值：一次启动只构造一次）。</summary>
    /// <param name="maintenanceOptions">
    /// 生效的维护选项（已合并供给同源的 scope / 预算 / 索引根，并已通过
    /// <see cref="MaintenanceOptions.Validate"/> 的 fail-closed 校验）。
    /// </param>
    /// <param name="acceptedScopes">
    /// 已通过供给解析器校验的**绝对**语料根（顺序即维护 scope 顺序）。
    /// </param>
    IFullTextIndexMaintenanceComposition Create(
        MaintenanceOptions maintenanceOptions,
        IReadOnlyList<string> acceptedScopes);
}
