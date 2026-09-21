using System.Reflection;
using PuddingCode.Operators;
using PuddingRuntime.Operators;

namespace PuddingRuntimeTests.Operators;

/// <summary>
/// S1a 基类守卫测试：分层约束（6 项 abstract + 唯一可变点）、降级契约（不冒泡）、
/// 缓存短路、审计旁挂、输入不匹配。
/// </summary>
[TestClass]
public sealed class OperatorBaseInfrastructureTests
{
    private static OperatorEnvironment Env(
        IClassifierModel? model = null,
        IOperatorJudgementCache? cache = null,
        IOperatorHealthObserver? health = null,
        IOperatorAuditSink? audit = null,
        TimeProvider? clock = null,
        TimeSpan? timeout = null,
        int modelRetryLimit = 0)
        => new()
        {
            Model = model,
            Cache = cache,
            Health = health,
            Audit = audit,
            Clock = clock ?? new FixedClock(OperatorTestData.Origin),
            Timeout = timeout ?? TimeSpan.FromSeconds(5),
            ModelRetryLimit = modelRetryLimit,
        };

    // ────────────────────────── 分层守卫 ──────────────────────────

    [TestMethod]
    public void OperatorBase_SixDeclarations_AreAbstract_AndEveryOtherMemberIsNonVirtual()
    {
        var baseType = typeof(OperatorBase<,>);
        var whitelist = new HashSet<string>(
            ["SceneKey", "Instruction", "OutputShape", "Threshold", "ProjectInput", "Project", "ClassifyCoreAsync"],
            StringComparer.Ordinal);

        foreach (var name in whitelist)
        {
            var members = baseType.GetMember(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            Assert.AreEqual(1, members.Length, $"{name} 必须恰好声明一次。");

            var isAbstract = members[0] switch
            {
                PropertyInfo property => property.GetMethod?.IsAbstract == true,
                MethodInfo method => method.IsAbstract,
                _ => false,
            };

            Assert.IsTrue(isAbstract, $"{name} 必须是 abstract：给了默认值基类就会变成上帝类，把场景差异压成开关。");
        }

        foreach (var member in baseType.GetMembers(
                     BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
        {
            if (whitelist.Contains(member.Name))
            {
                continue;
            }

            switch (member)
            {
                // 判据用「可覆盖」而不是裸 IsVirtual：C# 会把隐式接口实现发射为 virtual+final
                // （IsVirtual=true、IsFinal=true）——这类成员派生类无法覆盖，语义上仍是非虚。
                case PropertyInfo property:
                    Assert.IsFalse(
                        IsOverridable(property.GetMethod),
                        $"属性 {property.Name} 不得可覆盖：横切职责必须非虚。");
                    break;
                case MethodInfo method when !method.IsSpecialName:
                    Assert.IsFalse(
                        IsOverridable(method),
                        $"方法 {method.Name} 不得可覆盖：唯一可变点是 ClassifyCoreAsync。");
                    break;
            }
        }
    }

    [TestMethod]
    public void DerivedOperators_OverrideNothingOutsideTheWhitelist()
    {
        var whitelist = new HashSet<string>(
            ["SceneKey", "Instruction", "OutputShape", "Threshold", "ProjectInput", "Project", "ClassifyCoreAsync"],
            StringComparer.Ordinal);

        var assemblies = new[] { typeof(OperatorBase<,>).Assembly, typeof(OperatorBaseInfrastructureTests).Assembly };
        var scanned = 0;
        var violations = new List<string>();

        foreach (var assembly in assemblies)
        {
            foreach (var type in GetLoadableTypes(assembly))
            {
                if (!IsOperatorBaseDerived(type))
                {
                    continue;
                }

                scanned++;
                foreach (var member in type.GetMembers(
                             BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                {
                    foreach (var overridden in OverriddenMemberNames(member))
                    {
                        if (!whitelist.Contains(overridden))
                        {
                            violations.Add($"{type.FullName}.{overridden}");
                        }
                    }
                }
            }
        }

        Assert.IsTrue(scanned >= 3, $"守卫必须扫到真实派生类型（实际 {scanned} 个）——空跑不算通过。");
        Assert.AreEqual(0, violations.Count, "出现白名单外的成员覆盖：" + string.Join(", ", violations));
    }

    // ────────────────────────── 降级契约（不冒泡） ──────────────────────────

    [TestMethod]
    public async Task CoreThrow_ReturnsDegradedResult_WithoutBubbling()
    {
        var judge = new ProbeJudge(
            Env(model: new CountingModel(() => OperatorTestData.Answer(0.95d))),
            OperatorTestData.Policy(),
            (_, _, _) => throw new InvalidOperationException("boom"));

        var result = await judge.JudgeAsync(new ProbeContext());

        Assert.AreEqual(JudgeOutcome.Unknown, result.Outcome);
        Assert.AreEqual(OperatorReasonCodes.CoreFailure, result.Envelope.ReasonCode);
        Assert.IsTrue(result.Envelope.Reason.Contains("boom", StringComparison.Ordinal), "降级理由必须保留原始异常信息。");
        Assert.IsNull(result.Threshold);
    }

    [TestMethod]
    public async Task Cancellation_ReturnsDegradedResult_WithoutBubbling()
    {
        var judge = new ProbeJudge(
            Env(model: new CountingModel(() => OperatorTestData.Answer(0.95d))),
            OperatorTestData.Policy());

        using var preCancelled = new CancellationTokenSource();
        await preCancelled.CancelAsync();
        var beforeStart = await judge.JudgeAsync(new ProbeContext(), preCancelled.Token);
        Assert.AreEqual(OperatorReasonCodes.Cancelled, beforeStart.Envelope.ReasonCode);
        Assert.AreEqual(JudgeOutcome.Unknown, beforeStart.Outcome);

        using var midFlight = new CancellationTokenSource();
        var cancelling = new ProbeJudge(
            Env(model: new CountingModel(() => OperatorTestData.Answer(0.95d))),
            OperatorTestData.Policy(),
            async (_, _, ct) =>
            {
                await midFlight.CancelAsync();
                await Task.Delay(TimeSpan.FromSeconds(5), ct);
            });

        var duringRun = await cancelling.JudgeAsync(new ProbeContext(), midFlight.Token);
        Assert.AreEqual(OperatorReasonCodes.Cancelled, duringRun.Envelope.ReasonCode);
        Assert.AreEqual(JudgeOutcome.Unknown, duringRun.Outcome);
    }

    [TestMethod]
    public async Task Timeout_ReturnsDegradedResult_WithoutBubbling()
    {
        var judge = new ProbeJudge(
            Env(
                model: new CountingModel(() => OperatorTestData.Answer(0.95d)),
                timeout: TimeSpan.FromMilliseconds(40)),
            OperatorTestData.Policy(),
            async (_, _, ct) => await Task.Delay(TimeSpan.FromSeconds(5), ct));

        var result = await judge.JudgeAsync(new ProbeContext());

        Assert.AreEqual(OperatorReasonCodes.Timeout, result.Envelope.ReasonCode);
        Assert.AreEqual(JudgeOutcome.Unknown, result.Outcome);
    }

    [TestMethod]
    public async Task ModelFailure_ReturnsDegradedResult_WithoutBubbling()
    {
        var model = new CountingModel(() => throw new InvalidOperationException("model down"));
        var judge = new ProbeJudge(Env(model: model), OperatorTestData.Policy());

        var result = await judge.JudgeAsync(new ProbeContext());

        Assert.AreEqual(1, model.Calls);
        Assert.AreEqual(OperatorReasonCodes.ModelUnavailable, result.Envelope.ReasonCode);
        Assert.AreEqual(JudgeOutcome.Unknown, result.Outcome);
    }

    [TestMethod]
    public async Task InvalidThresholdPolicy_IsRejected_InsteadOfProducingAllYes()
    {
        var invalid = new ThresholdPolicy { PolicyId = "p-invalid", Version = 1, YesAtOrAbove = 0.2d, NoAtOrBelow = 0.8d };
        var judge = new ProbeJudge(
            Env(model: new CountingModel(() => OperatorTestData.Answer(0.99d))),
            invalid);

        var result = await judge.JudgeAsync(new ProbeContext());

        Assert.AreEqual(OperatorReasonCodes.InvalidThreshold, result.Envelope.ReasonCode);
        Assert.AreEqual(JudgeOutcome.Unknown, result.Outcome);
        Assert.AreNotEqual(JudgeOutcome.Yes, result.Outcome);
    }

    [TestMethod]
    public async Task ContextMismatch_ReturnsDegradedResult_InsteadOfThrowing()
    {
        var judge = new ProbeJudge(Env(model: new CountingModel(() => OperatorTestData.Answer(0.95d))), OperatorTestData.Policy());

        var foreign = await judge.JudgeAsync(new ForeignContext());
        Assert.AreEqual(OperatorReasonCodes.ContextMismatch, foreign.Envelope.ReasonCode);
        Assert.AreEqual(JudgeOutcome.Unknown, foreign.Outcome);

        var missing = await judge.JudgeAsync(null!);
        Assert.AreEqual(OperatorReasonCodes.ContextMismatch, missing.Envelope.ReasonCode);
    }

    // ────────────────────────── 横切行为 ──────────────────────────

    [TestMethod]
    public async Task CacheHit_MarksCached_AndShortCircuitsModelCall()
    {
        var cache = new InMemoryOperatorJudgementCache();
        var model = new CountingModel(() => OperatorTestData.Answer(0.95d, 0.5d));
        var judge = new ProbeJudge(Env(model: model, cache: cache), OperatorTestData.Policy());

        var first = await judge.JudgeAsync(new ProbeContext());
        var second = await judge.JudgeAsync(new ProbeContext());

        Assert.AreEqual(1, model.Calls, "第二次必须被缓存短路（模型只调一次）。");
        Assert.AreEqual(1, cache.Count);
        Assert.IsFalse(first.Envelope.Cached);
        Assert.IsTrue(second.Envelope.Cached);
        Assert.AreEqual(JudgeOutcome.Yes, second.Outcome);

        // 缓存短路的是模型调用；纯投影仍执行（确定性），因此投影次数为 2
        Assert.AreEqual(2, judge.ProjectCalls);
    }

    [TestMethod]
    public async Task AuditFailure_IsSwallowed_AndDecisionStaysIntact()
    {
        var judge = new ProbeJudge(
            Env(
                model: new CountingModel(() => OperatorTestData.Answer(0.95d, 0.5d)),
                audit: new ThrowingAuditSink()),
            OperatorTestData.Policy());

        var result = await judge.JudgeAsync(new ProbeContext());

        Assert.AreEqual(JudgeOutcome.Yes, result.Outcome, "审计写入失败不得改变已定裁决（裁决先于留痕）。");
        Assert.IsNull(result.Envelope.ReasonCode);
    }

    [TestMethod]
    public async Task HealthAndAudit_ObserveSuccess_AndTheirOwnFailuresAreIsolated()
    {
        var health = new RecordingHealthObserver();
        var audit = new RecordingAuditSink();
        var judge = new ProbeJudge(
            Env(model: new CountingModel(() => OperatorTestData.Answer(0.95d, 0.5d)), health: health, audit: audit),
            OperatorTestData.Policy());

        var result = await judge.JudgeAsync(new ProbeContext());

        Assert.AreEqual(1, health.Samples.Count);
        Assert.IsTrue(health.Samples[0].Succeeded);
        Assert.AreEqual(result.Envelope.JudgementId, audit.Records[0].JudgementId);
        Assert.IsNull(audit.Records[0].ReasonCode);

        var breakingHealth = new ProbeJudge(
            Env(
                model: new CountingModel(() => OperatorTestData.Answer(0.95d, 0.5d)),
                health: new ThrowingHealthObserver()),
            OperatorTestData.Policy());

        var stillDecided = await breakingHealth.JudgeAsync(new ProbeContext());
        Assert.AreEqual(JudgeOutcome.Yes, stillDecided.Outcome, "健康上报失败不得影响裁决。");
    }

    [TestMethod]
    public async Task Envelope_CarriesIdentityThresholdAndClockBasedLatency()
    {
        var clock = new FixedClock(OperatorTestData.Origin);
        var policy = OperatorTestData.Policy();
        var judge = new ProbeJudge(
            Env(model: new CountingModel(() => OperatorTestData.Answer(0.95d, 0.5d)), clock: clock),
            policy,
            (_, _, _) =>
            {
                clock.Advance(TimeSpan.FromMilliseconds(250));
                return Task.CompletedTask;
            });

        var result = await judge.JudgeAsync(new ProbeContext());

        Assert.AreEqual(JudgeOutcome.Yes, result.Outcome);
        Assert.AreEqual(250d, result.Envelope.LatencyMs ?? -1d, "耗时必须来自注入时钟。");
        Assert.AreEqual("digest-1", result.Envelope.InputDigest);
        Assert.AreEqual(7, result.Envelope.InstructionVersion);
        Assert.AreEqual("test-model", result.Envelope.ModelId);
        Assert.AreEqual("ws-1", result.Envelope.Identity!.WorkspaceId);
        Assert.AreEqual("policy-1", result.Threshold!.PolicyId);
        Assert.AreEqual(0.8d, result.Threshold!.YesAtOrAbove);
        Assert.AreEqual(0.2d, result.Threshold!.NoAtOrBelow);
        Assert.AreEqual(JudgementEnvelope.CurrentSchemaVersion, result.Envelope.SchemaVersion);
        Assert.AreEqual(
            OperatorScope.BuildJudgementId(result.Envelope.OperatorId, result.Envelope.OperatorVersion, 7, "digest-1"),
            result.Envelope.JudgementId);
    }

    [TestMethod]
    public async Task ScorerAndClassifierProjections_ProduceDegradedShapes()
    {
        var scorer = new ProbeScorer(Env(), (_, _, _) => throw new InvalidOperationException("boom"));
        var score = await scorer.ScoreAsync(new ProbeContext());
        Assert.AreEqual("degraded", score.Scale);
        Assert.AreEqual(0d, score.Score);
        Assert.AreEqual(OperatorReasonCodes.CoreFailure, score.Envelope.ReasonCode);

        var classifier = new ProbeClassifier(Env(), (_, _, _) => throw new InvalidOperationException("boom"));
        var classification = await classifier.ClassifyAsync(new ProbeContext());
        Assert.IsNull(classification.PrimaryLabel);
        Assert.IsEmpty(classification.Distribution);
        Assert.AreEqual(OperatorReasonCodes.CoreFailure, classification.Envelope.ReasonCode);

        // 正常路径：三个投影基类的正常输出各就其位
        var healthyScorer = await new ProbeScorer(Env()).ScoreAsync(new ProbeContext());
        Assert.AreEqual("0..1", healthyScorer.Scale);
        Assert.AreEqual(0.42d, healthyScorer.Score);

        var healthyClassifier = await new ProbeClassifier(Env()).ClassifyAsync(new ProbeContext());
        Assert.AreEqual("risk", healthyClassifier.PrimaryLabel);
        Assert.AreEqual(0.9d, healthyClassifier.Distribution["risk"]);
    }

    // ────────────────────────── 反射辅助 ──────────────────────────

    private static IEnumerable<Type> GetLoadableTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            return ex.Types.Where(type => type is not null).Select(type => type!);
        }
    }

    private static bool IsOperatorBaseDerived(Type type)
    {
        for (var current = type.BaseType; current is not null; current = current.BaseType)
        {
            if (current.IsGenericType && current.GetGenericTypeDefinition() == typeof(OperatorBase<,>))
            {
                return true;
            }
        }

        return false;
    }

    private static IEnumerable<string> OverriddenMemberNames(MemberInfo member)
    {
        switch (member)
        {
            case MethodInfo method when !method.IsSpecialName && IsOverride(method):
                yield return method.Name;
                break;
            case PropertyInfo property:
                foreach (var accessor in new[] { property.GetMethod, property.SetMethod })
                {
                    if (accessor is not null && IsOverride(accessor))
                    {
                        yield return property.Name;
                        break;
                    }
                }

                break;
        }
    }

    /// <summary>是否可被派生类覆盖（virtual 且非 final）。virtual+final（隐式接口实现）不可覆盖，算非虚。</summary>
    private static bool IsOverridable(MethodInfo? method)
        => method is not null && method.IsVirtual && !method.IsFinal;

    private static bool IsOverride(MethodInfo method)
        => method.IsVirtual
           && !method.IsAbstract
           && method.GetBaseDefinition().DeclaringType != method.DeclaringType;
}
