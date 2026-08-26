using PuddingCode.Scheduling;
using PuddingCode.Tasks;

namespace PuddingPlatform.Services.Scheduling;

/// <summary>
/// Safe first-stage resolver. Explicit anytime work is provable without a
/// pricing profile. Inherited and off-peak-only work stay Unknown until the
/// provider/model route and versioned price-window resolver are installed.
/// </summary>
public sealed class ConservativeExecutionWindowResolver : IExecutionWindowResolver
{
    private static readonly TimeSpan DecisionTtl = TimeSpan.FromSeconds(30);

    public Task<ExecutionWindowDecision> EvaluateAsync(
        string workspaceId,
        string agentId,
        TaskExecutionWindow requestedWindow,
        DateTimeOffset now,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        ct.ThrowIfCancellationRequested();

        return Task.FromResult(requestedWindow == TaskExecutionWindow.Anytime
            ? new ExecutionWindowDecision
            {
                Verdict = ExecutionWindowVerdict.Allow,
                Code = "allowed_anytime",
                EvaluatedAtUtc = now,
                ValidUntilUtc = now.Add(DecisionTtl),
            }
            : new ExecutionWindowDecision
            {
                Verdict = ExecutionWindowVerdict.Unknown,
                Code = requestedWindow == TaskExecutionWindow.OffPeakOnly
                    ? "execution_window_route_profile_unknown"
                    : "execution_window_inherited_policy_unknown",
                EvaluatedAtUtc = now,
                ValidUntilUtc = now,
            });
    }
}
