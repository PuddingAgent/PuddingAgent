namespace PuddingCodeIndex.Contracts.Retrieval;

/// <summary>
/// 检索结果（ADR-089 §2.1 / §2.2 / §2.4 / §2.3 的落地形状）：分层命中 + 双视图 + 降级标注 +
/// 真实总数 + 分页 + 落盘 + 分布 + 诊断 + 跨 scope 警告。
/// <para>
/// <b>本类型的唯一构造路径是 <see cref="Create"/> 与 <see cref="Empty"/></b>（无公开构造函数），
/// 所有跨字段不变量在构造期 fail-closed 检查。于是这些"空洞否定"在结构上不可表示：
/// ① <c>hits=0 但无 reason</c>；② <c>degraded=true 但无原因</c>；
/// ③ <c>NextSteps &gt; 1</c>；④ <c>只截断、不给总量/分布/落盘</c>。
/// </para>
/// <para>
/// <b>注意</b>：本类型的取值相等不比较集合字段（<see cref="Hits"/> 等按引用比较）——
/// 断言请逐项进行，或比较 <see cref="Describe"/> 之外的显式字段。
/// </para>
/// </summary>
public sealed record RetrievalResult
{
    private readonly RetrievalHit[] _hits;
    private readonly RetrievalNextStep[] _nextSteps;
    private readonly string[] _appliedFilters;
    private readonly ScopeOverlapWarning[] _scopeOverlapWarnings;

    private RetrievalResult(
        RetrievalRequest request,
        IReadOnlyList<RetrievalHit> hits,
        int totalCount,
        string indexVersion,
        RetrievalEmptyReason? emptyReason,
        IReadOnlyList<RetrievalNextStep>? nextSteps,
        bool degraded,
        string? degradedReason,
        RetrievalOverflow? overflow,
        RetrievalDistribution? distribution,
        QueryDiagnostics? diagnostics,
        IReadOnlyList<string>? appliedFilters,
        IReadOnlyList<ScopeOverlapWarning>? scopeOverlapWarnings,
        string? nextCursor)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(hits);

        if (string.IsNullOrWhiteSpace(indexVersion))
            throw new RetrievalContractViolationException(
                "结果必须带上索引版本 —— 确定性（同一 query + scope + 索引版本 ⇒ 同一结果）要求它（§2.1 第 5 条）。");

        var hitList = hits.ToArray();

        if (totalCount < 0)
            throw new RetrievalContractViolationException($"真实总数不得为负（收到 {totalCount}）。");

        if (hitList.Length > request.PageSize)
            throw new RetrievalContractViolationException(
                $"返回条数 {hitList.Length} 超过页大小 {request.PageSize} —— 单次返回必须有界（§8.7）。");

        if (totalCount < hitList.Length)
            throw new RetrievalContractViolationException(
                $"真实总数 {totalCount} 不得小于返回条数 {hitList.Length}（TurnCount 语义见 §2.2 A）。");

        RetrievalHitOrdering.EnsureOrdered(hitList, request.Intent);
        RetrievalHitOrdering.EnsureUniqueIdentities(hitList);

        var isTruncated = totalCount > hitList.Length;
        if (isTruncated)
        {
            if (overflow is null)
                throw new RetrievalContractViolationException(
                    "部分返回必须落盘并给出 Overflow（路径 + 条数 + 预算口径）—— 禁止静默截断（§2.2 / §8.7）。");

            if (string.IsNullOrWhiteSpace(nextCursor))
                throw new RetrievalContractViolationException(
                    "部分返回必须给出分页游标 —— 确定性排序是游标的前提（§2.2 A / §2.1 第 5 条）。");

            if (distribution is null || distribution.IsEmpty)
                throw new RetrievalContractViolationException(
                    "部分返回必须附带非空分布摘要 —— 否则 Agent 只看到 N 条，无法判断该收窄还是该接受（§2.2 A）。");
        }
        else
        {
            if (overflow is not null)
                throw new RetrievalContractViolationException("完整返回不得携带 Overflow（没有截断就没有落盘）。");

            if (!string.IsNullOrWhiteSpace(nextCursor))
                throw new RetrievalContractViolationException("完整返回不得携带分页游标。");
        }

