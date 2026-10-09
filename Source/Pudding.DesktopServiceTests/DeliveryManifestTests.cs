using Pudding.DesktopService;

namespace DesktopServiceTests;

/// <summary>
/// 交付清单校验（B7）：不合格就不该被交出去。这里钉住的是**必需的诚实字段**——
/// 尤其是"哪些结论内部证明不了"必须被明说（null = 没想过 ⇒ 拒绝；空列表 = 确实没有 ⇒ 允许）。
/// </summary>
public sealed class DeliveryManifestTests
{
    private static DeliveryManifest Manifest(
        string buildId = "build-2026-10-08.1",
        string commit = "3dc0bb1",
        IReadOnlyList<string>? artifacts = null,
        IReadOnlyList<DeliveryCheck>? checks = null,
        IReadOnlyList<string>? knownUnverified = null,
        IReadOnlyList<string>? requiredExternalSteps = null,
        DesktopAcceptance.Stage stage = DesktopAcceptance.Stage.ReadyForExternalDeploy) =>
        new(
            buildId,
            commit,
            artifacts ?? ["PuddingDesktop.exe", "PuddingAgent.exe"],
            checks ?? [new DeliveryCheck("dotnet test (all suites)", 1_845, 0)],
            knownUnverified ?? [],
            (knownUnverified ?? []).Count,
            requiredExternalSteps ?? ["startup", "restart", "crash-recovery", "exit-reclamation"],
            changelogFilesScanned: 0,
            changelogFilesTotal: 0,
            stage: stage);

    [Fact]
    public void ACompleteManifestIsAccepted()
    {
        Assert.Empty(DeliveryManifestValidator.Validate(Manifest()));
    }

    [Fact]
    public void TheBuildAndCommitMustBeTraceable()
    {
        var problems = DeliveryManifestValidator.Validate(Manifest(buildId: " ", commit: ""));

        Assert.Contains(problems, problem => problem.Contains("buildId", StringComparison.Ordinal));
        Assert.Contains(problems, problem => problem.Contains("commit", StringComparison.Ordinal));
    }

    [Fact]
    public void FailingChecksAreNeverDeliverable()
    {
        // 有失败项就不是"可交付"，无论别的检查多绿。
        var problems = DeliveryManifestValidator.Validate(
            Manifest(checks: [new DeliveryCheck("unit", 100, 0), new DeliveryCheck("smoke", 3, 2)]));

        Assert.Contains(problems, problem => problem.Contains("failing checks", StringComparison.Ordinal));
    }

    [Fact]
    public void ChecksMustHaveActuallyRun()
    {
        var problems = DeliveryManifestValidator.Validate(
            Manifest(checks: [new DeliveryCheck("nothing", 0, 0)]));

        Assert.Contains(problems, problem => problem.Contains("actually run", StringComparison.Ordinal));

        Assert.NotEmpty(DeliveryManifestValidator.Validate(
            Manifest(checks: [new DeliveryCheck("negative", -1, 0)])));
    }

    [Fact]
    public void KnownUnverifiedMustBeStatedExplicitly()
    {
        // null = 没想过 ⇒ 拒绝；空列表 = 确实没有 ⇒ 允许。这条区分是刻意的。
        // 这里必须**显式构造**（helper 会把 null 折成空列表，那样就测不到 null 了）。
        var missing = DeliveryManifestValidator.Validate(new DeliveryManifest(
            "build", "abc123", ["app.exe"], [new DeliveryCheck("tests", 1, 0)],
            knownUnverified: null,
            knownUnverifiedTotal: 0,
            requiredExternalSteps: ["startup", "restart", "crash-recovery", "exit-reclamation"]));
        Assert.Contains(missing, problem => problem.Contains("knownUnverified", StringComparison.Ordinal));

        Assert.Empty(DeliveryManifestValidator.Validate(Manifest(knownUnverified: [])));
        Assert.Empty(DeliveryManifestValidator.Validate(
            Manifest(knownUnverified: ["真机 CDP 行为未验证"])));

        Assert.NotEmpty(DeliveryManifestValidator.Validate(Manifest(knownUnverified: ["  "])));
    }

    [Fact]
    public void LifecycleVerdictStepsMustBeHandedToTheExternalController()
    {
        var problems = DeliveryManifestValidator.Validate(
            Manifest(requiredExternalSteps: ["startup"]));

        Assert.Contains(
            problems,
            problem => problem.Contains("restart", StringComparison.Ordinal)
                && problem.Contains("crash-recovery", StringComparison.Ordinal));
    }

