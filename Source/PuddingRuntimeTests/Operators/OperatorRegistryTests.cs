using PuddingCode.Classification;
using PuddingCode.Operators;
using PuddingRuntime.Operators;
using PuddingRuntime.Operators.Adapters;

namespace PuddingRuntimeTests.Operators;

/// <summary>
/// S1b 交付物 1 的守卫测试：注册期 fail-closed 三条（重复键 / 一个类型多原语端口 / 空场景键）
/// + 解析语义（命中 / 未命中 / 端口不符）。
/// <para>
/// 为什么这些守卫要单独测：它们拦的是「装配错误」，而装配错误在运行期表现为「某个场景静默走了默认分支」——
/// 那正是安全关键路径上最不该发生的失败模式。守卫一旦失效，编译器与既有测试都发现不了。
/// </para>
/// </summary>
[TestClass]
public sealed class OperatorRegistryTests
{
    // ---------- 守卫 3：空 / 空白场景键 ----------

    [TestMethod]
    public void Register_NullOrBlankSceneKey_IsRejected()
    {
        var registry = new OperatorRegistry();

        Assert.ThrowsExactly<ArgumentException>(() => registry.Register<IClassifier>(null!, new ClassifierOnlyOperator()));
        Assert.ThrowsExactly<ArgumentException>(() => registry.Register<IClassifier>(string.Empty, new ClassifierOnlyOperator()));
        Assert.ThrowsExactly<ArgumentException>(() => registry.Register<IClassifier>("   ", new ClassifierOnlyOperator()));

        Assert.IsTrue(registry.IsEmpty, "被拒绝的注册不得留下任何痕迹（不得部分写入）。");
        Assert.ThrowsExactly<ArgumentException>(() => registry.Resolve<IClassifier>(" "));
        Assert.ThrowsExactly<ArgumentException>(() => registry.TryResolve<IClassifier>("\t", out _));
    }

    [TestMethod]
    public void Register_NullOperator_IsRejected()
    {
        var registry = new OperatorRegistry();

        Assert.ThrowsExactly<ArgumentNullException>(() => registry.Register<IClassifier>("scene-a", null!));
        Assert.IsTrue(registry.IsEmpty);
    }

    // ---------- 守卫 1：重复场景键 ----------

    [TestMethod]
    public void Register_DuplicateSceneKey_IsRejected_AndFirstRegistrationSurvives()
    {
        var registry = new OperatorRegistry();
        var first = new ClassifierOnlyOperator(operatorId: "first");
        registry.Register<IClassifier>("scene-a", first);

        var thrown = Assert.ThrowsExactly<InvalidOperationException>(
            () => registry.Register<IClassifier>("scene-a", new ClassifierOnlyOperator(operatorId: "second")));

        StringAssert.Contains(thrown.Message, "scene-a", "重复注册的错误必须点名冲突的场景键。");
        Assert.AreEqual(1, registry.Count);
        Assert.AreSame(first, registry.Resolve<IClassifier>("scene-a"), "重复注册被拒后必须保留先注册的实例（不得覆盖）。");
    }

    // ---------- 守卫 2：一个类型最多实现一个原语端口 ----------

    [TestMethod]
    public void Register_TypeImplementingTwoPrimitivePorts_IsRejected_AndRegistryStaysEmpty()
    {
        var registry = new OperatorRegistry();

        var thrown = Assert.ThrowsExactly<InvalidOperationException>(
            () => registry.Register<TwoPrimitivePortOperator>("scene-two-ports", new TwoPrimitivePortOperator()));

        StringAssert.Contains(thrown.Message, nameof(IClassifier), "错误必须点名冲突的原语端口。");
        StringAssert.Contains(thrown.Message, nameof(IJudge), "错误必须点名冲突的原语端口。");
        Assert.IsTrue(registry.IsEmpty, "守卫 2 拒绝必须发生在写入之前（不得部分写入）。");
        Assert.IsEmpty(registry.SceneKeys);
    }

