namespace PuddingCodeIndex.Contracts.Retrieval;

/// <summary>
/// 检索请求（ADR-089 §2 的合同草案 + §2.2 双预算 + §2.3 过滤面）。
/// <para>
/// 缺省值即"Auto + 默认页 + 默认预算"：<b>不传 intent 就是 Auto</b>，
/// 由引擎承担意图推断与推荐排序（"避免让 Agent 打转"）。
/// </para>
/// <para>
/// 所有字段只读（无 <c>with</c> 可改）⇒ 校验无法被绕过。
/// </para>
/// </summary>
public sealed record RetrievalRequest
{
    /// <summary>锚点允许的关系面意图（与 <see cref="RetrievalIntentPolicy.RelationFilter"/> 同源）。</summary>
    private const string RelationFaceIntents = "Callers / Callees / References / Implementation / Impact";

    /// <summary>构造请求。</summary>
    /// <param name="query">查询词（不得为空 —— 空查询是无界噪音）。</param>
    /// <param name="scopeId">检索范围标识（不得为空）。</param>
    /// <param name="intent">单次检索偏好；缺省 <see cref="RetrievalIntent.Auto"/>。</param>
    /// <param name="filter">正交过滤面（可省略）。</param>
    /// <param name="pageSize">页大小（条数预算）。</param>
    /// <param name="cursor">分页游标（来自上一页的 <see cref="RetrievalResult.NextCursor"/>）。</param>
    /// <param name="maxBytes">字节预算。</param>
    /// <param name="maxTokens">token 预算（优先预算）。</param>
    /// <param name="anchor">
    /// 已解析的符号锚点（来自 <c>symbol_ref</c>）。给出锚点时 <paramref name="query"/> 可为空，
    /// 且 <paramref name="intent"/> 必须是关系面意图（见类注释）。
    /// </param>
    public RetrievalRequest(
        string query,
        string scopeId,
        RetrievalIntent intent = RetrievalIntent.Auto,
        RetrievalFilter? filter = null,
        int pageSize = RetrievalBudget.DefaultPageSize,
        string? cursor = null,
        int maxBytes = RetrievalBudget.DefaultMaxBytes,
        int maxTokens = RetrievalBudget.DefaultMaxTokens,
        SymbolAnchor? anchor = null)
    {
        // 空查询 = 无界噪音；唯一例外是给了锚点：此时"对谁做"由身份而非名字决定。
        if (string.IsNullOrWhiteSpace(query) && anchor is null)
            throw new ArgumentException("查询词不得为空。", nameof(query));

        // 锚点只在关系面有意义：其余意图走名字/路径/注释匹配，锚点会被静默忽略 ——
        // 那等于让"我明确要这个符号"退化成"给我一堆同名或同路径的东西"。
        if (anchor is not null && RetrievalIntentPolicy.RelationFilter(intent) is null)
            throw new ArgumentException(
                $"带符号锚点时意图必须是关系面（{RelationFaceIntents}）；当前 {intent} 需要查询词。",
                nameof(intent));

        if (string.IsNullOrWhiteSpace(scopeId))
            throw new ArgumentException("必须给出检索范围标识。", nameof(scopeId));

        if (pageSize < 1 || pageSize > RetrievalBudget.MaxPageSize)
            throw new ArgumentOutOfRangeException(
                nameof(pageSize),
                pageSize,
                $"页大小必须在 1..{RetrievalBudget.MaxPageSize} 之间（单次返回必须有界）。");

        if (maxBytes < 1)
            throw new ArgumentOutOfRangeException(nameof(maxBytes), maxBytes, "字节预算必须 ≥ 1。");

        if (maxTokens < 1)
            throw new ArgumentOutOfRangeException(nameof(maxTokens), maxTokens, "token 预算必须 ≥ 1。");

        Query = query?.Trim() ?? string.Empty;
        ScopeId = scopeId.Trim();
        Intent = intent;
        Filter = filter;
        Anchor = anchor;
        PageSize = pageSize;
        Cursor = string.IsNullOrWhiteSpace(cursor) ? null : cursor.Trim();
        MaxBytes = maxBytes;
        MaxTokens = maxTokens;
    }

    /// <summary>查询词。</summary>
    public string Query { get; }

    /// <summary>检索范围标识。</summary>
    public string ScopeId { get; }

    /// <summary>单次检索偏好（缺省即 <see cref="RetrievalIntent.Auto"/>）。</summary>
    public RetrievalIntent Intent { get; }

    /// <summary>正交过滤面。</summary>
    public RetrievalFilter? Filter { get; }

    /// <summary>页大小。</summary>
    public int PageSize { get; }

    /// <summary>分页游标。</summary>
    public string? Cursor { get; }

    /// <summary>字节预算。</summary>
    public int MaxBytes { get; }

    /// <summary>token 预算（优先）。</summary>
    public int MaxTokens { get; }

    /// <summary>已解析的符号锚点；缺省 <c>null</c> 表示按查询词检索。</summary>
    public SymbolAnchor? Anchor { get; }

    /// <summary>是否给了符号锚点（此时引擎不得再用名字找目标）。</summary>
    public bool HasAnchor => Anchor is not null;

    /// <summary>本次检索的主体（有锚点即锚点符号，否则查询词）—— 只用于回显与诊断。</summary>
    public string Subject => Anchor?.SymbolId ?? Query;

    /// <summary>是否未指定意图（引擎自行推断）。</summary>
    public bool IsAuto => RetrievalIntentPolicy.IsAuto(Intent);

    /// <summary>过滤面回显（"我替你加了哪些面"，§2.3）。</summary>
    public string FilterEcho => Filter?.Describe() ?? "no-filter";

    /// <summary>该请求是否带任何过滤面（决定"过滤性空 ≠ 真空"的裁决，§8.9）。</summary>
    public bool HasFilterConstraints => Filter?.HasAnyConstraint == true;
}
