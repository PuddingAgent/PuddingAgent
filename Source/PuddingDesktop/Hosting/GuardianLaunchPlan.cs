namespace PuddingDesktop.Hosting;

/// <summary>
/// Outcome of one probe while waiting for a freshly spawned guardian to expose
/// its control endpoint.
/// </summary>
public enum GuardianLaunchDecision
{
    /// <summary>The endpoint answered; the guardian is managed.</summary>
    Connected,

    /// <summary>The child is still starting up; probe again.</summary>
    KeepWaiting,

    /// <summary>
    /// The child exited with code 0, which is how the guardian reports that
    /// another process already owns the installation lease. This is a deliberate
    /// non-error exit and must be surfaced as such: collapsing it into a generic
    /// "endpoint did not appear" timeout is what made a real outage
    /// indistinguishable from a crash.
    /// </summary>
    FailLeaseOwner,

    /// <summary>The child exited with a non-zero code before listening.</summary>
    FailChildExited,

    /// <summary>The probe budget ran out while the child was still alive.</summary>
    FailTimeout,
}

/// <summary>
/// Pure decision logic for guardian startup. Holds no process, HTTP client or
/// clock so the exit-code branch can be tested directly.
/// </summary>
public static class GuardianLaunchPlan
{
    public const int DefaultMaxAttempts = 20;

    public static GuardianLaunchDecision Decide(
        bool connected,
        bool childExited,
        int? exitCode,
        int attempt,
        int maxAttempts)
    {
        if (connected) return GuardianLaunchDecision.Connected;

        if (childExited)
        {
            return exitCode == 0
                ? GuardianLaunchDecision.FailLeaseOwner
                : GuardianLaunchDecision.FailChildExited;
        }

        return attempt + 1 >= maxAttempts
            ? GuardianLaunchDecision.FailTimeout
            : GuardianLaunchDecision.KeepWaiting;
    }

    /// <summary>
    /// Builds the operator-facing message for a failed launch, or null when the
    /// caller should keep going. Keeping the wording here means the diagnostic
    /// text is covered by tests instead of living unverified inside the WinUI
    /// coordinator, where no test project can reach it.
    /// </summary>
    public static string? FailureMessage(GuardianLaunchDecision decision, int? exitCode, int port)
    {
        return decision switch
        {
            GuardianLaunchDecision.FailLeaseOwner =>
                $"Another guardian owns the installation lease and is not answering on 127.0.0.1:{port}. "
                + "Look for a lease-conflict record in the guardian state directory and for a stale guardian process.",
            GuardianLaunchDecision.FailChildExited =>
                $"Guardian exited with code {exitCode} before exposing its endpoint.",
            _ => null,
        };
    }
}