        if (totalCount == 0)
        {
            if (emptyReason is null)
                throw new RetrievalContractViolationException(
                    "0 命中必须给出 RetrievalEmptyReason —— 空洞否定不可表示（§2.1 第 3 条 / §8.6）。");
        }
        else if (emptyReason is not null)
        {
            throw new RetrievalContractViolationException("有命中却报了空结果原因 —— 事实自相矛盾。");
        }

        if (degraded)
        {
            if (string.IsNullOrWhiteSpace(degradedReason))
                throw new RetrievalContractViolationException(
                    "降级（结构化面 → 文本面）必须标注原因（§2.1 第 3/4 条 / §8.1）。");
        }
        else if (!string.IsNullOrWhiteSpace(degradedReason))
        {
            throw new RetrievalContractViolationException("未降级却给了降级原因 —— 事实自相矛盾。");
        }

        var resolvedDiagnostics = diagnostics ?? RetrievalOverloadDiagnostics.Evaluate(
            RetrievalOverloadFacts.FromHits(totalCount, request.PageSize, request.Intent, hitList));

        var stepList = nextSteps?.ToArray() ?? [];
        if (stepList.Length > 1)
            throw new RetrievalContractViolationException(
                $"至多给一条下一步（收到 {stepList.Length} 条）—— 给菜单等于让 Agent 打转（§2.1 第 7 条 / §8.6）。");

        var mustSuggest = emptyReason is not null || isTruncated || resolvedDiagnostics.IsOverloaded;
        if (mustSuggest && stepList.Length == 0)
            throw new RetrievalContractViolationException(
                "空结果 / 部分返回 / 过载都必须给出恰好一条可执行的下一步（§2.1 第 3、7 条 / §8.6、§8.8）。");

        if (stepList.Length == 1)
            ValidateStep(stepList[0], request, emptyReason, isTruncated, resolvedDiagnostics);

        var bytesUsed = 0;
        var tokensUsed = 0;
        foreach (var hit in hitList)
        {
            bytesUsed += hit.EstimatedBytes;
            tokensUsed += hit.EstimatedTokens;
        }

        if (bytesUsed > request.MaxBytes)
            throw new RetrievalContractViolationException(
                $"返回页字节估算 {bytesUsed} 超过字节预算 {request.MaxBytes}（超预算必须落盘并分页，§2.2 A）。");

        if (tokensUsed > request.MaxTokens)
            throw new RetrievalContractViolationException(
                $"返回页 token 估算 {tokensUsed} 超过 token 预算 {request.MaxTokens}（token 预算是优先预算，§2.2 A）。");

