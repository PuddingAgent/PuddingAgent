namespace PuddingDesktop.Hosting;

/// <summary>
/// How a guardian that already answers on the control port relates to the desktop
/// process that is starting right now.
/// </summary>
public enum GuardianAdoption
{
    /// <summary>The guardian supervises this process; reuse it as-is.</summary>
    AdoptedByCurrentProcess,

    /// <summary>
    /// The guardian did not report which desktop it supervises: either an older
    /// build without the field, or a fresh guardian that has not published its
    /// first identity snapshot yet. Acceptable, so a normal startup is never
    /// failed by a field that is simply absent.
    /// </summary>
    Unknown,

    /// <summary>Another desktop instance is still alive and owns this guardian.</summary>
    OwnedByAnotherLiveDesktop,

    /// <summary>
    /// The guardian adopted a desktop that has already exited. It answers on the
    /// port, so the startup path used to accept it as healthy, yet it supervises
    /// nothing: the live desktop is unmanaged and every later restart is invisible
    /// to it. This is the silent variant of "guardian is down", so it must be
    /// reported instead of silently accepted.
    /// </summary>
    AdoptsExitedDesktop,
}

/// <summary>
/// Pure decision logic for reusing a guardian that is already listening. Holds no
/// process handle, HTTP client or clock so every branch is reachable from tests.
/// </summary>
public static class GuardianAdoptionPlan
{
    public static GuardianAdoption Decide(int currentProcessId, int? supervisedDesktopPid, bool supervisedProcessAlive)
    {
        // Identity equality wins over the liveness probe: the caller's own pid is
        // trivially alive, and probing it would only add a race.
        if (supervisedDesktopPid is null) return GuardianAdoption.Unknown;
        if (supervisedDesktopPid.Value == currentProcessId) return GuardianAdoption.AdoptedByCurrentProcess;

        return supervisedProcessAlive
            ? GuardianAdoption.OwnedByAnotherLiveDesktop
            : GuardianAdoption.AdoptsExitedDesktop;
    }

    /// <summary>
    /// Operator-facing text for an adoption that must not be accepted, or null
    /// when the guardian may be reused. Keeping the wording here puts it under
    /// test coverage instead of leaving it unverified inside the WinUI coordinator.
    /// </summary>
    public static string? FailureMessage(GuardianAdoption adoption, int? supervisedDesktopPid, int port, int currentProcessId)
    {
        return adoption switch
        {
            GuardianAdoption.OwnedByAnotherLiveDesktop =>
                $"Guardian on 127.0.0.1:{port} supervises desktop pid {supervisedDesktopPid}, which is still running. "
                + $"This instance (pid {currentProcessId}) stays unmanaged rather than taking over another window's guardian.",
            GuardianAdoption.AdoptsExitedDesktop =>
                $"Guardian on 127.0.0.1:{port} still supervises desktop pid {supervisedDesktopPid}, which has exited. "
                + $"It answers but supervises nothing, so pid {currentProcessId} is unmanaged. "
                + $"Stop that guardian so a fresh one adopts pid {currentProcessId}.",
            _ => null,
        };
    }
}
