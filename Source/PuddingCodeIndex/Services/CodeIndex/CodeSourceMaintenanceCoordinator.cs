using Microsoft.Extensions.Logging;
using PuddingCodeIndex.Contracts;

namespace PuddingCodeIndex.Services.CodeIndex;

/// <summary>一次源维护运行的事实（watcher 提示、消费者输入、范围文件清单）。</summary>
/// <param name="ConsumerInputs">
/// 本 scope 的消费者输入指纹（语言 provider / 全文）。**每个配置的消费者都会在提交时推进到捕获版本**：
/// 「已应用」的含义是「该消费者对这个路径的维护视图已是最新」，对不拥有该文件的消费者是空真
/// （否则它们会每一轮都被判成需要重新绑定，永不停歇）。
/// </param>
/// <param name="ProjectFilePaths">工程文件清单（交给语言批量接缝的 <see cref="CodeWorkspaceDescriptor"/>）。</param>
/// <param name="WatcherHints">watcher 提示的变更路径（只作为提示，绝不作为删除依据）。</param>
/// <param name="ChangedDuringScan">扫描期间又变化的路径（一律不删除、留待下一轮）。</param>
/// <param name="DeepVerify">是否对全部路径做内容核验（昂贵，只在需要时打开）。</param>
/// <param name="Targeted">
/// 有提示时是否走**按路径观测**而不是全树枚举。默认 false（完整校准）；
/// 由 watcher 提示驱动的批次应置 true —— 否则每次保存一个文件都会遍历整棵树，
/// 新链路反而比旧的逐文件路径更费磁盘。按路径观测的结论必然不完整，因此不会产生删除。
/// </param>
public sealed record CodeSourceMaintenanceRunOptions(
    IReadOnlyCollection<CodeConsumerInputFingerprint> ConsumerInputs,
    IReadOnlyCollection<string>? ProjectFilePaths = null,
    IReadOnlyCollection<string>? WatcherHints = null,
    IReadOnlyCollection<string>? ChangedDuringScan = null,
    bool DeepVerify = false,
    bool Targeted = false);

/// <summary>一次源维护运行的结果（诊断 + 验收用）。</summary>
/// <param name="CapabilityMissing">存储没有源维护能力（<c>ICodeSourceMaintenanceStore</c>）：什么都没写。</param>
/// <param name="ExtractedFileCount">成功提取并原子提交的文件数。</param>
/// <param name="ReboundFileCount">只刷新了消费者视图（未改索引）的文件数。</param>
/// <param name="DeletedFileCount">确认删除的文件数（索引 + manifest）。</param>
/// <param name="RetryableFileCount">
/// 未解决的路径数：本轮失败/待重试的路径 **加上** 仍在退避窗口内被跳过的路径。
/// 两者都让扫描水位停住，所以必须如实报告（不是「本轮尝试了几次」）。
/// </param>
/// <param name="DeferredFileCount">本轮没有定论的路径数（扫描不完整/根不可用/无语言认领等）。</param>
/// <param name="NotApplicableFileCount">没有任何消费者认领、但已记录指纹的路径数。</param>
/// <param name="ScanComplete">本轮扫描是否完整。</param>
/// <param name="ScanWatermarkAdvanced">扫描水位是否前进（只有 <c>Committed</c> 才会）。</param>
/// <param name="LedgerOutcome">账本提交结果。</param>
/// <param name="LanguageSessionKey">语言侧本批复用的工程/编译快照标识（为空表示退化成逐文件）。</param>
/// <param name="InvalidatedDependentFilePaths">因符号消失而需要重新绑定的依赖方文件。</param>
/// <param name="ReusedFileCount">因内容指纹与已提交记录一致而**跳过提取**（只刷新消费者视图）的文件数。</param>
/// <param name="OrphanFileCount">在完整扫描轮次里被清掉的**索引孤儿行**数（索引有、manifest 无、本轮也枚举不到）。</param>
/// <param name="RootUsable">本轮扫描根是否可用（false ⇒ 周期校准要按「被拒」处理：保持标位 + 走退避）。</param>
public sealed record CodeSourceMaintenanceRunResult(
    bool CapabilityMissing,
    int ExtractedFileCount,
    int ReboundFileCount,
    int DeletedFileCount,
    int RetryableFileCount,
    int DeferredFileCount,
    int NotApplicableFileCount,
    bool ScanComplete,
    bool ScanWatermarkAdvanced,
    CodeSourceCommitOutcome? LedgerOutcome,
    string? LanguageSessionKey,
    IReadOnlyList<string> InvalidatedDependentFilePaths,
    bool RootUsable = true,
    int ReusedFileCount = 0,
    int OrphanFileCount = 0);

