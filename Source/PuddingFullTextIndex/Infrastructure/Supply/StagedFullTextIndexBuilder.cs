using System.Diagnostics;
using PuddingFullTextIndex.Contracts;

namespace PuddingFullTextIndex.Infrastructure.Supply;

/// <summary>
/// **安全供给 builder**（A2a）：把「直接写 live 索引」升级为
/// <b>先构建到 staging → 预算硬限校验 → 原子切换到 live</b>。
/// <para>
/// 一次供给的完整序列（任一步失败都**不动 live 一字节**）：
/// <list type="number">
/// <item><b>残留清理</b>（R5）：清掉 <c>.staging</c>/<c>.trash</c> 下最后写入早于
/// <see cref="SupplyCoordinatorOptions.StaleArtifactMaxAge"/> 的条目；清理失败只登记、不中断。</item>
/// <item><b>live 基线</b>：实测全部 live scope 索引字节（量不出 ⇒ fail-closed，不构建不切换）。</item>
/// <item><b>预检</b>（R2 第一道）：预测体积（job 载荷里的语料字节 × 既有系数）+ live 已用量 ≤ 预算；
/// 不合规 ⇒ 不启动构建，直接 <c>RejectedOverBudget</c>。</item>
/// <item><b>staging 构建</b>（R1）：用**指向 staging 根**的引擎实例构建，live 目录完全不被触碰。</item>
/// <item><b>实测硬限</b>（R2 第二道）：判定式 = <c>live 全部 scope 索引字节 + staging 实测字节 ≤ 预算</c>
/// （配置集合总预算口径，与预检/Plan 同一个函数）；超限 ⇒ 清 staging，未切 live。</item>
/// <item><b>原子切换</b>（R3）：先失效 live reader 缓存 → 旧 live 移入 <c>.trash</c> → staging 移入 live；
/// 第 3 步失败 ⇒ 把 <c>.trash</c> 副本移回 live 并报 <c>RolledBack</c>。</item>
/// <item><b>切换后收尾</b>：再失效一次 reader 缓存（保证后续查询看到新索引）、清理 staging 残留、
/// 尽力删除 <c>.trash</c> 副本（删除失败**不**让 job 变失败 —— live 已经就位）。</item>
/// </list>
/// </para>
/// <para>
/// 开关：<see cref="SupplyCoordinatorOptions.UseStaging"/>（默认 <c>true</c> = 安全路径）。
/// <c>false</c> 退回「直写 live」（A1 的旧行为），仅供对照/诊断 —— 那条路径**不做**预算硬限与原子切换。
/// </para>
/// <para>
/// ⚠️ 为什么用 scope 载荷而不是构造参数传预算/语料字节：见 <see cref="SupplyScope"/> 的说明
/// （<c>IFullTextIndexBuilder</c> 的签名被 CLI 组合根冻结，不可改 CLI）。
/// </para>
/// </summary>
public sealed class StagedFullTextIndexBuilder : IFullTextIndexBuilder, IFullTextIndexLiveUsage
{
    private readonly IFullTextIndexRootedEngine _liveEngine;
    private readonly Func<FullTextIndexOptions, IFullTextIndexRootedEngine> _stagingEngineFactory;
    private readonly FullTextIndexOptions _indexOptions;
    private readonly SupplyCoordinatorOptions _options;
    private readonly IIndexDirectorySwapper _swapper;

    /// <summary>已规范化的回归闸门阈值（A22a R4）：非法值已在构造时回落默认值并告警。</summary>
    private readonly double _minStagingToLiveDocRatio;

