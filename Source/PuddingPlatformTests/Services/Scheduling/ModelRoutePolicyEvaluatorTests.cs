using PuddingPlatform.Services.Scheduling;

namespace PuddingPlatformTests.Services.Scheduling;

/// <summary>
/// 阶段感知模型路由求值器的契约测试：确定性、硬门拒绝码、阶段偏好、
/// 以及「无解释模型选择」在结构上不可发生。
/// </summary>
[TestClass]
public sealed class ModelRoutePolicyEvaluatorTests
{
    [TestMethod]
    public void Evaluate_SameInputTwice_ProducesIdenticalDecisionAndFingerprint()
    {
        var policy = RoutePolicyCatalog.For("task-a", "explore");
        var context = new WorkUnitRouteContext("task-a", "explore", null, 1_000);
        var candidates = new[] { Profile("alpha", "small", cost: 1m), Profile("beta", "fast", cost: 4m) };

        var first = ModelRoutePolicyEvaluator.Evaluate(policy, context, candidates);
        var second = ModelRoutePolicyEvaluator.Evaluate(policy, context, candidates);

        Assert.AreEqual(first, second);
        Assert.AreEqual(first.Fingerprint, second.Fingerprint);
    }

    [TestMethod]
    public void Fingerprint_IsIndependentOfCandidateOrder()
    {
        var policy = RoutePolicyCatalog.For("task-a", "explore");
        var context = new WorkUnitRouteContext("task-a", "explore", null, 1_000);
        var alpha = Profile("alpha", "small");
        var beta = Profile("beta", "fast");

        var forward = ModelRoutePolicyEvaluator.Fingerprint(policy, context, [alpha, beta]);
        var reversed = ModelRoutePolicyEvaluator.Fingerprint(policy, context, [beta, alpha]);

        Assert.AreEqual(forward, reversed);
    }

    [TestMethod]
    public void Fingerprint_DistinguishesRiskClassification()
    {
        var policy = RoutePolicyCatalog.For("task-a", "plan");
        var candidates = new[] { Profile("alpha", "big", quality: 0.95m) };

        var high = ModelRoutePolicyEvaluator.Fingerprint(
            policy, new WorkUnitRouteContext("task-a", "plan", "high", 1_000), candidates);
        var low = ModelRoutePolicyEvaluator.Fingerprint(
            policy, new WorkUnitRouteContext("task-a", "plan", "low", 1_000), candidates);

        Assert.AreNotEqual(high, low);
    }

    [TestMethod]
    public void Evaluate_TaskTypeOrPhaseMismatch_IsRejected()
    {
        var policy = RoutePolicyCatalog.For("task-a", "explore");
        var candidates = new[] { Profile("alpha", "small") };

        var taskTypeMismatch = ModelRoutePolicyEvaluator.Evaluate(
            policy, new WorkUnitRouteContext("task-b", "explore", null, 1_000), candidates);
        var phaseMismatch = ModelRoutePolicyEvaluator.Evaluate(
            policy, new WorkUnitRouteContext("task-a", "plan", null, 1_000), candidates);

        Assert.IsFalse(taskTypeMismatch.Selected);
        Assert.AreEqual(
            ModelRoutePolicyEvaluator.PolicyContextMismatchCode, taskTypeMismatch.Code);
        Assert.IsFalse(phaseMismatch.Selected);
        Assert.AreEqual(
            ModelRoutePolicyEvaluator.PolicyContextMismatchCode, phaseMismatch.Code);
    }

    [TestMethod]
    public void Evaluate_MissingCapability_ReturnsCapabilityMissingCode()
    {
        var policy = RoutePolicyCatalog.For("task-a", "explore") with
        {
            RequiredCapabilityTags = ["code"],
        };

        var decision = ModelRoutePolicyEvaluator.Evaluate(
            policy,
            new WorkUnitRouteContext("task-a", "explore", null, 1_000),
            [Profile("alpha", "small", tags: "chat")]);

        Assert.IsFalse(decision.Selected);
        Assert.AreEqual("capability_missing:code", decision.Code);
    }

    [TestMethod]
    public void Evaluate_ContextWindowTooSmall_ReturnsCode()
    {
        var policy = RoutePolicyCatalog.For("task-a", "explore");

        var decision = ModelRoutePolicyEvaluator.Evaluate(
            policy,
            new WorkUnitRouteContext("task-a", "explore", null, 300_000),
            [Profile("alpha", "small", contextWindow: 100_000)]);

        Assert.IsFalse(decision.Selected);
        Assert.AreEqual(ModelRoutePolicyEvaluator.ContextWindowTooSmallCode, decision.Code);
    }

    [TestMethod]
    public void Evaluate_ToolProtocolUnsupported_ReturnsCode()
    {
        var policy = RoutePolicyCatalog.For("task-a", "change");

        var decision = ModelRoutePolicyEvaluator.Evaluate(
            policy,
            new WorkUnitRouteContext("task-a", "change", null, 1_000),
            [Profile("alpha", "small", supportsTools: false)]);

        Assert.IsFalse(decision.Selected);
        Assert.AreEqual(ModelRoutePolicyEvaluator.ToolProtocolUnsupportedCode, decision.Code);
    }

    [TestMethod]
    public void Evaluate_BelowQualityFloor_ReturnsCode()
    {
        var policy = RoutePolicyCatalog.For("task-a", "plan");

        var decision = ModelRoutePolicyEvaluator.Evaluate(
            policy,
            new WorkUnitRouteContext("task-a", "plan", "high", 1_000),
            [Profile("alpha", "cheap", quality: 0.50m)]);

        Assert.IsFalse(decision.Selected);
        Assert.AreEqual(ModelRoutePolicyEvaluator.QualityFloorNotMetCode, decision.Code);
    }

