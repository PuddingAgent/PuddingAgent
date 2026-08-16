using PuddingCode.Scheduling;
using PuddingCode.Tasks;

namespace PuddingPlatform.Services.Tasks;

/// <summary>
/// TB-05: 手工派发 Fence 占位实现 — 始终 allow（allowed_user_direct）。
/// <para>
/// 完整 WorkAdmissionFence（峰谷/优先级/可用性判定）留待 AU-01（ADR-072 ST-01.4）；
/// 本实现只满足「手工派发闭环」的最小 stub 需求，手工派发不经过峰谷拦截。
/// </para>
/// </summary>
public sealed class ManualAlwaysAllowFence : IWorkAdmissionFence
{
    /// <inheritdoc />
    public Task<WorkAdmissionDecision> EvaluateAsync(
        WorkAdmissionFenceInput input,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        return Task.FromResult(WorkAdmissionDecision.Allow(
            DecisionCode.AllowedUserDirect,
            validUntilUtc: null,
            reason: "Manual dispatch always allowed (stub fence; full fence is AU-01)."));
    }
}
