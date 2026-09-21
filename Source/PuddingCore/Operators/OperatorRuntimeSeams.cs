using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace PuddingCode.Operators;

/// <summary>
/// 算子指令：模型无关的指令文本 + 版本 + 问题集。
/// <para>版本参与缓存键与复现，因此<b>必填</b>；问题集为空表示本算子不调用模型（纯确定性算子）。</para>
/// </summary>
public sealed record OperatorInstruction
{
    /// <summary>指令正文。</summary>
    public required string Text { get; init; }

    /// <summary>指令版本（变更指令文本必须递增）。</summary>
    public required int Version { get; init; }

    /// <summary>问题集；为空表示不调用模型。</summary>
    public IReadOnlyList<JudgementQuestion> Questions { get; init; } = [];
}

/// <summary>
/// 算子输出形状：声明本算子产出什么（标签集 / 分数刻度），供投影与消费方对齐。
/// </summary>
public sealed record OperatorOutputShape
{
    /// <summary>标签集合（分类类算子）；非分类类为 null。</summary>
    public IReadOnlyList<string>? Labels { get; init; }

    /// <summary>分数刻度（例 <c>0..1</c>）；非打分类为 null。</summary>
    public string? ScoreScale { get; init; }
}

/// <summary>
/// 稳定原因码（降级 / 弃权的机器可读标记）。
/// <para>消费方按原因码判定降级，<b>不得</b>解析人类可读的 <c>Reason</c> 文本。</para>
/// </summary>
public static class OperatorReasonCodes
{
    /// <summary>未产生降级（成功裁决）。</summary>
    public const string Ok = "ok";

    /// <summary>调用方取消：按降级契约返回结论。</summary>
    public const string Cancelled = "operator_cancelled";

    /// <summary>算子超时：按降级契约返回结论。</summary>
    public const string Timeout = "operator_timeout";

    /// <summary>模型不可用（未配置 / 连续失败）：按降级契约返回结论。</summary>
    public const string ModelUnavailable = "operator_model_unavailable";

    /// <summary>算子内核异常（含返回 null）：按降级契约返回结论。</summary>
    public const string CoreFailure = "operator_core_failure";

    /// <summary>阈值策略配置非法：拒绝裁决，不得静默产生全 Yes / 全 No / 全 Abstain。</summary>
    public const string InvalidThreshold = "operator_invalid_threshold";

    /// <summary>输入上下文缺失或类型不匹配：按降级契约返回结论。</summary>
    public const string ContextMismatch = "operator_context_mismatch";
}

/// <summary>
/// 降级结论：稳定原因码 + 人类可读理由。
/// </summary>
/// <param name="ReasonCode">稳定原因码（见 <see cref="OperatorReasonCodes"/>）。</param>
/// <param name="Reason">人类可读理由（禁止留空）。</param>
public sealed record OperatorDegradation(string ReasonCode, string Reason);

/// <summary>
/// 算子健康采样（S1a 只定义接缝，不接健康面实现）。
/// </summary>
public sealed record OperatorHealthSample
{
    /// <summary>算子稳定标识。</summary>
    public required string OperatorId { get; init; }

    /// <summary>场景键。</summary>
    public required string SceneKey { get; init; }

    /// <summary>本次判定是否成功（降级为 false）。</summary>
    public required bool Succeeded { get; init; }

    /// <summary>本次判定是否命中缓存。</summary>
    public required bool Cached { get; init; }

    /// <summary>降级原因码；成功为 null。</summary>
    public string? ReasonCode { get; init; }

    /// <summary>耗时（毫秒）。</summary>
    public double? LatencyMs { get; init; }

    /// <summary>采样时间（UTC）。</summary>
    public required DateTimeOffset ObservedAtUtc { get; init; }
}

/// <summary>
/// 健康上报端口（接缝）。
/// <para><b>旁挂</b>语义：上报失败只影响观测，<b>不得</b>改变已定裁决（基类捕获并只记 Warning）。</para>
/// </summary>
public interface IOperatorHealthObserver
{
    /// <summary>上报一次采样；实现抛异常不得影响裁决。</summary>
    void Report(OperatorHealthSample sample);
}

/// <summary>
/// 审计旁挂记录。
/// <para>既有契约是「<b>裁决先于留痕</b>」：审计写入失败不改变已定裁决，只记 Warning。</para>
/// </summary>
public sealed record OperatorAuditRecord
{
    /// <summary>算子稳定标识。</summary>
    public required string OperatorId { get; init; }

    /// <summary>场景键。</summary>
    public required string SceneKey { get; init; }

    /// <summary>判定 id。</summary>
    public required string JudgementId { get; init; }

    /// <summary>人类可读理由。</summary>
    public required string Reason { get; init; }

    /// <summary>原因码；成功裁决可为 null。</summary>
    public string? ReasonCode { get; init; }

    /// <summary>本次判定是否命中缓存。</summary>
    public required bool Cached { get; init; }

    /// <summary>记录时间（UTC）。</summary>
    public required DateTimeOffset CreatedAtUtc { get; init; }
}

/// <summary>
/// 审计端口（接缝）。
/// <para><b>旁挂</b>语义：本端口<b>不得</b>成为同步必经环节——写入失败不改变已定裁决。</para>
/// </summary>
public interface IOperatorAuditSink
{
    /// <summary>旁挂写入一条审计记录；实现抛异常不得影响裁决。</summary>
    void Write(OperatorAuditRecord record);
}

