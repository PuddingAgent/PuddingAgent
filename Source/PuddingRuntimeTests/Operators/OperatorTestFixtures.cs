using PuddingCode.Operators;
using PuddingRuntime.Operators;

namespace PuddingRuntimeTests.Operators;

/// <summary>可控时钟：让耗时断言确定化（不用 sleep 猜时间）。</summary>
internal sealed class FixedClock : TimeProvider
{
    private DateTimeOffset _now;

    public FixedClock(DateTimeOffset now) => _now = now;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan delta) => _now = _now.Add(delta);
}

/// <summary>探针场景上下文（含溯源身份四元组）。</summary>
internal sealed class ProbeContext : IOperatorContext, IOperatorIdentityContext
{
    public string SceneKey { get; init; } = "probe-scene";

    public string InputDigest { get; init; } = "digest-1";

    public OperatorIdentity Identity { get; init; } = new()
    {
        WorkspaceId = "ws-1",
        SessionId = "session-1",
        AgentInstanceId = "agent-1",
        UserId = "user-1",
    };
}

/// <summary>另一个场景上下文：用于验证「类型不匹配 ⇒ 降级」。</summary>
internal sealed class ForeignContext : IOperatorContext
{
    public string SceneKey => "foreign-scene";

    public string InputDigest => "foreign-digest";
}

/// <summary>测试数据工厂。</summary>
internal static class OperatorTestData
{
    public static readonly DateTimeOffset Origin = new(2026, 9, 21, 4, 0, 0, TimeSpan.Zero);

    public static ModelJudgement Empty { get; } = new() { Answers = [], ModelId = "none", LatencyMs = 0 };

    public static ModelJudgement Answer(double score, double? confidence = null) => new()
    {
        Answers = [new ModelAnswer { QuestionKey = "q1", Score = score, Confidence = confidence }],
        ModelId = "test-model",
        LatencyMs = 7,
        RawDigest = "raw-digest",
    };

    public static ThresholdPolicy Policy(double yes = 0.8, double no = 0.2)
        => ThresholdPolicy.Create("policy-1", 3, yes, no, "high_risk", "probe-scene");

    public static OperatorScope Scope(string inputDigest = "digest-1", TimeProvider? clock = null) => new()
    {
        OperatorId = "PuddingRuntimeTests.Operators.ProbeOperator",
        OperatorVersion = "1.0.0",
        SceneKey = "probe-scene",
        InputDigest = inputDigest,
        InstructionVersion = 7,
        RenderedInput = "projected:" + inputDigest,
        Identity = new ProbeContext().Identity,
        Threshold = Policy().ToApplied(),
        StartedAtUtc = Origin,
        Clock = clock ?? new FixedClock(Origin),
        Cancellation = CancellationToken.None,
    };
}

/// <summary>统计调用次数的模型端口。</summary>
internal sealed class CountingModel : IClassifierModel
{
    private readonly Func<ModelJudgement> _factory;

    public CountingModel(Func<ModelJudgement> factory) => _factory = factory;

    public string ModelId => "test-model";

    public int Calls { get; private set; }

    public Task<ModelJudgement> JudgeAsync(ModelJudgementRequest request, CancellationToken ct = default)
    {
        Calls++;
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(_factory());
    }
}

/// <summary>记录采样并正常返回的健康端口。</summary>
internal sealed class RecordingHealthObserver : IOperatorHealthObserver
{
    public List<OperatorHealthSample> Samples { get; } = [];

    public void Report(OperatorHealthSample sample) => Samples.Add(sample);
}

/// <summary>总是抛异常的健康端口（验证旁挂失败被隔离）。</summary>
internal sealed class ThrowingHealthObserver : IOperatorHealthObserver
{
    public void Report(OperatorHealthSample sample) => throw new InvalidOperationException("health down");
}

/// <summary>记录审计记录并正常返回的审计端口。</summary>
internal sealed class RecordingAuditSink : IOperatorAuditSink
{
    public List<OperatorAuditRecord> Records { get; } = [];

    public void Write(OperatorAuditRecord record) => Records.Add(record);
}

/// <summary>总是抛异常的审计端口（验证「裁决先于留痕」不回退）。</summary>
internal sealed class ThrowingAuditSink : IOperatorAuditSink
{
    public void Write(OperatorAuditRecord record) => throw new InvalidOperationException("audit down");
}

/// <summary>
/// 探针判断算子：只实现 6 项声明 + 唯一可变点 <c>ClassifyCoreAsync</c>，行为由构造参数注入。
/// </summary>
internal sealed class ProbeJudge : JudgeBase<ProbeContext>
{
    private readonly ThresholdPolicy? _policy;
    private readonly Func<ProbeContext, OperatorScope, CancellationToken, Task>? _coreHook;

    public ProbeJudge(
        OperatorEnvironment environment,
        ThresholdPolicy? policy = null,
        Func<ProbeContext, OperatorScope, CancellationToken, Task>? coreHook = null)
        : base(environment)
    {
        _policy = policy;
        _coreHook = coreHook;
    }