    [TestMethod]
    public void Register_NonPrimitiveOperator_IsAllowedButUnreachableThroughAnyPrimitivePort()
    {
        // 规格只要求拒绝「>1 个原语端口」；实现 0 个原语端口属可观测的装配冗余：
        // 它能在注册表里存在，但任何原语端口都解析不到（不会把语义不符的实例塞给调用方）。
        var registry = new OperatorRegistry();
        registry.Register<NonPrimitiveOperator>("scene-non-primitive", new NonPrimitiveOperator());

        Assert.AreEqual(1, registry.Count);
        Assert.IsFalse(registry.TryResolve<IClassifier>("scene-non-primitive", out _));
        Assert.IsFalse(registry.TryResolve<IJudge>("scene-non-primitive", out _));
        Assert.IsFalse(registry.TryResolve<IScorer>("scene-non-primitive", out _));
    }

    // ---------- 解析语义 ----------

    [TestMethod]
    public void TryResolve_HitReturnsSameInstance_AndMissReturnsFalse()
    {
        var registry = new OperatorRegistry();
        var classifier = new ClassifierOnlyOperator(operatorId: "scene-a-op");
        var scorer = new ScorerOnlyOperator();
        registry.Register<IClassifier>("scene-a", classifier);
        registry.Register<IScorer>("scene-b", scorer);

        Assert.IsTrue(registry.TryResolve<IClassifier>("scene-a", out var resolved));
        Assert.AreSame(classifier, resolved);

        Assert.IsTrue(registry.TryResolve<IScorer>("scene-b", out var resolvedScorer));
        Assert.AreSame(scorer, resolvedScorer);

        // 未注册场景 ⇒ 未命中（不抛）
        Assert.IsFalse(registry.TryResolve<IClassifier>("scene-missing", out var missing));
        Assert.IsNull(missing);

        // 端口不符 ⇒ 按「未命中该端口」处理（调用方问的是「这个场景有没有会分类的算子」，答案是「没有」）
        Assert.IsFalse(registry.TryResolve<IJudge>("scene-a", out _));
        Assert.IsFalse(registry.TryResolve<IClassifier>("scene-b", out _));
    }

    [TestMethod]
    public void Resolve_MissingOrPortMismatch_ThrowsFailClosed()
    {
        var registry = new OperatorRegistry();
        registry.Register<IClassifier>("scene-a", new ClassifierOnlyOperator());

        var missing = Assert.ThrowsExactly<InvalidOperationException>(() => registry.Resolve<IClassifier>("scene-missing"));
        StringAssert.Contains(missing.Message, "scene-missing", "错误必须点名缺失的场景键。");

        var portMismatch = Assert.ThrowsExactly<InvalidOperationException>(() => registry.Resolve<IJudge>("scene-a"));
        StringAssert.Contains(portMismatch.Message, nameof(IJudge));
        StringAssert.Contains(portMismatch.Message, "scene-a");
    }

    [TestMethod]
    public void SceneKeys_IsDeterministicSortedSnapshot()
    {
        var registry = new OperatorRegistry();
        registry.Register<IClassifier>("scene-c", new ClassifierOnlyOperator());
        registry.Register<IClassifier>("scene-a", new ClassifierOnlyOperator());
        registry.Register<IClassifier>("scene-b", new ClassifierOnlyOperator());

        CollectionAssert.AreEqual(
            new[] { "scene-a", "scene-b", "scene-c" },
            registry.SceneKeys.ToArray(),
            "场景键快照必须确定性排序（否则健康面 / 诊断输出会随机抖动）。");

        // 快照语义：注册新场景后，先前取到的快照不得随之变化
        var snapshot = registry.SceneKeys;
        registry.Register<IClassifier>("scene-d", new ClassifierOnlyOperator());
        Assert.AreEqual(3, snapshot.Count, "SceneKeys 必须是快照，不是活视图。");
        Assert.AreEqual(4, registry.SceneKeys.Count);
    }