        Request = request;
        _hits = hitList;
        TotalCount = totalCount;
        IndexVersion = indexVersion.Trim();
        EmptyReason = emptyReason;
        _nextSteps = stepList;
        Degraded = degraded;
        DegradedReason = string.IsNullOrWhiteSpace(degradedReason) ? null : degradedReason.Trim();
        Overflow = overflow;
        Distribution = distribution ?? RetrievalDistribution.FromHits(hitList);
        Diagnostics = resolvedDiagnostics;
        _appliedFilters = NormalizeAppliedFilters(appliedFilters);
        _scopeOverlapWarnings = scopeOverlapWarnings?.ToArray() ?? [];
        NextCursor = string.IsNullOrWhiteSpace(nextCursor) ? null : nextCursor.Trim();
        BytesUsed = bytesUsed;
        TokensUsed = tokensUsed;
    }

    /// <summary>产生本结果的请求（页大小 / 预算 / intent / 过滤面的权威来源）。</summary>
    public RetrievalRequest Request { get; }

    /// <summary>本页命中（已按 intent 的显式全序排列，且身份唯一）。</summary>
    public IReadOnlyList<RetrievalHit> Hits => _hits;

    /// <summary>符号视图（同一符号只出现一次）。</summary>
    public IReadOnlyList<RetrievalSymbolView> Symbols => RetrievalHitViews.Symbols(_hits);

    /// <summary>文件视图。</summary>
    public IReadOnlyList<RetrievalFileView> Files => RetrievalHitViews.Files(_hits);

    /// <summary><b>真实命中总数</b>（去重后；不是返回条数）。</summary>
    public int TotalCount { get; }

    /// <summary>本页是否已按显式全序排列（恒为 true；游标前提，见 §2.1 第 5 条）。</summary>
    public bool IsOrdered => true;

    /// <summary>是否还有下一页（= 是否截断）。</summary>
    public bool HasMore => IsTruncated;

    /// <summary>是否截断（= 是否落盘）。</summary>
    public bool IsTruncated => Overflow is not null;

    /// <summary>下一页游标。</summary>
    public string? NextCursor { get; }

    /// <summary>落盘信息（超预算时必填）。</summary>
    public RetrievalOverflow? Overflow { get; }

    /// <summary>分布摘要（截断时必为非空）。</summary>
    public RetrievalDistribution Distribution { get; }

    /// <summary>查询诊断（特异度 + 过载原因）。</summary>
    public QueryDiagnostics Diagnostics { get; }

    /// <summary>是否降级为文本面结果。</summary>
    public bool Degraded { get; }

    /// <summary>降级原因（降级时必填）。</summary>
    public string? DegradedReason { get; }

    /// <summary>索引版本（确定性的一部分）。</summary>
    public string IndexVersion { get; }

    /// <summary>本次查询的意图（缺省即 Auto）。</summary>
    public RetrievalIntent Intent => Request.Intent;

    /// <summary>空结果原因（0 命中时必填）。</summary>
    public RetrievalEmptyReason? EmptyReason { get; }

    /// <summary>至多一条下一步建议。</summary>
    public IReadOnlyList<RetrievalNextStep> NextSteps => _nextSteps;

    /// <summary>唯一一条下一步（没有则 null）。</summary>
    public RetrievalNextStep? NextStep => _nextSteps.Length == 0 ? null : _nextSteps[0];

    /// <summary>本次调用实际生效的过滤面回显（"我替你加了哪些面"，§2.3）。</summary>
    public IReadOnlyList<string> AppliedFilters => _appliedFilters;

    /// <summary>重叠/嵌套 scope 警告（§2.4）。</summary>
    public IReadOnlyList<ScopeOverlapWarning> ScopeOverlapWarnings => _scopeOverlapWarnings;

    /// <summary>返回页的估算字节数。</summary>
    public int BytesUsed { get; }

    /// <summary>返回页的估算 token 数。</summary>
    public int TokensUsed { get; }

    /// <summary>
    /// 构造结果（唯一通用入口）。所有跨字段不变量在此 fail-closed。
    /// </summary>
    /// <param name="request">产生本结果的请求。</param>
    /// <param name="hits">本页命中（必须已按 intent 全序排列、身份唯一）。</param>
    /// <param name="totalCount">去重后的真实命中总数。</param>
    /// <param name="indexVersion">索引版本。</param>
    /// <param name="emptyReason">0 命中时的原因（必填）。</param>
    /// <param name="nextSteps">下一步建议（至多一条）。</param>
    /// <param name="degraded">是否降级为文本面。</param>
    /// <param name="degradedReason">降级原因（降级时必填）。</param>
    /// <param name="overflow">落盘信息（截断时必填）。</param>
    /// <param name="distribution">分布摘要（截断时必填非空）。</param>
    /// <param name="diagnostics">诊断（缺省由去重后的事实派生）。</param>
    /// <param name="appliedFilters">实际生效的过滤面回显。</param>
    /// <param name="scopeOverlapWarnings">重叠 scope 警告。</param>
    /// <param name="nextCursor">下一页游标（截断时必填）。</param>
    public static RetrievalResult Create(
        RetrievalRequest request,
        IReadOnlyList<RetrievalHit> hits,
        int totalCount,
        string indexVersion,
        RetrievalEmptyReason? emptyReason = null,
        IReadOnlyList<RetrievalNextStep>? nextSteps = null,
        bool degraded = false,
        string? degradedReason = null,
        RetrievalOverflow? overflow = null,
        RetrievalDistribution? distribution = null,
        QueryDiagnostics? diagnostics = null,
        IReadOnlyList<string>? appliedFilters = null,
        IReadOnlyList<ScopeOverlapWarning>? scopeOverlapWarnings = null,
        string? nextCursor = null) => new(
            request,
            hits,
            totalCount,
            indexVersion,
            emptyReason,
            nextSteps,
            degraded,
            degradedReason,
            overflow,
            distribution,
            diagnostics,
            appliedFilters,
            scopeOverlapWarnings,
            nextCursor);

    /// <summary>
    /// 构造空结果：<paramref name="reason"/> 是<b>必填</b>参数（没有"没有原因的 0 命中"这条路径）。
    /// </summary>
    public static RetrievalResult Empty(
        RetrievalRequest request,
        RetrievalEmptyReason reason,
        RetrievalNextStep nextStep,
        string indexVersion,
        bool degraded = false,
        string? degradedReason = null,
        IReadOnlyList<string>? appliedFilters = null,
        IReadOnlyList<ScopeOverlapWarning>? scopeOverlapWarnings = null,
        QueryDiagnostics? diagnostics = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(nextStep);

        return new RetrievalResult(
            request,
            [],
            0,
            indexVersion,
            reason,
            [nextStep],
            degraded,
            degradedReason,
            null,
            RetrievalDistribution.Empty,
            diagnostics,
            appliedFilters,
            scopeOverlapWarnings,
            null);
    }

    private static void ValidateStep(
        RetrievalNextStep step,
        RetrievalRequest request,
        RetrievalEmptyReason? emptyReason,
        bool isTruncated,
        QueryDiagnostics diagnostics)
    {
        if (emptyReason is { } reason)
        {
            switch (reason)
            {
                case RetrievalEmptyReason.NotIndexed:
                case RetrievalEmptyReason.IndexCorrupted:
                    Require(
                        step.Kind == RetrievalNextStepKind.RebuildIndex,
                        "索引缺失/损坏时应建议重建或修复索引（§2.1 第 3 条 ①）。");
                    break;

                case RetrievalEmptyReason.LanguageUnsupported:
                case RetrievalEmptyReason.IntentUnsupported:
                    Require(
                        step.Kind == RetrievalNextStepKind.UseAlternativeFace,
                        "语言/意图不支持时**必须**给出替代方案，不得报成\"没有\"（§5.2 / §8.3）。");
                    break;

                case RetrievalEmptyReason.FilteredOut:
                {
                    Require(
                        step.Kind == RetrievalNextStepKind.RelaxFilter,
                        "过滤性空必须建议放宽某个过滤面（§8.9）。");

                    var filter = request.Filter;
                    Require(
                        filter is not null && filter.HasAnyConstraint,
                        "报 FilteredOut 的请求必须确实带了过滤面 —— 否则就是真空（§8.9）。");

                    Require(
                        step.Relaxing is { } facet && filter!.ConstrainedFacets().Contains(facet),
                        "放宽的面必须是请求里实际生效的面（不能凭空建议一个没开的面）。");
                    break;
                }

                case RetrievalEmptyReason.GenuinelyAbsent:
                    Require(
                        !request.HasFilterConstraints,
                        "只有在没有任何过滤面时才可以报\"确实不存在\" —— 否则就是过滤性空（§8.9）。");
                    Require(
                        step.Kind is RetrievalNextStepKind.UseAlternativeFace or RetrievalNextStepKind.AcceptAbsence,
                        "确实不存在时只能接受缺失或换面交叉验证。");
                    break;

                default:
                    break;
            }
        }

        if (isTruncated || diagnostics.IsOverloaded)
        {
            Require(
                step.Kind == RetrievalNextStepKind.NarrowQuery,
                "超预算 / 过载必须给出**收窄**建议（目录限制 / 文件类型 / 组合条件 / 显式 intent）（§2.2 C / §8.8）。");
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new RetrievalContractViolationException(message);
    }

    private static string[] NormalizeAppliedFilters(IReadOnlyList<string>? appliedFilters)
    {
        if (appliedFilters is null || appliedFilters.Count == 0)
            return [];

        var set = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var raw in appliedFilters)
        {
            if (string.IsNullOrWhiteSpace(raw))
                throw new RetrievalContractViolationException("生效过滤面的回显项不得为空。");

            set.Add(raw.Trim());
        }

        return [.. set];
    }
}
