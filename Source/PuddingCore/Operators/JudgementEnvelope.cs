using System.Globalization;

namespace PuddingCode.Operators;

/// <summary>
/// 判定信封：三类投影（打分 / 判断 / 分类）共用的唯一结果载体。
/// <para>
/// 设计意图：同一信封可被三种投影共享，<b>输出语义由投影类型强制分离</b>
/// （见 <see cref="ScoreResult"/> / <see cref="JudgeResult"/> / <see cref="ClassificationResult"/>）。
/// </para>
/// <para>
/// 身份与可复现性字段缺一不可：缺了就无法回答「这次判定是谁做的、用哪版指令、对哪份输入、按哪个阈值」。
/// 全部字段只读（<c>init</c>）：产出后即定稿，调用方与审计只消费、不修改。
/// </para>
/// </summary>
public sealed record JudgementEnvelope
{
    /// <summary>当前信封模式版本；新增字段时递增（消费方按此判断可用字段集）。</summary>
    public const int CurrentSchemaVersion = 1;

    // —— 身份与可复现性（缺一不可）——

    /// <summary>判定 id（确定性推导，见 <see cref="OperatorScope.JudgementId"/>）。</summary>
    public required string JudgementId { get; init; }

    /// <summary>场景键。</summary>
    public required string SceneKey { get; init; }

    /// <summary>输入身份指纹。</summary>
    public required string InputDigest { get; init; }

    /// <summary>算子稳定标识。</summary>
    public required string OperatorId { get; init; }

    /// <summary>算子版本；未知为 null。</summary>
    public string? OperatorVersion { get; init; }

    /// <summary>产出本次判断的模型标识；无模型（纯规则）为 null。</summary>
    public string? ModelId { get; init; }

    /// <summary>指令版本；无指令为 null。</summary>
    public int? InstructionVersion { get; init; }

    /// <summary>已应用的阈值快照；无阈值为 null。</summary>
    public AppliedThreshold? Threshold { get; init; }

    /// <summary>本信封的模式版本。</summary>
    public required int SchemaVersion { get; init; }

    // —— 结果（按投影填充）——

    /// <summary>分数（量纲见 <see cref="ScoreScale"/>）；未打分时为 null。</summary>
    public double? Score { get; init; }

    /// <summary>分数刻度（例 <c>0..1</c>）；未打分时为 null。</summary>
    public string? ScoreScale { get; init; }

    /// <summary>判断结论；非判断类投影为 null（降级同样可见为 <see cref="JudgeOutcome.Unknown"/>）。</summary>
    public JudgeOutcome? Outcome { get; init; }

    /// <summary>分类主标签；非分类类投影为 null。</summary>
    public string? PrimaryLabel { get; init; }

    /// <summary>逐标签分布（键为标签名）；非分类类投影为 null。</summary>
    public IReadOnlyDictionary<string, double>? LabelDistribution { get; init; }

    // —— 置信（必须带种类）——

    /// <summary>置信度；未提供为 null。</summary>
    public double? Confidence { get; init; }

    /// <summary>置信度种类：<b>强制</b>区分模型自报与已校准。</summary>
    public required ConfidenceKind ConfidenceKind { get; init; }

    // —— 证据与成本 ——

    /// <summary>人类可读理由；降级场景也必须写明原因，禁止留空。</summary>
    public required string Reason { get; init; }

    /// <summary>稳定原因码（降级场景必填；成功裁决可为 null）。</summary>
    public string? ReasonCode { get; init; }

    /// <summary>可回溯证据列表；无证据为空列表。</summary>
    public IReadOnlyList<JudgementEvidence> Evidence { get; init; } = [];

    /// <summary>本次判定耗时（毫秒）。</summary>
    public double? LatencyMs { get; init; }

    /// <summary>本次判定是否命中缓存（模型调用被短路）。</summary>
    public bool Cached { get; init; }

    // —— 溯源 ——

    /// <summary>溯源身份；上下文未提供为 null。</summary>
    public OperatorIdentity? Identity { get; init; }

    /// <summary>所依据的原始事件 id 列表；无则 null。</summary>
    public IReadOnlyList<string>? SourceEventIds { get; init; }

    /// <summary>所依据的源码 / 快照指纹；无则 null。</summary>
    public string? SourceSha { get; init; }

    /// <summary>产出时间（UTC）。</summary>
    public required DateTimeOffset CreatedAtUtc { get; init; }
}

/// <summary>
/// 信封草稿：场景算子只填「结果语义」，身份 / 版本 / 阈值 / 计时 / 缓存标记由
/// <see cref="OperatorScope.BuildEnvelope"/> 统一补齐——避免每个场景重复拼装 20+ 字段而漏字段。
/// </summary>
public sealed record EnvelopeDraft
{
    /// <summary>人类可读理由（必填）。</summary>
    public required string Reason { get; init; }

    /// <summary>稳定原因码。</summary>
    public string? ReasonCode { get; init; }

    /// <summary>分数。</summary>
    public double? Score { get; init; }

    /// <summary>分数刻度。</summary>
    public string? ScoreScale { get; init; }

    /// <summary>判断结论。</summary>
    public JudgeOutcome? Outcome { get; init; }

    /// <summary>分类主标签。</summary>
    public string? PrimaryLabel { get; init; }

    /// <summary>逐标签分布。</summary>
    public IReadOnlyDictionary<string, double>? LabelDistribution { get; init; }

    /// <summary>置信度。</summary>
    public double? Confidence { get; init; }

    /// <summary>置信度种类（默认 <see cref="ConfidenceKind.Unknown"/>，不得默认按已校准处理）。</summary>
    public ConfidenceKind ConfidenceKind { get; init; } = ConfidenceKind.Unknown;

    /// <summary>可回溯证据。</summary>
    public IReadOnlyList<JudgementEvidence> Evidence { get; init; } = [];

    /// <summary>所依据的原始事件 id 列表。</summary>
    public IReadOnlyList<string>? SourceEventIds { get; init; }

    /// <summary>所依据的源码 / 快照指纹。</summary>
    public string? SourceSha { get; init; }

    /// <summary>模型标识（覆盖 <see cref="OperatorScope.ModelJudgement"/> 里的模型 id）。</summary>
    public string? ModelId { get; init; }

    /// <summary>算子版本（覆盖作用域默认值）。</summary>
    public string? OperatorVersion { get; init; }
}
