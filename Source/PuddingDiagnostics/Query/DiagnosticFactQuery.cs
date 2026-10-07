namespace PuddingCode.Diagnostics;

/// <summary>
/// 诊断事实的**查询契约**（可诊断基础设施设计 §7 Stage 4）。
/// <para>
/// 存在理由（现状实测缺口）：既有 <c>RuntimeActivityQuery</c> 只支持四个字段精确相等、
/// 无时间窗/无状态过滤/无分页，且 Sink 把 Limit 硬 clamp 到 500 而调用方索要 2000
/// —— 结果是「静默截断的数据被当成时间窗结论」。本契约把三件事变成显式事实：
/// ① 过滤维度；② 分页（limit/offset）；③ <see cref="DiagnosticFactPage.Truncated"/>
/// （**截断必须可见，不允许静默**）。
/// </para>
/// </summary>
public sealed record DiagnosticFactQuery
{
    public const int DefaultLimit = 200;
    public const int MaxLimit = 2000;

    public string? TraceId { get; init; }
    public string? SessionId { get; init; }
    public string? ExecutionId { get; init; }
    public string? TurnId { get; init; }
    public string? CorrelationId { get; init; }
    public string? Component { get; init; }
    public string? Operation { get; init; }
    public string? Status { get; init; }

    /// <summary>按稳定因果码过滤（事故查询的主力维度）。</summary>
    public string? CauseCode { get; init; }

    /// <summary>按因果类别过滤（transport / provider / local / vision）。</summary>
    public string? CauseCategory { get; init; }

    public DateTimeOffset? FromUtc { get; init; }
    public DateTimeOffset? ToUtc { get; init; }

    public int Limit { get; init; } = DefaultLimit;
    public int Offset { get; init; }

    /// <summary>时间排序方向；事故时间线用升序（默认降序＝最近优先）。</summary>
    public bool Ascending { get; init; }

    /// <summary>事故查询的规范形状：一轮 turn 的全部事实，时间升序。</summary>
    public static DiagnosticFactQuery ForTurn(string turnId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(turnId);
        return new DiagnosticFactQuery { TurnId = turnId, Ascending = true, Limit = MaxLimit };
    }

    /// <summary>把 Limit 收进合法区间（越界即 clamp，但结果里的 <c>Limit</c> 反映真实取值）。</summary>
    public DiagnosticFactQuery Normalized()
        => this with
        {
            Limit = Math.Clamp(Limit, 1, MaxLimit),
            Offset = Math.Max(Offset, 0),
        };
}

/// <summary>查询结果页。<see cref="Truncated"/> 为 true 表示「还可能更多」，必须显式呈现给消费者。</summary>
public sealed record DiagnosticFactPage
{
    public required IReadOnlyList<DiagnosticFact> Items { get; init; }
    public required bool Truncated { get; init; }
    public required int Limit { get; init; }
    public required int Offset { get; init; }

    public static DiagnosticFactPage Create(IReadOnlyList<DiagnosticFact> items, DiagnosticFactQuery query)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(query);

        var normalized = query.Normalized();
        return new DiagnosticFactPage
        {
            Items = items,
            Truncated = items.Count >= normalized.Limit,
            Limit = normalized.Limit,
            Offset = normalized.Offset,
        };
    }
}
