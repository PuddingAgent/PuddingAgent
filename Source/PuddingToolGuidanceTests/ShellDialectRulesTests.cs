using PuddingToolGuidance;
using PuddingToolGuidance.Rules;

namespace PuddingToolGuidanceTests;

/// <summary>
/// Shell 方言规则表驱动测试：每条规则都要有「应提示」与「不应提示」两侧。
/// <para>
/// 只有正例的规则等于没有规则 —— 它可能对小写敏感、对子串误命中、对正常命令也开火。
/// 所以反例与正例同等重要，且反例取的是**最容易误伤**的写法。
/// </para>
/// </summary>
[TestClass]
public sealed class ShellDialectRulesTests
{
    private static GuidanceAdvice? First(GuidanceInput input)
    {
        var engine = new GuidanceEngine(maxAdvices: 5);
        var summary = engine.Evaluate(input);
        return summary.Advices.Count == 0 ? null : summary.Advices[0];
    }

    // ── 规则 1：cmd 里的 Unix 命令 ─────────────────────────────

    [DataTestMethod]
    [DataRow("cmd", "dir /b | tail -5")]
    [DataRow("cmd", "find . -name *.cs | head")]
    [DataRow("cmd", "type a.txt | grep foo")]
    public void Unix_command_in_cmd_should_advise(string dialect, string command)
    {
        var advice = First(new GuidanceInput("shell", dialect, command));
        Assert.IsNotNull(advice);
        Assert.AreEqual("shell.unix_command_in_cmd", advice!.RuleId);
    }

    [DataTestMethod]
    [DataRow("powershell", "dir | Select-Object -Last 5")]
    [DataRow("bash", "ls | tail -5")]
    [DataRow("cmd", "findstr /n /c:\"abc\" file.cs")]
    [DataRow("cmd", "type file.txt")]
    public void Unix_command_elsewhere_should_not_advise(string dialect, string command)
    {
        var advice = First(new GuidanceInput("shell", dialect, command));
        Assert.IsNull(advice, $"不应提示：{dialect} / {command}");
    }

    // ── 规则 2：cmd 里的 PowerShell cmdlet ─────────────────────

    [DataTestMethod]
    [DataRow("cmd", "Remove-Item .\\temp\\a.txt")]
    [DataRow("cmd", "Get-ChildItem -Recurse")]
    public void Pwsh_cmdlet_in_cmd_should_advise(string dialect, string command)
    {
        var advice = First(new GuidanceInput("shell", dialect, command));
        Assert.IsNotNull(advice);
        Assert.AreEqual("shell.pwsh_cmdlet_in_cmd", advice!.RuleId);
    }

    [DataTestMethod]
    [DataRow("powershell", "Remove-Item .\\temp\\a.txt")]
    [DataRow("pwsh", "Get-ChildItem -Recurse")]
    public void Pwsh_cmdlet_in_pwsh_should_not_advise(string dialect, string command)
    {
        Assert.IsNull(First(new GuidanceInput("shell", dialect, command)));
    }

    // ── 规则 3：非 bash 方言里的 Bash 变量 ─────────────────────

    [DataTestMethod]
    [DataRow("cmd", "set X=1 && echo $X")]
    [DataRow("powershell", "echo $HOME")]
    public void Bash_variable_outside_bash_should_advise(string dialect, string command)
    {
        var advice = First(new GuidanceInput("shell", dialect, command));
        Assert.IsNotNull(advice);
        Assert.AreEqual("shell.bash_variable_outside_bash", advice!.RuleId);
    }

    [DataTestMethod]
    [DataRow("bash", "echo $HOME")]
    [DataRow("wsl", "echo $PATH")]
    [DataRow("pwsh", "Write-Output $env:PATH")]
    [DataRow("cmd", "echo %PATH%")]
    public void Bash_variable_in_bash_or_pwsh_env_should_not_advise(string dialect, string command)
    {
        Assert.IsNull(First(new GuidanceInput("shell", dialect, command)), $"{dialect} / {command}");
    }

    // ── 规则 4：findstr + 中文 ────────────────────────────────

    [TestMethod]
    public void Findstr_with_cjk_should_advise()
    {
        var advice = First(new GuidanceInput("shell", "cmd", "findstr /n /c:\"静态上下文\" a.cs"));
        Assert.IsNotNull(advice);
        Assert.AreEqual("encoding.findstr_with_cjk", advice!.RuleId);
    }

    [TestMethod]
    public void Findstr_without_cjk_should_not_advise()
    {
        Assert.IsNull(First(new GuidanceInput("shell", "cmd", "findstr /n /c:\"static\" a.cs")));
    }

    // ── 规则 5：cmd 的 & 串行 ─────────────────────────────────

    [TestMethod]
    public void Long_ampersand_chain_in_cmd_should_advise()
    {
        var advice = First(new GuidanceInput("shell", "cmd", "cmd1 & cmd2 & cmd3 & cmd4"));
        Assert.IsNotNull(advice);
        Assert.AreEqual("shell.cmd_ampersand_is_sequential", advice!.RuleId);
    }

    [TestMethod]
    public void Short_ampersand_chain_should_not_advise()
    {
        // 两三个 && 做顺序门禁是正常写法。
        Assert.IsNull(First(new GuidanceInput("shell", "cmd", "cd /d repo && dotnet build")));
    }

    // ── 非 shell 工具一律不提示 ───────────────────────────────

    [TestMethod]
    public void Non_shell_tool_should_not_advise()
    {
        Assert.IsNull(First(new GuidanceInput("file_read", null, null)));
    }
}
