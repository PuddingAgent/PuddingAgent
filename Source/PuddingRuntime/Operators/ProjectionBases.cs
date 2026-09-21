using PuddingCode.Operators;

namespace PuddingRuntime.Operators;

/// <summary>
/// 打分投影基类（基础设施第 2 层）：固定把内核结果投影为 <see cref="ScoreResult"/>。
/// <para>横切流程由 <see cref="OperatorBase{TCtx,TResult}"/> 提供，本层只固定输出语义与降级形状。</para>
/// </summary>
/// <typeparam name="TCtx">场景输入上下文类型。</typeparam>
public abstract class ScorerBase<TCtx> : OperatorBase<TCtx, ScoreResult>, IScorer
    where TCtx : class, IOperatorContext
{
    /// <summary>构造打分投影基类。</summary>
    /// <param name="environment">算子运行环境。</param>
    protected ScorerBase(OperatorEnvironment environment)
        : base(environment, BuildDegraded)
    {
    }

    /// <inheritdoc />
    public Task<ScoreResult> ScoreAsync(IOperatorContext context, CancellationToken ct = default) => RunAsync(context, ct);

    /// <summary>
    /// 降级形状：刻度固定为 <c>degraded</c>、分数置 0（无意义），
    /// <b>识别依据是信封的 <c>ReasonCode</c></b>——消费方不得把 0 当真实分数。
    /// </summary>
    private static ScoreResult BuildDegraded(OperatorScope scope, OperatorDegradation degradation) => new(
        "degraded",
        0d,
        scope.BuildEnvelope(new EnvelopeDraft
        {
            Reason = degradation.Reason,
            ReasonCode = degradation.ReasonCode,
            ScoreScale = "degraded",
            ConfidenceKind = ConfidenceKind.Unknown,
        }));
}

/// <summary>
/// 判断投影基类（基础设施第 2 层）：固定把内核结果投影为 <see cref="JudgeResult"/>。
/// </summary>
/// <typeparam name="TCtx">场景输入上下文类型。</typeparam>
public abstract class JudgeBase<TCtx> : OperatorBase<TCtx, JudgeResult>, IJudge
    where TCtx : class, IOperatorContext
{
    /// <summary>构造判断投影基类。</summary>
    /// <param name="environment">算子运行环境。</param>
    protected JudgeBase(OperatorEnvironment environment)
        : base(environment, BuildDegraded)
    {
    }

    /// <inheritdoc />
    public Task<JudgeResult> JudgeAsync(IOperatorContext context, CancellationToken ct = default) => RunAsync(context, ct);

    /// <summary>
    /// 降级形状：结论为 <see cref="JudgeOutcome.Unknown"/>（<b>不是</b> <see cref="JudgeOutcome.Abstain"/>——
    /// 弃权是合法裁决，降级不是），阈值与置信一律为 null、置信种类为 <see cref="ConfidenceKind.Unknown"/>。
    /// </summary>
    private static JudgeResult BuildDegraded(OperatorScope scope, OperatorDegradation degradation) => new(
        JudgeOutcome.Unknown,
        null,
        null,
        null,
        ConfidenceKind.Unknown,
        scope.BuildEnvelope(new EnvelopeDraft
        {
            Reason = degradation.Reason,
            ReasonCode = degradation.ReasonCode,
            ConfidenceKind = ConfidenceKind.Unknown,
        }));
}

/// <summary>
/// 分类投影基类（基础设施第 2 层）：固定把内核结果投影为 <see cref="ClassificationResult"/>。
/// </summary>
/// <typeparam name="TCtx">场景输入上下文类型。</typeparam>
public abstract class ClassifierBase<TCtx> : OperatorBase<TCtx, ClassificationResult>, IClassifier
    where TCtx : class, IOperatorContext
{
    private static readonly IReadOnlyDictionary<string, double> EmptyDistribution = new Dictionary<string, double>();

    /// <summary>构造分类投影基类。</summary>
    /// <param name="environment">算子运行环境。</param>
    protected ClassifierBase(OperatorEnvironment environment)
        : base(environment, BuildDegraded)
    {
    }

    /// <inheritdoc />
    public Task<ClassificationResult> ClassifyAsync(IOperatorContext context, CancellationToken ct = default) => RunAsync(context, ct);

    /// <summary>降级形状：主标签为 null、分布为空字典（不得为 null，避免消费方空引用）。</summary>
    private static ClassificationResult BuildDegraded(OperatorScope scope, OperatorDegradation degradation) => new(
        null,
        EmptyDistribution,
        scope.BuildEnvelope(new EnvelopeDraft
        {
            Reason = degradation.Reason,
            ReasonCode = degradation.ReasonCode,
            ConfidenceKind = ConfidenceKind.Unknown,
        }));
}