    /// <summary>
    /// 构造安全供给 builder。
    /// </summary>
    /// <param name="liveEngine">
    /// **live 根**上的引擎实例：直写模式的写入口，也是「语料根 → live 索引目录」映射与 reader 缓存失效的出处。
    /// 必须与查询侧**同一个实例**，否则失效打在别人的缓存上（A5 断言的就是这一点）。
    /// </param>
    /// <param name="stagingEngineFactory">
    /// 用给定选项（索引根被替换成 staging 根）造一个**隔离引擎实例**的工厂；该实例只在本次 job 内使用，
    /// 若实现 <see cref="IDisposable"/> 会被本类释放。
    /// </param>
    /// <param name="indexOptions">索引配置（只取 <see cref="FullTextIndexOptions.IndexRootDirectory"/> 作为布局根）。</param>
    /// <param name="options">供给策略（预算 / staging 开关 / 残留阈值）；null ⇒ 全部默认值。</param>
    public StagedFullTextIndexBuilder(
        IFullTextIndexRootedEngine liveEngine,
        Func<FullTextIndexOptions, IFullTextIndexRootedEngine> stagingEngineFactory,
        FullTextIndexOptions indexOptions,
        SupplyCoordinatorOptions? options = null)
        : this(liveEngine, stagingEngineFactory, indexOptions, options ?? new SupplyCoordinatorOptions(), DirectoryMoveSwapper.Instance)
    {
    }

    /// <summary>
    /// 测试用构造：额外注入「目录移动」原语，用于**可复现地**制造切换第 3 步失败（A4 回滚断言）
    /// 与断言切换步骤顺序。生产装配走公开构造函数（真实 <see cref="Directory.Move(string, string)"/>）。
    /// </summary>
    internal StagedFullTextIndexBuilder(
        IFullTextIndexRootedEngine liveEngine,
        Func<FullTextIndexOptions, IFullTextIndexRootedEngine> stagingEngineFactory,
        FullTextIndexOptions indexOptions,
        SupplyCoordinatorOptions options,
        IIndexDirectorySwapper swapper)
    {
        _liveEngine = liveEngine ?? throw new ArgumentNullException(nameof(liveEngine));
        _stagingEngineFactory = stagingEngineFactory ?? throw new ArgumentNullException(nameof(stagingEngineFactory));
        _indexOptions = indexOptions ?? throw new ArgumentNullException(nameof(indexOptions));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _swapper = swapper ?? throw new ArgumentNullException(nameof(swapper));

        if (string.IsNullOrWhiteSpace(indexOptions.IndexRootDirectory))
            throw new ArgumentException("IndexRootDirectory 不能为空。", nameof(indexOptions));

        if (_options.DefaultBudgetBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(options), "DefaultBudgetBytes 必须为正。");

        if (_options.StaleArtifactMaxAge <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(options), "StaleArtifactMaxAge 必须为正。");

        // A22a R4：阈值非法（NaN / ≤0 / >1）⇒ 回落默认值并告警，**绝不**按 0 处理（0 = 放行一切 = 闸门失效）。
        _minStagingToLiveDocRatio = NormalizeMinStagingToLiveDocRatio(_options.MinStagingToLiveDocRatio);
    }

    /// <summary>
    /// 实测全部 live scope 索引字节（<see cref="IFullTextIndexLiveUsage"/>）——
    /// 供 <c>PlanAsync</c> 用**同一口径**做报表，避免与构建侧硬限各说一套。
    /// </summary>
    public long MeasureLiveIndexBytes() =>
        SupplyIndexDirectoryLayout.MeasureLiveIndexBytes(_indexOptions.IndexRootDirectory);

    public async Task<SupplyBuildResult> BuildAsync(SupplyScope scope, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(scope);

        var stopwatch = Stopwatch.StartNew();

        if (!_options.UseStaging)
            return await BuildDirectAsync(scope, ct).ConfigureAwait(false);

        return await BuildStagedAsync(scope, stopwatch, ct).ConfigureAwait(false);
    }

    // ── 直写模式（对照/诊断；不做预算硬限与原子切换）────────────────────

    private async Task<SupplyBuildResult> BuildDirectAsync(SupplyScope scope, CancellationToken ct)
    {
        var result = await _liveEngine.BuildIndexAsync(scope.RootPath, filePatterns: null, ct).ConfigureAwait(false);

        return new SupplyBuildResult(
            result.Success,
            result.IndexedFileCount,
            result.TotalBytes,
            result.ElapsedMs,
            result.Error);
    }

    // ── staged 路径（默认）──────────────────────────────────────────────

