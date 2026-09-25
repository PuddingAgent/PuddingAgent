using PuddingFullTextIndex.Contracts;

namespace PuddingFullTextIndex.Infrastructure.Supply;

/// <summary>
/// 供给 job 状态机。<b>唯一</b>的合法流转表。
/// <para>
/// 合法：<c>Queued → Running</c>、<c>Queued → Cancelled</c>、<c>Running → Succeeded|Failed|Cancelled</c>。
/// 终态（Succeeded / Failed / Cancelled）不可再流转；违反者必须被拒绝并给出可读消息（不允许静默）。
/// </para>
/// </summary>
internal static class SupplyJobStateMachine
{
    internal static bool IsTerminal(SupplyJobState state) =>
        state is SupplyJobState.Succeeded or SupplyJobState.Failed or SupplyJobState.Cancelled;

    internal static bool CanTransition(SupplyJobState from, SupplyJobState to) => (from, to) switch
    {
        (SupplyJobState.Queued, SupplyJobState.Running) => true,
        (SupplyJobState.Queued, SupplyJobState.Cancelled) => true,
        (SupplyJobState.Running, SupplyJobState.Succeeded) => true,
        (SupplyJobState.Running, SupplyJobState.Failed) => true,
        (SupplyJobState.Running, SupplyJobState.Cancelled) => true,
        _ => false,
    };

    internal static string DescribeIllegalTransition(SupplyJobState from, SupplyJobState to) =>
        $"非法状态转换被拒绝：{from} → {to}"
        + "（合法路径：Queued → Running → Succeeded|Failed|Cancelled；终态不可再流转）";
}
