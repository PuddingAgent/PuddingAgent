using PuddingFullTextIndex;
using PuddingFullTextIndex.Contracts;
using PuddingFullTextIndex.Infrastructure.Supply;

namespace PuddingHost.Hosting;

/// <summary>
/// S5（2026-09-25）：默认装配 —— 宿主配置 → 组件协调器（A1）+ 暂存供给 builder（A2a）
/// + 语料清点 + 跨进程文件租约。
/// <para>
/// ⚠️ <b>引擎必须是查询侧同一个实例</b>：staged 供给在原子切换前后要失效 live 引擎的
/// reader/searcher 缓存（<c>IFullTextIndexRootedEngine.InvalidateScope</c>），
/// 失效打在别的实例上等于没失效（切换后仍会读到旧索引）。
/// </para>
/// <para>
/// ⚠️ 不写第二个「1 GiB」：预算与最小重建间隔**逐字取自**宿主配置
/// （<see cref="FullTextIndexSupplyOptions.MaxIndexBytes"/> /
/// <see cref="FullTextIndexSupplyOptions.MinRebuildInterval"/>）。
/// </para>
/// </summary>
public sealed class LuceneFullTextIndexSupplyCompositionFactory : IFullTextIndexSupplyCompositionFactory
{
    private readonly FullTextIndexOptions _indexOptions;
    private readonly IFullTextIndexRootedEngine _liveEngine;
    private readonly Func<FullTextIndexOptions, IFullTextIndexRootedEngine> _stagingEngineFactory;

    /// <summary>构造工厂。</summary>
    /// <param name="indexOptions">索引存储选项（只取索引根目录作为布局根：staging/trash/租约都在它下面）。</param>
    /// <param name="liveEngine">live 索引根上的引擎实例 —— 与查询侧必须是同一个实例。</param>
    /// <param name="stagingEngineFactory">
    /// 用「索引根被替换成 staging 根」的选项造一个隔离引擎实例（每次 job 一个）。
    /// </param>
    public LuceneFullTextIndexSupplyCompositionFactory(
        FullTextIndexOptions indexOptions,
        IFullTextIndexRootedEngine liveEngine,
        Func<FullTextIndexOptions, IFullTextIndexRootedEngine> stagingEngineFactory)
    {
        _indexOptions = indexOptions ?? throw new ArgumentNullException(nameof(indexOptions));
        _liveEngine = liveEngine ?? throw new ArgumentNullException(nameof(liveEngine));
        _stagingEngineFactory = stagingEngineFactory ?? throw new ArgumentNullException(nameof(stagingEngineFactory));
    }

    /// <inheritdoc />
    public IFullTextIndexSupplyComposition Create(FullTextIndexSupplyOptions supplyOptions)
    {
        ArgumentNullException.ThrowIfNull(supplyOptions);

        // R2 配置流入组件：宿主配置是唯一真源。「1 GiB」在宿主侧只允许出现在
        // FullTextIndexSupplyOptions.DefaultMaxIndexBytes（配置类）一处 —— 这里**不写字面量**。
        // 只在装配点映射一次：请求级不再重复传同一个数（避免两处各说一套）。
        var componentOptions = new SupplyCoordinatorOptions
        {
            DefaultBudgetBytes = supplyOptions.MaxIndexBytes,
            MinRebuildInterval = supplyOptions.MinRebuildInterval,
        };

        var coordinator = new FullTextIndexSupplyCoordinator(
            new FileSystemSupplyInventory(_indexOptions),
            new StagedFullTextIndexBuilder(_liveEngine, _stagingEngineFactory, _indexOptions, componentOptions),
            new FileSupplyLease(_indexOptions),
            componentOptions);

        return new LuceneFullTextIndexSupplyComposition(coordinator, componentOptions, _liveEngine);
    }
}

/// <summary>
/// 默认供给组合：协调器 + 配置快照 + per-scope 新鲜度探针（该 scope 自己的 live 索引目录 mtime）。
/// </summary>
internal sealed class LuceneFullTextIndexSupplyComposition : IFullTextIndexSupplyComposition
{
    private readonly IFullTextIndexRootedEngine _liveEngine;

    internal LuceneFullTextIndexSupplyComposition(
        IFullTextIndexSupplyCoordinator coordinator,
        SupplyCoordinatorOptions componentOptions,
        IFullTextIndexRootedEngine liveEngine)
    {
        Coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        ComponentOptions = componentOptions ?? throw new ArgumentNullException(nameof(componentOptions));
        _liveEngine = liveEngine ?? throw new ArgumentNullException(nameof(liveEngine));
    }

    /// <inheritdoc />
    public IFullTextIndexSupplyCoordinator Coordinator { get; }

    /// <inheritdoc />
    public SupplyCoordinatorOptions ComponentOptions { get; }

    /// <inheritdoc />
    public DateTimeOffset LiveIndexLastWriteUtc(string scopeRootPath)
    {
        try
        {
            // R3：per-scope。目录映射走组件内单一真源（ResolveIndexDirectory），
            // 宿主**不复刻** sha256(规范化全路径) 规则 —— 复刻即第三处真源。
            var indexDirectory = _liveEngine.ResolveIndexDirectory(scopeRootPath);
            if (!Directory.Exists(indexDirectory))
                return DateTimeOffset.MinValue;

            // GetLastWriteTimeUtc 的 Kind 已是 Utc；显式 SpecifyKind 让 DateTimeOffset 的偏移
            // 不依赖本机时区（确定性，避免在不同时区的机器上判出不同的「年龄」）。
            return new DateTimeOffset(DateTime.SpecifyKind(
                Directory.GetLastWriteTimeUtc(indexDirectory),
                DateTimeKind.Utc));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // 读不到（IO / 权限 / 非法路径）⇒ MinValue ⇒ 判需重建（安全方向，绝不因读不到就跳过）。
            return DateTimeOffset.MinValue;
        }
    }
}
