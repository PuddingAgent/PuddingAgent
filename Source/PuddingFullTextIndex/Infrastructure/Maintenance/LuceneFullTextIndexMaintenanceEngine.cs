using System.Diagnostics;
using Lucene.Net.Index;
using Lucene.Net.Store;
using Lucene.Net.Util;
using PuddingFullTextIndex.Contracts;
using PuddingFullTextIndex.Infrastructure.Search;
using PuddingFullTextIndex.Infrastructure.Supply;
using Directory = System.IO.Directory;

namespace PuddingFullTextIndex.Infrastructure.Maintenance;

/// <summary>
/// 真实 Lucene **局部写内核**（方案 §3.6 执行层 / §4.1「直写 live」/ §4.2 预检口径 / §4.5 可见性）。
/// <para>
/// <b>只做两件事</b>：把一个统一变更集按「先提取、后 delete/add、单批 commit」写进 live 索引；
/// 只读枚举索引里已有的路径清册（补偿扫描算 Delete 候选的来源）。
/// 供宿主 / 维护器通过 <see cref="IFullTextIndexMaintenanceEngine"/> 调用；本片**不接 Host**、
/// 不起 <c>FileSystemWatcher</c> / <c>Timer</c> / 长驻 <c>Task</c>、不获取跨进程租约、不做写入期配额 wrapper、
/// 不失效查询侧 reader（分别属 S3b / S3c / S3d）。
/// </para>
/// <para>
/// <b>§3.6 单批次顺序（本类逐条对齐）</b>：
/// <list type="number">
/// <item><description>① 每 scope 进程内 gate —— 复用查询侧<b>同一实例</b>的既有 gate（同一字典、同一键，
/// 见 <c>LuceneSearchEngine.GetScopeGate</c>），因此局部写与全量构建在同一进程内互斥。</description></item>
/// <item><description>② 跨进程 <c>FileSupplyLease</c> —— <b>本片不做（S3c）</b>。</description></item>
/// <item><description>③ 对每个 Upsert 做最终 stat 与过滤 —— 复用扫描期同一判定体
/// <see cref="FileCandidateCollector"/>（扩展名白名单 / 噪声名单 / 空文件 / 超限，唯一真源）。</description></item>
/// <item><description>④ <b>提取失败的文件不进入 Lucene delete 集合</b> —— 提取全部在内存中完成后才开始写；
/// 任一文件提取失败 ⇒ 它的旧文档<b>保留</b>，只登记待重试（绝不先删后失败）。</description></item>
/// <item><description>⑤ 打开<b>一个</b> <c>IndexWriter(OpenMode.CREATE_OR_APPEND)</c>；不可得锁 ⇒ 返回
/// <see cref="FullTextMutationState.Busy"/>（不抛、不提交）。</description></item>
/// <item><description>⑥ 成功的 Upsert：先 <c>DeleteDocuments(path)</c> 再添加当前文档
/// （幂等重写；文档构造复用引擎的 <c>AddDocument</c>，<b>不复制</b>结构）。</description></item>
/// <item><description>⑦ 确认删除：<c>DeleteDocuments(path)</c>。</description></item>
/// <item><description>⑧ 预算硬限检查 —— 本片做<b>写前预检</b>（§4.2 原文即为「写前」）：与 A2a 同一个
/// <see cref="SupplyBudgetCalculator.Fits"/> 口径；不在写入期拒绝（那是 S3b 的 quota wrapper）。</description></item>
/// <item><description>⑨ <c>Commit()</c> —— 单批一次；未 commit 的文档对查询不可见（本类只在提交成功后才返回
/// <see cref="FullTextMutationState.Applied"/> / <see cref="FullTextMutationState.PartiallyApplied"/>）。</description></item>
/// <item><description>⑩ <c>InvalidateScope(scope)</c> —— <b>本片不做（S3c）</b>：本类不持有查询侧 reader 缓存。</description></item>
/// <item><description>⑪ 释放 writer（每次批次创建 / 提交 / 释放，不长持 writer lock）；跨进程租约的释放属 S3c。</description></item>
/// <item><description>⑫ 推进 checkpoint —— <b>本片不写盘</b>：只<b>产出</b>
/// <see cref="FullTextMutationResult.CheckpointAdvanced"/>（由 <see cref="CheckpointAdvancePolicy"/> 判定，
/// 绝不回读输入）。</description></item>
/// </list>
/// 末句：取消在③～⑧任一点发生 ⇒ <b>rollback、不提交</b>，并返回
/// <see cref="FullTextMutationState.Cancelled"/>（契约允许外抛或返回显式取消态，本类选择后者以便调用方区分）。
/// </para>
/// <para>
/// <b>为什么不自己 stat 白名单 / 不自己拼文档</b>：扩展名与噪声名单的唯一真源在
/// <see cref="FullTextIndexOptions"/>（其自身派生自 <c>PathNoiseRules</c>），文档结构与提取路径的唯一真源在
/// <see cref="LuceneSearchEngine"/>；复制一份会让两种文档结构静默漂移（本片据此只放宽了那两个成员的可见性）。
/// </para>
/// </summary>
public sealed class LuceneFullTextIndexMaintenanceEngine : IFullTextIndexMaintenanceEngine
{
    /// <summary>
    /// writer 的 Lucene 版本常量。<b>必须与 <c>LuceneSearchEngine.MatchVersion</c> 同值</b>：两者写的是同一个索引目录，
    /// 版本漂移会让段格式与读取侧不一致。
    /// <para>
    /// ⚠️ 这是本类唯一一处对引擎内部常量的复述：引擎把它作为 <c>private static</c> 字段持有、未暴露访问器，
    /// 而本片允许新增的 internal 访问器只有 2 个（已用于「分析链」与「scope gate」）。
    /// 由测试持续守住：所有写路径都用同一个 <see cref="LuceneSearchEngine"/> 实例的真实搜索 / 清册读回校验，
    /// 版本一旦漂移索引即不可读 ⇒ 测试立刻红。
    /// </para>
    /// </summary>
    private static readonly LuceneVersion IndexWriterMatchVersion = LuceneVersion.LUCENE_48;

