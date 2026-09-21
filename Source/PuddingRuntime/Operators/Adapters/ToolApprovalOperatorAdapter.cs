using PuddingCode.Classification;
using PuddingCode.Operators;

namespace PuddingRuntime.Operators.Adapters;

/// <summary>
/// 工具审批算子适配器（S1b 交付物 2）：把<b>既有</b>工具审批分类器
/// （<see cref="IToolCallClassifier"/>）暴露为算子分类端口 <see cref="IClassifier"/>。
/// <para>
/// <b>本类只做「包装」，不做「改写」</b>：被包装者一行不改、行为逐位不变，适配器把它的输出<b>纯映射</b>到
/// 新端口。依赖倒置的正确用法是适配器——新端口不要求旧实现重写，只要求有人把既有实现暴露为新端口，
/// 这样<b>安全关键路径（审批仲裁）不被重构</b>，同时证明抽象真能承载真实消费者。
/// </para>
/// <para>
/// 只实现<b>一个</b>原语端口（分类）：审批裁决表达的是「标签 + 分布」，<b>不表达「多少分」</b>——
/// 这正是它属 <see cref="IClassifier"/> 而非 <see cref="IScorer"/> 的原因（见 §3.3 的映射说明）。
/// </para>
/// <para>
/// <b>fail-safe</b>：被包装者抛异常、返回 null、上下文类型不匹配一律转<b>降级结论</b> + 稳定原因码，
/// <b>禁止</b>向调用方冒泡异常（与 <see cref="IOperator"/> 端口契约一致）。
/// </para>
/// </summary>
public sealed class ToolApprovalOperatorAdapter : IClassifier
{
    /// <summary>场景键（注册表按此键索引本算子）。</summary>
    public const string SceneKeyValue = "tool_approval";

    /// <summary>证据种类：规则命中（引用 <c>AppliedRuleId</c>）。</summary>
    public const string EvidenceKindRule = "rule";

    /// <summary>
    /// 规范化标签键：仅放行本次。
    /// <para>与既有 <c>PerOutcomeConfidence</c> 的<b>文档化规范键</b>逐字一致（不得另造命名）。</para>
    /// </summary>
    public const string LabelAllowOnce = "allow_once";

    /// <summary>规范化标签键：放行且可沉淀长期规则。</summary>
    public const string LabelAllowPermanent = "allow_permanent";

    /// <summary>规范化标签键：仅拒绝本次。</summary>
    public const string LabelDenyOnce = "deny_once";

    /// <summary>规范化标签键：拒绝且可沉淀长期规则。</summary>
    public const string LabelDenyPermanent = "deny_permanent";

    /// <summary>规范化标签键：未产生有效裁决（降级 / 未知一律归入此键）。</summary>
    public const string LabelUnknown = "unknown";

    /// <summary>规范标签全集（顺序固定：规范键的对外声明）。</summary>
    public static readonly IReadOnlyList<string> CanonicalLabels =
    [
        LabelAllowOnce,
        LabelAllowPermanent,
        LabelDenyOnce,
        LabelDenyPermanent,
        LabelUnknown,
    ];

    /// <summary>
    /// 被包装分类器未提供理由时的占位理由：信封契约要求 <c>Reason</c> 必填非空，
    /// 因此这里只补<b>占位文本</b>，不改动任何结果字段（裁决结论不受影响）。
    /// </summary>
    public const string MissingReasonPlaceholder =
        "被包装分类器未提供理由（Reason 为空）：按信封契约填充占位理由，裁决结果字段不受影响。";

    /// <summary>无指令位：本适配器不调用模型、无指令文本，判定 id 的指令位固定 0（S1a 语义：null 表示无指令）。</summary>
    private const int NoInstructionVersion = 0;

    private static readonly IReadOnlyDictionary<string, double> EmptyDistribution = new Dictionary<string, double>();

    private readonly IToolCallClassifier _inner;
    private readonly TimeProvider _clock;

