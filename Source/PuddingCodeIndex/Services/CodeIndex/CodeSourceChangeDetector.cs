using PuddingCodeIndex.Contracts;

namespace PuddingCodeIndex.Services.CodeIndex;

/// <summary>
/// D2（2026-10-02 高磁盘读取修复）：把「三源提示 + 磁盘 stat + 持久 manifest + 消费者输入指纹」判定成
/// 一份**真实变更集**。
/// <para>
/// 这是纯逻辑：不读文件、不访问数据库、不看时钟。调用方提供全部事实（含需要核验路径的内容 hash），
/// 因此可以用小夹具精确锁定每条不变量，而不必依赖真实文件系统或真实索引。
/// </para>
/// <para>
/// 它取代的是被撤销的「按扩展名分类 → Ignore / Incremental / 全仓 Reconcile」补丁：文件属于哪种语言、
/// 配置变化影响哪些项目，都是**能力路由与依赖计划**（D3）的事，不是维护链路的分类。
/// </para>
/// <para><b>不变量（按 D2 §1–§7 逐条落地）</b></para>
/// <list type="number">
///   <item><description>watcher 只是提示：同路径重复提示合并；处理时以磁盘最终事实为准。</description></item>
///   <item><description>manifest 里没有的路径**即使 mtime 很旧也必须处理**；已知路径比较 mtime/length；
///     mtime 落在 <c>ScanStartedUtc - RacyOverlap</c> 之内也是候选，不能只按「大于上次索引时间」跳过。</description></item>
///   <item><description>stat 未变、没有提示、消费者输入未变且不在深度核验/racy 窗口时**直接复用已提交结果**：
///     不读正文、不打开语言 workspace。</description></item>
///   <item><description>mtime 不是绝对证明：提示与深度核验都必须核验内容；<c>RequiresContentHash</c> 为真时
///     调用方不得跳过 hash 就推进水位。</description></item>
///   <item><description>扫描期间又变化的路径<b>本轮不定论</b>（尤其不得删除）。</description></item>
///   <item><description>删除必须由**完整且根可用**的扫描得出：不完整扫描/根不可用只把记录标为未解决。</description></item>
///   <item><description>水位不掩盖失败：不完整或根不可用的轮次不推进扫描水位；watcher-only 批次也不推进。</description></item>
/// </list>
/// </summary>
public static class CodeSourceChangeDetector
{
    /// <summary>默认的 mtime 重叠窗口：覆盖同时间粒度（FAT/部分网络盘 2s）与轻微时钟回拨。</summary>
    public static readonly TimeSpan DefaultRacyOverlap = TimeSpan.FromSeconds(2);

