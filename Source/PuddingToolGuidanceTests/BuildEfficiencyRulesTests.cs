using PuddingToolGuidance;

namespace PuddingToolGuidanceTests;

/// <summary>
/// 构建/测试效率规则（P0）。两处最容易翻车的地方都在反例里：
/// ① 第一次跑测试本来就要构建，不该提示；
/// ② 单次超时可能只是慢，也不该提示。
/// </summary>
[TestClass]
public sealed class BuildEfficiencyRulesTests
{
    private const string TestCmd = "dotnet test Source/PuddingRuntimeTests/PuddingRuntimeTests.csproj -v q";

    private static IReadOnlyList<GuidanceAdvice> Evaluate(GuidanceInput input) =>
        new GuidanceEngine(maxAdvices: 5).Evaluate(input).Advices;

    private static GuidanceAdvice? Find(IReadOnlyList<GuidanceAdvice> advices, string ruleId) =>
        advices.FirstOrDefault(a => a.RuleId == ruleId);

    // ── 规则 A：重复 dotnet test 未带 --no-build ─────────────────

    [TestMethod]
    public void First_dotnet_test_run_does_not_advise()
    {
        var advices = Evaluate(new GuidanceInput(
            "terminal_start", "cmd", TestCmd, Succeeded: true, RepeatedCommandCount: 1));

        Assert.IsNull(Find(advices, "build.dotnet_test_without_no_build"),
            "第一次跑测试时工程本来就要构建，提示它只会变成噪声");
    }

    [TestMethod]
    public void Second_dotnet_test_run_advises()
    {
        var advices = Evaluate(new GuidanceInput(
            "terminal_start", "cmd", TestCmd, Succeeded: true, RepeatedCommandCount: 2));

        var advice = Find(advices, "build.dotnet_test_without_no_build");
        Assert.IsNotNull(advice);
        StringAssert.Contains(advice!.Message, "--no-build");
    }

    [TestMethod]
    public void Command_already_using_no_build_never_advises()
    {
        var advices = Evaluate(new GuidanceInput(
            "terminal_start", "cmd", TestCmd + " --no-build", Succeeded: true, RepeatedCommandCount: 5));

        Assert.IsNull(Find(advices, "build.dotnet_test_without_no_build"));
    }

    [TestMethod]
    public void Non_dotnet_command_never_advises()
    {
        var advices = Evaluate(new GuidanceInput(
            "terminal_start", "cmd", "dotnet build foo.csproj", Succeeded: true, RepeatedCommandCount: 9));

        Assert.IsNull(Find(advices, "build.dotnet_test_without_no_build"));
    }

    // ── 规则 B：连续超时 ───────────────────────────────────────

    [TestMethod]
    public void Single_timeout_does_not_advise()
    {
        var advices = Evaluate(new GuidanceInput(
            "terminal_start", "cmd", "dotnet test x", Succeeded: false,
            Error: "Command timed out after 600s", ConsecutiveFailureCount: 1));

        Assert.IsNull(Find(advices, "build.repeated_timeout_use_blame_hang"),
            "单次超时可能只是慢，先别劝人换工具");
    }

    [TestMethod]
    public void Repeated_timeout_advises_blame_hang()
    {
        var advices = Evaluate(new GuidanceInput(
            "terminal_start", "cmd", "dotnet test x", Succeeded: false,
            Error: "Command timed out after 600s", ConsecutiveFailureCount: 2));

        var advice = Find(advices, "build.repeated_timeout_use_blame_hang");
        Assert.IsNotNull(advice);
        StringAssert.Contains(advice!.Message, "--blame-hang");
    }

    [TestMethod]
    public void Repeated_non_timeout_failure_does_not_use_the_timeout_rule()
    {
        var advices = Evaluate(new GuidanceInput(
            "terminal_start", "cmd", "dotnet test x", Succeeded: false,
            Error: "MSB1009: project file does not exist", ConsecutiveFailureCount: 3));

        Assert.IsNull(Find(advices, "build.repeated_timeout_use_blame_hang"),
            "普通编译错误不该被说成超时");
    }

    [TestMethod]
    public void Success_never_triggers_timeout_rule()
    {
        var advices = Evaluate(new GuidanceInput(
            "terminal_start", "cmd", "dotnet test x", Succeeded: true,
            Error: null, ConsecutiveFailureCount: 0));

        Assert.IsNull(Find(advices, "build.repeated_timeout_use_blame_hang"));
    }
}