/// <summary>
/// 判定缓存端口。
/// <para>键 = <c>算子标识 | 算子版本 | 指令版本 | 输入指纹</c>；命中即置 <c>Cached=true</c> 并短路模型调用。</para>
/// </summary>
public interface IOperatorJudgementCache
{
    /// <summary>尝试取缓存；命中返回 true 并回填判断。</summary>
    bool TryGet(string key, out ModelJudgement judgement);

    /// <summary>写入缓存。</summary>
    void Set(string key, ModelJudgement judgement);
}

/// <summary>
/// 算子运行时作用域：由 <c>OperatorBase</c> 创建并注入，场景算子<b>只读消费</b>。
/// <para>
/// 承载身份 / 阈值 / 计时 / 取消令牌等运行时上下文，并负责把「结果语义草稿」
/// （<see cref="EnvelopeDraft"/>）补全为可落库的 <see cref="JudgementEnvelope"/>——
/// 身份、版本、阈值、计时、缓存标记统一在这里补齐，场景侧不重复拼装、也就不会漏字段。
/// </para>
/// </summary>
public sealed record OperatorScope
{
    /// <summary>算子稳定标识。</summary>
    public required string OperatorId { get; init; }

    /// <summary>算子版本；未提供为 null。</summary>
    public string? OperatorVersion { get; init; }

    /// <summary>场景键。</summary>
    public required string SceneKey { get; init; }

    /// <summary>输入身份指纹。</summary>
    public required string InputDigest { get; init; }

    /// <summary>指令版本（模型可复现性的关键之一）。</summary>
    public required int InstructionVersion { get; init; }

    /// <summary>场景侧投影好的结构化输入片段（非消息全文、非思维链）。</summary>
    public string RenderedInput { get; init; } = "";

    /// <summary>溯源身份；上下文未提供为 null。</summary>
    public OperatorIdentity? Identity { get; init; }

    /// <summary>已解析的阈值（连同结果落库，供事后解释「为什么通过」）；无阈值为 null。</summary>
    public AppliedThreshold? Threshold { get; init; }

    /// <summary>本次判定开始时间（UTC）。</summary>
    public required DateTimeOffset StartedAtUtc { get; init; }

    /// <summary>时间源（计时与可注入时钟）。</summary>
    public required TimeProvider Clock { get; init; }

    /// <summary>调用方取消令牌（不含基类的独立超时链路）。</summary>
    public CancellationToken Cancellation { get; init; }

    /// <summary>本次判定是否命中缓存（模型调用被短路）。</summary>
    public bool Cached { get; init; }

    /// <summary>基类解析出的模型判断；无模型（或模型不可用）时为 null。</summary>
    public ModelJudgement? ModelJudgement { get; init; }

    /// <summary>
    /// 判定 id：由（算子、算子版本、指令版本、输入指纹）确定性推导——
    /// 同一输入必得同一 id，便于复现与去重。
    /// </summary>
    public string JudgementId => BuildJudgementId(OperatorId, OperatorVersion, InstructionVersion, InputDigest);

    /// <summary>已耗时（毫秒），按当前时间源实时计算。</summary>
    public double ElapsedMs => Math.Max(0d, (Clock.GetUtcNow() - StartedAtUtc).TotalMilliseconds);

    /// <summary>
    /// 把场景侧的结果语义草稿补全为完整信封。
    /// </summary>
    /// <param name="draft">场景侧填写的「结果语义」草稿。</param>
    /// <exception cref="ArgumentNullException"><paramref name="draft"/> 为 null。</exception>
    public JudgementEnvelope BuildEnvelope(EnvelopeDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);

        return new JudgementEnvelope
        {
            JudgementId = JudgementId,
            SceneKey = SceneKey,
            InputDigest = InputDigest,
            OperatorId = OperatorId,
            OperatorVersion = draft.OperatorVersion ?? OperatorVersion,
            ModelId = draft.ModelId ?? ModelJudgement?.ModelId,
            InstructionVersion = InstructionVersion,
            Threshold = Threshold,
            SchemaVersion = JudgementEnvelope.CurrentSchemaVersion,
            Score = draft.Score,
            ScoreScale = draft.ScoreScale,
            Outcome = draft.Outcome,
            PrimaryLabel = draft.PrimaryLabel,
            LabelDistribution = draft.LabelDistribution,
            Confidence = draft.Confidence,
            ConfidenceKind = draft.ConfidenceKind,
            Reason = draft.Reason,
            ReasonCode = draft.ReasonCode,
            Evidence = draft.Evidence,
            LatencyMs = ElapsedMs,
            Cached = Cached,
            Identity = Identity,
            SourceEventIds = draft.SourceEventIds,
            SourceSha = draft.SourceSha,
            CreatedAtUtc = Clock.GetUtcNow(),
        };
    }

    /// <summary>确定性判定 id（同一输入 ⇒ 同一 id）。</summary>
    /// <param name="operatorId">算子标识。</param>
    /// <param name="operatorVersion">算子版本。</param>
    /// <param name="instructionVersion">指令版本。</param>
    /// <param name="inputDigest">输入指纹。</param>
    public static string BuildJudgementId(string operatorId, string? operatorVersion, int instructionVersion, string inputDigest)
    {
        var material = string.Join(
            '|',
            operatorId ?? "",
            operatorVersion ?? "",
            instructionVersion.ToString(CultureInfo.InvariantCulture),
            inputDigest ?? "");

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(material));
        return "jdg-" + Convert.ToHexString(hash)[..32].ToLowerInvariant();
    }
}