    private async Task<SupplyBuildResult> BuildStagedAsync(SupplyScope scope, Stopwatch stopwatch, CancellationToken ct)
    {
        var indexRoot = _indexOptions.IndexRootDirectory;
        var budget = SupplyBudgetCalculator.ResolveEffectiveBudget(scope.BudgetBytes, _options.DefaultBudgetBytes);

        // ① 残留清理（R5）：先清过期 staging/trash；失败只登记，绝不中断供给。
        var cleanup = CleanupStaleArtifacts(indexRoot);

        // ② live 基线：量不出来就不构建、不切换（fail-closed，而不是默默按 0 判合规）。
        long liveBytesBefore;
        try
        {
            liveBytesBefore = SupplyIndexDirectoryLayout.MeasureLiveIndexBytes(indexRoot);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new SupplyBuildResult(
                false, 0, 0, stopwatch.ElapsedMilliseconds,
                $"live 索引用量无法实测（{ex.GetType().Name}: {ex.Message}），按 fail-closed 不构建、不切换。",
                new SupplySwapReport(
                    SupplySwapOutcome.None, budget, 0, 0, 0,
                    StagingDirectory: null, TrashDirectory: null,
                    cleanup.RemovedEntries, cleanup.Error,
                    // 文档数探针未取：字节基线本身就量不出，本次未进入构建阶段。
                    LiveDocsBefore: null, StagingDocs: null, RegressionVerdict: null));
        }

        // ②-bis live 文档数基线（A22a R1）：与字节基线同一时刻取。live 在 ⑦ 之前不会被任何代码触碰，
        //      因此这里的探针值就是「切换前」的事实，供回归闸门与报告使用。
        var liveProbe = ProbeDocumentsSafely(_liveEngine, scope.RootPath);
        var liveDocs = liveProbe.Exists ? liveProbe.Documents : null;

        // ③ 预检（R2 第一道）：预测体积 + live 已用量。口径与 Plan、与第 ⑤ 步完全相同。
        var predictedIndexBytes = SupplyIndexSizeEstimator.PredictIndexBytes(scope.CorpusBytes ?? 0);
        if (!SupplyBudgetCalculator.Fits(liveBytesBefore, predictedIndexBytes, budget))
        {
            return new SupplyBuildResult(
                false, 0, 0, stopwatch.ElapsedMilliseconds,
                $"OverBudget：预检拒绝，未启动构建（{SupplyBudgetCalculator.Describe("预测索引体积", liveBytesBefore, predictedIndexBytes, budget)}）。",
                new SupplySwapReport(
                    SupplySwapOutcome.RejectedOverBudget, budget, 0, liveBytesBefore, liveBytesBefore,
                    StagingDirectory: null, TrashDirectory: null,
                    cleanup.RemovedEntries, cleanup.Error,
                    LiveDocsBefore: liveDocs, StagingDocs: null, RegressionVerdict: null));
        }

        // ④ staging 根（同卷、按 job 唯一）：<IndexRoot>/.staging/<sha256(scopeKey)>-<jobId>
        var jobToken = scope.JobId ?? Guid.NewGuid().ToString("N");
        var stagingRoot = SupplyIndexDirectoryLayout.ResolveStagingRoot(indexRoot, scope.ScopeKey, jobToken);
        var stagingEngine = CreateStagingEngine(stagingRoot);

        try
        {
            // staging 必须从零开始：同名目录存在 ⇒ jobId 复用或上次崩溃残留，失败而不是猜（不覆盖别人的目录）。
            if (Directory.Exists(stagingRoot))
            {
                return new SupplyBuildResult(
                    false, 0, 0, stopwatch.ElapsedMilliseconds,
                    $"staging 目录已存在（{stagingRoot}），拒绝复用/覆盖，本次未构建、未切换。",
                    new SupplySwapReport(
                        SupplySwapOutcome.None, budget, 0, liveBytesBefore, liveBytesBefore,
                        stagingRoot, TrashDirectory: null, cleanup.RemovedEntries, cleanup.Error,
                        // staging 尚未构建 ⇒ staging 文档数未取；live 基线已有。
                        LiveDocsBefore: liveDocs, StagingDocs: null, RegressionVerdict: null));
            }

            // ⑤ staging 构建（R1）：写的是 staging 根下的索引目录，live 完全不被触碰。
            var stagingBuild = await stagingEngine.BuildIndexAsync(scope.RootPath, filePatterns: null, ct).ConfigureAwait(false);
            var stagingIndexDir = stagingEngine.ResolveIndexDirectory(scope.RootPath);
            var stagingBytes = SupplyIndexDirectoryLayout.MeasureDirectoryBytes(stagingIndexDir);

            if (!stagingBuild.Success)
            {
                SupplyIndexDirectoryLayout.TryDelete(stagingRoot, out var stagingDeleteError);

                return new SupplyBuildResult(
                    false, stagingBuild.IndexedFileCount, stagingBuild.TotalBytes, stagingBuild.ElapsedMs,
                    $"staging 构建失败（live 未改动）：{stagingBuild.Error ?? "引擎返回 Success=false 但未给出原因"}{DeleteNote(stagingDeleteError)}",
                    new SupplySwapReport(
                        SupplySwapOutcome.None, budget, stagingBytes, liveBytesBefore, liveBytesBefore,
                        stagingRoot, TrashDirectory: null,
                        cleanup.RemovedEntries,
                        ArtifactCleanupResult.MergeError(cleanup.Error, stagingDeleteError),
                        // 构建失败 ⇒ 未走到探针（staging 已清），live 基线如实带上。
                        LiveDocsBefore: liveDocs, StagingDocs: null, RegressionVerdict: null));
            }

            // ⑥ 实测硬限（R2 第二道）：与预检/Plan 同一个判定函数。
            if (!SupplyBudgetCalculator.Fits(liveBytesBefore, stagingBytes, budget))
            {
                SupplyIndexDirectoryLayout.TryDelete(stagingRoot, out var stagingDeleteError);

                return new SupplyBuildResult(
                    false, stagingBuild.IndexedFileCount, stagingBuild.TotalBytes, stagingBuild.ElapsedMs,
                    $"OverBudget：{SupplyBudgetCalculator.Describe("staging 实测", liveBytesBefore, stagingBytes, budget)}；"
                    + $"未切换到 live（live 一字节未动，staging 已清理{DeleteNote(stagingDeleteError)}）。",
                    new SupplySwapReport(
                        SupplySwapOutcome.RejectedOverBudget, budget, stagingBytes, liveBytesBefore, liveBytesBefore,
                        stagingRoot, TrashDirectory: null,
                        cleanup.RemovedEntries,
                        ArtifactCleanupResult.MergeError(cleanup.Error, stagingDeleteError),
                        // 体积闸门先于文档数闸门 ⇒ staging 探针未取（体积已否决，就不多做一次读）；live 基线带上。
                        LiveDocsBefore: liveDocs, StagingDocs: null, RegressionVerdict: null));
            }

            // ⑥-bis 回归闸门（A22a R2）：体积合规**不等于**内容可信。2026-09-25 生产事故里，
            //        一次只含 0/99（另一轮 99/4510）文档的 staging 通过了上面两道体积闸门并被原子切换，
            //        静默替换掉 ~98 MB 的 live 满索引 ⇒ 这里再加一道**只看文档数**的 fail-closed 闸门：
            //        可疑 ⇒ 清 staging、不动 live 一字节、不建 .trash、不失效 reader 缓存。
            var stagingProbe = ProbeDocumentsSafely(stagingEngine, scope.RootPath);
            var gateDecision = SwapRegressionGate.Evaluate(stagingProbe, liveProbe, _minStagingToLiveDocRatio);

            if (gateDecision.Rejected)
            {
                SupplyIndexDirectoryLayout.TryDelete(stagingRoot, out var stagingGateDeleteError);

                return new SupplyBuildResult(
                    false, stagingBuild.IndexedFileCount, stagingBuild.TotalBytes, stagingBuild.ElapsedMs,
                    $"SuspiciousRegression：{gateDecision.Verdict}；"
                    + $"{SwapRegressionGate.DescribeFacts(gateDecision, _minStagingToLiveDocRatio)}；"
                    + $"未切换到 live（live 一字节未动，staging 已清理{DeleteNote(stagingGateDeleteError)}）。",
                    new SupplySwapReport(
                        SupplySwapOutcome.RejectedSuspiciousRegression, budget, stagingBytes,
                        liveBytesBefore, liveBytesBefore,
                        stagingRoot, TrashDirectory: null,
                        cleanup.RemovedEntries,
                        ArtifactCleanupResult.MergeError(cleanup.Error, stagingGateDeleteError),
                        gateDecision.LiveDocs, gateDecision.StagingDocs,
                        gateDecision.Ratio, gateDecision.Verdict));
            }

            // 闸门放行 ⇒ 已探到的文档数事实要随报告带下去（切换后 staging 目录已搬走，探针不可再取）。
            var stagingDocs = gateDecision.StagingDocs;
            var stagingRatio = gateDecision.Ratio;

            // ⑦ 原子切换（R3）
            var liveIndexDir = _liveEngine.ResolveIndexDirectory(scope.RootPath);
            var trashDir = SupplyIndexDirectoryLayout.ResolveTrashDirectory(indexRoot, scope.ScopeKey, DateTimeOffset.UtcNow);
            string? swapError = null;
            var oldLiveMoved = false;

            try
            {
                // ⑦-1 先失效 live 的 reader/searcher 缓存（R4）：陈旧 reader 会继续看到过期文档；
                //      且在 Windows 上未释放的 reader 句柄会让下面的 Move 直接被拒。
                _liveEngine.InvalidateScope(scope.RootPath);

                // ⑦-2 旧 live → .trash（同卷重命名）。先确保 .trash 根存在：
                //      Directory.Move 不会为「目标的父目录」代建目录，首次切换时它可能还不存在。
                if (Directory.Exists(liveIndexDir))
                {
                    Directory.CreateDirectory(SupplyIndexDirectoryLayout.TrashRoot(indexRoot));
                    _swapper.Move(liveIndexDir, trashDir);
                    oldLiveMoved = true;
                }

                // ⑦-3 staging → live
                _swapper.Move(stagingIndexDir, liveIndexDir);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                swapError = $"{ex.GetType().Name}: {ex.Message}";
            }

            if (swapError is not null)
            {
                var rollback = RollbackOldLive(liveIndexDir, trashDir, oldLiveMoved);
                SupplyIndexDirectoryLayout.TryDelete(stagingRoot, out var stagingDeleteError);

                // 回滚后同样失效一次：两次 Move 之间可能有并发读者缓存了错误内容。
                _liveEngine.InvalidateScope(scope.RootPath);

                var (liveBytesAfterRollback, measureError) = SafeMeasureLiveBytes(indexRoot, liveBytesBefore);

                return new SupplyBuildResult(
                    false, stagingBuild.IndexedFileCount, stagingBuild.TotalBytes, stagingBuild.ElapsedMs,
                    $"切换失败：{swapError}；{rollback.Detail}{DeleteNote(stagingDeleteError)}",
                    new SupplySwapReport(
                        SupplySwapOutcome.RolledBack, budget, stagingBytes, liveBytesBefore, liveBytesAfterRollback,
                        stagingRoot,
                        Directory.Exists(trashDir) ? trashDir : null,
                        cleanup.RemovedEntries,
                        ArtifactCleanupResult.MergeError(
                            ArtifactCleanupResult.MergeError(cleanup.Error, stagingDeleteError),
                            ArtifactCleanupResult.MergeError(rollback.Error, measureError)),
                        LiveDocsBefore: liveDocs, StagingDocs: stagingDocs,
                        RegressionRatio: stagingRatio, RegressionVerdict: null));
            }

            // ⑧ 切换成功后再次失效（R3 末句）：保证后续查询立刻看到新索引。
            _liveEngine.InvalidateScope(scope.RootPath);

            // ⑨ 残留清理：搬空的 staging 根 + 旧 live 的 .trash 副本。
            //    删除失败**不**让 job 变失败（live 已经就位），只如实登记（R3.4 / R5）。
            SupplyIndexDirectoryLayout.TryDelete(stagingRoot, out var finalStagingError);
            var trashRemoved = SupplyIndexDirectoryLayout.TryDelete(trashDir, out var trashDeleteError);

            var finalCleanupError = ArtifactCleanupResult.MergeError(
                ArtifactCleanupResult.MergeError(cleanup.Error, DeleteFailureNote("staging", finalStagingError)),
                DeleteFailureNote("trash", trashDeleteError));

            var (liveBytesAfterSwap, afterMeasureError) = SafeMeasureLiveBytes(indexRoot, liveBytesBefore);

            return new SupplyBuildResult(
                true, stagingBuild.IndexedFileCount, stagingBuild.TotalBytes, stagingBuild.ElapsedMs, null,
                new SupplySwapReport(
                    SupplySwapOutcome.Swapped, budget, stagingBytes, liveBytesBefore, liveBytesAfterSwap,
                    stagingRoot,
                    trashRemoved ? null : trashDir,
                    cleanup.RemovedEntries,
                    ArtifactCleanupResult.MergeError(finalCleanupError, afterMeasureError),
                    LiveDocsBefore: liveDocs, StagingDocs: stagingDocs,
                    RegressionRatio: stagingRatio, RegressionVerdict: null));
        }
        finally
        {
            if (stagingEngine is IDisposable disposable)
                disposable.Dispose();
        }
    }

