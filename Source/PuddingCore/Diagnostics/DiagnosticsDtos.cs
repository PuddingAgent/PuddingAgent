namespace PuddingCode.Diagnostics;

/// <summary>事件统计 DTO — 与前端 EventStats 类型对齐。</summary>
public sealed record EventStatsDto
{
    public required int TotalCount { get; init; }
    public required IReadOnlyList<EventStatusCountDto> ByStatus { get; init; }
    public required IReadOnlyList<EventComponentCountDto> ByComponent { get; init; }
}

/// <summary>按状态的事件计数。</summary>
public sealed record EventStatusCountDto
{
    public required string Status { get; init; }
    public required int Count { get; init; }
}

/// <summary>按组件的事件计数。</summary>
public sealed record EventComponentCountDto
{
    public required string Component { get; init; }
    public required int Count { get; init; }
}
