using PuddingCode.Operators;
using PuddingRuntime.Operators;
using PuddingRuntime.Services.Improvement.SkillValue;

namespace PuddingRuntimeTests.Services.Improvement;

/// <summary>
/// RSI-T2 契约测试：证明 <see cref="IScorer"/> 这条抽象<b>真的接上了</b>，而不是「能编译」。
/// <para>
/// 三条最有价值的用例是 N1 / N2 / N3：
/// <list type="bullet">
/// <item>N1 证明「无模型」不等于「降级」—— 确定性算子是基类的一等公民路径；</item>
/// <item>N2 证明模型端口被短路（不是「恰好没用到」）；</item>
/// <item>N3 证明阈值没有漏进打分器，且非法阈值仍由基类拦下（我们没有绕过基类校验）。</item>
/// </list>
/// </para>
/// <para>
/// <b>与规格的一处偏离（已如实记录）</b>：规格 §6 的 N2 原写「提供任意非空 <c>ModelJudgement</c>
/// ⇒ 结果与无模型路径一致」。但问题集为空时基类在 <c>ResolveJudgementAsync</c> 里<b>先短路</b>，
/// 模型永不被调用 ⇒ 根本没有 <c>ModelJudgement</c> 可投影，<c>Project</c> 在外部不可达（protected）。
/// 因此改为断言<b>更强且可验证</b>的形式：配置了模型端口后<b>调用次数必须为 0</b>，且结果与无模型
/// 路径逐位相同。<c>Project</c> 与内核共用同一纯函数由实现结构保证（并在此注释说明）。
/// </para>
/// </summary>
[TestClass]
public sealed class SkillValueScorerTests
{
    private static readonly DateTimeOffset WindowStart = new(2026, 9, 21, 10, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset WindowEnd = new(2026, 9, 21, 12, 0, 0, TimeSpan.Zero);

    // ---------------------------------------------------------------- N1

    /// <summary>N1：模型未配置 + 问题集为空 ⇒ 内核照常执行，结果<b>不得</b>是降级。</summary>
    /// <remarks>
    /// 这条钉住的是 <c>OperatorBase.ResolveJudgementAsync</c>（<c>model is null || Questions.Count == 0</c>
    /// ⇒ 无判断且<b>无失败</b>）这条语义。若哪天它被改成「无模型即降级」，本用例会<b>变红</b>而不是静默退化。
    /// </remarks>
    [TestMethod]
    public async Task N1_NoModelAndEmptyQuestions_KernelStillRunsAndIsNotDegraded()
    {
        var scorer = new SkillValueScorer(Environment());

        var result = await scorer.ScoreAsync(Context(injected: 2, failed: 0, bytes: 200));

        Assert.AreNotEqual("degraded", result.Scale, "无模型不得被当作降级：确定性算子是基类的一等公民路径。");
        Assert.IsNull(result.Envelope.ReasonCode, "成功裁决不应带降级原因码。");
        Assert.AreEqual(SkillValueScene.ValueScale, result.Scale);
    }

    // ---------------------------------------------------------------- N2

    /// <summary>N2：配置了模型端口也必须<b>一次都不调用</b>，且结果与无模型路径逐位相同。</summary>
    [TestMethod]
    public async Task N2_ConfiguredModelIsNeverCalled_AndResultMatchesNoModelPath()
    {
        var model = new CountingModel();
        var context = Context(injected: 3, failed: 1, bytes: 512);

        var withModel = await new SkillValueScorer(Environment(model)).ScoreAsync(context);
        var withoutModel = await new SkillValueScorer(Environment()).ScoreAsync(context);

        Assert.AreEqual(0, model.CallCount, "问题集为空时模型必须被短路，不得被调用。");
        Assert.AreEqual(withoutModel.Scale, withModel.Scale);
        Assert.AreEqual(withoutModel.Score, withModel.Score, 1e-12, "去掉模型不得改变分数。");
    }

    // ---------------------------------------------------------------- N3

    /// <summary>N3：阈值不得影响分数；非法阈值策略仍由<b>基类</b>拦下（证明没有绕过基类校验）。</summary>
    [TestMethod]
    public async Task N3_ThresholdNeverLeaksIntoScore_AndInvalidPolicyStillDegrades()
    {
        var context = Context(injected: 3, failed: 1, bytes: 900);
        var bare = await new SkillValueScorer(Environment()).ScoreAsync(context);

        var validPolicy = new ThresholdPolicy
        {
            PolicyId = "skill-value.default",
            Version = 1,
            YesAtOrAbove = 0.9,
            NoAtOrBelow = 0.1,
        };
        var withPolicy = await new SkillValueScorer(
                Environment(policies: new FixedPolicyProvider(validPolicy)))
            .ScoreAsync(context);

        Assert.AreEqual(bare.Scale, withPolicy.Scale, "阈值不得改变刻度。");
        Assert.AreEqual(bare.Score, withPolicy.Score, 1e-12, "阈值不得改变分数：打分器不得内含判据。");
        Assert.IsNull(withPolicy.Envelope.ReasonCode, "有阈值不等于有降级。");

        // 非法区间（YesAtOrAbove ≤ NoAtOrBelow）：基类必须拒绝裁决，且**不得**静默产生结论。
        var invalidPolicy = new ThresholdPolicy
        {
            PolicyId = "skill-value.broken",
            Version = 1,
            YesAtOrAbove = 0.1,
            NoAtOrBelow = 0.9,
        };
        var degraded = await new SkillValueScorer(
                Environment(policies: new FixedPolicyProvider(invalidPolicy)))
            .ScoreAsync(context);

        Assert.AreEqual("degraded", degraded.Scale);
        Assert.AreEqual(OperatorReasonCodes.InvalidThreshold, degraded.Envelope.ReasonCode);
    }

    // ---------------------------------------------------------------- N4

    /// <summary>N4：冷启动（无数据）<b>不是</b>低分，且与「真实低分」在刻度上可区分。</summary>
    [TestMethod]
    public async Task N4_ColdStartIsNotALowScore_AndIsDistinguishableFromARealLowScore()
    {
        var cold = await new SkillValueScorer(Environment())
            .ScoreAsync(Context(injected: 0, failed: 0, bytes: 0));

        Assert.AreEqual(SkillValueScene.InsufficientDataScale, cold.Scale);
        Assert.IsTrue(cold.Scale.Contains("insufficient-data", StringComparison.Ordinal));
        Assert.AreEqual(SkillValueScene.InsufficientDataReasonCode, cold.Envelope.ReasonCode);
        Assert.AreNotEqual("degraded", cold.Scale, "无数据不是故障：它没有失败，只是无法打分。");

        // 一个真实低分：被注入过 1 次、读失败 9 次、正文顶到代价上限 ⇒ 分真实地低，但**不是**无数据。
        var realLow = await new SkillValueScorer(Environment())
            .ScoreAsync(Context(injected: 1, failed: 9, bytes: SkillValueScene.CostCeilingBytes));

        Assert.AreEqual(SkillValueScene.ValueScale, realLow.Scale, "有观测就必须落在真实刻度上。");
        Assert.IsNull(realLow.Envelope.ReasonCode);
        Assert.IsTrue(realLow.Score is > 0d and < 0.2d, $"期望真实低分，实际 {realLow.Score}。");
        Assert.AreNotEqual(cold.Scale, realLow.Scale, "「无数据」与「真实低分」必须靠刻度可区分。");
    }

    // ---------------------------------------------------------------- N5

    /// <summary>
    /// N5：只有负证据（从未注入、但有读失败）时，分数<b>真实地</b>是 0，且刻度是真实刻度。
    /// 与 N4 的冷启动结果数值相同、刻度不同 —— 这一对用例共同证明「必须读刻度，不能读数字」。
    /// </summary>
    [TestMethod]
    public async Task N5_NegativeEvidenceOnly_ReportsRealScaleWithZeroScore()
    {
        var negativeOnly = await new SkillValueScorer(Environment())
            .ScoreAsync(Context(injected: 0, failed: 3, bytes: 0));

        Assert.AreEqual(SkillValueScene.ValueScale, negativeOnly.Scale);
        Assert.AreEqual(0d, negativeOnly.Score, 1e-12);
        Assert.IsNull(negativeOnly.Envelope.ReasonCode, "这是有效结论，不是降级，也不是「无数据」。");
    }

    // ---------------------------------------------------------------- N6

    /// <summary>N6：同一上下文两次调用 ⇒ 刻度与分数逐位相同（确定性）。</summary>
    [TestMethod]
    public async Task N6_SameContextProducesIdenticalScaleAndScore()
    {
        var scorer = new SkillValueScorer(Environment());
        var context = Context(injected: 4, failed: 2, bytes: 700);

        var first = await scorer.ScoreAsync(context);
        var second = await scorer.ScoreAsync(context);

        Assert.AreEqual(first.Scale, second.Scale);
        Assert.AreEqual(first.Score, second.Score, 0d, "确定性算子：同输入必须逐位相同。");
        Assert.AreEqual(first.Envelope.InputDigest, second.Envelope.InputDigest);
    }

    // ---------------------------------------------------------------- N7

    /// <summary>N7：单调性 —— 注入越多分非降；平均正文字节越大分非增。</summary>
    [TestMethod]
    public async Task N7_ScoreIsMonotoneInInjectionsAndInBytes()
    {
        var scorer = new SkillValueScorer(Environment());

        var previous = double.MinValue;
        for (var injections = 1; injections <= SkillValueScene.AdoptionSaturationN; injections++)
        {
            var result = await scorer.ScoreAsync(Context(injected: injections, failed: 0, bytes: 0));
            Assert.IsTrue(
                result.Score >= previous,
                $"注入次数 {injections} 时分数下降（{previous} → {result.Score}）：采用度必须单调不减。");
            previous = result.Score;
        }

        var previousBytes = double.MaxValue;
        foreach (var bytes in new long[] { 0, 512, 2048, SkillValueScene.CostCeilingBytes, SkillValueScene.CostCeilingBytes * 2L })
        {
            var result = await scorer.ScoreAsync(
                Context(injected: SkillValueScene.AdoptionSaturationN, failed: 0, bytes: bytes));
            Assert.IsTrue(
                result.Score <= previousBytes,
                $"平均正文 {bytes} 字节时分数上升（{previousBytes} → {result.Score}）：代价折扣必须单调不增。");
            previousBytes = result.Score;
        }
    }

    // ---------------------------------------------------------------- N8

    /// <summary>N8：fail-safe —— 上下文类型不匹配 / 为 null / 缺身份，都必须返回确定结论且不抛异常。</summary>
    [TestMethod]
    public async Task N8_FailSafe_OnForeignNullAndIdentitylessContexts()
    {
        var scorer = new SkillValueScorer(Environment());

        var foreign = await scorer.ScoreAsync(new ForeignContext());
        Assert.AreEqual("degraded", foreign.Scale);
        Assert.AreEqual(OperatorReasonCodes.ContextMismatch, foreign.Envelope.ReasonCode);

        var nullContext = await scorer.ScoreAsync(null!);
        Assert.AreEqual("degraded", nullContext.Scale);
        Assert.AreEqual(OperatorReasonCodes.ContextMismatch, nullContext.Envelope.ReasonCode);

        // 不实现 IOperatorIdentityContext 的上下文仍须正常工作（身份缺失不是错误）。
        var identityless = await scorer.ScoreAsync(Context(injected: 1, failed: 0, bytes: 10));
        Assert.AreNotEqual("degraded", identityless.Scale);
        Assert.IsNull(identityless.Envelope.Identity);
    }

    // ---------------------------------------------------------------- N9

    /// <summary>N9：结果刻度必须与声明的版本化刻度同源，且信封里的刻度与投影刻度一致。</summary>
    [TestMethod]
    public async Task N9_ResultScaleIsTheDeclaredVersionedScale()
    {
        var result = await new SkillValueScorer(Environment())
            .ScoreAsync(Context(injected: 2, failed: 0, bytes: 100));

        Assert.AreEqual(SkillValueScene.ValueScale, result.Scale);
        Assert.IsTrue(result.Scale.StartsWith("skill-value.v1", StringComparison.Ordinal), result.Scale);
        Assert.AreEqual(result.Scale, result.Envelope.ScoreScale, "信封与投影不得各说一套刻度。");
        Assert.IsTrue(
            result.Envelope.Reason.Contains(SkillValueScene.ValueScale, StringComparison.Ordinal),
            "理由文本必须写明量纲，否则分数离开代码就无法解释。");
    }

    // ---------------------------------------------------------------- helpers

    private static OperatorEnvironment Environment(
        IClassifierModel? model = null,
        IThresholdPolicyProvider? policies = null) => new()
        {
            Model = model,
            ThresholdPolicies = policies,
            Timeout = TimeSpan.FromSeconds(5),
        };

    private static SkillValueOperatorContext Context(
        int injected,
        int failed,
        long bytes,
        string skillId = "skill-a") => new()
        {
            SkillId = skillId,
            InjectionCount = injected,
            ReadFailureCount = failed,
            InjectedBytesTotal = bytes,
            DistinctKeywordCount = injected > 0 ? 1 : 0,
            DistinctAgentCount = injected > 0 ? 1 : 0,
            WindowStartUtc = WindowStart,
            WindowEndUtc = WindowEnd,
        };

    private sealed class CountingModel : IClassifierModel
    {
        public int CallCount { get; private set; }

        public string ModelId => "test-counting-model";

        public Task<ModelJudgement> JudgeAsync(ModelJudgementRequest request, CancellationToken ct = default)
        {
            CallCount++;
            throw new InvalidOperationException("本算子问题集为空，模型端口不应被调用。");
        }
    }

    private sealed class FixedPolicyProvider(ThresholdPolicy policy) : IThresholdPolicyProvider
    {
        public ThresholdPolicy? Resolve(string sceneKey) => policy;
    }

    private sealed record ForeignContext : IOperatorContext
    {
        public string SceneKey => "not.skill.value";

        public string InputDigest => "foreign-digest";
    }
}
