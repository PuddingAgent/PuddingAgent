using PuddingDesktop.Hosting;

namespace PuddingDesktop.Tests.Hosting;

/// <summary>
/// Guards the rule that an answering guardian is not automatically *our* guardian.
/// The regression it locks: a guardian left over from the previous desktop run keeps
/// adopting the dead pid, passes every port/installation check, and the live desktop
/// silently ends up unmanaged.
/// </summary>
public sealed class GuardianAdoptionPlanTests
{
    private const int CurrentPid = 4242;

    [Fact]
    public void Decide_SupervisedPidIsCurrent_IsAdoptedByCurrentProcess()
    {
        var adoption = GuardianAdoptionPlan.Decide(CurrentPid, supervisedDesktopPid: CurrentPid, supervisedProcessAlive: true);

        Assert.Equal(GuardianAdoption.AdoptedByCurrentProcess, adoption);
    }

    /// <summary>
    /// Identity must win over the liveness probe. Probing our own pid would introduce a
    /// race for no gain, and a false "not alive" reading would fail a healthy startup.
    /// </summary>
    [Fact]
    public void Decide_IdentityWins_EvenWhenLivenessProbeSaysDead()
    {
        var adoption = GuardianAdoptionPlan.Decide(CurrentPid, supervisedDesktopPid: CurrentPid, supervisedProcessAlive: false);

        Assert.Equal(GuardianAdoption.AdoptedByCurrentProcess, adoption);
    }

    [Fact]
    public void Decide_MissingPid_IsUnknown()
    {
        var adoption = GuardianAdoptionPlan.Decide(CurrentPid, supervisedDesktopPid: null, supervisedProcessAlive: false);

        Assert.Equal(GuardianAdoption.Unknown, adoption);
    }

    [Fact]
    public void Decide_OtherLiveDesktop_IsOwnedByAnotherLiveDesktop()
    {
        var adoption = GuardianAdoptionPlan.Decide(CurrentPid, supervisedDesktopPid: 31432, supervisedProcessAlive: true);

        Assert.Equal(GuardianAdoption.OwnedByAnotherLiveDesktop, adoption);
    }

    [Fact]
    public void Decide_OtherExitedDesktop_IsAdoptsExitedDesktop()
    {
        var adoption = GuardianAdoptionPlan.Decide(CurrentPid, supervisedDesktopPid: 31432, supervisedProcessAlive: false);

        Assert.Equal(GuardianAdoption.AdoptsExitedDesktop, adoption);
    }

    [Theory]
    [InlineData(GuardianAdoption.AdoptedByCurrentProcess)]
    [InlineData(GuardianAdoption.Unknown)]
    public void FailureMessage_ReusableAdoptions_AreNotReportedAsFailure(GuardianAdoption adoption)
    {
        // Unknown must stay silent: an older guardian build simply omits the field and a
        // healthy startup must not be failed by an absent property.
        Assert.Null(GuardianAdoptionPlan.FailureMessage(adoption, supervisedDesktopPid: null, port: 8200, currentProcessId: CurrentPid));
    }

    [Fact]
    public void FailureMessage_ExitedDesktop_NamesBothPids()
    {
        var message = GuardianAdoptionPlan.FailureMessage(
            GuardianAdoption.AdoptsExitedDesktop, supervisedDesktopPid: 31432, port: 8200, currentProcessId: CurrentPid);

        Assert.NotNull(message);
        Assert.Contains("31432", message);
        Assert.Contains(CurrentPid.ToString(), message);
        Assert.Contains("8200", message);
    }

    [Fact]
    public void FailureMessage_LiveOtherDesktop_ExplainsItWillNotTakeOver()
    {
        var message = GuardianAdoptionPlan.FailureMessage(
            GuardianAdoption.OwnedByAnotherLiveDesktop, supervisedDesktopPid: 31432, port: 8200, currentProcessId: CurrentPid);

        Assert.NotNull(message);
        Assert.Contains("still running", message);
    }
}