    // ---------- 与真实适配器的集成：注册表真能承载新端口的真实消费者 ----------

    [TestMethod]
    public async Task Register_RealAdapter_ResolvesThroughPort_AndKeepsEquivalence()
    {
        var verdict = ToolApprovalAdapterTestData.Cases[0].Verdict;
        var inner = new StubToolCallClassifier(verdict);
        var adapter = new ToolApprovalOperatorAdapter(inner, new FixedClock(OperatorTestData.Origin));

        var registry = new OperatorRegistry();
        registry.Register<IClassifier>(ToolApprovalOperatorAdapter.SceneKeyValue, adapter);

        CollectionAssert.AreEqual(new[] { "tool_approval" }, registry.SceneKeys.ToArray());

        var viaPort = registry.Resolve<IClassifier>(ToolApprovalOperatorAdapter.SceneKeyValue);
        Assert.AreSame(adapter, viaPort, "解析必须返回同一实例（注册表只是索引，不克隆算子）。");
        Assert.IsFalse(registry.TryResolve<IJudge>(ToolApprovalOperatorAdapter.SceneKeyValue, out _));

        var context = ToolApprovalOperatorContext.Create(ToolApprovalAdapterTestData.Cases[0].Context);
        var result = await viaPort.ClassifyAsync(context);

        Assert.AreEqual("allow_once", result.PrimaryLabel);
        Assert.AreEqual(verdict.Reason, result.Envelope.Reason);
        Assert.IsNull(result.Envelope.Score);
        Assert.IsNull(result.Envelope.Threshold);
    }

    // ---------- 测试替身 ----------

    /// <summary>只实现分类端口的算子。</summary>
    private sealed class ClassifierOnlyOperator(string operatorId = "op-classifier") : IClassifier
    {
        public string OperatorId { get; } = operatorId;

        public Task<ClassificationResult> ClassifyAsync(IOperatorContext context, CancellationToken ct = default)
            => Task.FromResult(SampleResults.Classification());
    }

    /// <summary>只实现打分端口的算子。</summary>
    private sealed class ScorerOnlyOperator : IScorer
    {
        public string OperatorId => "op-scorer";

        public Task<ScoreResult> ScoreAsync(IOperatorContext context, CancellationToken ct = default)
            => Task.FromResult(SampleResults.Score());
    }

    /// <summary><b>违规</b>算子：一个类型同时实现两个原语端口（守卫 2 必须拒绝它）。</summary>
    private sealed class TwoPrimitivePortOperator : IClassifier, IJudge
    {
        public string OperatorId => "op-two-ports";

        public Task<ClassificationResult> ClassifyAsync(IOperatorContext context, CancellationToken ct = default)
            => Task.FromResult(SampleResults.Classification());

        public Task<JudgeResult> JudgeAsync(IOperatorContext context, CancellationToken ct = default)
            => Task.FromResult(SampleResults.Judgement());
    }

    /// <summary>不实现任何原语端口的算子（可注册，但任何端口都解析不到）。</summary>
    private sealed class NonPrimitiveOperator : IOperator
    {
        public string OperatorId => "op-plain";
    }
}

/// <summary>构造最小投影结果（复用 S1a 的样例作用域，避免重复拼装信封字段）。</summary>
internal static class SampleResults
{
    private static JudgementEnvelope Envelope() => OperatorTestData.Scope()
        .BuildEnvelope(new EnvelopeDraft { Reason = "样例结论" });

    public static ClassificationResult Classification() => new(null, new Dictionary<string, double>(), Envelope());

    public static JudgeResult Judgement() => new(JudgeOutcome.Unknown, null, null, null, ConfidenceKind.Unknown, Envelope());

    public static ScoreResult Score() => new("0..1", 0.5d, Envelope());
}
