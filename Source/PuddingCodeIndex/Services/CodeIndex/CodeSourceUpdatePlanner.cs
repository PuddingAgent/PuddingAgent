using Microsoft.Extensions.Logging;
using PuddingCodeIndex.Contracts;

namespace PuddingCodeIndex.Services.CodeIndex;

/// <summary>
/// D3（2026-10-02 高磁盘读取修复）：把「真实变更集」规划成**可执行的更新计划**。
/// <para>
/// 它回答的是「到底要动哪些文件、用什么动作」，而不是「哪些扩展名算代码」——
/// 能力路由属于语言消费者，这里只做三件事：
/// <list type="number">
///   <item><description>把变更动作映射成执行动作（提取 / 只重绑 / 删除 / 本轮不动）。</description></item>
///   <item><description>用**已知的符号变化**沿反向依赖扩展受影响文件（只重绑，不重新提取）：
///     没有提取就没有依据，绝不凭「文件被改了」猜测影响面。</description></item>
///   <item><description>有界：扩展条数触顶时置 <c>Truncated</c>，让调用方下一轮继续，
///     既不静默丢掉依赖方，也不把整个 project 拉进来。</description></item>
/// </list>
/// </para>
/// <para>
/// 退避中的路径不参与扩展也不出现在执行组里（它有自己的节奏）；<c>RefreshFingerprintOnly</c>
/// 也不进计划 —— 那只是写 manifest，索引没有要改的东西。
/// </para>
/// </summary>
public sealed class CodeSourceUpdatePlanner
{
    /// <summary>默认的反向依赖扩展上限（一个 scope 一轮最多新增多少条依赖方）。</summary>
    public const int DefaultMaxDependencyExpansions = 512;

    private readonly ICodeGraphDependencyQuery _graph;
    private readonly int _maxDependencyExpansions;
    private readonly ILogger<CodeSourceUpdatePlanner>? _logger;

