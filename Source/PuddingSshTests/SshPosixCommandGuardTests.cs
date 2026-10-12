using PuddingSsh.Contracts;
using PuddingSsh.Diagnostics;
using PuddingSsh.Validation;

namespace PuddingSshTests;

/// <summary>
/// R1 回归（ADR-096 D1）：**工作目录必须是整个脚本的前置守卫**。
/// <para>
/// 旧实现是 <c>cd -- 'cwd' &amp;&amp; &lt;command&gt;</c>，只有命令的第一段受 <c>&amp;&amp;</c> 限制；
/// 复合脚本（分号 / 换行 / <c>||</c> / pipeline / 后台列表）在 cd 失败后**仍会执行**，
/// 并且后续段的退出码会覆盖 cd 的失败（审阅报告 F1 / 反例 JSON <c>cwd_failure_composite</c>）。
/// </para>
/// <para>
/// 这里只断言**字节精确串**（可独立测试）；真实 shell 语义由 Probe 的 <c>cwd</c> 场景在 POSIX shell 上验证。
/// </para>
/// </summary>
[TestClass]
public sealed class SshPosixCommandGuardTests
{
    private const string ExistingCwd = "/srv/pudding test";

    private static SshPosixExecutionPlan Compose(string command, string? cwd = ExistingCwd) =>
        SshPosixCommand.Compose(command, cwd, null);

    [TestMethod]
    public void GuardIsAFullLine_BeforeTheVerbatimCommand()
    {
        var plan = Compose("printf FIRST; printf SECOND");

        var lines = plan.ExecutedCommand.Split('\n');
        Assert.AreEqual($"cd -- '{ExistingCwd}' || exit $?", lines[0], "第一行必须只是 cwd 守卫");
        Assert.AreEqual("printf FIRST; printf SECOND", string.Join('\n', lines.Skip(1)), "守卫之后必须是**原样**命令");
    }

    [TestMethod]
    public void GuardUsesLineFeedOnly_NeverCarriageReturn()
    {
        var plan = Compose("true");

        Assert.DoesNotContain('\r', plan.ExecutedCommand, "执行串不得含 CR（跨平台回车会破坏远端 shell 解析）");
        StringAssert.Contains(plan.ExecutedCommand, "\n", "守卫与命令之间固定使用 LF");
    }

    [TestMethod]
    public void GuardDoesNotRelyOnSetEorSubshell()
    {
        var plan = Compose("printf FIRST; printf SECOND");

        Assert.IsFalse(plan.ExecutedCommand.Contains("set -e", StringComparison.Ordinal), "不得用 set -e 代替守卫");
        Assert.IsFalse(plan.ExecutedCommand.Contains("(cd", StringComparison.Ordinal), "不得把 cd 放进子 shell");
        StringAssert.StartsWith(plan.ExecutedCommand, "cd -- ");
        StringAssert.Contains(plan.ExecutedCommand, "|| exit $?");
    }

    [TestMethod]
    [DataRow("printf FIRST; printf SECOND")]
    [DataRow("printf FIRST\nprintf SECOND")]
    [DataRow("printf FIRST || printf SECOND")]
    [DataRow("printf FIRST | tr A-Z a-z")]
    [DataRow("printf FIRST & printf SECOND")]
    [DataRow("printf FIRST # trailing comment")]
    [DataRow("cat <<'EOF'\nline\nEOF")]
    public void CompositeScripts_KeepTheGuardOnItsOwnLine_AndTheOriginalBodyIntact(string command)
    {
        var plan = Compose(command);

        var guardEnd = plan.ExecutedCommand.IndexOf('\n');
        Assert.IsGreaterThan(0, guardEnd, "守卫必须独占第一行");
        Assert.AreEqual($"cd -- '{ExistingCwd}' || exit $?", plan.ExecutedCommand[..guardEnd]);
        Assert.AreEqual(command, plan.ExecutedCommand[(guardEnd + 1)..], "原始脚本体必须逐字保留（含内部换行）");
    }

    [TestMethod]
    public void CwdWordIsStillSingleQuoted_AndCwdIsReported()
    {
        var plan = Compose("id"); // 含空格的 cwd

        Assert.AreEqual("id", plan.ExecutedCommand.Split('\n')[1]);
        Assert.AreEqual(ExistingCwd, plan.EffectiveCwd);
        Assert.IsFalse(plan.UsedDefaultCwd);
        Assert.IsTrue(plan.ExecutedCommand.StartsWith("cd -- '/srv/pudding test' || exit $?\n", StringComparison.Ordinal));
    }

    [TestMethod]
    public void WithoutCwd_OriginalScriptIsUntouched()
    {
        const string script = "printf FIRST; printf SECOND";
        var plan = SshPosixCommand.Compose(script, null, null);

        Assert.AreEqual(script, plan.ExecutedCommand, "无 cwd 时不得添加任何守卫或包装");
        Assert.IsNull(plan.EffectiveCwd);
    }

    [TestMethod]
    public void HostileCwd_CannotEscapeTheGuardLine()
    {
        var plan = Compose("id", "/tmp/it's || exit 0 #");

        var lines = plan.ExecutedCommand.Split('\n');
        Assert.AreEqual("id", lines[1], "恶意 cwd 不得把命令挤到守卫行上或提前结束守卫");
        StringAssert.Contains(lines[0], "'\\''");
        Assert.HasCount(2, lines, "执行串只能是守卫行 + 命令本体");
    }

    [TestMethod]
    public void InvalidRequestedOrDefaultCwd_StillRejected()
    {
        Assert.AreEqual(
            SshErrorCodes.RequestInvalid,
            Assert.ThrowsExactly<SshComponentException>(() => SshPosixCommand.Compose("id", "relative", null)).ErrorCode);
        Assert.AreEqual(
            SshErrorCodes.ConfigInvalid,
            Assert.ThrowsExactly<SshComponentException>(() => SshPosixCommand.Compose("id", null, "relative-default")).ErrorCode);
    }

    [TestMethod]
    public void RequestValidator_ComposesTheSameGuardedPlan()
    {
        // 审批与执行必须调用同一个 Compose（ADR-096 D1）：两条路径产出的执行串必须逐字相同。
        var limits = new SshOperationLimits();
        var target = new SshTargetRef("dev-linux", "rev-1", "192.0.2.10", 22, "deploy", "trust-1");

        var (plan, _, _) = SshRequestValidator.ValidateExecute(
            new SshExecuteRequest(target, "printf FIRST; printf SECOND") { Cwd = ExistingCwd },
            limits,
            defaultCwd: null);

        Assert.AreEqual(Compose("printf FIRST; printf SECOND").ExecutedCommand, plan.ExecutedCommand);
    }
}
