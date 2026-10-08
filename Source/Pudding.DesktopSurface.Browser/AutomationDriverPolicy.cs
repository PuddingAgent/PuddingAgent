using PuddingBrowser.Abstractions;
using PuddingBrowser.Automation;

namespace Pudding.DesktopSurface.Browser;

/// <summary>
/// **把 Automation 里的纯策略适配成驱动注入点**（设计方案 §7/§8）。
///
/// 这一层存在的唯一理由是**边界**：驱动源码会被 WinUI 面链接编译，而那一面只允许引用
/// <c>PuddingBrowser.Abstractions</c>；Automation 里那些可测试的纯策略必须由**引用两侧**的
/// Desktop 侧适配进去（本程序集两者都引用），而不是让共享源码直接引用 Automation。
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
        PageEventPolicy.DescribePermission(
            kind, uri, PageEventPolicy.DecidePermission(kind));

    public string DescribeWindowRequest(string? uri) =>
        PageEventPolicy.DescribeWindow(uri, PageEventPolicy.DecideWindow());

    public string DescribeDownloadRequest(string? suggestedFileName, string? uri) =>
        PageEventPolicy.DescribeDownload(
            suggestedFileName, uri, PageEventPolicy.DecideDownload());
}