    /// <summary>创建规划器。</summary>
    /// <param name="graph">反向依赖查询端口（存储实现）。</param>
    /// <param name="maxDependencyExpansions">反向依赖扩展上限覆盖。</param>
    /// <param name="logger">可选日志。</param>
    public CodeSourceUpdatePlanner(
        ICodeGraphDependencyQuery graph,
        int maxDependencyExpansions = DefaultMaxDependencyExpansions,
        ILogger<CodeSourceUpdatePlanner>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(graph);

        if (maxDependencyExpansions <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxDependencyExpansions), maxDependencyExpansions, "Expansion limit must be positive.");
        }

        _graph = graph;
        _maxDependencyExpansions = maxDependencyExpansions;
        _logger = logger;
    }

    /// <summary>
    /// 生成执行计划。
    /// </summary>
    /// <param name="workspaceId">工作空间。</param>
    /// <param name="projectId">范围。</param>
    /// <param name="changeSet">真实变更集（来自校准/判定）。</param>
    /// <param name="semanticChanges">
    /// 本次真正提取出的语义变化（符号消失/签名改变）。只有列在这里的路径才会触发依赖扩展。
    /// </param>
    /// <param name="retryPaths">正在退避重试窗口内的路径（本轮不重复尝试，也不参与扩展）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task<CodeSourceUpdatePlan> PlanAsync(
        string workspaceId,
        string projectId,
        CodeSourceChangeSet changeSet,
        IReadOnlyCollection<CodeFileSemanticChange>? semanticChanges = null,
        IReadOnlyCollection<string>? retryPaths = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentNullException.ThrowIfNull(changeSet);

        var comparer = CodePathIdentity.PathComparer;
        var retry = new HashSet<string>(comparer);
        foreach (var path in retryPaths ?? [])
        {
            if (!string.IsNullOrWhiteSpace(path))
                retry.Add(path);
        }

        var items = new Dictionary<string, CodeSourceUpdateItem>(comparer);
        var truncated = false;

        // ① 直接变化 → 执行动作。冲突时保留更「重」的动作（提取 > 删除 > 只重绑 > 本轮不动）：
        //    同一个路径不可能既提取又删除，但保险起见不让轻动作覆盖重动作。
        foreach (var change in changeSet.Changes ?? [])
        {
            if (change is null || string.IsNullOrWhiteSpace(change.FilePath))
                continue;

            var mapped = MapAction(change.Action);
            if (mapped is null)
            {
                // RefreshFingerprintOnly / Reuse：索引没有要改的东西（前者只写 manifest）。
                continue;
            }

            var action = mapped.Value;
            var reasons = new List<string>(change.Reasons ?? []);

            if (action != CodeSourceUpdateAction.Delete
                && action != CodeSourceUpdateAction.RetryLater
                && retry.Contains(change.FilePath))
            {
                // 退避中的路径：本轮不动（它自己的重试节奏决定何时再试）。
                action = CodeSourceUpdateAction.RetryLater;
                reasons.Add(CodeSourceUpdateReasons.RetryBackoffActive);
            }

            Upsert(items, new CodeSourceUpdateItem(
                change.FilePath,
                action,
                reasons,
                change.Consumers ?? [],
                DependencyDepth: 0));
        }

        // ② 反向依赖扩展：只沿「已知符号变化」走，因此不需要猜测、也不会指数放大。
        var expandedCount = 0;

        foreach (var semanticChange in semanticChanges ?? [])
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (semanticChange is null
                || string.IsNullOrWhiteSpace(semanticChange.FilePath)
                || semanticChange.ChangedSymbolIds is null
                || semanticChange.ChangedSymbolIds.Count == 0)
            {
                continue;
            }

            // 自己在退避中：它的依赖方也先不动，等它这轮真正落定。
            if (retry.Contains(semanticChange.FilePath))
                continue;

            var symbolIds = semanticChange.ChangedSymbolIds
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct(StringComparer.Ordinal)
                .ToArray();

            if (symbolIds.Length == 0)
                continue;

            var dependents = await _graph
                .ListDependentFilePathsAsync(workspaceId, projectId, symbolIds, cancellationToken)
                .ConfigureAwait(false);

            var providers = items.TryGetValue(semanticChange.FilePath, out var owner)
                ? owner.Providers
                : [];

            foreach (var dependent in dependents)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (string.IsNullOrWhiteSpace(dependent))
                    continue;

                if (retry.Contains(dependent))
                    continue;

                if (items.TryGetValue(dependent, out var existing))
                {
                    // 它本来就要提取/删除：提取会自带重新绑定，删除不需要绑定。
                    if (existing.Action is CodeSourceUpdateAction.Extract
                        or CodeSourceUpdateAction.Delete
                        or CodeSourceUpdateAction.RetryLater)
                    {
                        continue;
                    }

                    if (existing.DependencyDepth <= 1
                        && existing.Reasons.Contains(CodeSourceUpdateReasons.DependentOfChangedSymbols))
                    {
                        continue;
                    }

                    items[dependent] = existing with
                    {
                        DependencyDepth = 1,
                        Reasons = existing.Reasons
                            .Concat([CodeSourceUpdateReasons.DependentOfChangedSymbols])
                            .Distinct(StringComparer.Ordinal)
                            .ToArray(),
                    };
                    continue;
                }

                if (expandedCount >= _maxDependencyExpansions)
                {
                    truncated = true;
                    continue;
                }

                expandedCount++;
                items[dependent] = new CodeSourceUpdateItem(
                    dependent,
                    CodeSourceUpdateAction.RebindOnly,
                    [CodeSourceUpdateReasons.DependentOfChangedSymbols],
                    providers,
                    DependencyDepth: 1);
            }
        }

        var ordered = items.Values
            .OrderBy(item => ActionRank(item.Action))
            .ThenBy(item => item.FilePath, comparer)
            .ToArray();

        var plan = new CodeSourceUpdatePlan(
            ordered,
            ordered.Count(item => item.Action == CodeSourceUpdateAction.Extract),
            ordered.Count(item => item.Action == CodeSourceUpdateAction.RebindOnly),
            ordered.Count(item => item.Action == CodeSourceUpdateAction.Delete),
            ordered.Count(item => item.Action == CodeSourceUpdateAction.RetryLater),
            expandedCount,
            truncated);

        if (truncated)
        {
            _logger?.LogWarning(
                "[CodeSourceUpdate] Scope {ScopeId}: reverse-dependency expansion hit the ceiling ({Limit}); the remaining dependents must be planned again in the next round.",
                projectId, _maxDependencyExpansions);
        }

        _logger?.LogInformation(
            "[CodeSourceUpdate] Scope {ScopeId}: {Extract} extract, {Rebind} rebind-only ({Expanded} from dependencies), {Delete} delete, {Retry} deferred/retry.",
            projectId, plan.ExtractCount, plan.RebindCount, plan.DependencyExpansionCount, plan.DeleteCount, plan.RetryCount);

        return plan;
    }

    private static void Upsert(
        Dictionary<string, CodeSourceUpdateItem> items,
        CodeSourceUpdateItem candidate)
    {
        if (!items.TryGetValue(candidate.FilePath, out var existing))
        {
            items[candidate.FilePath] = candidate;
            return;
        }

        if (ActionRank(candidate.Action) <= ActionRank(existing.Action))
        {
            items[candidate.FilePath] = candidate with
            {
                Reasons = existing.Reasons
                    .Concat(candidate.Reasons)
                    .Distinct(StringComparer.Ordinal)
                    .ToArray(),
                Providers = existing.Providers
                    .Concat(candidate.Providers)
                    .Distinct(StringComparer.Ordinal)
                    .ToArray(),
            };
        }
    }

    /// <summary>
    /// 变更动作 → 执行动作。<c>null</c> 表示**不进计划**：<see cref="CodeSourceAction.RefreshFingerprintOnly"/>
    /// 只更新 manifest 里的源元数据（索引没有要改的东西），<see cref="CodeSourceAction.Reuse"/> 本就不在变更集里。
    /// </summary>
    private static CodeSourceUpdateAction? MapAction(CodeSourceAction action) => action switch
    {
        CodeSourceAction.ReindexContent => CodeSourceUpdateAction.Extract,
        CodeSourceAction.RebindConsumers => CodeSourceUpdateAction.RebindOnly,
        CodeSourceAction.Delete => CodeSourceUpdateAction.Delete,
        CodeSourceAction.Deferred => CodeSourceUpdateAction.RetryLater,
        _ => null,
    };

    /// <summary>动作的排序权重：提取 → 只重绑 → 删除 → 本轮不动。</summary>
    private static int ActionRank(CodeSourceUpdateAction action) => action switch
    {
        CodeSourceUpdateAction.Extract => 0,
        CodeSourceUpdateAction.RebindOnly => 1,
        CodeSourceUpdateAction.Delete => 2,
        _ => 3,
    };
}