    /// <summary>构造适配器。</summary>
    /// <param name="inner">被包装的既有工具审批分类器（<b>不被本类修改</b>）。</param>
    /// <param name="clock">时间源；null 表示系统时钟（信封 <c>CreatedAtUtc</c> 用）。</param>
    /// <exception cref="ArgumentNullException"><paramref name="inner"/> 为 null。</exception>
    public ToolApprovalOperatorAdapter(IToolCallClassifier inner, TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(inner);
        _inner = inner;
        _clock = clock ?? TimeProvider.System;
    }

    /// <inheritdoc />
    /// <remarks>取被包装者的稳定标识：适配器不得自造新身份，否则审计溯源会与既有记录断开。</remarks>
    public string OperatorId => _inner.ClassifierId;

    /// <summary>算子版本（取承载本类型的程序集版本，与 S1a 基类同一取法）。</summary>
    public string? OperatorVersion => typeof(ToolApprovalOperatorAdapter).Assembly.GetName().Version?.ToString();

    /// <inheritdoc />
    /// <remarks>
    /// 上下文为 null / 类型不匹配一律转<b>降级结论</b>而<b>不</b>抛异常：端口契约要求同一原语的所有实现
    /// （基类派生算子与适配器）在失败模式上一致——调用方不应因为换了一个实现而开始收到异常。
    /// </remarks>
    public Task<ClassificationResult> ClassifyAsync(IOperatorContext context, CancellationToken ct = default)
        => RunAsync(context, ct);

    /// <summary>
    /// 便利入口：调用方持有既有裁决输入（<see cref="ToolCallClassificationContext"/>）时，
    /// 由本方法按规范路径算出确定性输入指纹后走同一端口。
    /// <para>存在的理由：指纹必须由<b>一处</b>计算（见 <see cref="ToolApprovalOperatorContext.ComputeInputDigest"/>），
    /// 否则调用方各自的拼装方式会让缓存键与去重失效。</para>
    /// </summary>
    /// <param name="approval">既有工具调用裁决输入。</param>
    /// <param name="ct">取消令牌。</param>
    /// <exception cref="ArgumentNullException"><paramref name="approval"/> 为 null。</exception>
    public Task<ClassificationResult> ClassifyApprovalAsync(
        ToolCallClassificationContext approval,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(approval);
        return RunAsync(ToolApprovalOperatorContext.Create(approval), ct);
    }

    /// <summary>
    /// 规范化标签映射：<b>纯函数、确定性</b>，是等价性测试的判定基准。
    /// <para>未知枚举值（未来新增成员）一律归入 <see cref="LabelUnknown"/>：不抛异常（fail-safe），不猜结论。</para>
    /// </summary>
    /// <param name="outcome">既有裁决结论。</param>
    public static string NormalizeLabel(ClassificationOutcome outcome) => outcome switch
    {
        ClassificationOutcome.AllowOnce => LabelAllowOnce,
        ClassificationOutcome.AllowPermanent => LabelAllowPermanent,
        ClassificationOutcome.DenyOnce => LabelDenyOnce,
        ClassificationOutcome.DenyPermanent => LabelDenyPermanent,
        _ => LabelUnknown,
    };

