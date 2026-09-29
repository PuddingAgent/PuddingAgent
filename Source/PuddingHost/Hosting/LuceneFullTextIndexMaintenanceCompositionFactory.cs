using PuddingFullTextIndex;
using PuddingFullTextIndex.Contracts;
using PuddingFullTextIndex.Infrastructure.Maintenance;
using PuddingFullTextIndex.Infrastructure.Search;
using PuddingFullTextIndex.Infrastructure.Supply;

namespace PuddingHost.Hosting;

/// <summary>
/// S5b（2026-09-27）：默认装配 —— 宿主配置 → 组件维护器（S3d）+ 局部写引擎（S3a/S3b/S3c）
/// + 跨进程文件租约 + reader 缓存失效接缝 + 资源压力探针。
/// <para>
/// ⚠️ <b>引擎必须是查询侧同一个实例</b>（与 <see cref="LuceneFullTextIndexSupplyCompositionFactory"/> 同一约束）：
/// 维护批次 commit 成功后要失效该语料根的 reader/searcher 缓存，失效打在别的实例上等于没失效。
/// 本类型因此只保留**一个**引擎字段（查询侧那个实例），局部写引擎与组合暴露的
/// <see cref="IFullTextIndexMaintenanceComposition.LiveEngine"/> 都取自它 —— 不给「维护用 A、查询用 B」留缝。
/// </para>
/// <para>
/// ⚠️ 不写第二个「2 秒」「1 GiB」：租约有界等待上界逐字取自
/// <see cref="MaintenanceOptions.LeaseWaitUpperBound"/>，预算逐字取自
/// <see cref="MaintenanceOptions.MaxIndexBytes"/>（已由宿主在
/// <see cref="FullTextIndexMaintenanceOptions.ApplySingleSource"/> 里与供给同源）。
/// </para>
/// </summary>
public sealed class LuceneFullTextIndexMaintenanceCompositionFactory : IFullTextIndexMaintenanceCompositionFactory
{
    private readonly FullTextIndexOptions _indexOptions;

    /// <summary>
    /// 查询侧**同一个**引擎实例。类型是 <see cref="LuceneSearchEngine"/>（而不是接口）：
    /// 局部写引擎的构造参数就是该具体类型（它 <c>sealed</c>，不存在别的实现）。
    /// </summary>
    private readonly LuceneSearchEngine _luceneEngine;

    private readonly IResourcePressureProbe _pressureProbe;
    private readonly TimeProvider _timeProvider;

    /// <summary>构造工厂。</summary>
    /// <param name="indexOptions">索引存储选项（只取索引根目录：live 索引、checkpoint、租约都在它下面）。</param>
    /// <param name="liveEngine">
    /// **查询侧同一个**引擎实例（D4）。这里 fail-closed 校验它确实是 Lucene 实现 ——
    /// 维护引擎要在这一实例上写索引并失效它的 reader 缓存。
    /// </param>
    /// <param name="pressureProbe">资源压力采样接缝（低优先级体检的唯一退避依据）。</param>
    /// <param name="timeProvider">业务时钟（checkpoint 水位线 / 去抖期限 / 快照时刻的唯一来源）。</param>
    public LuceneFullTextIndexMaintenanceCompositionFactory(
        FullTextIndexOptions indexOptions,
        IFullTextIndexRootedEngine liveEngine,
        IResourcePressureProbe pressureProbe,
        TimeProvider timeProvider)
    {
        _indexOptions = indexOptions ?? throw new ArgumentNullException(nameof(indexOptions));
        ArgumentNullException.ThrowIfNull(liveEngine);
        _pressureProbe = pressureProbe ?? throw new ArgumentNullException(nameof(pressureProbe));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

        _luceneEngine = liveEngine as LuceneSearchEngine
            ?? throw new InvalidOperationException(
                "S5b：维护组合根需要查询侧的 LuceneSearchEngine 实例（局部写引擎在同一实例上写索引，" +
                "并在 commit 后失效它的 reader 缓存）；收到的是 " + liveEngine.GetType().Name + "。");
    }

    /// <inheritdoc />
    public IFullTextIndexMaintenanceComposition Create(
        MaintenanceOptions maintenanceOptions,
        IReadOnlyList<string> acceptedScopes)
    {
        ArgumentNullException.ThrowIfNull(maintenanceOptions);
        ArgumentNullException.ThrowIfNull(acceptedScopes);

        var scopes = FullTextIndexMaintenanceOptions.BuildScopes(acceptedScopes, maintenanceOptions);

        // 局部写引擎：租约必填（不存在「不取租约」的退化装配），等待上界来自维护选项。
        var maintenanceEngine = new LuceneFullTextIndexMaintenanceEngine(
            _luceneEngine,
            _indexOptions,
            new FileSupplyLease(_indexOptions),
            maintenanceOptions.LeaseWaitUpperBound,
            new SearchEngineScopeReaderInvalidation(_luceneEngine));

        var maintenance = new LuceneFullTextIndexMaintenance(
            _indexOptions,
            maintenanceOptions,
            maintenanceEngine,
            _pressureProbe,
            _timeProvider);

        return new LuceneFullTextIndexMaintenanceComposition(
            maintenance,
            scopes,
            maintenanceOptions,
            _indexOptions,
            _luceneEngine);
    }
}

/// <summary>
/// 默认维护组合：维护器 + 生效 scope 清单 + 生效组件选项 + 查询侧同源的索引选项与引擎实例。
/// </summary>
internal sealed class LuceneFullTextIndexMaintenanceComposition : IFullTextIndexMaintenanceComposition
{
    internal LuceneFullTextIndexMaintenanceComposition(
        IFullTextIndexMaintenance maintenance,
        IReadOnlyList<FullTextMaintenanceScope> scopes,
        MaintenanceOptions componentOptions,
        FullTextIndexOptions indexOptions,
        IFullTextIndexRootedEngine liveEngine)
    {
        Maintenance = maintenance ?? throw new ArgumentNullException(nameof(maintenance));
        Scopes = scopes ?? throw new ArgumentNullException(nameof(scopes));
        ComponentOptions = componentOptions ?? throw new ArgumentNullException(nameof(componentOptions));
        IndexOptions = indexOptions ?? throw new ArgumentNullException(nameof(indexOptions));
        LiveEngine = liveEngine ?? throw new ArgumentNullException(nameof(liveEngine));
    }

    /// <inheritdoc />
    public IFullTextIndexMaintenance Maintenance { get; }

    /// <inheritdoc />
    public IReadOnlyList<FullTextMaintenanceScope> Scopes { get; }

    /// <inheritdoc />
    public MaintenanceOptions ComponentOptions { get; }

    /// <inheritdoc />
    public FullTextIndexOptions IndexOptions { get; }

    /// <inheritdoc />
    public IFullTextIndexRootedEngine LiveEngine { get; }
}
