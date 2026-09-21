namespace PuddingCode.Operators;

/// <summary>
/// 打分投影结果：只表达「多少分」，<b>不表达通过与否</b>（通过与否由阈值策略决定）。
/// </summary>
/// <param name="Scale">分数刻度（对应算子声明的 <see cref="OperatorOutputShape.ScoreScale"/>）。</param>
/// <param name="Score">分数。降级场景下刻度为 <c>degraded</c>，分数无意义，必须看信封的 <c>ReasonCode</c>。</param>
/// <param name="Envelope">承载身份、证据、置信与降级原因的公共信封。</param>
public sealed record ScoreResult(string Scale, double Score, JudgementEnvelope Envelope);

/// <summary>
/// 判断投影结果：只表达三值结论与阈值，<b>不表达分类标签</b>。
/// </summary>
/// <param name="Outcome">三值结论（<see cref="JudgeOutcome.Abstain"/> 是合法结果，降级为 <see cref="JudgeOutcome.Unknown"/>）。</param>
/// <param name="Score">判定依据的分数；无分数为 null。</param>
/// <param name="Threshold">已应用的阈值；无阈值为 null。</param>
/// <param name="Confidence">置信度；未提供为 null。</param>
/// <param name="ConfidenceKind">置信度种类。</param>
/// <param name="Envelope">承载身份、证据与降级原因的公共信封。</param>
public sealed record JudgeResult(
    JudgeOutcome Outcome,
    double? Score,
    AppliedThreshold? Threshold,
    double? Confidence,
    ConfidenceKind ConfidenceKind,
    JudgementEnvelope Envelope);

/// <summary>
/// 分类投影结果：只表达标签与分布，<b>不表达通过与否</b>。
/// </summary>
/// <param name="PrimaryLabel">主标签；无有效分类（含降级）为 null。</param>
/// <param name="Distribution">逐标签分布；无有效分类为空字典（不得为 null，避免调用方空引用）。</param>
/// <param name="Envelope">承载身份、证据与降级原因的公共信封。</param>
public sealed record ClassificationResult(
    string? PrimaryLabel,
    IReadOnlyDictionary<string, double> Distribution,
    JudgementEnvelope Envelope);