    /// <summary>writer 内存缓冲（与既有构建路径的取值一致：本片不做调优，也不引入 NRT）。</summary>
    private const double WriterRamBufferMegabytes = 48;

    private readonly LuceneSearchEngine _searchEngine;
    private readonly FullTextIndexOptions _options;

    /// <summary>
    /// 构造局部写内核。
    /// </summary>
    /// <param name="searchEngine">
    /// 查询侧同一实例（提供 scope → 索引目录的命名哈希真源、分析链、内容提取、文档构造与进程内 gate）。
    /// </param>
    /// <param name="options">
    /// 生效的索引选项（提供扩展名白名单 / 噪声名单 / 体积上限）。必须与构造 <paramref name="searchEngine"/> 时使用的同一实例
    /// （组合根负责），否则「能否索引」的判据会与实际建索引时不一致。
    /// </param>
    public LuceneFullTextIndexMaintenanceEngine(LuceneSearchEngine searchEngine, FullTextIndexOptions options)
    {
        _searchEngine = searchEngine ?? throw new ArgumentNullException(nameof(searchEngine));
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    // ── 局部写：ApplyChangesAsync ────────────────────────────────────────

    /// <inheritdoc />
    public async Task<FullTextMutationResult> ApplyChangesAsync(
        FullTextChangeSet changeSet,
        FullTextMutationBudget budget,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(changeSet);
        ArgumentNullException.ThrowIfNull(budget);

        var indexPath = _searchEngine.GetIndexDirectoryPath(changeSet.ScopeRoot);

        // ── ① 每 scope 进程内 gate（与全量构建共用同一把）──
        var gate = _searchEngine.GetScopeGate(indexPath);
        try
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return NoWriteResult(
                changeSet,
                FullTextMutationState.Cancelled,
                indexPath,
                "取消：等待 scope gate 期间被取消（未写入、未提交）。");
        }

        try
        {
            return await ApplyUnderGateAsync(changeSet, budget, indexPath, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // ③（提取）或写入前的任意取消检查外抛 —— 统一转成显式的 Cancelled（不伪装成功、不吞掉语义）。
            return NoWriteResult(
                changeSet,
                FullTextMutationState.Cancelled,
                indexPath,
                "取消：本批在提交前被取消（未提交）。");
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<FullTextMutationResult> ApplyUnderGateAsync(
        FullTextChangeSet changeSet,
        FullTextMutationBudget budget,
        string indexPath,
        CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return NoWriteResult(
                changeSet,
                FullTextMutationState.Cancelled,
                indexPath,
                "取消：进入批次前令牌已取消（未写入、未提交）。");
        }

        // ── 守卫 A：单批路径数上限（超限 ⇒ 不写入；防单批过大长持 writer lock）──
        if (changeSet.Changes.Count > budget.MaxPaths)
        {
            return NoWriteResult(
                changeSet,
                FullTextMutationState.Rejected,
                indexPath,
                $"拒绝：本批 {changeSet.Changes.Count} 个路径超过单批上限 {budget.MaxPaths}（未写入、未提交）。");
        }

        // ── 守卫 B：live 索引必须已存在（§3.5：绝不通过局部写入偷偷创建一份「初始全库索引」）──
        if (!Directory.Exists(indexPath))
        {
            return NoWriteResult(
                changeSet,
                FullTextMutationState.Rejected,
                indexPath,
                $"拒绝：live 索引目录不存在（{indexPath}）⇒ 需手动重建，本地写入不创建初始索引（未写入、未提交）。");
        }

        var indexBytesBefore = MeasureIndexBytes(indexPath);

        // ── ③ 最终 stat 与过滤（提取之前；失败者一律保留旧文档）──
        var verifiedUpserts = new List<VerifiedUpsert>();
        var confirmedDeletes = new List<string>();
        var retainedOldPaths = new List<string>();
        var failureNotes = new List<string>();

        foreach (var change in changeSet.Changes)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return NoWriteResult(
                    changeSet,
                    FullTextMutationState.Cancelled,
                    indexPath,
                    "取消：最终 stat 阶段被取消（未写入、未提交）。");
            }

            if (change.Kind == FullTextChangeKind.Delete)
            {
                confirmedDeletes.Add(change.FullPath);
                continue;
            }

            if (FileCandidateCollector.TryCollectCandidate(
                    change.FullPath, changeSet.ScopeRoot, _options, patterns: null, out var entry, out var skipReason))
            {
                verifiedUpserts.Add(new VerifiedUpsert(change.FullPath, entry.Size));
            }
            else
            {
                // 读不到 / 已消失 / 命中策略（扩展名、噪声、空文件、超限）⇒ 保留旧文档 + 登记待重试/待上游产出 Delete。
                // ⚠️ 绝不在这里产出 Delete 动作：那会把「读不到」当成「不存在」，误删仍然有效的旧文档。
                retainedOldPaths.Add(change.FullPath);
                failureNotes.Add($"{change.FullPath} :: 最终 stat/过滤未通过（{skipReason ?? "unknown"}）");
            }
        }

        // ── ⑧ 预算硬限的**写前预检**（§4.2：与 A2a 同一个 SupplyBudgetCalculator 口径）──
        // 增长估计取「本批全部 Upsert 的最终 stat 字节数」：它是实际写入量的保守估计（提取失败的文件也算在内 ⇒ 偏大不偏小）。
        // ⚠️ 写入期按实际输出字节拒绝属 S3b 的 quota wrapper；本片只做这一道预检。
        var predictedIncomingIndexBytes = 0L;
        foreach (var verified in verifiedUpserts)
            predictedIncomingIndexBytes += verified.ObservedBytes;

        var budgetFits = SupplyBudgetCalculator.Fits(budget.LiveIndexBytes, predictedIncomingIndexBytes, budget.MaxIndexBytes);

        if (!budgetFits)
        {
            return BuildResult(
                changeSet,
                FullTextMutationState.Rejected,
                0,
                0,
                retainedOldPaths,
                failureNotes,
                indexBytesBefore,
                MeasureIndexBytes(indexPath),
                null,
                $"拒绝：预算硬限不通过 —— {SupplyBudgetCalculator.Describe("本批待写文件", budget.LiveIndexBytes, predictedIncomingIndexBytes, budget.MaxIndexBytes)}"
                + "；rollback 保留上一个 commit（未写入、未提交）。");
        }

        // ── ④ 内容提取：全部在内存中完成后才开始任何 delete（§4.2「避免提取中途删旧文档」）──
        var preparedUpserts = new List<PreparedUpsert>();
        var deleteThenAddPaths = new List<string>();

        foreach (var verified in verifiedUpserts)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return NoWriteResult(
                    changeSet,
                    FullTextMutationState.Cancelled,
                    indexPath,
                    "取消：内容提取阶段被取消（未写入、未提交）。");
            }

