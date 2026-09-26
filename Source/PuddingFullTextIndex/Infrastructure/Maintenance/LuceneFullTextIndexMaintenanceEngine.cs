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
/// 不起 <c>FileSystemWatcher</c> / <c>Timer</c> / 长驻 <c>Task</c>（属 S3d）。写入期配额（S3b）已落地：writer 建在
/// <see cref="QuotaEnforcingDirectory"/> 上，超限以 <see cref="IndexWriteQuotaExceededException"/> 中止本批并 rollback。
/// 跨进程租约（S3c）已落地：每个合并批次<b>获取一次</b>、拿不到时**有界等待**（超时 ⇒
/// <see cref="FullTextMutationState.Busy"/>、未写入、未推进 checkpoint）、<b>取得租约之后</b>才做最终 stat；
/// index-root 级全局 gate 让多 scope 提交**全局串行**；commit 成功后**显式**失效查询侧 reader。
/// </para>
/// <para>
/// <b>§3.6 单批次顺序（本类逐条对齐）</b>：
/// <list type="number">
/// <item><description>⓪ index-root 级<b>全局写者 gate</b>（<see cref="IndexRootWriteGate"/>；§4.2 末条「多 scope 增量提交默认全局串行」）
/// —— 最先取得、最后释放；**固定加锁顺序** = 全局 index-root → per-scope → 跨进程租约（理由见
/// <c>ApplyChangesWithReportAsync</c> 的注释）。</description></item>
/// <item><description>① 每 scope 进程内 gate —— 复用查询侧<b>同一实例</b>的既有 gate（同一字典、同一键，
/// 见 <c>LuceneSearchEngine.GetScopeGate</c>），因此局部写与全量构建在同一进程内互斥。</description></item>
/// <item><description>② 跨进程租约（<see cref="IFullTextSupplyLease"/> / <see cref="FileSupplyLease"/>）——
/// <b>每个合并批次获取一次</b>（L1）：一次批次内 <c>TryAcquireAsync</c> 在无争用时恰好 1 次；拿不到时有界等待
/// （上界 <c>MaintenanceOptions.LeaseWaitUpperBound</c>），超时 ⇒ <see cref="FullTextMutationState.Busy"/>；
/// 批次结束（含失败 / 取消 / 异常）**无条件** <c>ReleaseAsync</c>。取租约在 ③ 的最终 stat <b>之前</b>
/// （§4.4：等待期间的变更不得丢——否则会把等待前的旧内容写进刚刚被手动重建的新索引）。</description></item>
/// <item><description>③ 对每个 Upsert 做最终 stat 与过滤 —— 复用扫描期同一判定体
/// <see cref="FileCandidateCollector"/>（扩展名白名单 / 噪声名单 / 空文件 / 超限，唯一真源）。</description></item>
/// <item><description>④ <b>提取失败的文件不进入 Lucene delete 集合</b> —— 提取全部在内存中完成后才开始写；
/// 任一文件提取失败 ⇒ 它的旧文档<b>保留</b>，只登记待重试（绝不先删后失败）。</description></item>
/// <item><description>⑤ 打开<b>一个</b> <c>IndexWriter(OpenMode.CREATE_OR_APPEND)</c>；不可得锁 ⇒ 返回
/// <see cref="FullTextMutationState.Busy"/>（不抛、不提交）。writer 建在写入期配额包装层
/// <see cref="QuotaEnforcingDirectory"/> 上，并显式用 <c>SerialMergeScheduler</c> ⇒ 合并写出的段文件
/// 同步经过计数，且合并必然发生在 commit 点写盘<b>之前</b>（超限位置始终可回滚）。</description></item>
/// <item><description>⑥ 成功的 Upsert：先 <c>DeleteDocuments(path)</c> 再添加当前文档
/// （幂等重写；文档构造复用引擎的 <c>AddDocument</c>，<b>不复制</b>结构）。</description></item>
/// <item><description>⑦ 确认删除：<c>DeleteDocuments(path)</c>。</description></item>
/// <item><description>⑧ 预算硬限 —— 双层：<b>写前预检</b>（§4.2 原文即为「写前」）与<b>写入期配额</b>（S3b）。
/// 预检与 A2a 同一个 <see cref="SupplyBudgetCalculator.Fits"/> 口径；写入期由 <see cref="QuotaEnforcingDirectory"/>
/// 统计实际输出字节（含自动合并写出的新段），越界 ⇒ <see cref="IndexWriteQuotaExceededException"/> 中止本批。
/// 两者都只允许「拒绝 + rollback 保留上一个 commit」，绝不先写后超。</description></item>
/// <item><description>⑨ <c>Commit()</c> —— 单批一次；未 commit 的文档对查询不可见（本类只在提交成功后才返回
/// <see cref="FullTextMutationState.Applied"/> / <see cref="FullTextMutationState.PartiallyApplied"/>）。</description></item>
/// <item><description>⑩ <c>InvalidateScope(scope)</c> —— <b>仅 commit 成功</b>时显式调用一次（经
/// <see cref="IScopeReaderInvalidation"/>，传 <c>changeSet.ScopeRoot</c>）：未提交（Busy / Rejected / Failed /
/// Cancelled / 无写入）一律 <b>0 次</b>。本类不持有 reader 缓存，实际失效由同一实例的
/// <c>LuceneSearchEngine.InvalidateScope</c> 完成（§4.5）。</description></item>
/// <item><description>⑪ 释放 writer（每次批次创建 / 提交 / 释放，不长持 writer lock）；随后释放跨进程租约（同一
/// <c>finally</c>，与提交与否无关）。</description></item>
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

    /// <summary>租约有界等待的最小重试间隔（有界等待的粒度；不含任何后台线程 / 定时器）。</summary>
    private static readonly TimeSpan LeaseRetryInterval = TimeSpan.FromMilliseconds(50);

    private readonly LuceneSearchEngine _searchEngine;
    private readonly FullTextIndexOptions _options;
    private readonly IFullTextSupplyLease _lease;
    private readonly TimeSpan _leaseWaitUpperBound;
    private readonly IScopeReaderInvalidation _readerInvalidation;

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
    /// <param name="lease">
    /// 跨进程 scope 写者租约（S3c）：<b>每个合并批次获取一次</b>、批次结束（成功 / 失败 / 取消 / 异常）无条件释放。
    /// 必填：不存在「不取租约」的退化装配（可选参数 = null 这类兼容设计会让租约被静默绕过）。
    /// </param>
    /// <param name="leaseWaitUpperBound">
    /// 租约**有界等待**上界（§4.4）。唯一真源是 <c>MaintenanceOptions.LeaseWaitUpperBound</c>，由组合根解析后传入；
    /// 构造时 fail-closed 校验（必须为正且 ≤ <c>MaintenanceOptions.MaxLeaseWaitAllowed</c>，见下）。
    /// </param>
    /// <param name="readerInvalidation">
    /// 查询侧 reader 缓存失效接缝（S3c）：<b>只有 commit 成功</b>才调用一次（§4.5）。
    /// </param>
    public LuceneFullTextIndexMaintenanceEngine(
        LuceneSearchEngine searchEngine,
        FullTextIndexOptions options,
        IFullTextSupplyLease lease,
        TimeSpan leaseWaitUpperBound,
        IScopeReaderInvalidation readerInvalidation)
    {
        _searchEngine = searchEngine ?? throw new ArgumentNullException(nameof(searchEngine));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _lease = lease ?? throw new ArgumentNullException(nameof(lease));
        _readerInvalidation = readerInvalidation ?? throw new ArgumentNullException(nameof(readerInvalidation));

        // fail-closed：等待上界必须来自 MaintenanceOptions 的合法区间。
        // 0 / 负值等价于「不等待」（那是*供给/构建*路径的语义，见 IFullTextSupplyLease 的接口注释），
        // 维护路径按 §4.4 必须「有界等待」⇒ 这里直接拒绝，绝不静默退化成不等待或不设上界。
        if (leaseWaitUpperBound <= TimeSpan.Zero || leaseWaitUpperBound > MaintenanceOptions.MaxLeaseWaitAllowed)
        {
            throw new ArgumentOutOfRangeException(
                nameof(leaseWaitUpperBound),
                $"租约等待上界必须在 (0, {MaintenanceOptions.MaxLeaseWaitAllowed}] 之间，收到 {leaseWaitUpperBound}。");
        }

        _leaseWaitUpperBound = leaseWaitUpperBound;
    }

    // ── 局部写：ApplyChangesAsync ────────────────────────────────────────

    /// <inheritdoc />
    public async Task<FullTextMutationResult> ApplyChangesAsync(
        FullTextChangeSet changeSet,
        FullTextMutationBudget budget,
        CancellationToken cancellationToken = default)
    {
        var (result, _) = await ApplyChangesWithReportAsync(changeSet, budget, cancellationToken).ConfigureAwait(false);
        return result;
    }

    /// <summary>
    /// 与 <see cref="ApplyChangesAsync"/> <b>同一实现</b>（唯一执行路径），额外产出本批的
    /// <see cref="IndexSizeReport"/>（方案 §6 S3 完成标准⑥「连续局部更新的体积增长有机器可读报告」）。
    /// <para>
    /// internal：本片（S3b）不接 Host ⇒ 报告暂由组件内测试消费。报告与结果来自<b>同一次执行</b>，
    /// 不存在「日志说一套、返回值说另一套」的可能。
    /// </para>
    /// </summary>
    internal async Task<(FullTextMutationResult Result, IndexSizeReport Report)> ApplyChangesWithReportAsync(
        FullTextChangeSet changeSet,
        FullTextMutationBudget budget,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(changeSet);
        ArgumentNullException.ThrowIfNull(budget);

        var indexPath = _searchEngine.GetIndexDirectoryPath(changeSet.ScopeRoot);
        var observation = new IndexSizeObservation();

        // ── ⓪ index-root 级**全局写者 gate**（§4.2 末条：多 scope 增量提交默认全局串行）──
        // **固定加锁顺序** = 全局 index-root gate → 每 scope 进程内 gate → 跨进程租约。
        // 为何必须固定、且为何全局必须在最外层：全局 gate 是唯一的跨 scope 共享资源，把它放在最外层 ⇒
        // 任何已持有内层锁（per-scope gate / 租约）的写者都**不再申请**它，等待图里不可能成环；
        // 若反过来（先 per-scope 再全局），会同时存在「持 S1 等全局」与「持全局等 S2」两种等待，
        // 一旦供给/构建路径将来也引入同一全局资源即成死锁环（供给路径只取 per-scope gate + 租约，故当前无环）。
        // 全局串行的目的见 §4.2：不让两个本进程 writer 同时消费**同一份剩余额度** ——
        // 真正的防双花在 ApplyUnderLeaseAsync 里：进入临界区后**重测** live 字节，与调用方传入值取更严的一侧。
        var indexRootGate = IndexRootWriteGate.For(_options.IndexRootDirectory);
        try
        {
            await indexRootGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            var canceled = NoWriteResult(
                changeSet,
                FullTextMutationState.Cancelled,
                indexPath,
                "取消：等待全局 index-root 写者 gate 期间被取消（未写入、未提交）。");
            return (canceled, IndexSizeReport.Create(canceled, budget, observation));
        }

        FullTextMutationResult result;
        try
        {
            result = await ApplyUnderIndexRootGateAsync(changeSet, budget, indexPath, observation, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            indexRootGate.Release();
        }

        return (result, IndexSizeReport.Create(result, budget, observation));
    }

    /// <summary>
    /// 全局 index-root gate 内的批次执行：再取**每 scope 进程内 gate**（与全量构建共用同一把）
    /// ⇒ <see cref="ApplyUnderGateAsync"/>（守卫 → 跨进程租约 → 最终 stat → 单批 commit → 失效 reader）。
    /// </summary>
    private async Task<FullTextMutationResult> ApplyUnderIndexRootGateAsync(
        FullTextChangeSet changeSet,
        FullTextMutationBudget budget,
        string indexPath,
        IndexSizeObservation observation,
        CancellationToken cancellationToken)
    {
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
            return await ApplyUnderGateAsync(changeSet, budget, indexPath, observation, cancellationToken)
                .ConfigureAwait(false);
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
        IndexSizeObservation observation,
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

        // ── ② 跨进程租约：**每个合并批次获取一次**（§4.4）；拿不到 ⇒ 有界等待，超时 ⇒ Busy ──
        // （守卫 A / B 都只读、不写，放在取租约之前：不值得为必然被拒的批次去动跨进程租约文件。）
        // 角色必须显式：供给路径用 Supply、维护路径用 Maintenance ⇒ 同进程内两条路径的默认 owner 不同，
        // 不会被租约判成「自己人」而重入（缺陷 ②）。同一角色跨批次仍可重入（见 ForCurrentProcess 注释）。
        var leaseOwner = SupplyLeaseOwner.ForCurrentProcess(SupplyLeaseRole.Maintenance);
        var leaseAttempt = await AcquireLeaseWithinBoundAsync(changeSet, leaseOwner, cancellationToken)
            .ConfigureAwait(false);

        if (!leaseAttempt.Acquired)
        {
            // 未写入任何字节、未推进 checkpoint；同一变更集下一轮重放必须成功（幂等 delete-then-add 允许重放）。
            return NoWriteResult(
                changeSet,
                FullTextMutationState.Busy,
                indexPath,
                DescribeLeaseBusy(leaseAttempt));
        }

        try
        {
            // ── ③ 最终 stat 必须在**取得租约之后**：等待期间文件被替换 / 删除 / 手动重建索引 ⇒
            //     写进去的必须是「现在」的磁盘事实，绝不是等待前观察到的旧内容（§4.4）。
            //     结构上这条顺序不可绕过：最终 stat 只存在于 ApplyUnderLeaseAsync，而它只从这里被调用。
            var result = await ApplyUnderLeaseAsync(changeSet, budget, indexPath, observation, cancellationToken)
                .ConfigureAwait(false);

            // ── ⑩ commit 成功 ⇒ **显式**失效查询侧 reader（§4.5：读者无需重启即可见新内容）──
            // 判据取契约字段：FullTextMutationResult.CommitMilliseconds 的语义是「commit 耗时；未提交时为 null」
            // （见 Contracts/IFullTextIndexMaintenanceEngine.cs）⇒ 非 null ⇔ 本批真的提交过。
            // 这样「无事可写（未开 writer、未 commit）」的 Applied 不会被误判为已提交（L6：未提交 ⇒ 0 次）。
            if (result.CommitMilliseconds is not null)
                _readerInvalidation.InvalidateScope(changeSet.ScopeRoot);

            return result;
        }
        finally
        {
            // 释放与提交与否无关：成功 / 取消 / Busy / 异常路径都必须放（否则同一 scope 会被自己永久 Busy）。
            // 令牌固定用 CancellationToken.None — 取消路径也必须释放（L1）。
            await _lease.ReleaseAsync(changeSet.ScopeKey, leaseOwner.OwnerId, CancellationToken.None)
                .ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 已取得租约后的批次主体：最终 stat → 写前预检 → 内容提取 → 单批 commit。
    /// <para>本方法**不**碰租约 / gate / reader 缓存失效 —— 那些都在调用方，确保「租约必须覆盖最终 stat」
    /// 这条顺序在代码结构上无法被绕过。</para>
    /// </summary>
    private async Task<FullTextMutationResult> ApplyUnderLeaseAsync(
        FullTextChangeSet changeSet,
        FullTextMutationBudget budget,
        string indexPath,
        IndexSizeObservation observation,
        CancellationToken cancellationToken)
    {
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
        // ⚠️ 这一道是「提取前」的粗筛，可能低估 Lucene 的实际输出（段格式开销）；写入期还有第二道按
        // **实际输出字节**的硬限（S3b 的 QuotaEnforcingDirectory，见 RunWriterSession）。两者都只允许
        // 「拒绝 + rollback 保留上一个 commit」，绝不先写后超。
        var predictedIncomingIndexBytes = 0L;
        foreach (var verified in verifiedUpserts)
            predictedIncomingIndexBytes += verified.ObservedBytes;

        // ── ⓪c 集合口径的 live 字节：**临界区内重测**，与调用方传入值取**更严**的一侧 ──
        // 为何必须重测：调用方的 LiveIndexBytes 是进锁**之前**量的；两个 scope 并发提交时双方手里都是
        // 「对方尚未写入」的旧值 ⇒ 两块批次各按旧值消费同一份剩余额度，合计必然突破 MaxIndexBytes（§4.2 末条
        // 正是为此要求「多 scope 增量提交默认全局串行」）。取 max 的语义：**只收紧、绝不放松**调用方预算 ——
        // 调用方若故意传入更大的 live（例如模拟「其它 scope 已占用」），依然原样生效。
        long liveIndexBytesNow;
        try
        {
            liveIndexBytesNow = SupplyIndexDirectoryLayout.MeasureLiveIndexBytes(_options.IndexRootDirectory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
            // fail-closed：量不出「全部 live scope 合计」就不允许写 —— 绝不默默按 0 判定
            // （那会把「量不到」当成「没占用」，从而绕过预算硬限）。
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
                $"拒绝：无法实测 live 集合用量（{ex.GetType().Name}: {ex.Message}）⇒ fail-closed，本批未写入、未提交。");
        }

        var effectiveLiveIndexBytes = Math.Max(budget.LiveIndexBytes, liveIndexBytesNow);
        var allowedGrowthBytes = effectiveLiveIndexBytes >= budget.MaxIndexBytes
            ? 0
            : budget.MaxIndexBytes - effectiveLiveIndexBytes;

        var budgetFits = SupplyBudgetCalculator.Fits(effectiveLiveIndexBytes, predictedIncomingIndexBytes, budget.MaxIndexBytes);

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
                $"拒绝：预算硬限不通过 —— {SupplyBudgetCalculator.Describe("本批待写文件", effectiveLiveIndexBytes, predictedIncomingIndexBytes, budget.MaxIndexBytes)}"
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

        // ── ⑤⑥⑦⑨⑪ 单批 writer 会话（S3b：writer 建在写入期配额包装层上）──
        var session = RunWriterSession(
            indexPath, budget, allowedGrowthBytes, deleteThenAddPaths, confirmedDeletes, preparedUpserts, observation,
            cancellationToken);
        var indexBytesAfter = MeasureIndexBytes(indexPath);

        // fail-loud 不变量：已提交 ⇔ 无短路态（RunWriterSession 的所有分支都满足这一点；不满足说明内部逻辑漏洞）。
        if (session.Committed != (session.ShortCircuitState is null))
        {
            throw new InvalidOperationException(
                "writer 会话结果自相矛盾：Committed 与 ShortCircuitState 不一致（内部逻辑错误）。");
        }

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
    /// 一个批次的单次 writer 会话：打开（**写入期配额包装层**）→ delete-then-add → commit → 释放
    /// （**不**长持 writer lock）。
    /// <para>
    /// 失败语义：取不到锁 ⇒ <see cref="FullTextMutationState.Busy"/>；取消 ⇒
    /// <see cref="FullTextMutationState.Cancelled"/>；**写入期配额越界** ⇒
    /// <see cref="FullTextMutationState.Rejected"/>（本批未 commit）；其它异常 ⇒
    /// <see cref="FullTextMutationState.Failed"/>；未提交的一律由 <c>finally</c> 走 <c>Rollback()</c>
    /// （未提交的变更被丢弃，索引回到上一个 commit）。
    /// </para>
    /// <para>
    /// <b>为什么合并必须是同步的</b>（<c>SerialMergeScheduler</c>）：自动合并写出的新段文件同样要计入同一个预算，
    /// 而且「超限」必须发生在 commit 点写盘<b>之前</b>才能靠 rollback 保住旧 commit。若用默认的后台合并调度器，
    /// 越过预算的合并可能落在提交之后 —— 那时既无法回滚，也证明不了「提交后不产生无界临时段」。
    /// </para>
    /// </summary>
    private WriterSessionOutcome RunWriterSession(
        string indexPath,
        FullTextMutationBudget budget,
        long allowedGrowthBytes,
        IReadOnlyList<string> deleteThenAddPaths,
        IReadOnlyList<string> confirmedDeletePaths,
        IReadOnlyList<PreparedUpsert> preparedUpserts,
        IndexSizeObservation observation,
        CancellationToken cancellationToken)
    {
        // 本批允许的**实际输出增长**由调用方在**临界区内**算好（= 集合预算 − max(调用方 live, 临界区内重测 live)）：
        // 两个并发 scope 不可能再各按「对方尚未写入」的旧值消费同一份剩余额度（§4.2）。
        // 0 表示本批不得写出任何新字节（合同 FullTextMutationBudget.RemainingBytes 的语义）。
        var quotaDirectory = new QuotaEnforcingDirectory(
            FSDirectory.Open(indexPath),
            budget.MaxIndexBytes,
            allowedGrowthBytes);
        observation.WriterSessionOpened = true;

        IndexWriter? writer = null;
        var committed = false;
        var sessionState = new WriterSessionState();

        try
        {
            try
            {
                // CREATE_OR_APPEND：局部写永远追加在既有索引之后（目录存在性已由守卫 B 保证）。
                writer = new IndexWriter(
                    quotaDirectory,
                    new IndexWriterConfig(IndexWriterMatchVersion, _searchEngine.Analyzer)
                    {
                        OpenMode = OpenMode.CREATE_OR_APPEND,
                        RAMBufferSizeMB = WriterRamBufferMegabytes,
                        MergeScheduler = new SerialMergeScheduler(),
                    });
            }
            catch (LockObtainFailedException ex)
            {
                // 互斥未取得（另一 writer 持同一索引目录）⇒ 不抛异常、不提交（按 Busy/Retry 处理）。
                sessionState.Outcome = new WriterSessionOutcome(
                    false,
                    null,
                    FullTextMutationState.Busy,
                    $"互斥失败：writer lock 被占用（{ex.Message}）⇒ 本批未写入、未提交。");
            }

            if (sessionState.Outcome is null)
            {
                // DeleteDocuments 保留旧接口语义：delete 不存在路径的文档是 no-op，不算失败。
                foreach (var path in deleteThenAddPaths)
                    writer!.DeleteDocuments(new Term("path", path));

                foreach (var path in confirmedDeletePaths)
                    writer!.DeleteDocuments(new Term("path", path));

                // 文档构造复用引擎的同一实现（不复制结构 ⇒ 两种写入路径不会静默漂移）。
                foreach (var prepared in preparedUpserts)
                    LuceneSearchEngine.AddDocument(writer!, prepared.FullPath, prepared.Content);

                if (cancellationToken.IsCancellationRequested)
                {
                    sessionState.Outcome = new WriterSessionOutcome(
                        false,
                        null,
                        FullTextMutationState.Cancelled,
                        "取消：提交前被取消 ⇒ 已 rollback、未提交。");
                }
                else
                {
                    // 先把本线程触发的合并全部落地（SerialMergeScheduler 下合并就在本线程执行），
                    // 再判定是否已越界：越界 ⇒ **不 commit**（由 finally 的 rollback 保留上一个 commit）。
                    writer!.WaitForMerges();

                    if (quotaDirectory.Violation is { } violationBeforeCommit)
                    {
                        sessionState.Outcome = QuotaOutcome(violationBeforeCommit, "flush / merge 写出");
                    }
                    else
                    {
                        var stopwatch = Stopwatch.StartNew();
                        writer!.Commit();
                        var commitMilliseconds = stopwatch.Elapsed.TotalMilliseconds;
                        committed = true;
                        sessionState.Outcome = new WriterSessionOutcome(true, commitMilliseconds, null, null);
                    }
                }
            }
        }
        catch (Exception ex) when (quotaDirectory.Violation is not null)
        {
            // 越界以**明确异常**中止本批。异常本身可能被 Lucene 包装（例如合并路径折成
            // MergePolicy.MergeException），故判定依据取包装层记录的越界事实，不依赖异常类型 / 文本。
            sessionState.Outcome = QuotaOutcome(quotaDirectory.Violation, ex.GetType().Name);
        }
        catch (OperationCanceledException)
        {
            sessionState.Outcome = new WriterSessionOutcome(
                false,
                null,
                FullTextMutationState.Cancelled,
                "取消：提交前被取消 ⇒ 已 rollback、未提交。");
        }
        catch (Exception ex)
        {
            sessionState.Outcome = new WriterSessionOutcome(
                false,
                null,
                FullTextMutationState.Failed,
                $"致命失败：本批已 rollback、未提交（{ex.GetType().Name}: {ex.Message}）。");
        }
        finally
        {
            var removedByRollback = 0;

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
                    var removedBeforeRollback = quotaDirectory.DeletedFileCount;
                    writer.Rollback();
                    removedByRollback = quotaDirectory.DeletedFileCount - removedBeforeRollback;
                    sessionState.RolledBack = true;
                }
            }

            observation.Capture(quotaDirectory, sessionState.RolledBack, removedByRollback);
        }

        if (sessionState.Outcome is not { } outcome)
        {
            // fail-loud：终态必须由业务分支 / catch 之一写入；走到这里说明本方法有逻辑漏洞（不伪造终态）。
            throw new InvalidOperationException(
                "writer 会话未产生终态：所有分支都必须写 sessionState.Outcome（内部逻辑错误）。");
        }

        return outcome;
    }

    /// <summary>
    /// 写入期配额越界的统一终态：**不提交**、由 <c>finally</c> 的 <c>Rollback()</c> 保留上一个 commit。
    /// 消息里带上「写入期配额」与专用异常名，便于与写前预检的拒绝区分（后者讲「预算硬限 / 本批待写文件」）。
    /// </summary>
    private static WriterSessionOutcome QuotaOutcome(IndexWriteQuotaExceededException violation, string source)
        => new(
            false,
            null,
            FullTextMutationState.Rejected,
            $"拒绝：写入期配额超限 —— {source}（{nameof(IndexWriteQuotaExceededException)}）"
            + $"；本批未 commit、已 rollback 保留上一个 commit。{violation.Message}");

    /// <summary>
    /// 一次「有界等待」的租约获取汇总（仅诊断与 Busy 消息共用；不上报、不污染业务结果）。
    /// </summary>
    /// <param name="Acquired">是否在等待上界内取得。</param>
    /// <param name="LastResult">最后一次尝试的原始结果（取得或未取得；用于持有者 / 原因可读化）。</param>
    /// <param name="Attempts">实际调用 <c>TryAcquireAsync</c> 的次数（无争用时恰为 1）。</param>
    /// <param name="Waited">从第一次尝试到定论的实测耗时。</param>
    private readonly record struct LeaseAttempt(
        bool Acquired,
        SupplyLeaseAcquireResult? LastResult,
        int Attempts,
        TimeSpan Waited);

    /// <summary>
    /// **有界等待**地取得本批次的跨进程租约（§4.4）。
    /// <para>
    /// 语义：每批次只调用本方法一次（L1）；每次重试都是「一次 <c>TryAcquireAsync</c> + 一次有界延迟」，
    /// 延迟由<b>调用方 token + 上界</b>驱动（<c>Task.Delay</c>），<b>不起任何后台线程 / 定时器</b>。
    /// 超时后返回 <c>Acquired = false</c>，由调用方转成 <see cref="FullTextMutationState.Busy"/>（不写入、不推进 checkpoint）。
    /// </para>
    /// <para>
    /// ⚠️ 与 <see cref="IFullTextSupplyLease"/> 接口注释「拿不到租约必须直接放弃本次构建（不得等待、不得写索引）」的
    /// 差异：那是***供给 / 构建*路径**的语义（一次构建失败就让位给下一次供给，人工重跑代价低）；
    /// 维护路径是**背景渐进**的（变更集每轮重放，但长持租约的可能是分钟级的手动重建）⇒ 按 §4.4 采用
    /// <b>有界等待</b>：既不错过「很快就能拿到的窗口」，也不把维护线程无限挂住。接口与本片都没有被修改：
    /// 等待循环在调用侧，租约实现仍然「一次尝试、一次回答」。
    /// </para>
    /// </summary>
    private async Task<LeaseAttempt> AcquireLeaseWithinBoundAsync(
        FullTextChangeSet changeSet,
        SupplyLeaseOwner owner,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var attempts = 0;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var result = await _lease
                .TryAcquireAsync(changeSet.ScopeKey, owner, changeSet.BatchId, cancellationToken)
                .ConfigureAwait(false);
            attempts++;

            if (result.Acquired)
                return new LeaseAttempt(true, result, attempts, stopwatch.Elapsed);

            var waited = stopwatch.Elapsed;
            var remaining = _leaseWaitUpperBound - waited;
            if (remaining <= TimeSpan.Zero)
                return new LeaseAttempt(false, result, attempts, waited);

            await Task.Delay(remaining < LeaseRetryInterval ? remaining : LeaseRetryInterval, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    /// <summary>租约等待超时的可读说明：上界 / 实测等待 / 尝试次数 / 持有者 / 租约实现给出的原因。</summary>
    private string DescribeLeaseBusy(LeaseAttempt attempt)
    {
        var holder = attempt.LastResult?.Holder is { } h
            ? $"当前持有者 {h.OwnerId}（pid={h.ProcessId} @ {h.MachineName}，自 {h.StartedAtUtc:O} 起，"
                + $"最近心跳 {h.HeartbeatUtc:O}，已过期={h.IsExpired}，job={h.JobId ?? "(无)"}）"
            : "当前持有者未知（查阅时租约已释放或无法读取）";

        var reason = attempt.LastResult?.Message;
        return $"互斥失败：跨进程租约在 {_leaseWaitUpperBound.TotalMilliseconds:F0} ms 的有界等待内未取得"
            + $"（实测等待 {attempt.Waited.TotalMilliseconds:F0} ms，尝试 {attempt.Attempts} 次）；{holder}"
            + (string.IsNullOrEmpty(reason) ? string.Empty : $"；{reason}")
            + "；本批未写入任何字节、未提交、未推进 checkpoint（下一轮重放同一变更集：幂等 delete-then-add 允许重放）。";
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
    /// 索引完整性探针（方案 §3.5 / §3.7）：核对 live 索引与**磁盘路径集合**的差异，给出可区分的三态结论。
    /// <list type="number">
    /// <item><description>索引目录**不存在** ⇒ <see cref="FullTextIndexIntegrityState.ManualRebuildRequired"/>
    /// （终态，不是「跳过」；**绝不**通过局部写偷偷初始化一份「初始全库索引」）。</description></item>
    /// <item><description>目录存在但**不是可读的索引** / 读不出 / 无法完整枚举磁盘 ⇒ 同样 <c>ManualRebuildRequired</c>
    /// （「无法证明新鲜」就是不许宣称健康）。</description></item>
    /// <item><description>可读且路径集合与磁盘一致 ⇒ <see cref="FullTextIndexIntegrityState.Healthy"/>；
    /// 有差异 ⇒ <see cref="FullTextIndexIntegrityState.Degraded"/>（差异应作为统一变更集发布出去，由维护层做）。</description></item>
    /// </list>
    /// <para>
    /// <b>只读</b>：只经 <see cref="FullTextIndexPaths"/> 的命名哈希定位索引目录、只用 <c>FSDirectory</c> + <c>DirectoryReader</c> 读取，
    /// <b>不创建</b>任何目录 / 文件、不开 <c>IndexWriter</c>、不动 reader 缓存、不写 <c>checkpoint</c>。
    /// </para>
    /// <para>
    /// 磁盘侧的「可索引」判定与补偿扫描**共用**同一走查器（<c>MaintenanceCorpusScan</c>）与同一判定体
    /// （<see cref="FileCandidateCollector"/>）；不变量：本探针报 <c>Healthy</c> ⇔ 维护层的补偿扫描算不出任何变更。
    /// </para>
    /// <para>
    /// ⚠️ <c>CheckedPathCount</c> = 本轮实际参与比较的路径数（索引侧 + 磁盘侧的并集）；
    /// <b>低优先级</b>与<b>切片</b>是维护层的职责（专用 <c>BelowNormal</c> 线程 + <c>HealthCheckSliceFiles</c> 限制每轮发布的修复条数），
    /// 本探针本身不做资源压力退避 —— 它只回答「差异是什么」。
    /// </para>
    /// </summary>
    /// <param name="scopeRoot">scope 语料根。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public Task<FullTextIndexIntegrityProbe> ProbeIntegrityAsync(
        string scopeRoot,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scopeRoot);
        cancellationToken.ThrowIfCancellationRequested();

        var scopeKey = FullTextChangeCoalescer.NormalizeComparisonKey(scopeRoot);
        var indexPath = _searchEngine.GetIndexDirectoryPath(scopeRoot);

        // ① 索引目录不存在 ⇒ ManualRebuildRequired。
        if (!Directory.Exists(indexPath))
        {
            return Task.FromResult(new FullTextIndexIntegrityProbe(
                scopeKey,
                FullTextIndexIntegrityState.ManualRebuildRequired,
                IndexDirectoryExists: false,
                IndexedPathCount: null,
                IndexedDocumentCount: null,
                CheckedPathCount: 0,
                MismatchCount: 0,
                MismatchedPaths: Array.Empty<string>(),
                IndexBytes: null,
                Message: $"live 索引目录不存在（{indexPath}）⇒ 需手动重建；本探针只读，绝不通过局部写偷偷初始化。"));
        }

        var indexBytes = MeasureDirectoryBytesOrNull(indexPath);

        // ② 目录存在但读不出 / 不是可读的索引 ⇒ 同样 ManualRebuildRequired（「无法证明新鲜」不许宣称健康）。
        List<IndexedPathEntry> inventory;
        try
        {
            using (var directory = FSDirectory.Open(indexPath))
            {
                if (!DirectoryReader.IndexExists(directory))
                {
                    return Task.FromResult(new FullTextIndexIntegrityProbe(
                        scopeKey,
                        FullTextIndexIntegrityState.ManualRebuildRequired,
                        IndexDirectoryExists: true,
                        IndexedPathCount: null,
                        IndexedDocumentCount: null,
                        CheckedPathCount: 0,
                        MismatchCount: 0,
                        MismatchedPaths: Array.Empty<string>(),
                        indexBytes,
                        Message: $"索引目录存在但不是可读的 Lucene 索引（{indexPath}）⇒ 需手动重建；探针不会初始化 / 修复它。"));
                }
            }

            inventory = ReadInventory(scopeRoot, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Task.FromResult(new FullTextIndexIntegrityProbe(
                scopeKey,
                FullTextIndexIntegrityState.ManualRebuildRequired,
                IndexDirectoryExists: true,
                IndexedPathCount: null,
                IndexedDocumentCount: null,
                CheckedPathCount: 0,
                MismatchCount: 0,
                MismatchedPaths: Array.Empty<string>(),
                indexBytes,
                Message: $"索引目录存在但读不出（{ex.GetType().Name}: {ex.Message}）⇒ 无法证明新鲜 ⇒ 需手动重建。"));
        }

        // ③ 磁盘侧路径集合（与补偿扫描共用同一走查器与同一可索引判定）。
        var scan = MaintenanceCorpusScan.EnumerateFiles(scopeRoot, _options, cancellationToken);
        if (!scan.IsComplete)
        {
            return Task.FromResult(new FullTextIndexIntegrityProbe(
                scopeKey,
                FullTextIndexIntegrityState.ManualRebuildRequired,
                IndexDirectoryExists: true,
                IndexedPathCount: null,
                IndexedDocumentCount: null,
                CheckedPathCount: 0,
                MismatchCount: 0,
                MismatchedPaths: Array.Empty<string>(),
                indexBytes,
                Message: $"无法完整枚举磁盘路径集合（失败目录 {scan.FailedDirectoryCount} 个：{scan.FirstFailure}）"
                    + "⇒ 无法证明新鲜 ⇒ 需手动重建（探针只读）。"));
        }

        var diskPaths = new HashSet<string>(StringComparer.Ordinal);
        var diskDisplayPaths = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var file in scan.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!FileCandidateCollector.TryCollectCandidate(file, scopeRoot, _options, patterns: null, out _, out _))
                continue;

            var key = FullTextChangeCoalescer.NormalizeComparisonKey(file);
            diskPaths.Add(key);
            diskDisplayPaths[key] = file;
        }

        var indexPaths = new HashSet<string>(StringComparer.Ordinal);
        var documentCount = 0;
        foreach (var entry in inventory)
        {
            indexPaths.Add(entry.NormalizedPath);
            documentCount += entry.DocumentCount;
        }

        // CheckedPathCount 取**并集**：两侧的路径各被实际核对了一次，同一路径不重复计数。
        var checkedPaths = new HashSet<string>(indexPaths, StringComparer.Ordinal);
        foreach (var key in diskPaths)
            checkedPaths.Add(key);

        var mismatched = new List<string>();
        foreach (var entry in inventory)
        {
            if (!diskPaths.Contains(entry.NormalizedPath))
                mismatched.Add(entry.FullPath);
        }

        foreach (var key in diskPaths)
        {
            if (!indexPaths.Contains(key))
                mismatched.Add(diskDisplayPaths[key]);
        }

        var state = mismatched.Count == 0
            ? FullTextIndexIntegrityState.Healthy
            : FullTextIndexIntegrityState.Degraded;

        var message = state == FullTextIndexIntegrityState.Healthy
            ? $"索引与磁盘路径集合一致（索引 {indexPaths.Count} 路径 / 磁盘 {diskPaths.Count} 路径）。"
            : $"发现 {mismatched.Count} 条差异（索引 {indexPaths.Count} 路径 / 磁盘 {diskPaths.Count} 路径）⇒ Degraded。";

        return Task.FromResult(new FullTextIndexIntegrityProbe(
            scopeKey,
            state,
            IndexDirectoryExists: true,
            IndexedPathCount: indexPaths.Count,
            IndexedDocumentCount: documentCount,
            CheckedPathCount: checkedPaths.Count,
            MismatchCount: mismatched.Count,
            MismatchedPaths: mismatched,
            IndexBytes: indexBytes,
            Message: message));
    }

    /// <summary>索引目录字节（不可测 ⇒ <c>null</c>，绝不伪报 0）。</summary>
    private static long? MeasureDirectoryBytesOrNull(string indexPath)
    {
        try
        {
            return SupplyIndexDirectoryLayout.MeasureDirectoryBytes(indexPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

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

    /// <summary>
    /// 一次 writer 会话的可变状态：终态在业务分支 / <c>catch</c> 里写入，体积报告在 <c>finally</c> 收尾
    /// **之后**统一取 —— 回滚清理掉的段文件计数只有在回滚真的发生之后才是准的。
    /// </summary>
    private sealed class WriterSessionState
    {
        /// <summary>会话终态；<c>null</c> 表示「尚未定论」（仍在 try 块内）。</summary>
        internal WriterSessionOutcome? Outcome { get; set; }

        /// <summary>本会话是否走了 rollback（未提交）。</summary>
        internal bool RolledBack { get; set; }
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