    public int ProjectCalls { get; private set; }

    protected override string SceneKey => "probe-scene";

    protected override OperatorInstruction Instruction => new()
    {
        Text = "判断该动作是否安全。",
        Version = 7,
        Questions = [new JudgementQuestion { Key = "q1", Prompt = "是否安全？", Choices = ["yes", "no"] }],
    };

    protected override OperatorOutputShape OutputShape => new() { ScoreScale = "0..1" };

    protected override ThresholdPolicy? Threshold => _policy;

    protected override string ProjectInput(ProbeContext context) => "projected:" + context.InputDigest;

    protected override JudgeResult Project(ModelJudgement judgement, ProbeContext context, OperatorScope scope)
    {
        ProjectCalls++;

        var answer = judgement.Answers.Count > 0 ? judgement.Answers[0] : null;
        var outcome = answer?.Score is { } score && Threshold is not null
            ? Threshold.Apply(score)
            : JudgeOutcome.Unknown;

        return new JudgeResult(
            outcome,
            answer?.Score,
            scope.Threshold,
            answer?.Confidence,
            ConfidenceKind.ModelSelfReported,
            scope.BuildEnvelope(new EnvelopeDraft
            {
                Reason = "投影完成",
                Outcome = outcome,
                Score = answer?.Score,
                ScoreScale = OutputShape.ScoreScale,
                Confidence = answer?.Confidence,
                ConfidenceKind = ConfidenceKind.ModelSelfReported,
            }));
    }

    protected override async Task<JudgeResult> ClassifyCoreAsync(ProbeContext context, OperatorScope scope, CancellationToken ct)
    {
        if (_coreHook is not null)
        {
            await _coreHook(context, scope, ct).ConfigureAwait(false);
        }

        return Project(scope.ModelJudgement ?? OperatorTestData.Empty, context, scope);
    }
}

/// <summary>探针打分算子（无问题集 ⇒ 不调用模型）。</summary>
internal sealed class ProbeScorer : ScorerBase<ProbeContext>
{
    private readonly Func<ProbeContext, OperatorScope, CancellationToken, Task>? _coreHook;

    public ProbeScorer(OperatorEnvironment environment, Func<ProbeContext, OperatorScope, CancellationToken, Task>? coreHook = null)
        : base(environment)
    {
        _coreHook = coreHook;
    }

    protected override string SceneKey => "probe-scene";

    protected override OperatorInstruction Instruction => new() { Text = "给该动作打分。", Version = 1 };

    protected override OperatorOutputShape OutputShape => new() { ScoreScale = "0..1" };

    protected override ThresholdPolicy? Threshold => null;

    protected override string ProjectInput(ProbeContext context) => "projected:" + context.InputDigest;

    protected override ScoreResult Project(ModelJudgement judgement, ProbeContext context, OperatorScope scope)
        => new(
            "0..1",
            0.42d,
            scope.BuildEnvelope(new EnvelopeDraft { Reason = "打分完成", ScoreScale = "0..1", Score = 0.42d }));

    protected override async Task<ScoreResult> ClassifyCoreAsync(ProbeContext context, OperatorScope scope, CancellationToken ct)
    {
        if (_coreHook is not null)
        {
            await _coreHook(context, scope, ct).ConfigureAwait(false);
        }

        return Project(scope.ModelJudgement ?? OperatorTestData.Empty, context, scope);
    }
}

/// <summary>探针分类算子（无问题集 ⇒ 不调用模型）。</summary>
internal sealed class ProbeClassifier : ClassifierBase<ProbeContext>
{
    private readonly Func<ProbeContext, OperatorScope, CancellationToken, Task>? _coreHook;

    public ProbeClassifier(OperatorEnvironment environment, Func<ProbeContext, OperatorScope, CancellationToken, Task>? coreHook = null)
        : base(environment)
    {
        _coreHook = coreHook;
    }

    protected override string SceneKey => "probe-scene";

    protected override OperatorInstruction Instruction => new() { Text = "给该动作分类。", Version = 1 };

    protected override OperatorOutputShape OutputShape => new() { Labels = ["risk", "safe"] };

    protected override ThresholdPolicy? Threshold => null;

    protected override string ProjectInput(ProbeContext context) => "projected:" + context.InputDigest;

    protected override ClassificationResult Project(ModelJudgement judgement, ProbeContext context, OperatorScope scope)
        => new(
            "risk",
            new Dictionary<string, double> { ["risk"] = 0.9d, ["safe"] = 0.1d },
            scope.BuildEnvelope(new EnvelopeDraft { Reason = "分类完成", PrimaryLabel = "risk" }));

    protected override async Task<ClassificationResult> ClassifyCoreAsync(ProbeContext context, OperatorScope scope, CancellationToken ct)
    {
        if (_coreHook is not null)
        {
            await _coreHook(context, scope, ct).ConfigureAwait(false);
        }

        return Project(scope.ModelJudgement ?? OperatorTestData.Empty, context, scope);
    }
}
