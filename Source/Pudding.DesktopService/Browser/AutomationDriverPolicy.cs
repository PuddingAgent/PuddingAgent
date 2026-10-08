using PuddingBrowser.Abstractions;
using PuddingBrowser.Automation;

namespace Pudding.DesktopService.Browser;

/// <summary>
/// **把 Automation 里的纯策略适配成驱动注入点**（设计方案 §7/§8）。
///
/// 存在的唯一理由是**边界**：驱动源码会被 WinUI 面链接编译，而那一面只允许引用
/// <c>PuddingBrowser.Abstractions</c>；Automation 里那些可测试的纯策略必须由**同时引用两侧**的程序集
/// 适配进去。放在 <c>Pudding.DesktopService</c> 是因为浏览器工作区控制器（win 与 WinUI 两个宿主都编译它）
/// 正好引用这一层 —— 因此两个宿主拿到的是**同一份**策略。
///
/// 本类**不含任何判定逻辑**：全部委托给 <see cref="ScriptDialogPolicy"/>、<see cref="PageEventPolicy"/>、
/// <see cref="NativeInputSequence"/>、<see cref="ScreenshotPlan"/>，因此策略只有一份、测试也只有一份。
/// </summary>
public sealed class AutomationDriverPolicy : IBrowserDriverPolicy
{
    public static AutomationDriverPolicy Instance { get; } = new();

    private AutomationDriverPolicy()
    {
    }

    public DriverDialogAnswer AnswerScriptDialog(string kind, string? message)
    {
        var parsed = kind switch
        {
            "Alert" => ScriptDialogPolicy.Kind.Alert,
            "Confirm" => ScriptDialogPolicy.Kind.Confirm,
            "Prompt" => ScriptDialogPolicy.Kind.Prompt,
            _ => ScriptDialogPolicy.Kind.Alert,
        };

        var answer = ScriptDialogPolicy.Decide(parsed);
        return new DriverDialogAnswer(
            answer == ScriptDialogPolicy.Answer.Accept,
            ScriptDialogPolicy.Describe(parsed, message, answer));
    }

    public string DescribePermissionRequest(string kind, string? uri) =>
        PageEventPolicy.DescribePermission(kind, uri, PageEventPolicy.DecidePermission(kind));

    public string DescribeWindowRequest(string? uri) =>
        PageEventPolicy.DescribeWindow(uri, PageEventPolicy.DecideWindow());

    public string DescribeDownloadRequest(string? suggestedFileName, string? uri) =>
        PageEventPolicy.DescribeDownload(suggestedFileName, uri, PageEventPolicy.DecideDownload());

    public IBrowserPageEventLog? CreateEventLog() => new EventLogAdapter();

    public IReadOnlyList<DriverCall>? PlanClick(double x, double y, int clickCount) =>
        Map(NativeInputSequence.PlanClick(Target(x, y), clickCount));

    public IReadOnlyList<DriverCall>? PlanHover(double x, double y) =>
        Map(NativeInputSequence.PlanHover(Target(x, y)));

    public IReadOnlyList<DriverCall>? PlanScroll(double x, double y, double deltaX, double deltaY) =>
        Map(NativeInputSequence.PlanScroll(Target(x, y), deltaX, deltaY));

    public IReadOnlyList<DriverCall>? PlanFill(string? text) => Map(NativeInputSequence.PlanFill(text));

    public IReadOnlyList<DriverCall>? PlanType(string? text) => Map(NativeInputSequence.PlanType(text));

    /// <summary>键名不认识时**明确不支持**（返回 null 让驱动 fail closed），而不是抛异常穿透驱动。</summary>
    public IReadOnlyList<DriverCall>? PlanPress(string? key) =>
        NativeInputSequence.TryResolveKey(key, out _) ? Map(NativeInputSequence.PlanPress(key)) : null;

    public IReadOnlyList<DriverCall>? ReleaseModifiers(int modifiers) =>
        Map(NativeInputSequence.ReleaseModifiers(modifiers));

    public string? BuildScreenshotParameters(
        int? quality, bool fullPage, double contentX, double contentY, double contentWidth, double contentHeight) =>
        ScreenshotPlan.Build(
            quality,
            fullPage,
            fullPage ? new ScreenshotPlan.ContentSize(contentX, contentY, contentWidth, contentHeight) : null);

    /// <summary>驱动只需要目标中心点，因此这里用 0×0 的盒子（中心即该点）。</summary>
    private static NativeInputSequence.InputTarget Target(double x, double y) => new(x, y, 0, 0);

    private static IReadOnlyList<DriverCall> Map(IReadOnlyList<NativeInputSequence.CdpCall> calls) =>
        calls.Select(call => new DriverCall(call.Method, call.ParametersJson)).ToArray();

    /// <summary>把 Automation 的有界日志适配成注入接口（"(xN)" 的渲染在这里，逻辑仍在 Automation）。</summary>
    private sealed class EventLogAdapter : IBrowserPageEventLog
    {
        private readonly PageEventLog _inner = new();

        public void Add(string? summary) => _inner.Add(summary);

        public bool IsEmpty => _inner.IsEmpty;

        public IReadOnlyList<string> Entries =>
            _inner.Entries
                .Select(entry => entry.Count > 1 ? $"{entry.Summary} (x{entry.Count})" : entry.Summary)
                .ToArray();
    }
}