    /// <summary>
    /// 判定一次扫描/一批提示。同一规范化路径在结果里最多出现一条。
    /// </summary>
    /// <param name="request">全部事实。</param>
    /// <returns>变更集与水位结论。</returns>
    public static CodeSourceChangeSet Detect(CodeSourceScanRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var comparer = CodePathIdentity.PathComparer;
        var overlap = request.RacyOverlap ?? DefaultRacyOverlap;
        if (overlap < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(request), overlap, "Racy overlap must not be negative.");

        var watcherHints = ToSet(request.WatcherHints, comparer);
        var changedDuringScan = ToSet(request.ChangedDuringScan, comparer);
        var observedHashes = ToDictionary(request.ObservedContentHashes, comparer);
        var consumerInputs = ToConsumerMap(request.ConsumerInputs);
        var manifest = ToManifestMap(request.Manifest, comparer);

        var changes = new List<CodeSourceChange>();
        var observedPaths = new HashSet<string>(comparer);

        var reuseCount = 0;
        var reindexCount = 0;
        var rebindCount = 0;
        var metadataRefreshCount = 0;
        var deferredCount = 0;

        foreach (var observation in request.Observations ?? [])
        {
            if (observation is null || string.IsNullOrWhiteSpace(observation.FilePath))
                continue;

            var path = observation.FilePath;
            if (!observedPaths.Add(path))
                continue; // 重复观察：同路径只判定一次（watcher 重复提示同样合并）

            manifest.TryGetValue(path, out var entry);

            var sources = CodeSourceChangeSource.MTimeScan;
            var reasons = new List<string>();
            var consumers = new List<string>();

            if (request.DeepVerify)
            {
                sources |= CodeSourceChangeSource.IntegrityCheck;
                reasons.Add(CodeSourceChangeReasons.DeepVerify);
            }

            var hinted = watcherHints.Contains(path);
            if (hinted)
            {
                sources |= CodeSourceChangeSource.Watcher;
                reasons.Add(CodeSourceChangeReasons.WatcherHint);
            }

            // 扫描期间又变了：最终状态未知 —— 不删除、不提交，下一轮重新核对。
            if (changedDuringScan.Contains(path))
            {
                reasons.Add(CodeSourceChangeReasons.ChangedDuringScan);
                changes.Add(new CodeSourceChange(
                    path,
                    CodeSourceAction.Deferred,
                    sources,
                    reasons,
                    [],
                    RequiresContentHash: false));
                deferredCount++;
                continue;
            }

            // 新路径（或首次没有基线）：即使 mtime 很旧也必须处理。
            if (entry is null)
            {
                reasons.Add(CodeSourceChangeReasons.NewFile);
                changes.Add(new CodeSourceChange(
                    path,
                    CodeSourceAction.ReindexContent,
                    sources,
                    reasons,
                    ConsumerIds(consumerInputs, applied: null),
                    RequiresContentHash: true));
                reindexCount++;
                continue;
            }

            var consumersToRefresh = FindConsumersToRefresh(entry, consumerInputs, reasons);
            foreach (var provider in consumersToRefresh)
            {
                sources |= CodeSourceChangeSource.Configuration;
                consumers.Add(provider);
            }

            var statKnown = observation.LastWriteTimeUtc is not null && observation.Length is not null;
            if (!statKnown)
            {
                // 读不到 stat 绝不等于未变化：必须核验内容。
                reasons.Add(CodeSourceChangeReasons.StatUnreadable);
            }
            else if (entry.Fingerprint is null
                || entry.Fingerprint.LastWriteTimeUtc != observation.LastWriteTimeUtc!.Value
                || entry.Fingerprint.Length != observation.Length!.Value)
            {
                reasons.Add(CodeSourceChangeReasons.StatChanged);
            }

            var racy = request.ScanStartedUtc is { } scanStarted
                && observation.LastWriteTimeUtc is { } mtime
                && mtime >= scanStarted - overlap;
            if (racy)
                reasons.Add(CodeSourceChangeReasons.RacyWindow);

            var statDiffers = reasons.Contains(CodeSourceChangeReasons.StatChanged)
                || reasons.Contains(CodeSourceChangeReasons.StatUnreadable);
            var needsContentVerification = statDiffers
                || hinted
                || racy
                || request.DeepVerify
                || entry.Fingerprint is null
                || !entry.Complete;

            if (!needsContentVerification && consumersToRefresh.Count == 0)
            {
                // ① stat 未变 ② 无提示 ③ 消费者输入未变 ④ 不在窗口/深度核验 → 直接复用已提交结果。
                reuseCount++;
                continue;
            }

            if (!needsContentVerification)
            {
                // 只有消费者输入变了：内容一致 ⇒ 重新绑定，不重新提取。
                changes.Add(new CodeSourceChange(
                    path,
                    CodeSourceAction.RebindConsumers,
                    sources,
                    reasons,
                    consumersToRefresh,
                    RequiresContentHash: false));
                rebindCount++;
                continue;
            }

            if (observedHashes.TryGetValue(path, out var observedFingerprint))
            {
                // 稳定读取校验：hash 必须来自与本次观察同一份 stat 的内容。
                // 不一致说明读取与写入竞争（读到了另一份版本）—— 绝不能提交，重新观察后下一轮再来。
                if (!statKnown
                    || observedFingerprint.LastWriteTimeUtc != observation.LastWriteTimeUtc!.Value
                    || observedFingerprint.Length != observation.Length!.Value)
                {
                    reasons.Add(CodeSourceChangeReasons.UnstableRead);
                    changes.Add(new CodeSourceChange(
                        path,
                        CodeSourceAction.Deferred,
                        sources,
                        reasons,
                        [],
                        RequiresContentHash: false));
                    deferredCount++;
                    continue;
                }

                var contentChanged = entry.Fingerprint is null
                    || !string.Equals(
                        entry.Fingerprint.ContentHash,
                        observedFingerprint.ContentHash,
                        StringComparison.Ordinal);

                if (!contentChanged)
                {
                    reasons.Add(CodeSourceChangeReasons.ContentUnchanged);

                    if (consumersToRefresh.Count > 0)
                    {
                        changes.Add(new CodeSourceChange(
                            path,
                            CodeSourceAction.RebindConsumers,
                            sources,
                            reasons,
                            consumersToRefresh,
                            RequiresContentHash: false));
                        rebindCount++;
                    }
                    else
                    {
                        changes.Add(new CodeSourceChange(
                            path,
                            CodeSourceAction.RefreshFingerprintOnly,
                            sources,
                            reasons,
                            [],
                            RequiresContentHash: false));
                        metadataRefreshCount++;
                    }

                    continue;
                }

                reasons.Add(CodeSourceChangeReasons.ContentChanged);
                changes.Add(new CodeSourceChange(
                    path,
                    CodeSourceAction.ReindexContent,
                    sources,
                    reasons,
                    ConsumerIds(consumerInputs, entry.AppliedVersions),
                    RequiresContentHash: true));
                reindexCount++;
                continue;
            }

            // 需要核验但还没有 hash：调用方必须先稳定读取 + hash，不得假定未变化。
            changes.Add(new CodeSourceChange(
                path,
                CodeSourceAction.ReindexContent,
                sources,
                reasons,
                ConsumerIds(consumerInputs, entry.AppliedVersions),
                RequiresContentHash: true));
            reindexCount++;
        }

        var (deleted, unresolved) = DetectMissing(
            manifest,
            observedPaths,
            request,
            changedDuringScan,
            changes);

        // 只有「完整枚举 + 根可用 + 确实扫描过」的轮次才算完整核对：watcher-only 批次没有枚举磁盘，
        // 既不能得出删除结论，也不能推进扫描水位。
        var scanComplete = request.ScopeComplete
            && request.RootUsable
            && request.ScanStartedUtc is not null;
        deferredCount += unresolved.Count;

        var nextWatermark = scanComplete && request.ScanStartedUtc is { } started
            ? started
            : request.PreviousWatermarkUtc;

        return new CodeSourceChangeSet(
            changes,
            scanComplete,
            nextWatermark,
            reuseCount,
            reindexCount,
            rebindCount,
            metadataRefreshCount,
            deleted.Count,
            deferredCount,
            unresolved.Count);
    }

