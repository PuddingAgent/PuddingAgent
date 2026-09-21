using System.Globalization;
using PuddingCode.Operators;
using PuddingRuntime.Operators;

namespace PuddingRuntime.Services.Improvement.SkillValue;

/// <summary>
/// 技能价值打分器（RSI-G2 的 <see cref="IScorer"/> <b>首个真实消费者</b>）。
/// <para>
/// <b>纯确定性、无 LLM</b>：本算子的问题集为空，因此基类的
/// <c>ResolveJudgementAsync</c> 在 <c>model is null || Questions.Count == 0</c> 时返回
/// 「无判断且<b>无失败</b>」，随后内核照常执行（<c>OperatorBase.cs:243-247</c>）。
/// 也就是说「无模型」<b>不等于</b>「降级」—— 确定性算子是基类的一等公民路径，
/// 不需要绕过 <c>OperatorBase</c>，也不需要给基类加任何场景特判。
/// </para>
/// <para>
/// <b>本类不判通过与否</b>：只产出分数；阈值来自 provider，且不参与任何判断。
/// 阈值一旦住进打分器，事后就无法回答「为什么放行」，自我改进候选也能悄悄移动判据。
/// </para>
/// </summary>
public sealed class SkillValueScorer : ScorerBase<SkillValueOperatorContext>
{
    /// <summary>构造打分器。</summary>
    /// <param name="environment">算子运行环境（本算子不需要模型端口）。</param>
    public SkillValueScorer(OperatorEnvironment environment)
        : base(environment)
    {
    }

    /// <inheritdoc />
    protected override string SceneKey => SkillValueScene.SceneKey;

    /// <summary>
    /// 指令：<b>问题集刻意为空</b> —— 这是「不调用模型」的声明方式，
    /// 也是本算子能作为纯确定性打分器运行的关键。改成非空会静默把本算子变成模型调用算子。
    /// </summary>
    protected override OperatorInstruction Instruction => new()
    {
        Text = SkillValueScene.InstructionText,
        Version = SkillValueScene.InstructionVersion,
        Questions = [],
    };

    /// <inheritdoc />
    protected override OperatorOutputShape OutputShape => new()
    {
        ScoreScale = SkillValueScene.ValueScale,
    };

    /// <summary>
    /// 阈值<b>必须外置</b>：从注入的 provider 解析，本类不硬编码任何阈值常量，
    /// 也不自行与分数比较。解析不到即为 null（基类按「无阈值」语义处理）。
    /// </summary>
    protected override ThresholdPolicy? Threshold => ResolveThresholdFromProvider();

    /// <summary>输入投影：确定性渲染，只含事实字段，不含消息全文。</summary>
    protected override string ProjectInput(SkillValueOperatorContext context) => string.Join(
        '|',
        "scene=" + SkillValueScene.SceneKey,
        "skill=" + context.SkillId,
        "injected=" + context.InjectionCount.ToString(CultureInfo.InvariantCulture),
        "readFailed=" + context.ReadFailureCount.ToString(CultureInfo.InvariantCulture),
        "bytes=" + context.InjectedBytesTotal.ToString(CultureInfo.InvariantCulture));

    /// <summary>
    /// 结果投影。<b>与内核共用同一纯函数</b>（<see cref="BuildResult"/>），因此它<b>不</b>读取
    /// <paramref name="judgement"/>：本算子是确定性的，「去掉模型不改变分数」是设计事实而不是巧合。
    /// <para>
    /// 注：在空问题集路径下本方法<b>不会被调用</b>（没有模型判断可投影）；它必须存在是因为基类把它
    /// 定为抽象成员。共用纯函数保证「万一被调用」也给出逐位相同的结果。
    /// </para>
    /// </summary>
    protected override ScoreResult Project(
        ModelJudgement judgement,
        SkillValueOperatorContext context,
        OperatorScope scope) => BuildResult(context, scope);

    /// <summary>唯一可变点：场景内核（纯函数，不抛异常）。</summary>
    protected override Task<ScoreResult> ClassifyCoreAsync(
        SkillValueOperatorContext context,
        OperatorScope scope,
        CancellationToken ct) => Task.FromResult(BuildResult(context, scope));

