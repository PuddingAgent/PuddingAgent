namespace PuddingDesktop.Foundation;

/// <summary>
/// 工具能力可用性真源（设计规格 §13.2 IMG11 / §13.5）。
/// <para>
/// 工具首页卡片上的「可用 / 待接入」标签，与标签页的
/// <see cref="ToolTabAvailability"/> 必须来自同一处判断 —— 否则文案会和真实能力
/// 漂移：把待接入项画成可用，或把可用项写成待接入（两者都是误导用户）。
/// 这里只做纯映射，可在无 UI 边界单测；WinUI 层只负责把标签贴到卡片上。
/// </para>
/// </summary>
public static class ToolAvailabilityCatalog
{
    /// <summary>
    /// 该能力的真实可用性。待接入 = Shell 没有组件执行它，既不起进程也不伪装运行。
    /// </summary>
    public static ToolTabAvailability For(ToolTabKind kind) => kind switch
    {
        ToolTabKind.Terminal or ToolTabKind.Artifact or ToolTabKind.Panel =>
            ToolTabAvailability.Deferred,
        _ => ToolTabAvailability.Ready,
    };

    /// <summary>工具首页卡片右侧的可用性标签文案。</summary>
    public static string CardLabel(ToolTabKind kind) =>
        For(kind) == ToolTabAvailability.Deferred ? "待接入" : "可用";
}
