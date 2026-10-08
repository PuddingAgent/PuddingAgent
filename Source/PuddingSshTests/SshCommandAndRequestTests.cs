using PuddingSsh.Contracts;
using PuddingSsh.Diagnostics;
using PuddingSsh.Validation;

namespace PuddingSshTests;

/// <summary>
/// POSIX 封装（设计 §7.3）：<c>cd -- 'cwd' &amp;&amp; &lt;command&gt;</c>，单引号编码，
/// cwd 必须是干净的绝对路径，命令 16 KiB/NUL 边界。
/// </summary>
[TestClass]
public sealed class SshPosixCommandTests
{
    [TestMethod]
    public void Command_WithoutCwd_IsPassedThroughVerbatim()
    {
        var plan = SshPosixCommand.Compose("uname -a; echo $HOME", null, null);

        Assert.AreEqual("uname -a; echo $HOME", plan.ExecutedCommand);
        Assert.IsNull(plan.EffectiveCwd);
        Assert.IsFalse(plan.UsedDefaultCwd);
    }

    [TestMethod]
    public void Cwd_IsSingleQuoted_AndCdFailureStopsTheCommand()
    {
        var plan = SshPosixCommand.Compose("uname -a", "/srv/pudding test", null);

        Assert.AreEqual("cd -- '/srv/pudding test' && uname -a", plan.ExecutedCommand);
        Assert.AreEqual("/srv/pudding test", plan.EffectiveCwd);
        Assert.IsFalse(plan.UsedDefaultCwd);
    }

    [TestMethod]
    public void RequestedCwd_WinsOver_DefaultCwd_AndIsFlaggedWhenInherited()
    {
        var fromDefault = SshPosixCommand.Compose("id", null, "/srv/default");
        Assert.AreEqual("cd -- '/srv/default' && id", fromDefault.ExecutedCommand);
        Assert.IsTrue(fromDefault.UsedDefaultCwd);

        var fromRequest = SshPosixCommand.Compose("id", "/per/call", "/srv/default");
        Assert.AreEqual("cd -- '/per/call' && id", fromRequest.ExecutedCommand);
        Assert.IsFalse(fromRequest.UsedDefaultCwd);
    }

    [TestMethod]
    [DataRow("/tmp/a b", "'/tmp/a b'")]
    [DataRow("/tmp/it's", "'/tmp/it'\\''s'")]
    [DataRow("/tmp/$HOME`id`", "'/tmp/$HOME`id`'")]
    [DataRow("/tmp/\"quoted\"", "'/tmp/\"quoted\"'")]
    public void Quote_EncodesPerPosixSingleQuoteRules(string value, string expected)
    {
        Assert.AreEqual(expected, SshPosixCommand.Quote(value));
    }

    [TestMethod]
    public void SingleQuoteInCwd_CannotEscapeTheWrapper()
    {
        const string hostileCwd = "/tmp/it's; rm -rf /";
        var plan = SshPosixCommand.Compose("id", hostileCwd, null);

        Assert.IsTrue(plan.ExecutedCommand.StartsWith("cd -- '", StringComparison.Ordinal));
        Assert.IsTrue(plan.ExecutedCommand.EndsWith("' && id", StringComparison.Ordinal));

        // 反向解码：把包装里的 cwd 词按 POSIX 单引号规则解回来，必须逐字等于原值
        // ⇒ 分号、空格都在**同一个词**里，没有逃逸成命令分隔符。
        var cwdWord = plan.ExecutedCommand["cd -- ".Length..^" && id".Length];
        Assert.AreEqual(hostileCwd, DecodeSingleQuoted(cwdWord));
    }

    private static string DecodeSingleQuoted(string shellWord)
    {
        Assert.IsTrue(
            shellWord.Length >= 2 && shellWord[0] == '\'' && shellWord[^1] == '\'',
            $"不是合法的单引号词：{shellWord}");

        return shellWord[1..^1].Replace("'\\''", "'", StringComparison.Ordinal);
    }

    [TestMethod]
    [DataRow("relative/path")]
    [DataRow("~")]
    [DataRow("")]
    [DataRow("/tmp/bad\nnewline")]
    [DataRow("/tmp/bad\ttab")]
    [DataRow("/tmp/bad\0nul")]
    public void InvalidCwd_IsRejected(string cwd)
    {
        Assert.IsFalse(SshPosixCommand.IsValidCwd(cwd));
        var error = Assert.ThrowsExactly<SshComponentException>(
            () => SshPosixCommand.Compose("id", cwd, null));
        Assert.AreEqual(SshErrorCodes.RequestInvalid, error.ErrorCode);
    }

    [TestMethod]
    public void InvalidDefaultCwd_IsConfigErrorNotRequestError()
    {
        var error = Assert.ThrowsExactly<SshComponentException>(
            () => SshPosixCommand.Compose("id", null, "relative-default"));
        Assert.AreEqual(SshErrorCodes.ConfigInvalid, error.ErrorCode);
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("   ")]
    [DataRow("echo\0hidden")]
    public void InvalidCommand_IsRejected(string command)
    {
        var error = Assert.ThrowsExactly<SshComponentException>(
            () => SshPosixCommand.Compose(command, null, null));
        Assert.AreEqual(SshErrorCodes.RequestInvalid, error.ErrorCode);
    }