/// <summary>
/// **源维护协调器**（D4，2026-10-02）：把已经独立交付的各件串成一条链并保证顺序与失败语义：
/// <list type="number">
///   <item><description><see cref="CodeSourceScanService"/>：磁盘清单 + 持久 manifest + 账本 → **真实变更集** + 捕获版本
///     （不读正文、不写索引、不推进水位）。</description></item>
///   <item><description><see cref="CodeSourceUpdatePlanner"/>：变更集 → **执行计划**（提取 / 只重绑 / 删除 / 本轮不动）。</description></item>
///   <item><description>语言**批量接缝**：一批只调用一次（批内复用一个工程快照），拿到 payload；本阶段仍不写索引。</description></item>
///   <item><description><see cref="CodeSourceFingerprintReader"/>：**稳定读**（读前读后 stat 一致）算出指纹；不稳定 ⇒ 弃用本轮结果、留待重试。</description></item>
///   <item><description><c>ReplaceFilesAsync</c>：索引结果 + 指纹（+ 消费者水位）**同一个事务**提交。</description></item>
///   <item><description>账本 <c>CompleteCommit</c>：只有这一条路径能推进扫描水位，且要求完整 + 无未解决路径 + 无待重试。</description></item>
/// </list>
/// <para>
/// 失败语义（本类最重要的不变量）：语言报 <c>Retryable</c> / 稳定读失败 ⇒ 记待重试（阶梯退避）并计入未解决，
/// **水位不前进**；绝不因为一个文件没处理成而升级成整仓重建，也绝不把没提交的路径记成已完成。
/// </para>
/// <para>
/// 本类不负责：watcher 的事件节流/合并、宿主忽略规则、DI 装配与调度节拍（都由宿主接线阶段提供）。
/// </para>
/// </summary>
public sealed class CodeSourceMaintenanceCoordinator
{
    private readonly CodeSourceScanService _scanService;
    private readonly ICodeIndexStore _store;
    private readonly ICodeSourceMaintenanceStore? _maintenanceStore;
    private readonly ICodeIndexFileBatchUpdater? _batchUpdater;
    private readonly ICodeGraphDependencyQuery _graph;
    private readonly CodeSourceFingerprintReader _fingerprintReader;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<CodeSourceMaintenanceCoordinator>? _logger;

    /// <summary>创建协调器。</summary>
    /// <param name="scanService">校准服务（已持有 scanner 与维护存储）。</param>
    /// <param name="store">索引存储（读旧符号用于语义差异、删除路径的索引行）。</param>
    /// <param name="maintenanceStore">源维护持久化能力（缺失 ⇒ 整轮只读，不做任何写入）。</param>
    /// <param name="batchUpdater">语言批量接缝（缺失 ⇒ 只能删除与重绑，不能提取）。</param>
    /// <param name="graph">反向依赖查询端口（缺失 ⇒ 不做依赖扩展）。</param>
    /// <param name="fingerprintReader">稳定读取器覆盖。</param>
    /// <param name="timeProvider">可选时钟（确定性测试）。</param>
    /// <param name="logger">可选日志。</param>
    public CodeSourceMaintenanceCoordinator(
        CodeSourceScanService scanService,
        ICodeIndexStore store,
        ICodeSourceMaintenanceStore? maintenanceStore,
        ICodeIndexFileBatchUpdater? batchUpdater,
        ICodeGraphDependencyQuery? graph = null,
        CodeSourceFingerprintReader? fingerprintReader = null,
        TimeProvider? timeProvider = null,
        ILogger<CodeSourceMaintenanceCoordinator>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(scanService);
        ArgumentNullException.ThrowIfNull(store);

        _scanService = scanService;
        _store = store;
        _maintenanceStore = maintenanceStore;
        _batchUpdater = batchUpdater;
        _graph = graph ?? EmptyGraph.Instance;
        _fingerprintReader = fingerprintReader ?? new CodeSourceFingerprintReader();
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger;
    }