    /// <summary>
    /// manifest 有、本次磁盘枚举未见：只有**完整且根可用**的扫描才能得出删除；
    /// 否则保留记录并标为未解决（下一轮重新核对）。
    /// </summary>
    private static (List<string> Deleted, List<string> Unresolved) DetectMissing(
        IReadOnlyDictionary<string, CodeSourceEntry> manifest,
        HashSet<string> observedPaths,
        CodeSourceScanRequest request,
        HashSet<string> changedDuringScan,
        List<CodeSourceChange> changes)
    {
        var deleted = new List<string>();
        var unresolved = new List<string>();
        var diskEnumerated = request.ScopeComplete && request.ScanStartedUtc is not null;

        foreach (var (path, _) in manifest)
        {
            if (string.IsNullOrWhiteSpace(path) || observedPaths.Contains(path))
                continue;

            if (changedDuringScan.Contains(path))
            {
                // 扫描期间刚被观察到变化：最终磁盘状态未知，绝不能当删除。
                unresolved.Add(path);
                changes.Add(new CodeSourceChange(
                    path,
                    CodeSourceAction.Deferred,
                    CodeSourceChangeSource.Watcher,
                    [CodeSourceChangeReasons.ChangedDuringScan],
                    [],
                    RequiresContentHash: false));
                continue;
            }

            if (!request.RootUsable)
            {
                unresolved.Add(path);
                changes.Add(new CodeSourceChange(
                    path,
                    CodeSourceAction.Deferred,
                    CodeSourceChangeSource.MTimeScan,
                    [CodeSourceChangeReasons.RootUnusable, CodeSourceChangeReasons.ScanIncomplete],
                    [],
                    RequiresContentHash: false));
                continue;
            }

            if (!diskEnumerated)
            {
                unresolved.Add(path);
                changes.Add(new CodeSourceChange(
                    path,
                    CodeSourceAction.Deferred,
                    CodeSourceChangeSource.MTimeScan,
                    [CodeSourceChangeReasons.ScanIncomplete],
                    [],
                    RequiresContentHash: false));
                continue;
            }

            deleted.Add(path);
            changes.Add(new CodeSourceChange(
                path,
                CodeSourceAction.Delete,
                CodeSourceChangeSource.MTimeScan,
                [CodeSourceChangeReasons.MissingOnDisk],
                [],
                RequiresContentHash: false));
        }

        return (deleted, unresolved);
    }