    /// <summary>
    /// 打分公式（纯函数，可独立验证）：
    /// <code>
    /// adoption    = min(1, n / AdoptionSaturationN)
    /// reliability = n / (n + f)
    /// costRatio   = min(1, (bytes / max(n,1)) / CostCeilingBytes)
    /// score       = clamp01((0.5*adoption + 0.5*reliability) * (1 - 0.5*costRatio))
    /// </code>
    /// 性质：注入次数越多分越高（饱和），读取失败越多分越低，平均正文越大分越低。
    /// <b>调用方必须只在 <c>n + f &gt; 0</c> 时使用本函数</b> —— 无观测数据要走冷启动契约（见
    /// <see cref="SkillValueScene.InsufficientDataScale"/>），不得用本函数的 0 冒充分数。
    /// </summary>
    /// <param name="injectionCount">注入次数（n ≥ 0）。</param>
    /// <param name="readFailureCount">读取失败次数（f ≥ 0）。</param>
    /// <param name="injectedBytesTotal">注入正文总字节数。</param>
    public static double ComputeScore(int injectionCount, int readFailureCount, long injectedBytesTotal)
    {
        var observations = injectionCount + readFailureCount;
        if (observations <= 0)
        {
            // 契约边界：无数据不是低分。返回 0 只是为了让本函数是全函数；
            // BuildResult 在此之前就已改走 insufficient-data 刻度，绝不会把它当作分数输出。
            return 0d;
        }

        var adoption = Math.Min(1d, injectionCount / (double)SkillValueScene.AdoptionSaturationN);
        var reliability = injectionCount / (double)observations;
        var averageBytes = injectedBytesTotal / (double)Math.Max(injectionCount, 1);
        var costRatio = Math.Min(1d, averageBytes / SkillValueScene.CostCeilingBytes);

        var weighted = (SkillValueScene.AdoptionWeight * adoption)
                       + (SkillValueScene.ReliabilityWeight * reliability);
        var discounted = weighted * (1d - (SkillValueScene.MaxCostDiscount * costRatio));

        return Math.Clamp(discounted, 0d, 1d);
    }

    private static ScoreResult BuildResult(SkillValueOperatorContext context, OperatorScope scope)
    {
        var observations = context.ObservationCount;

        if (observations <= 0)
        {
            // ⭐ 冷启动：无数据 ≠ 低分。
            // Score=0 是占位（不可解读），识别依据是刻度串与原因码。
            // 「没有数据」与「数据表明很差」在数值上相同、在含义上不同 —— 这是刻意的，
            // 它逼调用方读刻度而不是读数字。
            return new ScoreResult(
                SkillValueScene.InsufficientDataScale,
                0d,
                scope.BuildEnvelope(new EnvelopeDraft
                {
                    Score = 0d,
                    ScoreScale = SkillValueScene.InsufficientDataScale,
                    ReasonCode = SkillValueScene.InsufficientDataReasonCode,
                    Reason = "无观测数据（injected=0, readFailed=0）：无数据不等于低分，"
                             + "Score=0 仅为占位，必须按刻度与原因码判定本结果不可用于排序。",
                    ConfidenceKind = ConfidenceKind.Unknown,
                    Evidence =
                    [
                        new JudgementEvidence
                        {
                            Kind = "skill_usage_telemetry",
                            Reference = "skill-usage:" + context.SkillId,
                            Note = "观测次数为 0：既可能是从未被命中，也可能是采集器尚未激活；二者当前不可区分。",
                        },
                    ],
                }));
        }

        var score = ComputeScore(context.InjectionCount, context.ReadFailureCount, context.InjectedBytesTotal);
        var averageBytes = context.InjectedBytesTotal / (double)Math.Max(context.InjectionCount, 1);

        return new ScoreResult(
            SkillValueScene.ValueScale,
            score,
            scope.BuildEnvelope(new EnvelopeDraft
            {
                Score = score,
                ScoreScale = SkillValueScene.ValueScale,
                // 成功裁决 ReasonCode 为 null（契约允许）；降级才必须带码。
                ReasonCode = null,
                Reason =
                    $"技能价值分 {score.ToString("F4", CultureInfo.InvariantCulture)}"
                    + $"（刻度 {SkillValueScene.ValueScale}）："
                    + $"injected={context.InjectionCount.ToString(CultureInfo.InvariantCulture)}, "
                    + $"readFailed={context.ReadFailureCount.ToString(CultureInfo.InvariantCulture)}, "
                    + $"平均正文 {averageBytes.ToString("F0", CultureInfo.InvariantCulture)} 字节。"
                    + "本分数只表达排序，不表达通过与否。",
                // 置信度：本算子不产出置信估计。写 Calibrated 会声称一个未经校准的事实，
                // 因此取 Unknown 且不给数值 —— 宁可没有，也不要一个看起来能用的错值。
                Confidence = null,
                ConfidenceKind = ConfidenceKind.Unknown,
                Evidence =
                [
                    new JudgementEvidence
                    {
                        Kind = "skill_usage_telemetry",
                        Reference = "skill-usage:" + context.SkillId,
                        Note =
                            $"injected={context.InjectionCount.ToString(CultureInfo.InvariantCulture)} "
                            + $"readFailed={context.ReadFailureCount.ToString(CultureInfo.InvariantCulture)} "
                            + $"bytes={context.InjectedBytesTotal.ToString(CultureInfo.InvariantCulture)} "
                            + $"window={context.WindowStartUtc?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)}"
                            + $"..{context.WindowEndUtc?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)}",
                    },
                ],
            }));
    }
}
