namespace PuddingCode.Operators;

/// <summary>
/// 单个判断问题（对应支持 choice 的模型的一次受限作答）。
/// </summary>
public sealed record JudgementQuestion
{
    /// <summary>问题键（模型作答按此键回填 <see cref="ModelAnswer.QuestionKey"/>）。</summary>
    public required string Key { get; init; }

    /// <summary>问题正文。</summary>
    public required string Prompt { get; init; }

    /// <summary>受限选项；为空表示自由作答。</summary>
    public IReadOnlyList<string>? Choices { get; init; }
}

/// <summary>
/// 一次模型判定请求（场景侧已投影好的结构化输入，不含消息全文、不含思维链）。
/// </summary>
public sealed record ModelJudgementRequest
{
    /// <summary>场景键。</summary>
    public required string SceneKey { get; init; }

    /// <summary>指令正文。</summary>
    public required string Instruction { get; init; }

    /// <summary>指令版本。</summary>
    public required int InstructionVersion { get; init; }

    /// <summary>问题集（至少一个）。</summary>
    public required IReadOnlyList<JudgementQuestion> Questions { get; init; }

    /// <summary>场景侧投影好的结构化输入片段。</summary>
    public required string RenderedInput { get; init; }

    /// <summary>输入身份指纹（与缓存键一致）。</summary>
    public required string InputDigest { get; init; }
}

/// <summary>
/// 模型对单个问题的作答。
/// </summary>
public sealed record ModelAnswer
{
    /// <summary>回答对应的问题键。</summary>
    public required string QuestionKey { get; init; }

    /// <summary>模型给出的分数；作答为非分数形态时为 null。</summary>
    public double? Score { get; init; }

    /// <summary>模型选择的选项；自由作答时为 null。</summary>
    public string? Choice { get; init; }

    /// <summary>逐选项概率（如模型提供）；无则 null。</summary>
    public IReadOnlyDictionary<string, double>? ChoiceProbabilities { get; init; }

    /// <summary>模型自报置信度（<b>未经校准</b>）。</summary>
    public double? Confidence { get; init; }
}

/// <summary>
/// 一次模型判定的完整结果。
/// </summary>
public sealed record ModelJudgement
{
    /// <summary>逐问题作答。</summary>
    public required IReadOnlyList<ModelAnswer> Answers { get; init; }

    /// <summary>产出本判断的模型标识。</summary>
    public required string ModelId { get; init; }

    /// <summary>模型调用耗时（毫秒）。</summary>
    public required int LatencyMs { get; init; }

    /// <summary>原始响应指纹（用于复现比对）；无则 null。</summary>
    public string? RawDigest { get; init; }
}