    [Fact]
    public void AnInternalManifestCannotDeclareALaterStage()
    {
        var problems = DeliveryManifestValidator.Validate(
            Manifest(stage: DesktopAcceptance.Stage.ExternalLifecycleVerified));

        Assert.Contains(
            problems, problem => problem.Contains("ready-for-external-deploy", StringComparison.Ordinal));
    }

    [Fact]
    public void ArtifactsAndChecksAreRequired()
    {
        Assert.NotEmpty(DeliveryManifestValidator.Validate(Manifest(artifacts: [])));
        Assert.NotEmpty(DeliveryManifestValidator.Validate(Manifest(artifacts: ["  "])));
        Assert.NotEmpty(DeliveryManifestValidator.Validate(Manifest(checks: [])));
        Assert.NotEmpty(DeliveryManifestValidator.Validate(Manifest(checks: [new DeliveryCheck("  ", 1, 0)])));
        Assert.NotEmpty(DeliveryManifestValidator.Validate(null));
    }

    [Fact]
    public void SerializationRoundTripsWithCamelCaseKeys()
    {
        var json = DeliveryManifestValidator.Serialize(Manifest(
            knownUnverified: ["真机 CDP 行为未验证"],
            checks: [new DeliveryCheck("automation", 276, 0)]));

        // 外部控制器要能直接读：键名用 camelCase、字段齐全。
        Assert.Contains("\"buildId\"", json);
        Assert.Contains("\"knownUnverified\"", json);
        Assert.Contains("\"requiredExternalSteps\"", json);
        Assert.Contains("真机 CDP 行为未验证", json);
        // 阶段用**交付用语**（外部控制器读的是这套词，不该猜枚举名）。`r`n        Assert.Contains("ready-for-external-deploy", json);
    }

    [Fact]
    public void DeserializationKeepsTheCorpusCountsAndRetryNotes()
    {
        // 取红用例（round-128）：这三个字段曾经**写得进、读不回**（反序列化时没有传给构造器），
        // 后果是外部一侧永远看不到"重跑后才绿"，也没有语料规模 —— 静默丢字段。
        var baseline = Manifest();
        var original = new DeliveryManifest(
            baseline.BuildId,
            baseline.Commit,
            baseline.Artifacts,
            baseline.Checks,
            baseline.KnownUnverified,
            baseline.KnownUnverifiedTotal,
            baseline.RequiredExternalSteps,
            changelogFilesScanned: 319,
            changelogFilesTotal: 319,
            retryNotes: ["PuddingHost.Tests : first attempt had 1 failing test(s)"]);

        var json = System.Text.Json.JsonSerializer.Serialize(
            new
            {
                buildId = original.BuildId,
                commit = original.Commit,
                artifacts = original.Artifacts,
                checks = original.Checks.Select(check => new { name = check.Name, passed = check.Passed, failed = check.Failed }),
                knownUnverified = original.KnownUnverified,
                knownUnverifiedTotal = original.KnownUnverifiedTotal,
                requiredExternalSteps = original.RequiredExternalSteps,
                changelogFilesScanned = original.ChangelogFilesScanned,
                changelogFilesTotal = original.ChangelogFilesTotal,
                retryNotes = original.RetryNotes,
            });

        Assert.True(DeliveryManifestValidator.TryDeserialize(json, out var parsed, out var problems),
            string.Join("; ", problems));

        Assert.Equal(319, parsed!.ChangelogFilesScanned);
        Assert.Equal(319, parsed.ChangelogFilesTotal);
        Assert.Single(parsed.RetryNotes);
        Assert.Contains("first attempt had 1 failing test(s)", parsed.RetryNotes[0], StringComparison.Ordinal);
    }
    [Fact]
    public void ATotalSmallerThanTheListedItemsIsRejected()
    {
        // 总数是"实际找到多少条"的声明；比列出条数还小就是自相矛盾 ⇒ 校验必须拒绝。
        var manifest = Manifest(knownUnverified: ["一", "二", "三"]) with { };
        var inconsistent = new DeliveryManifest(
            manifest.BuildId,
            manifest.Commit,
            manifest.Artifacts,
            manifest.Checks,
            manifest.KnownUnverified,
            knownUnverifiedTotal: 2,
            manifest.RequiredExternalSteps);

        var problems = DeliveryManifestValidator.Validate(inconsistent);

        Assert.Contains(problems, problem => problem.Contains("knownUnverifiedTotal", StringComparison.Ordinal));
    }
}
