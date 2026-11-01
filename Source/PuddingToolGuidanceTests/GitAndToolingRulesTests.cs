using PuddingToolGuidance;

namespace PuddingToolGuidanceTests;

/// <summary>
/// Git 安全与工具链别名（P1）。
/// <para>
/// 这几条的共同点：出问题时**不会报错**，只会静默做错 —— 所以反例必须贴着实测场景写，
/// 否则很容易把正常命令也提示一遍（例如单独一条 <c>git stash</c> 是完全正确的用法）。
/// </para>
/// </summary>
[TestClass]
public sealed class GitAndToolingRulesTests
{
    private static IReadOnlyList<GuidanceAdvice> Evaluate(string command) =>
        new GuidanceEngine(maxAdvices: 5)
            .Evaluate(new GuidanceInput("terminal_start", "cmd", command)).Advices;

    private static GuidanceAdvice? Find(string command, string ruleId) =>
        Evaluate(command).FirstOrDefault(a => a.RuleId == ruleId);

    // ── git add -A ────────────────────────────────────────────

    [DataTestMethod]
    [DataRow("git add -A Source")]
    [DataRow("git add --all")]
    [DataRow("git add .")]
    [DataRow("git add -A Docs && git commit -m x")]
    public void Add_all_should_advise(string command)
    {
        var advice = Find(command, "git.add_all_may_include_temp_artifacts");
        Assert.IsNotNull(advice, command);
        StringAssert.Contains(advice!.Message, "git status", "提示要给可执行的替代动作");
    }

    [DataTestMethod]
    [DataRow("git add Source/PuddingRuntime/Services/AgentLoop/ToolGuidanceHook.cs")]
    [DataRow("git status --porcelain")]
    public void Explicit_paths_should_not_advise_for_add_all(string command)
    {
        Assert.IsNull(Find(command, "git.add_all_may_include_temp_artifacts"), command);
    }

    // ── git stash 链 ─────────────────────────────────────────

    [TestMethod]
    public void Stash_on_a_chain_should_advise()
    {
        var advice = Find("git stash push -- a.csproj && dotnet test x && git stash pop", "git.stash_recovery_on_same_chain");
        Assert.IsNotNull(advice);
        StringAssert.Contains(advice!.Message, "分两次调用");
    }

    [TestMethod]
    public void Standalone_stash_should_not_advise()
    {
        Assert.IsNull(Find("git stash push -- a.csproj", "git.stash_recovery_on_same_chain"),
            "单独一条 stash 是正确用法");
    }

    // ── python 别名 ──────────────────────────────────────────

    [DataTestMethod]
    [DataRow("python -c \"print(1)\"")]
    [DataRow("python3 script.py")]
    [DataRow("cd /d repo && python -V")]
    public void Python_alias_should_advise(string command)
    {
        var advice = Find(command, "host.python_alias_is_store_stub");
        Assert.IsNotNull(advice, command);
        StringAssert.Contains(advice!.Message, "py -3");
    }

    [DataTestMethod]
    [DataRow("py -3 script.py")]
    [DataRow("findstr /c:\"python\" notes.txt")]
    [DataRow("echo python")]
    public void Non_invocation_should_not_advise(string command)
    {
        Assert.IsNull(Find(command, "host.python_alias_is_store_stub"), command);
    }
}