    private async Task<ClassificationResult> RunAsync(IOperatorContext? context, CancellationToken ct)
    {
        // 上下文缺失 / 类型不匹配 ⇒ 降级（与 S1a 入口语义一致：不得按「未命中」静默放行，也不抛异常）。
        if (context is not ToolApprovalOperatorContext typed)
        {
            return Degrade(
                context,
                OperatorReasonCodes.ContextMismatch,
                "输入上下文不是工具审批场景上下文：按降级契约返回结论，不得按未命中静默放行。");
        }

        ClassificationVerdict verdict;
        try
        {
            verdict = await _inner.ClassifyAsync(typed.Approval, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return Degrade(
                typed,
                OperatorReasonCodes.Cancelled,
                "被包装分类器在取消 / 超时链路中中止：按降级契约返回结论（取消不是放行）。");
        }
        catch (Exception ex)
        {
            // 被包装者契约要求 fail-safe，理论上不抛；真抛了也绝不冒泡（异常类型与消息保留在理由里，
            // 但堆栈不进信封——Reason 必须保持短小可读，堆栈属日志侧责任）。
            return Degrade(
                typed,
                OperatorReasonCodes.CoreFailure,
                $"被包装分类器抛出异常 {ex.GetType().Name}：{ex.Message}；按降级契约返回结论，不冒泡异常。");
        }

        if (verdict is null)
        {
            return Degrade(
                typed,
                OperatorReasonCodes.CoreFailure,
                "被包装分类器返回 null：按降级契约返回结论，不冒泡异常。");
        }

        try
        {
            return Map(typed, verdict);
        }
        catch (Exception ex)
        {
            // 映射期异常（例如被包装者返回了自相矛盾的分布对象）同样按降级处理：
            // 本端口的契约是「禁止冒泡异常」，降级比中断审批链路安全。
            return Degrade(
                typed,
                OperatorReasonCodes.CoreFailure,
                $"把既有裁决映射到算子信封时失败 {ex.GetType().Name}：{ex.Message}；按降级契约返回结论。");
        }
    }

    /// <summary>
    /// 纯映射（规格 §3.3 逐条落地）：<see cref="ClassificationVerdict"/> → <see cref="ClassificationResult"/>。
    /// </summary>
    private ClassificationResult Map(ToolApprovalOperatorContext context, ClassificationVerdict verdict)
    {
        var primaryLabel = NormalizeLabel(verdict.Outcome);

        // 分布：为空（null）则空字典 —— 不得伪造任何值。非 null 时原样承载（不做归一化、不做补键）。
        IReadOnlyDictionary<string, double> distribution = verdict.PerOutcomeConfidence ?? EmptyDistribution;

        // 置信度：取主标签对应的自报值；该键缺失 ⇒ null（不得回退到别的键、不得填 0）。
        double? confidence = distribution.TryGetValue(primaryLabel, out var reported) ? reported : null;

        IReadOnlyList<JudgementEvidence> evidence = Array.Empty<JudgementEvidence>();
        var ruleId = verdict.AppliedRuleId;
        if (!string.IsNullOrWhiteSpace(ruleId))
        {
            evidence = [new JudgementEvidence { Kind = EvidenceKindRule, Reference = ruleId }];
        }

        var reason = string.IsNullOrWhiteSpace(verdict.Reason) ? MissingReasonPlaceholder : verdict.Reason;

        var envelope = BuildEnvelope(
            context,
            new EnvelopeDraft
            {
                Reason = reason,
                ReasonCode = verdict.ReasonCode,
                PrimaryLabel = primaryLabel,
                LabelDistribution = distribution,
                Confidence = confidence,
                // 恒为「模型自报」：键来自分类器的自报分布，**未校准**，不得标成 Calibrated。
                ConfidenceKind = ConfidenceKind.ModelSelfReported,
                Evidence = evidence,
                ModelId = verdict.ClassifierModel,
                // SourceEventIds / SourceSha 留空：既有裁决记录不携带原始事件 id 与源码指纹（不编造）。
            },
            verdict.LatencyMs);

        return new ClassificationResult(primaryLabel, distribution, envelope);
    }

    /// <summary>
    /// 降级结论（S1a 降级契约）：主标签 null、分布空字典、置信种类 <see cref="ConfidenceKind.Unknown"/>、
    /// 稳定 <see cref="OperatorReasonCodes"/> 原因码。
    /// </summary>
    /// <remarks>
    /// 降级路径的置信种类取 <see cref="ConfidenceKind.Unknown"/>（对齐 S1a 降级形状）：
    /// 此时<b>没有</b>任何被包装裁决，也就没有任何「自报值」可标 —— 标 <see cref="ConfidenceKind.ModelSelfReported"/>
    /// 会凭空宣称一个不存在的自报。两条约束（「映射路径恒为 ModelSelfReported」与「绝不标 Calibrated」）同时成立。
    /// </remarks>
    private ClassificationResult Degrade(IOperatorContext? context, string reasonCode, string reason)
    {
        var inputDigest = context is ToolApprovalOperatorContext typed
            ? typed.InputDigest
            : context?.InputDigest ?? string.Empty;

        var envelope = BuildEnvelope(
            context as ToolApprovalOperatorContext,
            new EnvelopeDraft
            {
                Reason = reason,
                ReasonCode = reasonCode,
                PrimaryLabel = null,
                LabelDistribution = EmptyDistribution,
                Confidence = null,
                ConfidenceKind = ConfidenceKind.Unknown,
            },
            latencyMs: null,
            inputDigest: inputDigest);

        return new ClassificationResult(null, EmptyDistribution, envelope);
    }

    /// <summary>
    /// 补齐信封字段。
    /// <para>
    /// 本适配器<b>不</b>继承 <see cref="OperatorScope"/> 所属的基类链路（它只包装既有实现，不走模型内核），
    /// 因此在这里显式拼装；与 <c>OperatorScope.BuildEnvelope</c> 有<b>两处有意差异</b>（其余字段语义相同）：
    /// <list type="number">
    /// <item><c>LatencyMs</c> 取<b>被包装裁决的耗时</b>（<c>verdict.LatencyMs</c>），不取适配器自身耗时——
    /// 适配器只是转发，耗时数字必须是真实裁决的成本（规格 §3.3 映射要求）；</item>
    /// <item><c>InstructionVersion</c> 固定 <c>null</c>：包装器无指令文本（S1a：null 表示「无指令」），
    /// 而基类路径的 <c>OperatorScope.InstructionVersion</c> 是非空 <c>int</c>，故此处不复用其补全逻辑。</item>
    /// </list>
    /// <c>Score</c> / <c>ScoreScale</c> / <c>Threshold</c> / <c>Outcome</c> 一律 <c>null</c>（不落任何值）：
    /// 审批裁决没有「单一分数」，其阈值也尚未以 <c>ThresholdPolicy</c> 暴露（已知缺口，非本切片范围）——
    /// <b>留空</b>是唯一诚实的表达，伪造分数或阈值会让下游把「无分数」误读成「低分」。
    /// </para>
    /// </summary>
    private JudgementEnvelope BuildEnvelope(
        ToolApprovalOperatorContext? context,
        EnvelopeDraft draft,
        double? latencyMs,
        string? inputDigest = null)
    {
        var digest = inputDigest ?? context?.InputDigest ?? string.Empty;

        return new JudgementEnvelope
        {
            // 判定 id 复用 S1a 的确定性推导（算子 + 算子版本 + 指令位 + 输入指纹 ⇒ 同一输入同一 id）。
            JudgementId = OperatorScope.BuildJudgementId(OperatorId, OperatorVersion, NoInstructionVersion, digest),
            SceneKey = SceneKeyValue,
            InputDigest = digest,
            OperatorId = OperatorId,
            OperatorVersion = OperatorVersion,
            ModelId = draft.ModelId,
            InstructionVersion = null,
            Threshold = null,
            SchemaVersion = JudgementEnvelope.CurrentSchemaVersion,
            Score = null,
            ScoreScale = null,
            Outcome = null,
            PrimaryLabel = draft.PrimaryLabel,
            LabelDistribution = draft.LabelDistribution,
            Confidence = draft.Confidence,
            ConfidenceKind = draft.ConfidenceKind,
            Reason = draft.Reason,
            ReasonCode = draft.ReasonCode,
            Evidence = draft.Evidence,
            LatencyMs = latencyMs,
            // 适配器不参与 S1a 判定缓存（既有审批链路自带缓存 / 审计），故恒为 false。
            Cached = false,
            Identity = context?.Identity ?? (context as IOperatorIdentityContext)?.Identity,
            SourceEventIds = null,
            SourceSha = null,
            CreatedAtUtc = _clock.GetUtcNow(),
        };
    }
}
