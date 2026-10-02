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
    bool RootUsable = true);

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
            return EmptyResult(capabilityMissing: false, scanRun.ChangeSet.ScanComplete, scanRun.RootUsable);
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

        var extractPaths = firstPlan.Items
            .Where(item => item.Action == CodeSourceUpdateAction.Extract)
            .Select(item => item.FilePath)
            .ToArray();

        var extraction = await ExtractAsync(
                workspaceId, projectId, rootPath, options, extractPaths, scanRun.CapturedVersion, cancellationToken)
            .ConfigureAwait(false);

        var outcomes = extraction.Outcomes;

        // 稳定读取 + 旧符号差异 + 组装替换批次。
        var replacements = new List<CodeSourceFileReplacement>();
        var notApplicable = new List<CodeSourceEntry>();
        var failures = new List<(string FilePath, string Reason)>();
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
            var fileRead = await _fingerprintReader.ReadAsync(payload.FilePath, cancellationToken).ConfigureAwait(false);
            if (!fileRead.Stable)
            {
                failures.Add((payload.FilePath, fileRead.Reason ?? "unstable read"));
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
                BuildEntry(payload.FilePath, fileRead.Fingerprint!, scanRun.CapturedVersion, consumerInputs, payload.Language)));

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
        if (rebindPaths.Length > 0)
        {
            var entries = new List<CodeSourceEntry>();
            foreach (var path in rebindPaths)
            {
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
        if (deletePaths.Length > 0)
        {
            await _store.RemoveFilesAsync(workspaceId, projectId, deletePaths, cancellationToken).ConfigureAwait(false);
            await _maintenanceStore
                .SaveSourceManifestAsync(workspaceId, projectId, [], deletePaths, cancellationToken)
                .ConfigureAwait(false);
        }

        if (notApplicable.Count > 0)
        {
            await _maintenanceStore
                .SaveSourceManifestAsync(workspaceId, projectId, notApplicable, [], cancellationToken)
                .ConfigureAwait(false);
        }

        // ④ 账本：成功路径清退避，失败路径记退避（并计入未解决），只有真的提交成功才可能推进水位。
        foreach (var path in extractedPaths)
            ledger.ClearRetry(path);

        foreach (var path in deletePaths)
            ledger.ClearRetry(path);

        foreach (var (filePath, reason) in failures)
            ledger.RecordFailure(filePath, reason, now);

        var commitOutcome = ledger.CompleteCommit(new CodeSourceCommitCompletion(
            Epoch: snapshot.Ledger.Epoch,
            CapturedVersion: scanRun.CapturedVersion,
            ProviderAdvances: consumerInputs
                .Where(input => !string.IsNullOrWhiteSpace(input.ProviderId))
                .Select(input => new CodeSourceProviderAdvance(input.ProviderId, scanRun.CapturedVersion))
                .ToArray(),
            ScanStartedUtc: scanRun.ScanStartedUtc,
            ScanComplete: scanRun.ChangeSet.ScanComplete,
            UnresolvedPathCount: failures.Count));

        await _maintenanceStore.SaveMaintenanceLedgerAsync(workspaceId, projectId, ledger.Snapshot(), cancellationToken)
            .ConfigureAwait(false);

        var deferred = secondPlan.Items.Count(item => item.Action == CodeSourceUpdateAction.RetryLater);

        // 「未解决」= 本轮失败 + 仍在退避窗口内的路径：两者都让水位停住，必须如实报告。
        var backingOffSet = backingOff.ToHashSet(CodePathIdentity.PathComparer);
        var unresolved = failures.Count
            + secondPlan.Items.Count(item =>
                item.Action == CodeSourceUpdateAction.RetryLater && backingOffSet.Contains(item.FilePath));

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
            RootUsable: scanRun.RootUsable);
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

    private static CodeSourceMaintenanceRunResult EmptyResult(bool capabilityMissing, bool scanComplete, bool rootUsable) =>
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
            RootUsable: rootUsable);

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
