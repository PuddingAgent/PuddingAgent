using Pudding.DesktopService;

namespace DesktopServiceTests;

/// <summary>
/// 交付清单生成器（B7 交付链的最后一块）：
/// 事实汇总 → 清单 → **同一套校验自检**；不合格就不产出清单。
/// </summary>
public sealed class DeliveryManifestBuilderTests
{
    private static DeliveryBuildFacts Facts(
        string? buildId = "build-2026-10-08",
        string? commit = "58aa44f",
        IReadOnlyList<string>? artifacts = null,
        IReadOnlyList<DeliveryCheck>? checks = null,
        IReadOnlyList<string>? changelogs = null,
        IReadOnlyList<string>? additional = null,
        IReadOnlyList<string>? steps = null) =>
        new(
            buildId,
            commit,
            artifacts ?? ["PuddingDesktop.exe"],
            checks ?? [new DeliveryCheck("全部套件", 1_845, 0)],
            changelogs ?? ["- **真机未验证**：CDP 行为需外部 smoke 复核。"],
            additional,
            steps);

    [Fact]
    public void BuildsAManifestThatCarriesTheUnverifiedItemsFromTheLog()
    {
        Assert.True(DeliveryManifestBuilder.TryBuild(Facts(), out var manifest, out var problems), string.Join("; ", problems));

        Assert.NotNull(manifest);
        Assert.Equal("build-2026-10-08", manifest!.BuildId);
        Assert.Contains(manifest.KnownUnverified!, item => item.Contains("真机未验证", StringComparison.Ordinal));

        // 生命周期四步在调用方没给时**自动填上**（这条不能靠记性）。
        Assert.Equal(DeliveryManifest.RequiredLifecycleSteps, manifest.RequiredExternalSteps);

        // 产物可序列化给外部控制器（含中文、阶段用交付用语）。
        var json = DeliveryManifestValidator.Serialize(manifest);
        Assert.Contains("ready-for-external-deploy", json);
        Assert.Contains("真机未验证", json);
    }

    [Fact]
    public void AdditionalUnverifiedItemsAreMergedAndDeduplicated()
    {
        Assert.True(DeliveryManifestBuilder.TryBuild(
            Facts(additional: ["本机无 WebView2", "真机未验证：CDP 行为需外部 smoke 复核。"]),
            out var manifest,
            out _));

        var items = manifest!.KnownUnverified!;
        Assert.Equal(2, items.Count);
        Assert.Contains("本机无 WebView2", items);
        // 与日志提取出来的重复项只保留一份。
        Assert.Equal(1, items.Count(item => item.Contains("CDP 行为", StringComparison.Ordinal)));
    }

    [Fact]
    public void RefusesToBuildWithoutTheMinimumFacts()
    {
        Assert.False(DeliveryManifestBuilder.TryBuild(Facts(buildId: " "), out var noBuild, out var buildProblems));
        Assert.Null(noBuild);
        Assert.Contains(buildProblems, problem => problem.Contains("buildId", StringComparison.Ordinal));

        Assert.False(DeliveryManifestBuilder.TryBuild(Facts(artifacts: []), out _, out var artifactProblems));
        Assert.Contains(artifactProblems, problem => problem.Contains("artifact", StringComparison.Ordinal));

        Assert.False(DeliveryManifestBuilder.TryBuild(Facts(checks: []), out _, out var checkProblems));
        Assert.Contains(checkProblems, problem => problem.Contains("check", StringComparison.Ordinal));
    }

    [Fact]
    public void FailingChecksMakeTheBuildRefuseToProduceAManifest()
    {
        // 生成器不自己写规则：它把判断交给同一套校验器，因此"有失败项"照样拦得住。
        Assert.False(DeliveryManifestBuilder.TryBuild(
            Facts(checks: [new DeliveryCheck("全部套件", 1_800, 45)]),
            out var manifest,
            out var problems));

        Assert.Null(manifest);
        Assert.Contains(problems, problem => problem.Contains("failing checks", StringComparison.Ordinal));
    }

    [Fact]
    public void EmptyChangelogsStillProduceAnExplicitKnownUnverifiedField()
    {
        // 空列表 = "确实没有未验证项"，不是 null（清单要求显式给出）。
        Assert.True(DeliveryManifestBuilder.TryBuild(Facts(changelogs: []), out var manifest, out _));
        Assert.NotNull(manifest!.KnownUnverified);
        Assert.Empty(manifest.KnownUnverified!);
    }

    [Fact]
    public void CustomExternalStepsAreKeptWhenTheyCoverTheLifecycle()
    {
        Assert.True(DeliveryManifestBuilder.TryBuild(
            Facts(steps: ["startup", "restart", "crash-recovery", "exit-reclamation", "uninstall"]),
            out var manifest,
            out _));
        Assert.Contains("uninstall", manifest!.RequiredExternalSteps);

        // 覆盖不全时由校验器拦下（生成器不重复实现规则）。
        Assert.False(DeliveryManifestBuilder.TryBuild(Facts(steps: ["startup"]), out _, out var problems));
        Assert.Contains(problems, problem => problem.Contains("lifecycle verdict steps", StringComparison.Ordinal));
    }

    [Fact]
    public void NullFactsAreRefusedInsteadOfThrowing()
    {
        Assert.Throws<ArgumentNullException>(() => DeliveryManifestBuilder.TryBuild(null!, out _, out _));
    }
}