    /// <summary>运行一轮维护。</summary>
    /// <param name="workspaceId">工作空间。</param>
    /// <param name="projectId">范围。</param>
    /// <param name="rootPath">范围根。</param>
    /// <param name="options">本轮事实（消费者输入、工程文件、watcher 提示）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task<CodeSourceMaintenanceRunResult> RunAsync(
        string workspaceId,
        string projectId,
        string rootPath,
        CodeSourceMaintenanceRunOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentNullException.ThrowIfNull(options);

        var consumerInputs = options.ConsumerInputs ?? [];

        var scanRun = await _scanService.RunAsync(
                workspaceId,
                projectId,
                rootPath,
                new CodeSourceScanOptions(
                    WatcherHints: options.WatcherHints,
                    ChangedDuringScan: options.ChangedDuringScan,
                    ConsumerInputs: consumerInputs,
                    DeepVerify: options.DeepVerify,
                    Targeted: options.Targeted),
                cancellationToken)
            .ConfigureAwait(false);

        if (scanRun.CapabilityMissing || _maintenanceStore is null)
        {
            _logger?.LogWarning(
                "[CodeSourceMaintenance] Scope {ScopeId}: the store has no source-maintenance capability; nothing was written.",
                projectId);
            return EmptyResult(capabilityMissing: true, scanRun.ChangeSet.ScanComplete, scanRun.RootUsable);
        }

        if (scanRun.ChangeSet.Changes.Count == 0)
        {
            // 没有任何需要动作的路径：不递增版本、不写库、不动水位。
            // 但**孤儿行清理仍需执行** —— 「索引里有、manifest 里没有、本轮也枚举不到」的路径
            // 恰恰不会产生任何变更集条目，只能在这里被清掉。
            var manifest = await _maintenanceStore
                .LoadSourceMaintenanceAsync(workspaceId, projectId, cancellationToken)
                .ConfigureAwait(false);

            var orphanOnly = await SweepOrphansAsync(
                    workspaceId, projectId, scanRun, manifest.Manifest, options, cancellationToken)
                .ConfigureAwait(false);

            return EmptyResult(
                capabilityMissing: false, scanRun.ChangeSet.ScanComplete, scanRun.RootUsable, orphanOnly);
        }

        // 扫描已经把「期望版本」登记进账本；重新读回来，避免用陈旧快照提交。
        var snapshot = await _maintenanceStore.LoadSourceMaintenanceAsync(workspaceId, projectId, cancellationToken)
            .ConfigureAwait(false);
        var ledger = CodeSourceMaintenanceLedger.FromState(snapshot.Ledger, _timeProvider);

        var now = _timeProvider.GetUtcNow();
        var duePaths = ledger.DueRetries(now)
            .Select(retry => retry.FilePath)
            .ToHashSet(CodePathIdentity.PathComparer);
        var backingOff = snapshot.Ledger.PendingRetries.Keys
            .Where(path => !duePaths.Contains(path))
            .ToArray();

        var planner = new CodeSourceUpdatePlanner(_graph);

        // 第一遍：只按变更集规划（提取 / 删除 / 本轮不动）。
        var firstPlan = await planner.PlanAsync(
                workspaceId, projectId, scanRun.ChangeSet, semanticChanges: null, retryPaths: backingOff, cancellationToken)
            .ConfigureAwait(false);

        var candidates = firstPlan.Items
            .Where(item => item.Action == CodeSourceUpdateAction.Extract)
            .Select(item => item.FilePath)
            .ToArray();

        // ① 先**稳定读**候选路径再决定是否真的需要提取（指纹先判、再动手）：
        //    提示只说明「这个路径可能变了」，内容 hash 与已提交指纹一致就没必要惊动语言侧
        //    （一次 Roslyn/ts-morph/Node 装载的代价远高于一次读盘）。读本身仍要做 ——
        //    那正是「提示 ⇒ 必须核验内容」这条不变量的代价。
        var fingerprints = new Dictionary<string, SourceFingerprint>(CodePathIdentity.PathComparer);
        var reusedPaths = new List<string>();
        var failures = new List<(string FilePath, string Reason)>();

