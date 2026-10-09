using Pudding.DesktopService;

namespace DesktopServiceTests;

/// <summary>
/// 外部验收会话（B7 收口）：内部交付清单 + 外部 smoke 清单 + 外部观察事实 → 结论。
/// 这里钉住的是四条**拒绝**：未执行不等于通过、有失败即拒、构建不一致即拒、生命周期缺证据即拒。
/// </summary>
public sealed class ExternalAcceptanceSessionTests
{
    private static DeliveryManifest Manifest() => new(
        "build-2026-10-09",
        "34c8326",
        ["PuddingDesktop.exe", "PuddingAgent.exe"],
        [new DeliveryCheck("全部套件", 2_100, 0)],
        ["真机未验证：需要外部 smoke"],
        1,
        DeliveryManifest.RequiredLifecycleSteps);

    private static IReadOnlyList<SmokeObservation> AllObserved(
        SmokeObservationOutcome outcome = SmokeObservationOutcome.Passed,
        string evidence = "已按清单观察，现象与期望一致") =>
        BrowserSmokePlan.Checks.Select(check => new SmokeObservation(check.Id, outcome, evidence)).ToArray();

    private static IReadOnlyList<LifecycleEvidence> AllLifecycle() =>
        DeliveryManifest.RequiredLifecycleSteps
            .Select(step => new LifecycleEvidence(step, Passed: true, $"外部控制器记录了 {step} 的事实"))
            .ToArray();

    private static ExternalAcceptanceInput Input(
        IReadOnlyList<SmokeObservation>? observations = null,
        IReadOnlyList<LifecycleEvidence>? lifecycle = null,
        string observedBuildId = "build-2026-10-09") =>
        new(Manifest(), observedBuildId, observations ?? AllObserved(), lifecycle ?? AllLifecycle());

    [Fact]
    public void ACompleteExternalRunIsConcluded()
    {
        Assert.True(ExternalAcceptanceSession.TryConclude(Input(), out var problems), string.Join("; ", problems));
        Assert.Empty(problems);
    }

    [Fact]
    public void NotRunningAnObservationIsNotAPass()
    {
        // "没测" 不等于 "通过" —— 这是最容易被含糊带过的一条。
        var observations = AllObserved().ToArray();
        observations[3] = new SmokeObservation(observations[3].CheckId, SmokeObservationOutcome.NotRun, null);

        Assert.False(ExternalAcceptanceSession.TryConclude(Input(observations), out var problems));
        Assert.Contains(problems, problem => problem.Contains("was not run", StringComparison.Ordinal));

        // 完全缺失某条观察，同样拒绝。
        var missing = AllObserved().Where(observation => observation.CheckId != "S-08").ToArray();
        Assert.Contains(
            ExternalAcceptanceSession.TryConclude(Input(missing), out var missingProblems) ? [] : missingProblems,
            problem => problem.Contains("S-08", StringComparison.Ordinal));
    }

    [Fact]
    public void AFailedObservationBlocksTheVerdict()
    {
        var observations = AllObserved().ToArray();
        observations[7] = new SmokeObservation(observations[7].CheckId, SmokeObservationOutcome.Failed, "零位移却报告成功");

        Assert.False(ExternalAcceptanceSession.TryConclude(Input(observations), out var problems));
        Assert.Contains(problems, problem => problem.Contains("failed", StringComparison.Ordinal));
    }

    [Fact]
    public void PassingWithoutEvidenceIsRefused()
    {
        var observations = AllObserved(evidence: "   ");
        Assert.False(ExternalAcceptanceSession.TryConclude(Input(observations), out var problems));
        Assert.Contains(problems, problem => problem.Contains("without recorded evidence", StringComparison.Ordinal));

        var weakLifecycle = DeliveryManifest.RequiredLifecycleSteps
            .Select(step => new LifecycleEvidence(step, Passed: true, Evidence: null))
            .ToArray();
        Assert.False(ExternalAcceptanceSession.TryConclude(Input(lifecycle: weakLifecycle), out var lifecycleProblems));
        Assert.Contains(
            lifecycleProblems,
            problem => problem.Contains("without any recorded evidence", StringComparison.Ordinal));
    }

    [Fact]
    public void ObservationsFromAnotherBuildAreRefused()
    {
        // 旧构建上的观察不能证明新构建（这正是两段式验收要防的那件事）。
        Assert.False(ExternalAcceptanceSession.TryConclude(
            Input(observedBuildId: "build-2026-10-08"), out var problems));

        Assert.Contains(problems, problem => problem.Contains("delivered manifest", StringComparison.Ordinal));

        Assert.False(ExternalAcceptanceSession.TryConclude(Input(observedBuildId: "  "), out var blank));
        Assert.Contains(blank, problem => problem.Contains("must name the build", StringComparison.Ordinal));
    }

    [Fact]
    public void MissingOrFailingLifecycleStepsBlockTheVerdict()
    {
        Assert.False(ExternalAcceptanceSession.TryConclude(Input(lifecycle: []), out var missing));
        Assert.Contains(missing, problem => problem.Contains("crash-recovery", StringComparison.Ordinal));

        var failing = AllLifecycle().ToArray();
        failing[1] = failing[1] with { Passed = false };
        Assert.False(ExternalAcceptanceSession.TryConclude(Input(lifecycle: failing), out var failed));
        Assert.Contains(failed, problem => problem.Contains("did not pass", StringComparison.Ordinal));
    }

    [Fact]
    public void DuplicateAndUnknownObservationsAreRefused()
    {
        var duplicated = AllObserved().Append(new SmokeObservation("S-01", SmokeObservationOutcome.Passed, "又看了一遍")).ToArray();
        Assert.Contains(
            ExternalAcceptanceSession.TryConclude(Input(duplicated), out var duplicateProblems) ? [] : duplicateProblems,
            problem => problem.Contains("recorded 2 times", StringComparison.Ordinal));

        var unknown = AllObserved().Append(new SmokeObservation("S-99", SmokeObservationOutcome.Passed, "某条")).ToArray();
        Assert.Contains(
            ExternalAcceptanceSession.TryConclude(Input(unknown), out var unknownProblems) ? [] : unknownProblems,
            problem => problem.Contains("unknown smoke checks", StringComparison.Ordinal));
    }

    [Fact]
    public void TheAcceptanceRecordRendersEveryCheckAndStep()
    {
        var input = Input();
        ExternalAcceptanceSession.TryConclude(input, out _);
        var markdown = ExternalAcceptanceSession.ToMarkdown(input, concluded: true);

        foreach (var check in BrowserSmokePlan.Checks)
        {
            Assert.Contains(check.Id, markdown);
        }

        foreach (var step in DeliveryManifest.RequiredLifecycleSteps)
        {
            Assert.Contains(step, markdown);
        }

        Assert.Contains("external-lifecycle-verified", markdown);
        Assert.Contains("build-2026-10-09", markdown);

        // 未通过的渲染也要如实（含缺失步骤）。
        var unverified = ExternalAcceptanceSession.ToMarkdown(Input(lifecycle: []), concluded: false);
        Assert.Contains("缺证据", unverified);
        Assert.Contains("未通过", unverified);
    }
}