    /// <summary>
    /// A22a R4：回归闸门阈值规范化。非法值（<c>NaN</c> / <c>Infinity</c> / <c>≤ 0</c> / <c>&gt; 1</c>）
    /// ⇒ 回落 <see cref="SupplyCoordinatorOptions.DefaultMinStagingToLiveDocRatio"/> 并**告警**；
    /// 绝不按 0 处理 —— 0 会让 <c>stagingDocs &lt; liveDocs × 0</c> 恒假，等于闸门静默失效。
    /// </summary>
    private static double NormalizeMinStagingToLiveDocRatio(double ratio)
    {
        if (double.IsNaN(ratio) || double.IsInfinity(ratio) || ratio <= 0d || ratio > 1d)
        {
            Trace.TraceWarning(
                $"MinStagingToLiveDocRatio={ratio.ToString(System.Globalization.CultureInfo.InvariantCulture)} 非法（要求 (0, 1]）⇒ 回落默认值 "
                + $"{SupplyCoordinatorOptions.DefaultMinStagingToLiveDocRatio}；不按 0 处理（0 会放行一切）。");
            return SupplyCoordinatorOptions.DefaultMinStagingToLiveDocRatio;
        }

        return ratio;
    }

    /// <summary>
    /// 文档数探针的安全封装（A22a R1）：探针自身抛异常时按「存在但读不出」（<c>Exists=true, Documents=null</c>）
    /// 处理 —— 即 fail-closed：对 staging 触发 G1、对 live 触发 G4，既不伪报 0 也不放行。
    /// </summary>
    private static IndexDocumentProbe ProbeDocumentsSafely(IFullTextIndexRootedEngine engine, string corpusRootPath)
    {
        try
        {
            return engine.ProbeDocuments(corpusRootPath);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Trace.TraceWarning(
                $"索引文档数探针异常（{ex.GetType().Name}: {ex.Message}）⇒ 按「存在但读不出」处理（fail-closed）。");
            return new IndexDocumentProbe(Exists: true, Documents: null);
        }
    }