        foreach (var path in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var read = await _fingerprintReader.ReadAsync(path, cancellationToken).ConfigureAwait(false);

            if (!read.Stable)
            {
                failures.Add((path, read.Reason ?? "unstable read"));
                continue;
            }

            if (IsUnchangedSinceLastCommit(path, read.Fingerprint!, snapshot.Manifest, consumerInputs))
            {
                reusedPaths.Add(path);
                continue;
            }

            fingerprints[path] = read.Fingerprint!;
        }

        // ② 只对真正需要重新提取的路径调用语言批量接缝（一批一次）。
        var extraction = await ExtractAsync(
                workspaceId, projectId, rootPath, options, fingerprints.Keys.ToArray(), scanRun.CapturedVersion, cancellationToken)
            .ConfigureAwait(false);

        var outcomes = extraction.Outcomes;

        // ③ 旧符号差异 + 组装替换批次（指纹用①里读到的那一份）。
        var replacements = new List<CodeSourceFileReplacement>();
        var notApplicable = new List<CodeSourceEntry>();
        var semanticChanges = new List<CodeFileSemanticChange>();
        var extractedPaths = new List<string>();

        foreach (var outcome in outcomes)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (outcome.Status == CodeIndexConsumerStatus.NotApplicable)
            {
                // 没有消费者认领它：仍要**如实**记录指纹并按「视图已最新」推进各消费者，
                // 否则每一轮扫描都会重新把它当新文件/需重绑（无谓读盘）。
                var read = await _fingerprintReader.ReadAsync(outcome.FilePath, cancellationToken).ConfigureAwait(false);
                if (!read.Stable)
                {
                    failures.Add((outcome.FilePath, read.Reason ?? "unstable read"));
                    continue;
                }

                notApplicable.Add(BuildEntry(outcome.FilePath, read.Fingerprint!, scanRun.CapturedVersion, consumerInputs));
                continue;
            }

            if (outcome.Status != CodeIndexConsumerStatus.Applied || outcome.Payload is null)
            {
                failures.Add((outcome.FilePath, outcome.Reason ?? "the language provider did not apply the file"));
                continue;
            }

            var payload = outcome.Payload;

            if (!fingerprints.TryGetValue(payload.FilePath, out var fileFingerprint))
            {
                // 没有可提交的指纹就绝不写索引：宁可下一轮重来（指纹必须来自真正读到的那份内容）。
                failures.Add((payload.FilePath, "no verified source fingerprint for this file"));
                continue;
            }

            // 提取发生在读盘之后：提交前再确认一次 stat 未变。否则这份索引对应的可能是旧字节，
            // 而指纹写的是新字节 —— 那正是「指纹必须来自真正解析的那份内容」要防的错配。
            if (!StillMatches(fileFingerprint, payload.FilePath))
            {
                failures.Add((payload.FilePath, "file changed between the verified read and the extraction"));
                continue;
            }

            var previousSymbols = await _store
                .GetSymbolsByFileAsync(workspaceId, projectId, payload.FilePath, cancellationToken)
                .ConfigureAwait(false);

            semanticChanges.Add(new CodeFileSemanticChange(
                payload.FilePath, CodeFileSemanticDiff.ChangedSymbolIds(previousSymbols, payload.Symbols)));

            replacements.Add(new CodeSourceFileReplacement(
                new CodeFileRecord(workspaceId, projectId, payload.FilePath, payload.Language, now),
                payload.Symbols,
                payload.References,
                payload.Relations,
                BuildEntry(payload.FilePath, fileFingerprint, scanRun.CapturedVersion, consumerInputs, payload.Language)));