    [TestMethod]
    public void CommandLengthBoundary_IsExactly16KiBOfUtf8()
    {
        var asciiAtLimit = new string('a', SshRequestLimits.MaxCommandUtf8Bytes);
        Assert.AreEqual(
            asciiAtLimit,
            SshPosixCommand.Compose(asciiAtLimit, null, null).ExecutedCommand,
            "正好 16 KiB 的 ASCII 命令必须通过");

        var overLimit = new string('a', SshRequestLimits.MaxCommandUtf8Bytes + 1);
        var error = Assert.ThrowsExactly<SshComponentException>(
            () => SshPosixCommand.Compose(overLimit, null, null));
        Assert.AreEqual(SshErrorCodes.RequestInvalid, error.ErrorCode);

        // 上限按 **UTF-8 字节** 计：3 字节汉字在 5461 个字符时越界（16383 字节通过，16386 越界）。
        var multibyteAtLimit = new string('中', 5461);
        Assert.AreEqual(16383, System.Text.Encoding.UTF8.GetByteCount(multibyteAtLimit));
        _ = SshPosixCommand.Compose(multibyteAtLimit, null, null);

        var multibyteOver = new string('中', 5462);
        Assert.ThrowsExactly<SshComponentException>(() => SshPosixCommand.Compose(multibyteOver, null, null));
    }
}

/// <summary>请求/目标校验：形状非法必须在**连接之前**以稳定错误码拒绝。</summary>
[TestClass]
public sealed class SshRequestValidatorTests
{
    private static SshTargetRef Target(
        string hostId = "dev-linux",
        string host = "192.0.2.10",
        int port = 22,
        string username = "deploy") =>
        new(hostId, "rev-1", host, port, username, "trust-1");

    [TestMethod]
    public void ValidTargetAndRequest_Pass()
    {
        var limits = new SshOperationLimits();
        SshRequestValidator.ValidateTarget(Target());

        var (plan, timeout, budget) = SshRequestValidator.ValidateExecute(
            new SshExecuteRequest(Target(), "uname -a") { Cwd = "/srv/app" },
            limits,
            defaultCwd: null);

        Assert.AreEqual("cd -- '/srv/app' && uname -a", plan.ExecutedCommand);
        Assert.AreEqual(TimeSpan.FromSeconds(60), timeout);
        Assert.AreEqual(64 * 1024, budget);
    }

    [TestMethod]
    public void Limits_OnlyShrink_NeverGrow()
    {
        var limits = new SshOperationLimits
        {
            OperationTimeoutSeconds = 60,
            MaxOperationTimeoutSeconds = 120,
            MaxOutputBytes = 64 * 1024,
        };

        var (_, clampedTimeout, clampedBudget) = SshRequestValidator.ValidateExecute(
            new SshExecuteRequest(Target(), "id") { TimeoutSeconds = 600, MaxOutputBytes = 10 * 1024 * 1024 },
            limits,
            defaultCwd: null);
        Assert.AreEqual(TimeSpan.FromSeconds(120), clampedTimeout, "请求不得突破主机上限");
        Assert.AreEqual(64 * 1024, clampedBudget, "请求不得突破主机预算");

        var (_, shrunkTimeout, shrunkBudget) = SshRequestValidator.ValidateExecute(
            new SshExecuteRequest(Target(), "id") { TimeoutSeconds = 5, MaxOutputBytes = 1024 },
            limits,
            defaultCwd: null);
        Assert.AreEqual(TimeSpan.FromSeconds(5), shrunkTimeout);
        Assert.AreEqual(1024, shrunkBudget);

        var (_, deadlineTimeout, _) = SshRequestValidator.ValidateExecute(
            new SshExecuteRequest(Target(), "id") { TimeoutSeconds = 60 },
            limits,
            defaultCwd: null,
            hostDeadline: TimeSpan.FromSeconds(9));
        Assert.AreEqual(TimeSpan.FromSeconds(9), deadlineTimeout, "宿主 deadline 必须能进一步收紧");
    }

    [TestMethod]
    [DataRow("", "192.0.2.10", 22, "deploy")]
    [DataRow("dev-linux", "", 22, "deploy")]
    [DataRow("dev-linux", "192.0.2.10", 0, "deploy")]
    [DataRow("dev-linux", "192.0.2.10", 65536, "deploy")]
    [DataRow("dev-linux", "192.0.2.10", 22, "")]
    public void InvalidTarget_IsConfigError(string hostId, string host, int port, string username)
    {
        var error = Assert.ThrowsExactly<SshComponentException>(
            () => SshRequestValidator.ValidateTarget(Target(hostId, host, port, username)));
        Assert.AreEqual(SshErrorCodes.ConfigInvalid, error.ErrorCode);
    }

    [TestMethod]
    public void MissingRevisions_AreConfigErrors()
    {
        var noRevision = new SshTargetRef("h", "", "192.0.2.10", 22, "deploy", "trust-1");
        Assert.AreEqual(
            SshErrorCodes.ConfigInvalid,
            Assert.ThrowsExactly<SshComponentException>(() => SshRequestValidator.ValidateTarget(noRevision)).ErrorCode);

        var noTrust = new SshTargetRef("h", "rev-1", "192.0.2.10", 22, "deploy", "");
        Assert.AreEqual(
            SshErrorCodes.ConfigInvalid,
            Assert.ThrowsExactly<SshComponentException>(() => SshRequestValidator.ValidateTarget(noTrust)).ErrorCode);
    }
}
