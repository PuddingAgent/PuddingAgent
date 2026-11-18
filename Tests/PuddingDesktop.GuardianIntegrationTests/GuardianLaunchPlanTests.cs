using PuddingDesktop.Hosting;

namespace PuddingDesktop.Tests.Hosting;

public sealed class GuardianLaunchPlanTests
{
    [Fact]
    public void Decide_ConnectedWins_EvenIfChildAlreadyExited()
    {
        var decision = GuardianLaunchPlan.Decide(
            connected: true, childExited: true, exitCode: 1, attempt: 3, maxAttempts: 20);

        Assert.Equal(GuardianLaunchDecision.Connected, decision);
    }

    /// <summary>
    /// The guardian returns 0 to say "another process owns the installation
    /// lease". This is the exact case that used to be reported as a generic
    /// endpoint timeout, so it must stay distinguishable.
    /// </summary>
    [Fact]
    public void Decide_ChildExitZero_ReportsLeaseOwner()
    {
        var decision = GuardianLaunchPlan.Decide(
            connected: false, childExited: true, exitCode: 0, attempt: 2, maxAttempts: 20);

        Assert.Equal(GuardianLaunchDecision.FailLeaseOwner, decision);
    }

    [Fact]
    public void Decide_ChildExitNonZero_ReportsChildExited()
    {
        var decision = GuardianLaunchPlan.Decide(
            connected: false, childExited: true, exitCode: 1, attempt: 2, maxAttempts: 20);

        Assert.Equal(GuardianLaunchDecision.FailChildExited, decision);
    }

    [Fact]
    public void Decide_ChildStillAliveBeforeBudget_KeepsWaiting()
    {
        var decision = GuardianLaunchPlan.Decide(
            connected: false, childExited: false, exitCode: null, attempt: 0, maxAttempts: 20);

        Assert.Equal(GuardianLaunchDecision.KeepWaiting, decision);
    }

    [Fact]
    public void Decide_ChildStillAliveOnLastAttempt_FailsWithTimeout()
    {
        var decision = GuardianLaunchPlan.Decide(
            connected: false, childExited: false, exitCode: null, attempt: 19, maxAttempts: 20);

        Assert.Equal(GuardianLaunchDecision.FailTimeout, decision);
    }

    [Fact]
    public void Decide_SecondToLastAttemptStillWaits()
    {
        var decision = GuardianLaunchPlan.Decide(
            connected: false, childExited: false, exitCode: null, attempt: 18, maxAttempts: 20);

        Assert.Equal(GuardianLaunchDecision.KeepWaiting, decision);
    }

    [Fact]
    public void Decide_SingleAttemptBudgetFailsImmediately()
    {
        var decision = GuardianLaunchPlan.Decide(
            connected: false, childExited: false, exitCode: null, attempt: 0, maxAttempts: 1);

        Assert.Equal(GuardianLaunchDecision.FailTimeout, decision);
    }

    [Fact]
    public void Decide_NullExitCodeWithLiveChild_IsNeverTreatedAsLeaseOwner()
    {
        var decision = GuardianLaunchPlan.Decide(
            connected: false, childExited: false, exitCode: null, attempt: 5, maxAttempts: 20);

        Assert.NotEqual(GuardianLaunchDecision.FailLeaseOwner, decision);
        Assert.NotEqual(GuardianLaunchDecision.FailChildExited, decision);
    }

    [Fact]
    public void DefaultMaxAttempts_MatchesThePollingBudgetUsedByTheCoordinator()
    {
        Assert.Equal(20, GuardianLaunchPlan.DefaultMaxAttempts);
    }
}
