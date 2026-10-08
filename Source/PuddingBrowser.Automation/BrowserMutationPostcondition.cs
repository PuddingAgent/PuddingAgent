using Pudding.Contracts;
using Pudding.Contracts.Desktop;

namespace PuddingBrowser.Automation;

/// <summary>
/// 调用方**显式声明**的变更类后置条件（设计方案 §1.1/§4.2）。
///
/// 为什么需要它：原实现用「所有 mutating 结果版本必须严格递增」当通用后置条件，等于**按动作类别
/// 推断必然导航**。实测反例：<c>click</c> 只打开菜单、SPA 内部更新、<c>fill</c> 只改 value，
/// 版本都不会推进 ⇒ 动作成功却被判成 <c>internal_error</c>；而导航竞态下读到旧版本同样会假失败。
/// 后置条件必须由调用方声明、由观测事实判定。
/// </summary>
public enum BrowserMutationExpectation
{
    /// <summary>未声明后置条件：<b>不得</b>因版本未推进而失败。</summary>
    None,

    /// <summary>显式声明「本动作应提交新文档」（当前仅 navigate 使用）。</summary>
    DocumentNavigation,
}

/// <summary>一次变更类动作的观测事实。</summary>
public sealed record BrowserMutationOutcome(
    BrowserAutomationOperation Operation,
    DesktopPageVersion PinnedVersion,
    DesktopPageVersion ObservedVersion,
    BrowserMutationExpectation Expectation = BrowserMutationExpectation.None,
    bool NavigationObserved = false);

/// <summary>
/// 变更类动作的**后置条件**（设计方案 §4.2「按动作验证、按事实等待」）。
///
/// 两条传输（Bridge 与 DesktopService）必须调用<b>同一份</b>判据，否则映射层会各自再验出
/// 相反的结论 —— 审计已证实这正是「同一动作一条路成功、另一条路失败」的来源。
/// </summary>
public static class BrowserMutationPostcondition
{
    /// <summary>
    /// 校验一次变更类动作的结果；返回 <c>null</c> 表示接受。
    ///
    /// 判据：
    /// • <b>没有活版本是真问题</b>：引用必须带活版本，否则旧引用无法作废、新引用无法建立；
    /// • 观测到文档提交（或版本推进）⇒ 满足任何期望；
    /// • 未声明后置条件 ⇒ 版本未推进<b>不是</b>失败（fill/type/select/check/hover/scroll、
    ///   以及只开菜单或触发 SPA 更新的 click/press 都属于这一类）；
    /// • 只有声明了「应提交新文档」却没观测到提交时才失败，且如实报「结果不明」而非可重试失败。
    /// </summary>
    public static DesktopCapabilityError? Validate(BrowserMutationOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(outcome);

        if (!Enum.IsDefined(outcome.Operation))
        {
            throw new ArgumentOutOfRangeException(
                nameof(outcome), outcome.Operation, "Automation operation is not registered.");
        }

        if (!Enum.IsDefined(outcome.Expectation))
        {
            throw new ArgumentOutOfRangeException(
                nameof(outcome), outcome.Expectation, "Mutation expectation is not registered.");
        }

        if (!outcome.ObservedVersion.IsKnown)
        {
            return DesktopCapabilityError.Internal(
                $"{outcome.Operation} returned no live page version; element references would be unusable");
        }

        if (outcome.NavigationObserved || outcome.ObservedVersion.Value > outcome.PinnedVersion.Value)
        {
            return null;
        }

        // 关键修正：没有声明后置条件就不判失败 —— 不按动作名推断导航，也不要求状态变化。
        if (outcome.Expectation != BrowserMutationExpectation.DocumentNavigation)
        {
            return null;
        }

        return DesktopCapabilityError.OutcomeUnknown(
            $"{outcome.Operation} declared a document navigation but neither a navigation nor a new"
            + " document version was observed");
    }

    /// <summary>
    /// 标签页动作的后置条件：<b>不</b>拿「另一页的版本推进」充当证据
    /// （设计方案 §4.2：Activate、关闭最后一页等必须分别定义结果）。
    ///
    /// • <c>close</c>：证据是「确实关掉了」；关掉最后一页后没有剩余页面，因此**不**检查版本；
    /// • <c>activate</c>/<c>new</c>：必须回带活动页的活版本，否则新引用无法建立。
    /// </summary>
    public static DesktopCapabilityError? ValidateTabs(
        DesktopTabAction action,
        bool tabClosed,
        DesktopPageVersion observedVersion)
    {
        if (!Enum.IsDefined(action))
        {
            throw new ArgumentOutOfRangeException(nameof(action), action, "Tab action is not registered.");
        }

        if (action == DesktopTabAction.Close)
        {
            return tabClosed
                ? null
                : DesktopCapabilityError.Internal("tab close reported no closed tab");
        }

        return observedVersion.IsKnown
            ? null
            : DesktopCapabilityError.Internal(
                $"tab action '{DesktopTabActionWire.NameOf(action)}' returned no live page version");
    }
}
