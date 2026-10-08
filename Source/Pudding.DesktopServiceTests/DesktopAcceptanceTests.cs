using Pudding.DesktopService;

namespace DesktopServiceTests;

/// <summary>
/// 两段式验收判定（项目强制规则的可执行版本）。
/// 这里钉住的是**拒绝**：内部 Agent 不能声称"新构建已加载"，也不能判定自身生命周期。
/// </summary>
public sealed class DesktopAcceptanceTests
{
    private static DesktopAcceptance.Evidence Evidence(
        DesktopAcceptance.Stage stage,
        DesktopAcceptance.Claimant claimant = DesktopAcceptance.Claimant.InternalAgent,
        string? buildId = "build-1234",
        IReadOnlyList<string>? checks = null,
        bool ranOnANewBuild = false,
        bool exercisesLifecycle = false) =>
        new(stage, claimant, buildId, checks ?? ["dotnet test ... 276/276"], ranOnANewBuild, exercisesLifecycle);

    [Fact]
    public void ReadyForExternalDeploy_NeedsABuildIdAndAtLeastOneCheck()
    {
        Assert.True(DesktopAcceptance.CanClaim(
            Evidence(DesktopAcceptance.Stage.ReadyForExternalDeploy), out var ok), ok);
        Assert.Null(ok);

        Assert.False(DesktopAcceptance.CanClaim(
            Evidence(DesktopAcceptance.Stage.ReadyForExternalDeploy, buildId: "  "), out var noBuild));
        Assert.Contains("build", noBuild);

        Assert.False(DesktopAcceptance.CanClaim(
            Evidence(DesktopAcceptance.Stage.ReadyForExternalDeploy, checks: []), out var noChecks));
        Assert.Contains("at least one recorded check", noChecks);
    }

    [Fact]
    public void InProductFunctionalComplete_RequiresANewBuild()
    {
        // 最容易骗过自己的一条：在**旧进程**上看到功能可用 ≠ 新代码已被加载。
        Assert.False(DesktopAcceptance.CanClaim(
            Evidence(DesktopAcceptance.Stage.InProductFunctionalComplete, ranOnANewBuild: false), out var reason));
        Assert.Contains("newly started build", reason);

        Assert.True(DesktopAcceptance.CanClaim(
            Evidence(DesktopAcceptance.Stage.InProductFunctionalComplete, ranOnANewBuild: true), out var ok), ok);
    }

    [Fact]
    public void InternalAgent_CannotClaimLifecycleVerification()
    {
        // 承载自身的进程判定不了自己的启动/重启/崩溃恢复/退出回收（单实例转发、退出后无法观察子进程）。
        Assert.False(DesktopAcceptance.CanClaim(
            Evidence(
                DesktopAcceptance.Stage.ExternalLifecycleVerified,
                DesktopAcceptance.Claimant.InternalAgent,
                ranOnANewBuild: true,
                exercisesLifecycle: true),
            out var reason));
        Assert.Contains("external controller", reason);
    }

    [Fact]
    public void ExternalController_NeedsToActuallyExerciseTheLifecycle()
    {
        Assert.False(DesktopAcceptance.CanClaim(
            Evidence(
                DesktopAcceptance.Stage.ExternalLifecycleVerified,
                DesktopAcceptance.Claimant.ExternalController,
                exercisesLifecycle: false),
            out var reason));
        Assert.Contains("exercise the lifecycle", reason);

        Assert.True(DesktopAcceptance.CanClaim(
            Evidence(
                DesktopAcceptance.Stage.ExternalLifecycleVerified,
                DesktopAcceptance.Claimant.ExternalController,
                exercisesLifecycle: true),
            out var ok), ok);
    }

    [Fact]
    public void UnregisteredValuesAreRefused()
    {
        Assert.False(DesktopAcceptance.CanClaim(
            new DesktopAcceptance.Evidence(
                (DesktopAcceptance.Stage)99,
                DesktopAcceptance.Claimant.InternalAgent,
                "build",
                ["check"],
                true,
                true),
            out var reason));
        Assert.Contains("registered", reason);

        Assert.False(DesktopAcceptance.CanClaim(
            new DesktopAcceptance.Evidence(
                DesktopAcceptance.Stage.ReadyForExternalDeploy,
                (DesktopAcceptance.Claimant)99,
                "build",
                ["check"],
                false,
                false),
            out _));
    }

    [Fact]
    public void StageNamesMatchTheDeliveryVocabulary()
    {
        Assert.Equal("ready-for-external-deploy",
            DesktopAcceptance.NameOf(DesktopAcceptance.Stage.ReadyForExternalDeploy));
        Assert.Equal("in-product-functional-complete",
            DesktopAcceptance.NameOf(DesktopAcceptance.Stage.InProductFunctionalComplete));
        Assert.Equal("external-lifecycle-verified",
            DesktopAcceptance.NameOf(DesktopAcceptance.Stage.ExternalLifecycleVerified));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => DesktopAcceptance.NameOf((DesktopAcceptance.Stage)99));
    }
}
