namespace PuddingRuntime.Services.Skills.Telemetry;

/// <summary>
/// 技能使用遥测的终态结果。
/// 为什么用显式枚举而不是 bool：后续打分器要按终态分桶统计（注入成功 / 读正文失败），
/// 显式枚举让 JSONL 对人和程序都可读，也不让一个 bool 冒充两种语义。
/// </summary>
public enum SkillUsageOutcome
{
    /// <summary>正文读取成功并已注入上下文。</summary>
    Injected,

    /// <summary>正文读取失败（文件缺失 / IO 异常 / 正文为空），本次未注入。</summary>
    ReadFailed,
}

/// <summary>
/// 单条技能使用遥测记录。
/// 语义约定：每个「被关键词命中」的技能在每次 EnforceAsync 中恰好产生一条记录，
/// Outcome 为该次命中的终态 —— 避免同一命中被双计数污染后续统计。
/// </summary>
public sealed record SkillUsageRecord
{
    public required string SkillId { get; init; }

    public required string AgentInstanceId { get; init; }

    /// <summary>本次实际命中的关键词。只记命中项，绝不落全量关键词空间。</summary>
    public IReadOnlyList<string> MatchedKeywords { get; init; } = [];

    /// <summary>是否注入成功。仅 Outcome=Injected 时为 true。</summary>
    public bool Injected { get; init; }

    /// <summary>注入正文的 UTF-8 字节数；未注入时为 0。供后续估算上下文成本。</summary>
    public int ContentBytes { get; init; }

    /// <summary>读正文失败原因；成功注入时为 null。</summary>
    public string? FailureReason { get; init; }

    public required DateTimeOffset OccurredAtUtc { get; init; }

    public SkillUsageOutcome Outcome { get; init; }
}
