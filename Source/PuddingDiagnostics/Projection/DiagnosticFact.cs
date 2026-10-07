namespace PuddingCode.Diagnostics;

/// <summary>事实种类（与 <c>RuntimeActivityStatuses</c> 解耦：这里只关心它在事故时间线上的角色）。</summary>
public static class DiagnosticFactKinds
{
    /// <summary>一次尝试（started/retried/failed/succeeded 都可）。</summary>
    public const string Attempt = "attempt";

    /// <summary>终态（turn 级别失败结论）。</summary>
    public const string Terminal = "terminal";
}

/// <summary>
/// 事故视图的**输入**（可诊断基础设施设计 §5.5）。
/// <para>
/// 这是刻意收窄的自我描述形状：叶子组件不认识 <c>RuntimeActivity</c>／EF 实体，
/// 由消费方在边界处适配（Stage 4）。字段只保留回答 Q1–Q5 所必需的量。
/// </para>
/// </summary>
public sealed record DiagnosticFact
{
    public required string Kind { get; init; }
    public required string Status { get; init; }
    public required DateTimeOffset OccurredAtUtc { get; init; }

    public string? TraceId { get; init; }
    public string? TurnId { get; init; }
    public string? ErrorId { get; init; }
    public string? Component { get; init; }
    public string? Operation { get; init; }

    public int? Attempt { get; init; }
    public int? MaxRetries { get; init; }

    /// <summary>稳定因果码（分类器输出）。</summary>
    public string? CauseCode { get; init; }

    /// <summary>阶段线路值（<see cref="DiagnosticPhases"/>）。</summary>
    public string? Phase { get; init; }

    public long? DurationMs { get; init; }
    public long? RequestBytes { get; init; }
    public string? UserMessage { get; init; }
    public string? RemediationHint { get; init; }
    public IReadOnlyDictionary<string, string>? Evidence { get; init; }
}

/// <summary>时间线上的一次尝试。</summary>
public sealed record IncidentAttempt(
    int? Attempt,
    string Status,
    string? CauseCode,
    string? Phase,
    long? DurationMs,
    long? RequestBytes,
    DateTimeOffset OccurredAtUtc);

/// <summary>一次事故的完整答案形状（Q1–Q5）。</summary>
public sealed record IncidentView
{
    public required string TurnId { get; init; }
    public required string TerminalStatus { get; init; }
    public string? TraceId { get; init; }
    public string? ErrorId { get; init; }
    public string? RootCauseCode { get; init; }
    public string? RootCauseMessage { get; init; }
    public string? RemediationHint { get; init; }
    public DiagnosticPhaseKind Phase { get; init; }
    public int? MaxRetries { get; init; }

    /// <summary>按发生时间升序的尝试明细。</summary>
    public required IReadOnlyList<IncidentAttempt> Attempts { get; init; }

    /// <summary>一句话中文结论（含数字：尝试次数 / 阶段 / 体积 / 耗时）。</summary>
    public required string Conclusion { get; init; }

    /// <summary>证据定位（errorId、trace、事实行）——人据此回到日志/库。</summary>
    public required IReadOnlyList<string> EvidenceLinks { get; init; }
}

/// <summary>纯格式化助手（人类可读结论用；不依赖文化设置，便于断言）。</summary>
public static class DiagnosticFormatting
{
    public static string Bytes(long? bytes)
    {
        if (bytes is null or < 0)
            return "未知";

        double value = bytes.Value;
        string[] units = ["B", "KiB", "MiB", "GiB"];
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0
            ? $"{bytes.Value} B"
            : $"{value.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)} {units[unit]}";
    }

    public static string Duration(long? milliseconds)
        => milliseconds is null ? "未知" : $"{milliseconds.Value} ms";
}
