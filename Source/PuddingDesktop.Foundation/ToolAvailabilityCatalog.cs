namespace PuddingDesktop.Foundation;

/// <summary>
/// 工具能力可用性真源（设计规格 §13.2 IMG11 / §13.5）。
/// <para>
/// 工具首页卡片上的「可用 / 待接入」标签、卡片第二行的说明文字、以及「这张卡片
/// 能不能点」，与标签页的 <see cref="ToolTabAvailability"/> 必须来自同一处判断 ——
/// 否则文案会和真实能力漂移：把待接入项画成可用，或把可用项写成待接入（两者都是
/// 误导用户）。
/// </para>
/// <para>
/// 2026-10-22（用户 UX 清单第 ⑩ 条）：真实出现过两类漂移 ——
/// ①交互面板的芯片写着「可用」，副标题却残留「（等待接入）」（渲染通道早就通了）；
/// ②「新建终端」芯片写着「待接入」，卡片却照样可点，点进去只剩一句占位说明。
/// 因此说明文字与可点状态都收到这里，卡片本身只是把这里的结果贴上去。
/// </para>
/// <para>这里只做纯映射，可在无 UI 边界单测；WinUI 层不写任何能力判断。</para>
/// </summary>
public static class ToolAvailabilityCatalog
{
    /// <summary>
    /// 该能力的真实可用性。待接入 = Shell 没有组件执行它，既不起进程也不伪装运行。
    /// </summary>
    /// <remarks>
    /// 制成品**不是**待接入：制品库由 <c>PanelCraftStore</c> 落在本地数据根，交互面板的
    /// 「保存到制品库」、列表、继续编辑/重命名/删除都已经能跑（Core 的「Agent 成果接口」
    /// 另当其说，但那张卡片承诺的是本机制品库，所以按真实能力记 Ready）。
    /// </remarks>
    public static ToolTabAvailability For(ToolTabKind kind) => kind switch
    {
        // 终端会话组件仍未接入：只有标签页实例与状态位，不执行命令。
        ToolTabKind.Terminal => ToolTabAvailability.Deferred,
        _ => ToolTabAvailability.Ready,
    };

    /// <summary>工具首页卡片右侧的可用性标签文案。</summary>
    public static string CardLabel(ToolTabKind kind) =>
        For(kind) == ToolTabAvailability.Deferred ? "待接入" : "可用";

    /// <summary>
    /// 工具首页卡片第二行的说明。与 <see cref="CardLabel"/> 同源：待接入的能力必须在这里
    /// 说清缺什么，可用的能力**不许**再出现「待接入 / 尚未接入」这类字眼。
    /// </summary>
    public static string CardSubtitle(ToolTabKind kind) => kind switch
    {
        ToolTabKind.Home => "工具区入口总览",
        ToolTabKind.Terminal => "在会话工作区运行命令（终端会话组件尚未接入，入口已禁用）",
        ToolTabKind.Browser => "浏览网页，并指定 Agent 目标页面",
        ToolTabKind.Artifact => "从交互面板保存到制品库的画面，可继续编辑、重命名或删除",
        ToolTabKind.Output => "打开并预览本机的文档、数据或日志",
        ToolTabKind.Panel => "Agent 推送的媒体画布：Markdown、图表与设计图纸",
        ToolTabKind.Document => "在工具区阅读 Agent 输出的文档",
        _ => string.Empty,
    };

    /// <summary>
    /// 这张入口是否可点。未接线的能力不给可点入口 —— 点进去只剩占位说明，
    /// 用户会以为是自己点错了。说明文字由 <see cref="CardSubtitle"/> 负责。
    /// </summary>
    public static bool IsEntryEnabled(ToolTabKind kind) =>
        For(kind) == ToolTabAvailability.Ready;
}