    [TestMethod]
    public void Evaluate_VerifierPhase_RequiresIsolatedReadOnlyRoute()
    {
        var policy = RoutePolicyCatalog.For("task-a", "verify");
        var context = new WorkUnitRouteContext("task-a", "verify", null, 1_000);

        var isolated = ModelRoutePolicyEvaluator.Evaluate(
            policy,
            context,
            [
                Profile("standard-provider", "m", quality: 0.99m),
                Profile("isolated-provider", "m", quality: 0.85m, security: RoutePolicyCatalog.IsolatedReadOnlySecurityTier),
            ]);

        Assert.IsTrue(isolated.Selected);
        Assert.AreEqual("isolated-provider", isolated.ProviderId);

        var noneIsolated = ModelRoutePolicyEvaluator.Evaluate(
            policy, context, [Profile("standard-provider", "m", quality: 0.99m)]);

        Assert.IsFalse(noneIsolated.Selected);
        Assert.AreEqual(ModelRoutePolicyEvaluator.SecurityTierMismatchCode, noneIsolated.Code);
    }

    [TestMethod]
    public void Evaluate_PlanPhase_PrefersHighestQuality_AndExplorePrefersLowestCost()
    {
        var candidates = new[]
        {
            Profile("alpha", "cheap", quality: 0.88m, cost: 0.5m),
            Profile("beta", "strong", quality: 0.97m, cost: 9m),
        };

        var plan = ModelRoutePolicyEvaluator.Evaluate(
            RoutePolicyCatalog.For("task-a", "plan"),
            new WorkUnitRouteContext("task-a", "plan", "high", 1_000),
            candidates);

        Assert.IsTrue(plan.Selected);
        Assert.AreEqual("beta", plan.ProviderId);

        var explore = ModelRoutePolicyEvaluator.Evaluate(
            RoutePolicyCatalog.For("task-a", "explore"),
            new WorkUnitRouteContext("task-a", "explore", null, 1_000),
            candidates);

        Assert.IsTrue(explore.Selected);
        Assert.AreEqual("alpha", explore.ProviderId);
    }

    [TestMethod]
    public void Evaluate_PreferredProvider_WinsTiesBeforeSoftPreference()
    {
        var policy = RoutePolicyCatalog.For("task-a", "change") with
        {
            PreferredProviderIds = ["beta"],
        };

        var decision = ModelRoutePolicyEvaluator.Evaluate(
            policy,
            new WorkUnitRouteContext("task-a", "change", null, 1_000),
            [
                Profile("alpha", "cheap", quality: 0.99m, cost: 0.1m),
                Profile("beta", "strong", quality: 0.80m, cost: 8m),
            ]);

        Assert.IsTrue(decision.Selected);
        Assert.AreEqual("beta", decision.ProviderId);
    }

    [TestMethod]
    public void Evaluate_NoEligibleCandidate_IsNotSelectedAndExplainsWhy()
    {
        var policy = RoutePolicyCatalog.For("task-a", "change");
        var context = new WorkUnitRouteContext("task-a", "change", null, 1_000);

        var decision = ModelRoutePolicyEvaluator.Evaluate(policy, context, []);

        Assert.IsFalse(decision.Selected);
        Assert.AreEqual(ModelRoutePolicyEvaluator.NoCompatibleCode, decision.Code);
        Assert.IsNull(decision.ProviderId);
        Assert.IsNull(decision.ModelId);
        Assert.IsTrue(decision.Reason.Length > 0);
        Assert.AreEqual(64, decision.Fingerprint.Length);
    }

    [TestMethod]
    public void Reason_IsComposedOfEnumeratedTokensOnly()
    {
        var policy = RoutePolicyCatalog.For("Task-A", "Plan");
        var context = new WorkUnitRouteContext("Task-A", "Plan", "high", 1_000);

        var decision = ModelRoutePolicyEvaluator.Evaluate(
            policy, context, [Profile("alpha", "big", quality: 0.95m)]);

        Assert.AreEqual(
            "tasktype=task-a;phase=plan;risk=high;preference=HighestQuality;"
            + "qualityfloor=0.85;contextrequired=1000;security=",
            decision.Reason);
    }

    [TestMethod]
    public void Catalog_VerifierAndToolPhases_DeclareTheirHardGates()
    {
        var verify = RoutePolicyCatalog.For("t", "Verify");
        Assert.IsTrue(verify.RequiresToolProtocol is false);
        Assert.AreEqual(RoutePolicyCatalog.IsolatedReadOnlySecurityTier, verify.RequiredSecurityTier);

        var change = RoutePolicyCatalog.For("t", "change");
        Assert.IsTrue(change.RequiresToolProtocol);

        var deploy = RoutePolicyCatalog.For("t", "deploy");
        Assert.IsTrue(deploy.RequiresToolProtocol);

        var unknown = RoutePolicyCatalog.For("t", "not-a-phase");
        Assert.AreEqual(RouteSelectionPreference.Balanced, unknown.Preference);
        Assert.AreEqual(0.75m, unknown.MinimumQualityScore);
    }

    private static ModelCapabilityProfile Profile(
        string providerId,
        string modelId,
        decimal quality = 0.90m,
        decimal cost = 1m,
        int contextWindow = 100_000,
        bool supportsTools = true,
        string security = RoutePolicyCatalog.StandardSecurityTier,
        params string[] tags)
        => new(
            providerId,
            modelId,
            "openai",
            tags.Length == 0 ? ["chat"] : tags,
            contextWindow,
            supportsTools,
            quality,
            security,
            cost,
            cost);
}