    private IFullTextIndexRootedEngine CreateStagingEngine(string stagingRoot)
    {
        var stagingOptions = _indexOptions with { IndexRootDirectory = stagingRoot };
        return _stagingEngineFactory(stagingOptions)
            ?? throw new InvalidOperationException("staging 引擎工厂返回 null（装配错误）。");
    }

    /// <summary>
    /// 把 <c>.trash</c> 副本移回 live（R3 回滚）。**不覆盖**已存在的 live 目录：
    /// 若切换后 live 已存在，说明状态不明，宁可原样保留并如实上报，也不做破坏性猜测。
    /// </summary>
    private (bool Restored, string Detail, string? Error) RollbackOldLive(string liveIndexDir, string trashDir, bool oldLiveMoved)
    {
        if (!oldLiveMoved)
            return (false, "旧 live 原本不存在，无需回滚（live 保持不存在）", null);

        try
        {
            if (Directory.Exists(liveIndexDir))
                return (false, "live 目录在切换失败后已存在，未覆盖（需人工核对）", "rollback-skipped-live-exists");

            _swapper.Move(trashDir, liveIndexDir);
            return (true, "旧 live 已从 .trash 移回（回滚成功）", null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return (false, $"回滚失败，.trash 副本仍保留在 {trashDir}", $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// 残留清理（R5）：清 <c>.staging</c> / <c>.trash</c> 下最后写入时间早于
    /// <see cref="SupplyCoordinatorOptions.StaleArtifactMaxAge"/> 的条目。
    /// <para>
    /// ⚠️ 只比条目**自身**的时间戳（不递归求子树最大 mtime）：目录 mtime 会随「直接子项增删」更新，
    /// 因此阈值必须显著大于单次构建时长才不会误删进行中的 job —— 默认 24h vs 实测 Source scope ≈ 69s，
    /// 量级差三个数量级。若将来出现小时级以上的构建，必须同步提高该阈值。
    /// </para>
    /// </summary>
    private ArtifactCleanupResult CleanupStaleArtifacts(string indexRoot)
    {
        var removed = new List<string>();
        string? error = null;
        var thresholdUtc = DateTime.UtcNow - _options.StaleArtifactMaxAge;

        foreach (var root in new[]
                 {
                     SupplyIndexDirectoryLayout.StagingRoot(indexRoot),
                     SupplyIndexDirectoryLayout.TrashRoot(indexRoot),
                 })
        {
            if (!Directory.Exists(root))
                continue;

            string[] entries;
            try
            {
                entries = Directory.GetFileSystemEntries(root);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                error = ArtifactCleanupResult.MergeError(error, $"{Path.GetFileName(root)} 枚举失败（{ex.GetType().Name}）");
                continue;
            }

            foreach (var entry in entries)
            {
                DateTime lastWriteUtc;
                try
                {
                    lastWriteUtc = Directory.Exists(entry)
                        ? Directory.GetLastWriteTimeUtc(entry)
                        : File.GetLastWriteTimeUtc(entry);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    error = ArtifactCleanupResult.MergeError(error, $"{Path.GetFileName(entry)} 时间戳读取失败（{ex.GetType().Name}）");
                    continue;
                }

                // 比阈值新的条目一律保留：可能正被并发进程使用（R5 明确要求）。
                if (lastWriteUtc > thresholdUtc)
                    continue;

                if (!SupplyIndexDirectoryLayout.TryDelete(entry, out var deleteError))
                {
                    error = ArtifactCleanupResult.MergeError(error, $"{Path.GetFileName(entry)} 删除失败（{deleteError}）");
                    continue;
                }

                removed.Add($"{Path.GetFileName(root)}/{Path.GetFileName(entry)}");
            }
        }

        return new ArtifactCleanupResult(removed, error);
    }

    private static (long Bytes, string? Error) SafeMeasureLiveBytes(string indexRoot, long fallback)
    {
        try
        {
            return (SupplyIndexDirectoryLayout.MeasureLiveIndexBytes(indexRoot), null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (fallback, $"live 用量复测失败（{ex.GetType().Name}）");
        }
    }

    private static string DeleteNote(string? deleteError) =>
        deleteError is null ? string.Empty : $"；⚠️ 残留清理未完成（{deleteError}）";

    /// <summary>删除失败的带标签说明（进 <see cref="SupplySwapReport.CleanupError"/>，写清是哪个残留没删掉）。</summary>
    private static string? DeleteFailureNote(string label, string? deleteError) =>
        deleteError is null ? null : $"{label} 残留删除失败（{deleteError}）";
}