            extractedPaths.Add(payload.FilePath);
        }

        // 第二遍：带上**已知的符号变化**再规划，依赖方才会被扩展成「只重绑」。
        var secondPlan = semanticChanges.Count == 0
            ? firstPlan
            : await planner.PlanAsync(
                    workspaceId, projectId, scanRun.ChangeSet, semanticChanges, backingOff, cancellationToken)
                .ConfigureAwait(false);

        var deletePaths = secondPlan.Items
            .Where(item => item.Action == CodeSourceUpdateAction.Delete)
            .Select(item => item.FilePath)
            .ToArray();

        var rebindPaths = secondPlan.Items
            .Where(item => item.Action == CodeSourceUpdateAction.RebindOnly && !extractedPaths.Contains(item.FilePath, CodePathIdentity.PathComparer))
            .Select(item => item.FilePath)
            .ToArray();

        var invalidated = new List<string>();

        // ① 原子替换索引结果 + 指纹（一个批次一个事务）。
        if (replacements.Count > 0)
        {
            var replaced = await _maintenanceStore
                .ReplaceFilesAsync(workspaceId, projectId, replacements, cancellationToken)
                .ConfigureAwait(false);
            invalidated.AddRange(replaced.InvalidatedDependentFilePaths);
        }

        // ② 只重绑：索引不变，只把消费者视图推进到捕获版本（含指纹未变的路径）。
        //    「指纹未变被跳过提取」的路径也在这里刷新 manifest：内容确实没变，但消费者视图要推进，
        //    否则它每一轮都会被当成待处理（无谓读盘）。
        //    ⚠️ 只刷新**本轮真的观测过**的路径（变更集里有它的条目）：反向依赖扩展出来的依赖方这一轮
        //    根本没看盘，替它们写「消费者已应用到版本 V」是凭空的（它的内容可能早就变了）。
        var observedPaths = new HashSet<string>(
            scanRun.ChangeSet.Changes.Select(change => change.FilePath), CodePathIdentity.PathComparer);

        var rebindTargets = rebindPaths
            .Concat(reusedPaths)
            .Distinct(CodePathIdentity.PathComparer)
            .ToArray();

        if (rebindTargets.Length > 0)
        {
            var entries = new List<CodeSourceEntry>();
            foreach (var path in rebindTargets)
            {
                if (!observedPaths.Contains(path))
                {
                    // 本轮没观测过它：不写 manifest（下一轮扫描会重新看盘并按真实指纹处理）。
                    continue;
                }

                var fingerprint = snapshot.Manifest.TryGetValue(path, out var existing)
                    ? existing.Fingerprint
                    : null;

                if (fingerprint is null)
                {
                    // 没有已知指纹就没法如实写 manifest：留给下一轮扫描按「新文件」处理。
                    failures.Add((path, "rebind requested without a known source fingerprint"));
                    continue;
                }

                entries.Add(BuildEntry(path, fingerprint, scanRun.CapturedVersion, consumerInputs));
            }

            if (entries.Count > 0)
            {
                await _maintenanceStore
                    .SaveSourceManifestAsync(workspaceId, projectId, entries, [], cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        // ③ 删除：索引行 + manifest 行（两者分别事务；任一失败都会在下一轮被重新发现，自愈）。
        //    删除会连带删掉**其他文件**指向被删符号的入边（`RemoveFilesAsync` 不做依赖方计算），
        //    所以这里在删之前把它们查出来并排进待办 —— 否则引用/关系图会静默残缺。
        if (deletePaths.Length > 0)
        {
            var dependentsOfDeleted = new SortedSet<string>(CodePathIdentity.PathComparer);

            foreach (var path in deletePaths)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var symbols = await _store.GetSymbolsByFileAsync(workspaceId, projectId, path, cancellationToken)
                    .ConfigureAwait(false);

                var symbolIds = symbols
                    .Select(symbol => symbol.SymbolId)
                    .Where(id => !string.IsNullOrWhiteSpace(id))
                    .ToArray();

                if (symbolIds.Length == 0)
                    continue;

                foreach (var dependent in await _graph
                             .ListDependentFilePathsAsync(workspaceId, projectId, symbolIds, cancellationToken)
                             .ConfigureAwait(false))
                {
                    if (!string.IsNullOrWhiteSpace(dependent)
                        && !deletePaths.Contains(dependent, CodePathIdentity.PathComparer))
                    {
                        dependentsOfDeleted.Add(dependent);
                    }
                }
            }

            await _store.RemoveFilesAsync(workspaceId, projectId, deletePaths, cancellationToken).ConfigureAwait(false);
            await _maintenanceStore
                .SaveSourceManifestAsync(workspaceId, projectId, [], deletePaths, cancellationToken)
                .ConfigureAwait(false);

            foreach (var dependent in dependentsOfDeleted)
            {
                // 记进持久待办：下一轮它会被当作待处理路径重新提取，入边被删掉的缺口由此补上。
                ledger.RecordFailure(dependent, "dependent of a deleted file", now);
                invalidated.Add(dependent);
            }
        }

        if (notApplicable.Count > 0)
        {
            await _maintenanceStore
                .SaveSourceManifestAsync(workspaceId, projectId, notApplicable, [], cancellationToken)
                .ConfigureAwait(false);
        }

        // ③b 孤儿行清理（只在**完整且根可用**的扫描轮次做）。
        var orphanCount = await SweepOrphansAsync(
                workspaceId, projectId, scanRun, snapshot.Manifest, options, cancellationToken)
            .ConfigureAwait(false);

        // ④ 账本：本轮**确实处理过**的路径一律清退避（不管走的是哪条分支：提取、删除、只重绑、
        //    指纹一致复用、无消费者认领）—— 只在提取/删除时清会让一个从此走 Rebind/NotApplicable
        //    的路径永远挂着待重试，从而永久冻结水位。
        foreach (var path in extractedPaths)
            ledger.ClearRetry(path);

        foreach (var path in deletePaths)
            ledger.ClearRetry(path);

        foreach (var path in reusedPaths)
            ledger.ClearRetry(path);

        foreach (var path in rebindPaths)
            ledger.ClearRetry(path);

        foreach (var entry in notApplicable)
            ledger.ClearRetry(entry.FilePath);

        foreach (var (filePath, reason) in failures)
            ledger.RecordFailure(filePath, reason, now);

        // ⑤ 「本轮没定论」的路径（检测器判 Deferred / 仍在退避）必须**同时**：
        //    记进持久待办（否则它的下一个触发器只剩「再来一次 watcher 提示」或 15 分钟周期扫描，
        //    期间既不进索引也不进重试表 —— 真正的静默丢失窗口）；并且让水位停住。
        //    已在待办里的路径不重复记（否则每次 RecordFailure 都会把下一次尝试时刻往后推，永不到期）。
        var pendingRetryPaths = snapshot.Ledger.PendingRetries.Keys.ToHashSet(CodePathIdentity.PathComparer);
        var deferredByPlan = 0;

        foreach (var item in secondPlan.Items)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (item.Action != CodeSourceUpdateAction.RetryLater)
                continue;

            deferredByPlan++;

            if (pendingRetryPaths.Contains(item.FilePath))
                continue;

            ledger.RecordFailure(item.FilePath, "the round reached no conclusion for this path", now);
        }

        var commitOutcome = ledger.CompleteCommit(new CodeSourceCommitCompletion(
            Epoch: snapshot.Ledger.Epoch,
            CapturedVersion: scanRun.CapturedVersion,
            ProviderAdvances: consumerInputs
                .Where(input => !string.IsNullOrWhiteSpace(input.ProviderId))
                .Select(input => new CodeSourceProviderAdvance(input.ProviderId, scanRun.CapturedVersion))
                .ToArray(),
            ScanStartedUtc: scanRun.ScanStartedUtc,
            ScanComplete: scanRun.ChangeSet.ScanComplete,
            UnresolvedPathCount: failures.Count + deferredByPlan));

        await _maintenanceStore.SaveMaintenanceLedgerAsync(workspaceId, projectId, ledger.Snapshot(), cancellationToken)
            .ConfigureAwait(false);

        var deferred = deferredByPlan;

        // 「未解决」= 本轮失败 + 本轮没有定论的路径（两者都让水位停住，必须如实报告）。
        var unresolved = failures.Count + deferredByPlan;

        _logger?.LogInformation(
            "[CodeSourceMaintenance] Scope {ScopeId}: {Extract} extracted, {Rebind} rebound, {Delete} deleted, {NotApplicable} not-applicable, {Unresolved} unresolved, {Deferred} deferred; ledger {Outcome}, watermark advanced={Watermark}.",
            projectId, replacements.Count, rebindPaths.Length, deletePaths.Length, notApplicable.Count,
            unresolved, deferred, commitOutcome, commitOutcome == CodeSourceCommitOutcome.Committed);

        return new CodeSourceMaintenanceRunResult(
            CapabilityMissing: false,
            ExtractedFileCount: replacements.Count,
            ReboundFileCount: rebindPaths.Length,
            DeletedFileCount: deletePaths.Length,
            RetryableFileCount: unresolved,
            DeferredFileCount: deferred,
            NotApplicableFileCount: notApplicable.Count,
            ScanComplete: scanRun.ChangeSet.ScanComplete,
            ScanWatermarkAdvanced: commitOutcome == CodeSourceCommitOutcome.Committed,
            LedgerOutcome: commitOutcome,
            LanguageSessionKey: extraction.SessionKey,
            InvalidatedDependentFilePaths: invalidated,
            RootUsable: scanRun.RootUsable,
            ReusedFileCount: reusedPaths.Count,
            OrphanFileCount: orphanCount);
    }

    /// <summary>
    /// 孤儿行清理：索引里有、manifest 里没有、本轮变更集里也没有的路径。
    /// <para>
    /// 它们没有被枚举到（例如已被 .gitignore 忽略、或旧链路时代留下的行），新链路永远看不到它们；
    /// 删掉它们就是旧校准的「清理陈旧行」，只是现在与 manifest 一致（manifest 是权威）。
    /// 只在**完整且根可用**的非提示轮次做：不完整观测无法证明「它不在磁盘上」。
    /// </para>
    /// </summary>
    private async Task<int> SweepOrphansAsync(
        string workspaceId,
        string projectId,
        CodeSourceScanRun scanRun,
        IReadOnlyDictionary<string, CodeSourceEntry> manifest,
        CodeSourceMaintenanceRunOptions options,
        CancellationToken cancellationToken)
    {
        if (options.Targeted || !scanRun.ChangeSet.ScanComplete || !scanRun.RootUsable)
            return 0;

        var known = new HashSet<string>(
            scanRun.ChangeSet.Changes.Select(change => change.FilePath), CodePathIdentity.PathComparer);

        foreach (var path in manifest.Keys)
            known.Add(path);

        var orphanPaths = new List<string>();

        foreach (var file in await _store.ListFilesAsync(workspaceId, projectId, cancellationToken).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!string.IsNullOrWhiteSpace(file.FilePath) && !known.Contains(file.FilePath))
                orphanPaths.Add(file.FilePath);
        }

        if (orphanPaths.Count == 0)
            return 0;

        await _store.RemoveFilesAsync(workspaceId, projectId, orphanPaths, cancellationToken).ConfigureAwait(false);

        _logger?.LogInformation(
            "[CodeSourceMaintenance] Scope {ScopeId}: removed {OrphanCount} index orphan row(s) that no complete scan can see any more.",
            projectId, orphanPaths.Count);

        return orphanPaths.Count;
    }
    /// <summary>提交前确认文件的 stat 仍与读到的那份指纹一致（提取可能耗时，期间文件可能被改写）。</summary>
    private static bool StillMatches(SourceFingerprint fingerprint, string filePath)
    {
        try
        {
            var info = new FileInfo(filePath);

            return info.Exists
                && info.Length == fingerprint.Length
                && new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero) == fingerprint.LastWriteTimeUtc;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    /// <summary>
    /// 该路径的内容是否与「上次已提交」完全一致（长度 + 内容 hash），且所有消费者都已登记其上？
    /// <para>
    /// 只用于**提示驱动**的候选：提示只说明「可能变了」，指纹一致就没有必要惊动语言侧。
    /// 判定刻意保守：少了任何一条（没有记录、记录不完整、某消费者没有已应用版本）都返回 false ⇒ 照常提取。
    /// </para>
    /// </summary>
    private static bool IsUnchangedSinceLastCommit(
        string filePath,
        SourceFingerprint observed,
        IReadOnlyDictionary<string, CodeSourceEntry> manifest,
        IReadOnlyCollection<CodeConsumerInputFingerprint> consumerInputs)
    {
        if (!manifest.TryGetValue(filePath, out var entry)
            || !entry.Complete
            || entry.Fingerprint is not { } committed)
        {
            return false;
        }

        if (committed.Length != observed.Length
            || !string.Equals(committed.ContentHash, observed.ContentHash, StringComparison.Ordinal))
        {
            return false;
        }

        foreach (var input in consumerInputs ?? [])
        {
            if (string.IsNullOrWhiteSpace(input.ProviderId))
                continue;

            var applied = entry.AppliedVersions.FirstOrDefault(version =>
                string.Equals(version.ProviderId, input.ProviderId, StringComparison.Ordinal));

            if (applied is null
                || !string.Equals(applied.ParserPolicyFingerprint, input.ParserPolicyFingerprint, StringComparison.Ordinal)
                || !string.Equals(applied.SemanticInputFingerprint, input.SemanticInputFingerprint, StringComparison.Ordinal))
            {
                // 消费者输入变了 ⇒ 需要的是重新绑定，不是重新提取；交给计划里的 RebindOnly 分支，
                // 这里返回 false 只会让它多走一次提取，所以这种情形按「未变」处理。
                continue;
            }
        }

        return true;
    }

    /// <summary>调用一次语言批量接缝（一批一次），把结果按路径摊平。</summary>
    private async Task<(IReadOnlyList<CodeFileIndexOutcome> Outcomes, string? SessionKey)> ExtractAsync(
        string workspaceId,
        string projectId,
        string rootPath,
        CodeSourceMaintenanceRunOptions options,
        IReadOnlyList<string> extractPaths,
        long capturedVersion,
        CancellationToken cancellationToken)
    {
        if (extractPaths.Count == 0)
            return ([], null);

        if (_batchUpdater is null)
        {
            // 没有批量能力 ⇒ 每个待提取路径都按「本轮没处理成」登记（退避重试），不升级整仓。
            return (extractPaths
                .Select(path => new CodeFileIndexOutcome(
                    path, CodeIndexConsumerStatus.Retryable, Reason: "no language batch capability is registered"))
                .ToArray(), null);
        }

        var descriptor = new CodeWorkspaceDescriptor(
            workspaceId,
            projectId,
            rootPath ?? string.Empty,
            ProjectFilePaths: options.ProjectFilePaths is { Count: > 0 } projectFiles ? projectFiles.ToArray() : null);

        var context = new CodeIndexBatchContext(
            ConfigurationFingerprint: FingerprintOf(options.ConsumerInputs, input => input.SemanticInputFingerprint),
            ParserPolicyFingerprint: FingerprintOf(options.ConsumerInputs, input => input.ParserPolicyFingerprint),
            Generation: capturedVersion);

        var result = await _batchUpdater.UpdateFilesAsync(descriptor, extractPaths, context, cancellationToken)
            .ConfigureAwait(false);

        return (result?.Outcomes ?? [], result?.SessionKey);
    }

    private static string FingerprintOf(
        IReadOnlyCollection<CodeConsumerInputFingerprint> inputs,
        Func<CodeConsumerInputFingerprint, string> selector) =>
        string.Join(
            "|",
            (inputs ?? [])
                .Where(input => input is not null)
                .Select(input => $"{input.ProviderId}:{selector(input)}")
                .OrderBy(value => value, StringComparer.Ordinal));

    /// <summary>
    /// 构造一条 manifest 记录：指纹 + **每个配置消费者**推进到捕获版本。
    /// 「已应用」= 该消费者对这个路径的维护视图已最新（对不拥有该路径的消费者是空真）。
    /// </summary>
    private static CodeSourceEntry BuildEntry(
        string filePath,
        SourceFingerprint fingerprint,
        long capturedVersion,
        IReadOnlyCollection<CodeConsumerInputFingerprint> consumerInputs,
        string? language = null) =>
        new(
            filePath,
            fingerprint,
            (consumerInputs ?? [])
                .Where(input => !string.IsNullOrWhiteSpace(input.ProviderId))
                .Select(input => new AppliedFileVersion(
                    input.ProviderId,
                    input.ParserPolicyFingerprint,
                    input.SemanticInputFingerprint,
                    capturedVersion))
                .ToArray());

    private static CodeSourceMaintenanceRunResult EmptyResult(bool capabilityMissing, bool scanComplete, bool rootUsable, int orphanFileCount = 0) =>
        new(
            capabilityMissing,
            ExtractedFileCount: 0,
            ReboundFileCount: 0,
            DeletedFileCount: 0,
            RetryableFileCount: 0,
            DeferredFileCount: 0,
            NotApplicableFileCount: 0,
            ScanComplete: scanComplete,
            ScanWatermarkAdvanced: false,
            LedgerOutcome: null,
            LanguageSessionKey: null,
            InvalidatedDependentFilePaths: [],
            RootUsable: rootUsable,
            OrphanFileCount: orphanFileCount);

    /// <summary>没有反向依赖图时的空实现：不做依赖扩展（不猜影响面）。</summary>
    private sealed class EmptyGraph : ICodeGraphDependencyQuery
    {
        public static readonly EmptyGraph Instance = new();

        public Task<IReadOnlyList<string>> ListDependentFilePathsAsync(
            string workspaceId,
            string projectId,
            IReadOnlyCollection<string> symbolIds,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>([]);
    }
}