            string? content;
            try
            {
                content = await _searchEngine
                    .ExtractContentAsync(verified.FullPath, Path.GetExtension(verified.FullPath), cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw; // 取消是控制流：交回 ApplyChangesAsync 统一转成 Cancelled，绝不当作「坏文件」吞掉
            }
            catch (Exception ex)
            {
                // 单文件隔离（§1.3 / §5.1）：提取失败只影响它自己 ⇒ 保留旧文档、进入待重试集合。
                content = null;
                failureNotes.Add($"{verified.FullPath} :: 内容提取失败（{ex.GetType().Name}: {ex.Message}）");
            }

            // ★ §3.6 第 4 步：**提取失败的文件绝不进入 Lucene delete 集合**（否则旧文档被删而新文档没写上 ⇒ 该文件从索引里消失）。
            var enterDeleteSet = content is not null;

            if (enterDeleteSet)
                deleteThenAddPaths.Add(verified.FullPath);

            if (content is not null)
                preparedUpserts.Add(new PreparedUpsert(verified.FullPath, content));
            else
                retainedOldPaths.Add(verified.FullPath);
        }

        // 无事可写（无成功 Upsert 且无确认删除）⇒ 不打开 writer、不 commit（无谓的 commit 会平白改变索引字节）。
        if (deleteThenAddPaths.Count == 0 && confirmedDeletes.Count == 0)
        {
            return BuildResult(
                changeSet,
                retainedOldPaths.Count > 0
                    ? FullTextMutationState.PartiallyApplied
                    : FullTextMutationState.Applied,
                0,
                0,
                retainedOldPaths,
                failureNotes,
                indexBytesBefore,
                indexBytesBefore,
                null,
                DescribeRetained(retainedOldPaths, failureNotes));
        }

