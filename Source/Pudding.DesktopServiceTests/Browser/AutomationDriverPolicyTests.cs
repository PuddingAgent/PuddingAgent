using Pudding.DesktopService.Browser;

namespace DesktopServiceTests.Browser;

/// <summary>
/// 驱动策略适配器（§7/§8）：它**只转发**，因此这里验证的是「转发到了 Automation 的同一份策略」，
/// 而不是另写一套判定（那正是要避免的漂移）。
/// </summary>
public sealed class AutomationDriverPolicyTests
{
    [Fact]
    public void ScriptDialogsForwardToTheAutomationPolicy()
    {
        var policy = AutomationDriverPolicy.Instance;

        // alert 只有一个 OK ⇒ 接受；confirm/prompt ⇒ 取消（肯定可能触发不可逆操作）。
        Assert.True(policy.AnswerScriptDialog("Alert", "hi").Accept);
        Assert.False(policy.AnswerScriptDialog("Confirm", "sure?").Accept);
        Assert.False(policy.AnswerScriptDialog("Prompt", "name?").Accept);

        var confirm = policy.AnswerScriptDialog("Confirm", "sure?");
        Assert.Contains("kind=confirm", confirm.Summary);
        Assert.Contains("answered=dismiss", confirm.Summary);
    }

    [Fact]
    public void PageEventSummariesForwardToTheAutomationPolicy()
    {
        var policy = AutomationDriverPolicy.Instance;

        var permission = policy.DescribePermissionRequest("Camera", "https://example.test/secret?token=abc");
        Assert.Contains("kind=camera", permission);
        Assert.Contains("answered=deny", permission);
        Assert.Contains("host=https://example.test", permission);

        // 摘要不回显路径与查询串（避免把 token 带进日志）。
        Assert.DoesNotContain("token", permission);
        Assert.DoesNotContain("secret", permission);

        Assert.Contains("answered=suppress", policy.DescribeWindowRequest("https://example.test/p"));
        Assert.Contains("no file was written", policy.DescribeDownloadRequest("a.pdf", "https://example.test/d"));
    }
}
