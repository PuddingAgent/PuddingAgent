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
    /// <summary>构造请求。</summary>
    /// <param name="query">查询词（不得为空 —— 空查询是无界噪音）。</param>
    /// <param name="scopeId">检索范围标识（不得为空）。</param>
    /// <param name="intent">单次检索偏好；缺省 <see cref="RetrievalIntent.Auto"/>。</param>
    /// <param name="filter">正交过滤面（可省略）。</param>
    /// <param name="pageSize">页大小（条数预算）。</param>
    /// <param name="cursor">分页游标（来自上一页的 <see cref="RetrievalResult.NextCursor"/>）。</param>
    /// <param name="maxBytes">字节预算。</param>
    /// <param name="maxTokens">token 预算（优先预算）。</param>
    public RetrievalRequest(
        string query,
        string scopeId,
        RetrievalIntent intent = RetrievalIntent.Auto,
        RetrievalFilter? filter = null,
        int pageSize = RetrievalBudget.DefaultPageSize,
        string? cursor = null,
        int maxBytes = RetrievalBudget.DefaultMaxBytes,
        int maxTokens = RetrievalBudget.DefaultMaxTokens)
    {
        if (string.IsNullOrWhiteSpace(query))
            throw new ArgumentException("查询词不得为空。", nameof(query));

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

        Query = query.Trim();
        ScopeId = scopeId.Trim();
        Intent = intent;
        Filter = filter;
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

    /// <summary>是否未指定意图（引擎自行推断）。</summary>
    public bool IsAuto => RetrievalIntentPolicy.IsAuto(Intent);

    /// <summary>过滤面回显（"我替你加了哪些面"，§2.3）。</summary>
    public string FilterEcho => Filter?.Describe() ?? "no-filter";

    /// <summary>该请求是否带任何过滤面（决定"过滤性空 ≠ 真空"的裁决，§8.9）。</summary>
    public bool HasFilterConstraints => Filter?.HasAnyConstraint == true;
}
