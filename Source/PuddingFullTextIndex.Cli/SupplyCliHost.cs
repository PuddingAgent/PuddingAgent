using PuddingFullTextIndex.Contracts;
using PuddingFullTextIndex.Infrastructure.Search;
using PuddingFullTextIndex.Infrastructure.Supply;

namespace PuddingFullTextIndex.Cli;

/// <summary>
/// 索引构建端口的度量记录器：协调器把 builder 的度量折进 job 的 <c>Message</c> 文本，
/// 这里把**结构化**的原始结果留下来，供 <c>build --wait</c> 如实打印 <c>IndexedFileCount/TotalBytes/ElapsedMs</c>。
/// </summary>
public sealed class RecordingIndexBuilder : IFullTextIndexBuilder
{
    private readonly IFullTextIndexBuilder _inner;

    public RecordingIndexBuilder(IFullTextIndexBuilder inner)
    {
        ArgumentNullException.ThrowIfNull(inner);
        _inner = inner;
    }

    /// <summary>最近一次构建结果；本进程没跑过构建时为 null（例如合并到已有成功 job）。</summary>
    public SupplyBuildResult? LastResult { get; private set; }

    public async Task<SupplyBuildResult> BuildAsync(SupplyScope scope, CancellationToken ct = default)
    {
        var result = await _inner.BuildAsync(scope, ct).ConfigureAwait(false);
        LastResult = result;
        return result;
    }
}

/// <summary>组合根快照：CLI 组装出的全部端口（与 A1 协调器构造函数一一对应）。</summary>
/// <param name="Engine">真实引擎（<c>status</c> 用它的公开 <c>HasIndex</c> 才是权威判定）。</param>
/// <param name="Inventory">语料清点端口。</param>
/// <param name="Recorder">构建端口（记录度量的包装器；协调器拿到的就是它）。</param>
/// <param name="Lease">跨进程 scope 租约。</param>
/// <param name="CoordinatorOptions">协调器策略（含 OwnerId）。</param>
public sealed record SupplyCliComposition(
    IFullTextSearchEngine Engine,
    IFullTextIndexSupplyInventory Inventory,
    RecordingIndexBuilder Recorder,
    IFullTextSupplyLease Lease,
    SupplyCoordinatorOptions CoordinatorOptions);

/// <summary>
/// CLI 的可注入装配面（默认值 = A1 真实构件；A2 的 staging/预算硬限/原子切换一律不碰）。
/// <para>
/// 单测用替身替换 builder / lease / coordinator，就能在**不起进程、不碰真实索引根**的前提下
/// 覆盖 Rejected / Busy / Failed / 跨进程 等分支。
/// </para>
/// </summary>
public sealed record SupplyCliHost(
    Func<FullTextIndexOptions, IFullTextSearchEngine>? EngineFactory = null,
    Func<FullTextIndexOptions, IFullTextIndexSupplyInventory>? InventoryFactory = null,
    Func<IFullTextSearchEngine, IFullTextIndexBuilder>? BuilderFactory = null,
    Func<FullTextIndexOptions, IFullTextSupplyLease>? LeaseFactory = null,
    Func<SupplyCoordinatorOptions>? CoordinatorOptionsFactory = null,
    Func<SupplyCliComposition, IFullTextIndexSupplyCoordinator>? CoordinatorFactory = null,
    Func<TimeSpan, CancellationToken, Task>? DelayAsync = null,
    TimeSpan? WaitPollInterval = null,
    TimeSpan? BuildTimeout = null)
{
    /// <summary><c>build --wait</c> 的默认轮询间隔。</summary>
    public static TimeSpan DefaultWaitPollInterval { get; } = TimeSpan.FromMilliseconds(250);

    /// <summary><c>build --wait</c> 的默认等待上限（超时 ⇒ 退出码 3，绝不谎报成功）。</summary>
    public static TimeSpan DefaultBuildTimeout { get; } = TimeSpan.FromMinutes(10);

    internal IFullTextSearchEngine CreateEngine(FullTextIndexOptions options) =>
        (EngineFactory ?? (o => new LuceneSearchEngine(o)))(options);

    internal IFullTextIndexSupplyInventory CreateInventory(FullTextIndexOptions options) =>
        (InventoryFactory ?? (o => new FileSystemSupplyInventory(o)))(options);

    internal IFullTextIndexBuilder CreateBuilder(IFullTextSearchEngine engine) =>
        (BuilderFactory ?? (e => new FullTextSearchEngineIndexBuilder(e)))(engine);

    internal IFullTextSupplyLease CreateLease(FullTextIndexOptions options) =>
        (LeaseFactory ?? (o => new FileSupplyLease(o)))(options);

    internal SupplyCoordinatorOptions CreateCoordinatorOptions() =>
        (CoordinatorOptionsFactory ?? (() => new SupplyCoordinatorOptions()))();

    internal IFullTextIndexSupplyCoordinator CreateCoordinator(SupplyCliComposition composition) =>
        (CoordinatorFactory ?? (c => new FullTextIndexSupplyCoordinator(
            c.Inventory, c.Recorder, c.Lease, c.CoordinatorOptions)))(composition);

    internal Task WaitAsync(TimeSpan delay, CancellationToken ct) =>
        (DelayAsync ?? ((d, c) => Task.Delay(d, c)))(delay, ct);

    internal TimeSpan WaitPollIntervalValue => WaitPollInterval ?? DefaultWaitPollInterval;

    internal TimeSpan BuildTimeoutValue => BuildTimeout ?? DefaultBuildTimeout;
}
