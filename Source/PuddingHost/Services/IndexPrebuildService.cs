using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PuddingFullTextIndex;
using PuddingFullTextIndex.Contracts;
using PuddingHost.Hosting;

namespace PuddingAgent.Services;

/// <summary>
/// 应用启动时在后台按**配置显式列出的 scope**构建 Lucene 全文索引。
/// <para>
/// 契约（U4-7 R4，硬约束）：
/// <list type="bullet">
/// <item><c>StartAsync</c> **永不阻塞宿主启动**（长活在启动路径之外，照
/// <c>CodeIndexMaintenanceHostedService</c> 的精神）。</item>
/// <item>**默认配置**（<c>system.json</c> 无 <c>FullTextIndex</c> 节 / <c>Enabled=false</c>）下
/// <c>StartAsync</c> 立即返回，**不建索引、零索引 I/O、不记 Error**。</item>
/// <item>校验不过 ⇒ fail-closed：记 Error 说清原因，然后**什么都不做**（绝不静默空转、绝不部分生效）。</item>
/// </list>
/// </para>
/// <para>
/// 历史缺陷（U4-7 §1 实测）：旧实现用 <c>Directory.GetCurrentDirectory()</c> 当索引目标（本机 = 运行时
/// bin 目录），与其自身注释「工作区根目录」不符。现在目标**只能来自配置**
/// （<see cref="FullTextIndexSupplyOptions.Scopes"/>，相对项以显式声明的
/// <see cref="FullTextIndexSupplyOptions.WorkspaceRoot"/> 为基准），**不再读 CWD**。
/// </para>
/// </summary>
public sealed class IndexPrebuildService : IHostedService
{
    /// <summary>
    /// 预建开始前的默认延迟：先让 Kestrel 绑定端口（启动即 fire-and-forget 会在 THREAD_POOL 有限时饥饿绑定）。
    /// </summary>
    public static readonly TimeSpan DefaultStartupDelay = TimeSpan.FromSeconds(10);

    /// <summary>
    /// 预建开始前的延迟。生产用 <see cref="DefaultStartupDelay"/>；单测置 <c>TimeSpan.Zero</c> 以便在毫秒级
    /// 观测"后台路径到底做没做"（否则每个断言都要等 10 秒）。**只影响 Enabled=true 的路径**，
    /// 默认关闭时本属性根本不参与（<see cref="StartAsync"/> 立即返回）。
    /// </summary>
    public TimeSpan StartupDelay { get; init; } = DefaultStartupDelay;

    private readonly IFullTextSearchEngine _searchEngine;
    private readonly IOptions<FullTextIndexSupplyOptions> _supplyOptions;
    private readonly FullTextIndexOptions _indexOptions;
    private readonly ILogger<IndexPrebuildService> _logger;

    /// <summary>Creates the prebuild service.</summary>
    /// <param name="searchEngine">全文索引引擎（生产实现为 Lucene）。</param>
    /// <param name="supplyOptions">供给参数（Data 目录 <c>system.json</c> 的 <c>FullTextIndex</c> 节）。</param>
    /// <param name="indexOptions">索引存储选项（只读其索引根目录，用于判断索引新旧）。</param>
    /// <param name="logger">Logger.</param>
    public IndexPrebuildService(
        IFullTextSearchEngine searchEngine,
        IOptions<FullTextIndexSupplyOptions> supplyOptions,
        FullTextIndexOptions indexOptions,
        ILogger<IndexPrebuildService> logger)
    {
        _searchEngine = searchEngine;
        _supplyOptions = supplyOptions;
        _indexOptions = indexOptions;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken ct)
    {
        var options = _supplyOptions.Value;
        var resolution = FullTextIndexSupplyResolver.Resolve(options);

        // 默认路径（Enabled=false）：立即返回，零索引 I/O，且**不记 Error** —— 「没开」不是错误。
        if (resolution.IsNoOp)
            return Task.CompletedTask;

        if (!resolution.Succeeded)
        {
            // fail-closed：配置开着但校验不过 ⇒ 大声拒绝，且什么都不做。
            // 禁止「静默空转」（会让「开了但不工作」不可见）与「部分生效」（会让配置错了不可见）。
            _logger.LogError(
                "[IndexPrebuild] Full-text index supply configuration rejected; nothing will be indexed. {Reasons}",
                resolution.Describe());
            return Task.CompletedTask;
        }

        _logger.LogInformation(
            "[IndexPrebuild] Full-text index supply enabled; {Count} scope(s) accepted: {Scopes}",
            resolution.AcceptedScopes.Count,
            string.Join(", ", resolution.AcceptedScopes));

        // 启动路径之外的后台作业；StartAsync 立即返回。
        _ = Task.Run(() => PrebuildAsync(resolution, ct), ct);
        return Task.CompletedTask;
    }

    private async Task PrebuildAsync(FullTextIndexSupplyResolution resolution, CancellationToken ct)
    {
        try
        {
            await Task.Delay(StartupDelay, ct).ConfigureAwait(false);

            var minRebuildInterval = _supplyOptions.Value.MinRebuildInterval;

            foreach (var scope in resolution.AcceptedScopes)
            {
                ct.ThrowIfCancellationRequested();

                var hasIndex = _searchEngine.HasIndex(scope);
                if (!IndexPrebuildFreshness.ShouldRebuild(
                        hasIndex, IndexRootLastWriteUtc(), DateTimeOffset.UtcNow, minRebuildInterval))
                {
                    _logger.LogInformation(
                        "[IndexPrebuild] Index for {Dir} is fresh (index root mtime {LastWrite:o}, " +
                        "min rebuild interval {Interval}), skipping",
                        scope, IndexRootLastWriteUtc(), minRebuildInterval);
                    continue;
                }

                _logger.LogInformation("[IndexPrebuild] Building index for {Dir}...", scope);
                var result = await _searchEngine.BuildIndexAsync(scope, ct: ct).ConfigureAwait(false);

                if (result.Success)
                {
                    _logger.LogInformation(
                        "[IndexPrebuild] Index build completed for {Dir}: {Files} files, {Bytes} bytes, {Elapsed}ms",
                        scope, result.IndexedFileCount, result.TotalBytes, result.ElapsedMs);
                }
                else
                {
                    _logger.LogWarning("[IndexPrebuild] Index build failed for {Dir}: {Error}", scope, result.Error);
                }
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("[IndexPrebuild] Index build cancelled during shutdown");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[IndexPrebuild] Index build error (non-fatal)");
        }
    }

    /// <summary>
    /// 索引根目录的最后写入时间（UTC）。读不到（不存在 / IO 失败）⇒ <see cref="DateTimeOffset.MinValue"/>，
    /// 于是「年龄」极大 ⇒ <see cref="IndexPrebuildFreshness.ShouldRebuild"/> 判为需要重建（安全方向）。
    /// </summary>
    private DateTimeOffset IndexRootLastWriteUtc()
    {
        try
        {
            var root = _indexOptions.IndexRootDirectory;
            return Directory.Exists(root)
                ? Directory.GetLastWriteTimeUtc(root)
                : DateTimeOffset.MinValue;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return DateTimeOffset.MinValue;
        }
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}