    /// <summary>
    /// 找出因**消费者输入**（解析器策略 / 语义输入）变化而需要推进的消费者。
    /// 内容一致时可以只重新绑定，不必重新提取正文。
    /// </summary>
    private static List<string> FindConsumersToRefresh(
        CodeSourceEntry entry,
        IReadOnlyDictionary<string, CodeConsumerInputFingerprint> consumerInputs,
        List<string> reasons)
    {
        var toRefresh = new List<string>();

        foreach (var (providerId, input) in consumerInputs)
        {
            var applied = FindApplied(entry, providerId);

            if (applied is null)
            {
                toRefresh.Add(providerId);
                AddReason(reasons, CodeSourceChangeReasons.ConsumerNeverApplied);
                continue;
            }

            if (!string.Equals(
                    applied.ParserPolicyFingerprint,
                    input.ParserPolicyFingerprint,
                    StringComparison.Ordinal))
            {
                toRefresh.Add(providerId);
                AddReason(reasons, CodeSourceChangeReasons.ParserPolicyChanged);
                continue;
            }

            if (!string.Equals(
                    applied.SemanticInputFingerprint,
                    input.SemanticInputFingerprint,
                    StringComparison.Ordinal))
            {
                toRefresh.Add(providerId);
                AddReason(reasons, CodeSourceChangeReasons.SemanticInputChanged);
            }
        }

        return toRefresh;
    }

    private static AppliedFileVersion? FindApplied(CodeSourceEntry entry, string providerId)
    {
        foreach (var applied in entry.AppliedVersions ?? [])
        {
            if (applied is not null
                && string.Equals(applied.ProviderId, providerId, StringComparison.Ordinal))
            {
                return applied;
            }
        }

        return null;
    }

    /// <summary>
    /// 需要推进的消费者：当前配置里存在的消费者并集（消费者被注销时不再要求它提交）。
    /// </summary>
    private static List<string> ConsumerIds(
        IReadOnlyDictionary<string, CodeConsumerInputFingerprint> consumerInputs,
        IReadOnlyList<AppliedFileVersion>? applied)
    {
        var ids = new List<string>();

        foreach (var (providerId, _) in consumerInputs)
            ids.Add(providerId);

        foreach (var version in applied ?? [])
        {
            if (version is not null && !ids.Contains(version.ProviderId, StringComparer.Ordinal))
                ids.Add(version.ProviderId);
        }

        return ids;
    }

    private static void AddReason(List<string> reasons, string reason)
    {
        if (!reasons.Contains(reason, StringComparer.Ordinal))
            reasons.Add(reason);
    }

    private static HashSet<string> ToSet(IReadOnlyCollection<string>? values, StringComparer comparer)
    {
        var set = new HashSet<string>(comparer);

        foreach (var value in values ?? [])
        {
            if (!string.IsNullOrWhiteSpace(value))
                set.Add(value);
        }

        return set;
    }

    private static Dictionary<string, SourceFingerprint> ToDictionary(
        IReadOnlyDictionary<string, SourceFingerprint>? values,
        StringComparer comparer)
    {
        var map = new Dictionary<string, SourceFingerprint>(comparer);

        foreach (var (path, fingerprint) in values ?? new Dictionary<string, SourceFingerprint>())
        {
            if (!string.IsNullOrWhiteSpace(path) && fingerprint is not null)
                map[path] = fingerprint;
        }

        return map;
    }

    private static Dictionary<string, CodeSourceEntry> ToManifestMap(
        IReadOnlyDictionary<string, CodeSourceEntry>? manifest,
        StringComparer comparer)
    {
        var map = new Dictionary<string, CodeSourceEntry>(comparer);

        foreach (var (path, entry) in manifest ?? new Dictionary<string, CodeSourceEntry>())
        {
            if (!string.IsNullOrWhiteSpace(path) && entry is not null)
                map[path] = entry;
        }

        return map;
    }

    private static Dictionary<string, CodeConsumerInputFingerprint> ToConsumerMap(
        IReadOnlyCollection<CodeConsumerInputFingerprint>? inputs)
    {
        var map = new Dictionary<string, CodeConsumerInputFingerprint>(StringComparer.Ordinal);

        foreach (var input in inputs ?? [])
        {
            if (input is not null && !string.IsNullOrWhiteSpace(input.ProviderId))
                map[input.ProviderId] = input;
        }

        return map;
    }
}
