using PuddingFullTextIndex;
using PuddingFullTextIndex.Contracts;

namespace PuddingHost.Hosting;

/// <summary>
/// S5（2026-09-25）：宿主侧「供给组合」端口 —— 宿主对全文索引的**唯一**写入口。
/// <para>
/// 为什么需要它：U4-7 的 <c>IndexPrebuildService</c> 直接调用
/// <c>IFullTextSearchEngine.BuildIndexAsync</c> 写 live 索引，绕开了 A1 协调器
/// （scope 规范化 / 幂等合并 / 跨进程租约 / job 状态机）与 A2a 暂存供给
/// （staging 构建 → 预算硬限 → 原子切换）。S5 把写路径整条换成
/// 「向协调器提交 → 轮询到终态」，本端口就是那条路径的句柄。
/// </para>
/// <para>
/// 依赖方向：<c>PuddingHost → PuddingFullTextIndex</c>（组合根持有组件；组件不认识宿主）。
/// 本类型只依赖组件的 **public Contracts**，不引用任何 <c>internal</c> 布局实现
/// （<c>SupplyIndexDirectoryLayout</c> 等仍是组件私有）。
/// </para>
/// </summary>
public interface IFullTextIndexSupplyComposition
{
    /// <summary>组件内唯一的供给入口（A1）。宿主只提交任务与轮询状态，**不直接写索引**。</summary>
    IFullTextIndexSupplyCoordinator Coordinator { get; }

    /// <summary>
    /// **实际生效**的组件侧供给策略（R2）：由宿主配置显式流入
    /// （见 <see cref="IFullTextIndexSupplyCompositionFactory.Create"/>）。
    /// 供日志与断言核对「配置值 == 组件收到的值」，避免两侧各自漂移。
    /// </summary>
    SupplyCoordinatorOptions ComponentOptions { get; }

    /// <summary>
    /// 某个 scope 的 **live 索引目录**最后写入时间（UTC）；读不到 ⇒ <see cref="DateTimeOffset.MinValue"/>
    /// （安全方向：判为需要重建，绝不因读不到就跳过）。
    /// <para>
    /// R3 per-scope 新鲜度：口径是「该 scope **自己**的索引目录」，**不是**整个索引根 ——
    /// 索引根 mtime 会把 A scope 的新鲜度串给 B scope（U4-7 的既有缺陷）。
    /// 目录由 <see cref="IFullTextIndexRootedEngine.ResolveIndexDirectory"/> 解析，
    /// 该成员是组件内「语料根 → 索引目录」映射的**单一真源**（宿主**不复刻**哈希规则）。
    /// </para>
    /// </summary>
    DateTimeOffset LiveIndexLastWriteUtc(string scopeRootPath);
}

/// <summary>
/// 供给组合的**惰性**工厂（S5）。
/// <para>
/// 为什么不直接注册 <see cref="IFullTextIndexSupplyCoordinator"/>：R4 要求
/// 「<c>Enabled=false</c> ⇒ 不解析 scope、**不构造协调器组合**、不 touch 索引根」。
/// 工厂对象本身在宿主启动时就存在（很轻：只持有引擎引用，不持有任何索引根句柄），
/// 而协调器 / staged builder / 语料清点 / 文件租约的实例化被推迟到
/// <c>IndexPrebuildService</c> 真正进入供给路径之后 —— 默认关闭时**永不发生**。
/// </para>
/// </summary>
public interface IFullTextIndexSupplyCompositionFactory
{
    /// <summary>按宿主配置构造一次供给组合（调用方负责复用返回值：一次启动只构造一次）。</summary>
    /// <param name="supplyOptions">
    /// 供给参数（Data 目录 <c>system.json</c> 的 <c>FullTextIndex</c> 节）。
    /// <c>MaxIndexBytes</c>（预算）与 <c>MinRebuildInterval</c> 会被**显式传入组件**
    /// （<see cref="SupplyCoordinatorOptions"/>），而不是让组件用自带常量兜底 ——
    /// 这样「1 GiB」在宿主侧仍然只有一个真源（配置类），组件侧的常量退化为未接线时的兜底。
    /// </param>
    IFullTextIndexSupplyComposition Create(FullTextIndexSupplyOptions supplyOptions);
}
