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
/// 本类**不含任何判定逻辑**：全部委托给 <see cref="ScriptDialogPolicy"/> 与
/// <see cref="PageEventPolicy"/>，因此策略只有一份、测试也只有一份。
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
}