        // ── ⑤⑥⑦⑨⑪ 单批 writer 会话 ──
        var session = RunWriterSession(indexPath, deleteThenAddPaths, confirmedDeletes, preparedUpserts, cancellationToken);
        var indexBytesAfter = MeasureIndexBytes(indexPath);

        if (session.ShortCircuitState is { } shortCircuitState)
        {
            return BuildResult(
                changeSet,
                shortCircuitState,
                0,
                0,
                retainedOldPaths,
                failureNotes,
                indexBytesBefore,
                indexBytesAfter,
                null,
                session.Message);
        }

        return BuildResult(
            changeSet,
            retainedOldPaths.Count > 0
                ? FullTextMutationState.PartiallyApplied
                : FullTextMutationState.Applied,
            preparedUpserts.Count,
            confirmedDeletes.Count,
            retainedOldPaths,
            failureNotes,
            indexBytesBefore,
            indexBytesAfter,
            session.CommitMilliseconds,
            DescribeRetained(retainedOldPaths, failureNotes));
    }

    /// <summary>
    /// 一个批次的单次 writer 会话：打开 → delete-then-add → commit → 释放（**不**长持 writer lock）。
    /// <para>
    /// 失败语义：取不到锁 ⇒ <see cref="FullTextMutationState.Busy"/>；取消 ⇒
    /// <see cref="FullTextMutationState.Cancelled"/>；其它异常 ⇒ <see cref="FullTextMutationState.Failed"/>；
    /// 三者一律经 <c>finally</c> 走 <c>Rollback()</c>（未提交的变更被丢弃，索引回到上一个 commit）。
    /// </para>
    /// </summary>
    private WriterSessionOutcome RunWriterSession(
        string indexPath,
        IReadOnlyList<string> deleteThenAddPaths,
        IReadOnlyList<string> confirmedDeletePaths,
        IReadOnlyList<PreparedUpsert> preparedUpserts,
        CancellationToken cancellationToken)
    {
        IndexWriter? writer = null;
        var committed = false;

        try
        {
            try
            {
                // CREATE_OR_APPEND：局部写永远追加在既有索引之后（目录存在性已由守卫 B 保证）。
                writer = new IndexWriter(
                    FSDirectory.Open(indexPath),
                    new IndexWriterConfig(IndexWriterMatchVersion, _searchEngine.Analyzer)
                    {
                        OpenMode = OpenMode.CREATE_OR_APPEND,
                        RAMBufferSizeMB = WriterRamBufferMegabytes,
                    });
            }
            catch (LockObtainFailedException ex)
            {
                // 互斥未取得（另一 writer 持同一索引目录）⇒ 不抛异常、不提交（按 Busy/Retry 处理）。
                return new WriterSessionOutcome(
                    false,
                    null,
                    FullTextMutationState.Busy,
                    $"互斥失败：writer lock 被占用（{ex.Message}）⇒ 本批未写入、未提交。");
            }

            // DeleteDocuments 保留旧接口语义：delete 不存在路径的文档是 no-op，不算失败。
            foreach (var path in deleteThenAddPaths)
                writer.DeleteDocuments(new Term("path", path));

            foreach (var path in confirmedDeletePaths)
                writer.DeleteDocuments(new Term("path", path));

            // 文档构造复用引擎的同一实现（不复制结构 ⇒ 两种写入路径不会静默漂移）。
            foreach (var prepared in preparedUpserts)
                LuceneSearchEngine.AddDocument(writer, prepared.FullPath, prepared.Content);

            if (cancellationToken.IsCancellationRequested)
            {
                return new WriterSessionOutcome(
                    false,
                    null,
                    FullTextMutationState.Cancelled,
                    "取消：提交前被取消 ⇒ 已 rollback、未提交。");
            }

            var stopwatch = Stopwatch.StartNew();
            writer.Commit();
            var commitMilliseconds = stopwatch.Elapsed.TotalMilliseconds;
            committed = true;

            return new WriterSessionOutcome(true, commitMilliseconds, null, null);
        }
        catch (OperationCanceledException)
        {
            return new WriterSessionOutcome(
                false,
                null,
                FullTextMutationState.Cancelled,
                "取消：提交前被取消 ⇒ 已 rollback、未提交。");
        }
        catch (Exception ex)
        {
            return new WriterSessionOutcome(
                false,
                null,
                FullTextMutationState.Failed,
                $"致命失败：本批已 rollback、未提交（{ex.GetType().Name}: {ex.Message}）。");
        }
        finally
        {
            if (writer is not null)
            {
                if (committed)
                {
                    // 提交已落盘（Commit 返回即持久化）；释放阶段的异常不改变终态。
                    try { writer.Dispose(); } catch { /* 释放失败不改变已提交事实 */ }
                }
                else
                {
                    // Rollback 自身即「关闭 + 删除本批新建的段文件」⇒ 索引回到上一个 commit；此处不再重复 Dispose。
                    writer.Rollback();
                }
            }
        }
    }

    // ── 只读：EnumerateIndexedPathsAsync ────────────────────────────────

    /// <inheritdoc />
    public IAsyncEnumerable<IndexedPathEntry> EnumerateIndexedPathsAsync(
        string scopeRoot,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scopeRoot);

        return EnumerateCore();

        async IAsyncEnumerable<IndexedPathEntry> EnumerateCore()
        {
            foreach (var entry in ReadInventory(scopeRoot, cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();
                await Task.Yield();
                yield return entry;
            }
        }
    }

    /// <summary>
    /// 读取索引里已有路径的清册（**只读**：不创建目录、不写索引、不动 reader 缓存、不占用 gate）。
    /// <para>
    /// 计数只数<b>未被删除</b>的文档（<c>AtomicReader.LiveDocs</c>）：delete-then-add 之后旧文档仍标记在段里，
    /// 若用词表 docFreq 计数会把「已删除但未合并」的文档算进去 ⇒ 清册数字与实际可见文档不符。
    /// </para>
    /// <para>
    /// 失败语义：索引目录不存在 / 不是索引 ⇒ 空清册（「没有索引」是真实陈述，且本方法绝不创建索引目录）；
    /// 目录存在但<b>读不出</b>（损坏 / 权限）⇒ 异常外抛（fail-closed，调用方必须区分「空」与「读不出」）。
    /// </para>
    /// </summary>
    private List<IndexedPathEntry> ReadInventory(string scopeRoot, CancellationToken cancellationToken)
    {
        var indexPath = _searchEngine.GetIndexDirectoryPath(scopeRoot);
        var entries = new List<IndexedPathEntry>();

        if (!Directory.Exists(indexPath))
            return entries;

        using var directory = FSDirectory.Open(indexPath);
        if (!DirectoryReader.IndexExists(directory))
            return entries;

        using var reader = DirectoryReader.Open(directory);
        var pathFieldOnly = new HashSet<string>(StringComparer.Ordinal) { "path" };
        var order = new List<string>();
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var leaf in reader.Leaves)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var atomic = (AtomicReader)leaf.Reader;
            var liveDocs = atomic.LiveDocs;
            var maxDoc = atomic.MaxDoc;

            for (var docId = 0; docId < maxDoc; docId++)
            {
                if (liveDocs is not null && !liveDocs.Get(docId))
                    continue;

                var path = atomic.Document(docId, pathFieldOnly).Get("path");
                if (string.IsNullOrEmpty(path))
                    continue;

                if (counts.TryGetValue(path, out var seen))
                {
                    counts[path] = seen + 1;
                }
                else
                {
                    counts[path] = 1;
                    order.Add(path);
                }
            }
        }

        foreach (var path in order)
            entries.Add(new IndexedPathEntry(path, FullTextChangeCoalescer.NormalizeComparisonKey(path), counts[path]));

        return entries;
    }

    /// <summary>
    /// 体检探针：本片**未实现**（属 S3d：需要资源压力采样、小切片游走与三态判定）。
    /// <para>
    /// ⚠️ 这里显式抛 <see cref="NotSupportedException"/> 而不是返回一个「看着像健康」的结论：
    /// 让「未实现」在调用点立刻可见，而不是把「没做体检」伪装成 <c>Healthy</c>。
    /// </para>
    /// </summary>
    public Task<FullTextIndexIntegrityProbe> ProbeIntegrityAsync(
        string scopeRoot,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException(
            "ProbeIntegrityAsync 属 S3d（资源压力退避 + 小切片差异核对 + 三态判定），本片（S3a）不实现。");

    // ── 结果构造与度量 ──────────────────────────────────────────────────

    /// <summary>
    /// 组装结果。<see cref="FullTextMutationResult.CheckpointAdvanced"/> 是<b>产物</b>：
    /// 先用 <c>false</c> 构造，再由 <see cref="CheckpointAdvancePolicy"/> 判定（它只读 State / FailedCount /
    /// RetainedOldCount，<b>不回读</b>本字段），最后与「调用方声明本轮是否为可推进的完整补偿轮次」取合取。
    /// <para>
    /// 因此「输入说谎」（<see cref="FullTextChangeSet.RequiresCheckpointAdvance"/> 为 true 而本批有失败/保留项）
    /// 不改判定：策略否决即否决。
    /// </para>
    /// </summary>
    private static FullTextMutationResult BuildResult(
        FullTextChangeSet changeSet,
        FullTextMutationState state,
        int upsertAppliedCount,
        int deleteAppliedCount,
        IReadOnlyList<string> retainedOldPaths,
        IReadOnlyList<string> failureNotes,
        long? indexBytesBefore,
        long? indexBytesAfter,
        double? commitMilliseconds,
        string? message)
    {
        // 失败路径 = 保留旧文档的路径 ∪ 其它失败说明来源（本片只有前者，仍按契约去重汇总）。
        var failedPaths = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in retainedOldPaths)
        {
            if (seen.Add(path))
                failedPaths.Add(path);
        }

        var candidate = new FullTextMutationResult(
            changeSet.BatchId,
            changeSet.ScopeKey,
            state,
            upsertAppliedCount,
            deleteAppliedCount,
            retainedOldPaths.Count,
            failedPaths.Count,
            failedPaths,
            retainedOldPaths,
            indexBytesBefore,
            indexBytesAfter,
            commitMilliseconds,
            CheckpointAdvanced: false,
            ComposeMessage(message, failureNotes));

        var checkpointAdvanced =
            CheckpointAdvancePolicy.AllowsAdvance(candidate) && changeSet.RequiresCheckpointAdvance;

        return candidate with { CheckpointAdvanced = checkpointAdvanced };
    }

    /// <summary>「未写入」类终态（Rejected / Busy / Cancelled）的统一构造：两个字节数都实测（不可测为 null）。</summary>
    private FullTextMutationResult NoWriteResult(
        FullTextChangeSet changeSet,
        FullTextMutationState state,
        string indexPath,
        string message)
        => BuildResult(
            changeSet,
            state,
            0,
            0,
            Array.Empty<string>(),
            Array.Empty<string>(),
            MeasureIndexBytes(indexPath),
            MeasureIndexBytes(indexPath),
            null,
            message);

    private static string? ComposeMessage(string? message, IReadOnlyList<string> failureNotes)
    {
        if (failureNotes.Count == 0)
            return message;

        var notes = string.Join("；", failureNotes);
        return message is null ? notes : $"{message} | {notes}";
    }

    private static string? DescribeRetained(IReadOnlyList<string> retainedOldPaths, IReadOnlyList<string> failureNotes)
        => retainedOldPaths.Count == 0
            ? ComposeMessage(null, failureNotes)
            : $"{retainedOldPaths.Count} 个路径保留旧文档（提取 / 读取失败，下次必须重放；checkpoint 不推进）";

    /// <summary>
    /// 实测索引目录字节数（提交前 / 提交后的判定口径）。
    /// <para>不可测（目录不存在 / 读不出）⇒ <c>null</c>：契约明确禁止伪报 0（0 与「读不到」必须可区分）。</para>
    /// </summary>
    internal static long? MeasureIndexBytes(string indexDirectory)
    {
        try
        {
            if (!Directory.Exists(indexDirectory))
                return null;

            var total = 0L;
            foreach (var file in Directory.EnumerateFiles(indexDirectory, "*", SearchOption.AllDirectories))
                total += new FileInfo(file).Length;

            return total;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
            return null;
        }
    }

    /// <summary>最终 stat 通过、等待内容提取的 Upsert（<see cref="ObservedBytes"/> 用于写前预检的增长估计）。</summary>
    private readonly record struct VerifiedUpsert(string FullPath, long ObservedBytes);

    /// <summary>已在内存中构造完成的待写文档（内容先于任何 delete 准备完毕）。</summary>
    private readonly record struct PreparedUpsert(string FullPath, string Content);

    /// <summary>一次 writer 会话的结果：提交成功，或被某条失败语义短路（此时未提交）。</summary>
    private readonly record struct WriterSessionOutcome(
        bool Committed,
        double? CommitMilliseconds,
        FullTextMutationState? ShortCircuitState,
        string? Message);
}
