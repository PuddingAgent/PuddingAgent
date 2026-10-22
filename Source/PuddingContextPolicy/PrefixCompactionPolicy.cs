namespace PuddingContextPolicy;

/// <summary>Thresholds are harness defaults, not provider limits. Pressure uses effective input and upload budgets.</summary>
public sealed record PrefixCompactionThresholds(
    double Prepare = .60, double Commit = .65, double Wait = .70,
    double Target = .45, int MinimumEpochRounds = 4, double UploadWait = .95);

public sealed record PrefixCompactionDecision(
    bool Prepare, bool CommitReady, bool WaitForCandidate, double Pressure, string Reason);

/// <summary>Pure scheduling: speculation never changes the cached prefix; only a boundary commit does.</summary>
public static class PrefixCompactionPolicy
{
    public static PrefixCompactionDecision Decide(
        long tokens, long tokenLimit, long bytes, long byteLimit,
        long growthTokens = 0, long growthBytes = 0, int roundsSinceMaintenance = int.MaxValue,
        PrefixCompactionThresholds? thresholds = null, long effectiveInputLimit = 0)
    {
        var t = thresholds ?? new();
        if (!(0 < t.Prepare && t.Prepare < t.Commit && t.Commit < t.Wait && t.Wait <= 1
            && 0 < t.Target && t.Target < t.Prepare && t.MinimumEpochRounds >= 0
            && t.Commit < t.UploadWait && t.UploadWait <= 1))
            throw new ArgumentOutOfRangeException(nameof(thresholds));
        static double Ratio(long used, long limit) => limit > 0 ? Math.Max(0, (double)used) / limit : 0;
        var pressure = Math.Max(Ratio(tokens, tokenLimit), Ratio(bytes, byteLimit));
        var forecast = Math.Max(Ratio(tokens + Math.Max(0, growthTokens), tokenLimit),
            Ratio(bytes + Math.Max(0, growthBytes), byteLimit));
        var inputLimitReached = effectiveInputLimit > 0 && tokens + Math.Max(0, growthTokens) >= effectiveInputLimit;
        var wait = Ratio(tokens, tokenLimit) >= t.Wait || Ratio(bytes, byteLimit) >= t.UploadWait
            || forecast >= 1 || inputLimitReached;
        var cooldown = roundsSinceMaintenance < t.MinimumEpochRounds;
        return new(wait || (!cooldown && pressure >= t.Prepare),
            wait || (!cooldown && pressure >= t.Commit), wait, pressure,
            wait ? "capacity_reserve" : cooldown ? "prefix_epoch_cooldown" :
            pressure >= t.Commit ? "safe_boundary_commit" : pressure >= t.Prepare ? "prepare_candidate" : "stable_prefix");
    }

    /// <summary>
    /// Explicit append-only admission. Every frozen message, including protected prefix and suffix, must
    /// remain at its exact position. Generation/route/tools/policy must still match. No fuzzy matching.
    /// </summary>
    public static CandidateApplicabilityResult ValidateAppendOnly(
        FrozenCandidateDescriptor frozen, IReadOnlyList<string> frozenHistory, CurrentContextDescriptor current)
    {
        if (current.Generation != frozen.SourceGeneration) return Reject("generation_changed");
        if (current.RouteVersion != frozen.RouteVersion) return Reject("route_changed");
        if (current.ToolSpecVersion != frozen.ToolSpecVersion) return Reject("tool_spec_changed");
        if (current.PolicyVersion != frozen.PolicyVersion) return Reject("policy_changed");
        if (frozenHistory.Count == 0 || current.MessageIdentities.Count < frozenHistory.Count)
            return Reject("frozen_prefix_changed");
        for (var i = 0; i < frozenHistory.Count; i++)
            if (!string.Equals(frozenHistory[i], current.MessageIdentities[i], StringComparison.Ordinal))
                return Reject("frozen_prefix_changed");
        return new(CandidateApplicabilityVerdict.Applicable, "append_only_applicable");
    }

    private static CandidateApplicabilityResult Reject(string reason) => new(CandidateApplicabilityVerdict.StaleCandidate, reason);
}
