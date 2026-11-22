using System.Diagnostics;
using PuddingRuntime.Services.Tools;

namespace PuddingRuntimeTests.Tools;

/// <summary>
/// 锁定 shell 选择与启动参数的唯一真源。
///
/// 背景：<c>shell</c> 工具在 Windows 上默认走 pwsh 7，而 <c>terminal_*</c> 工具
/// 曾硬编码 <c>cmd.exe</c>。两个入口默认引擎不同 ⇒ 同一命令在一个工具里验证通过、
/// 搬到另一个工具就语法失败，模型只能靠试错。本测试把「两边共用同一份解析」锁成判据：
///
///   · <c>ResolveShell(null)</c> 在 pwsh 可用时必须给出 PowerShell（而不是 cmd）；
///   · <c>ResolvePowerShellExecutable()</c> 必须优先 pwsh 7，而不是 5.1；
///   · cmd 的 argv 必须走 <c>Arguments</c> 原样传递（ArgumentList 会把内层引号
///     转义成 <c>\"</c>，cmd 不认，含引号命令会被静默破坏成「exit≠0 且输出全空」）。
///
/// 这些断言都能取红：把默认引擎改回 cmd、把优先级换成 powershell.exe、
/// 把 cmd 分支改成 ArgumentList，对应用例立即失败。
/// </summary>
[TestClass]
public class HostShellResolverTests
{
    /// <summary>
    /// 判据 1：Windows 上 auto 必须解析为 PowerShell 家族（前提是 pwsh 存在），
    /// 而不是历史行为的 cmd。这条直接锁「与 shell 工具保持一致」。
    /// </summary>
    [TestMethod]
    public void ResolveShell_AutoOnWindows_PrefersPowerShellOverCmd()
    {
        if (!OperatingSystem.IsWindows())
            return; // 非 Windows 的 auto 语义是 bash/sh，不在此判据范围

        if (!HostShellResolver.CommandExists("pwsh"))
            return; // 宿主没装 pwsh 时退到 5.1 或 cmd 都是合理降级

        Assert.AreEqual(
            HostShellResolver.PowerShellShell,
            HostShellResolver.ResolveShell(null),
            "auto 在 Windows 上必须解析为 PowerShell 家族，不能回退到 cmd");
        Assert.AreEqual(
            HostShellResolver.PowerShellShell,
            HostShellResolver.ResolveShell("auto"),
            "显式 auto 与省略参数必须等价");
    }

    /// <summary>判据 2：pwsh 与 powershell 是同一家族的两个入口名，必须归一到同一标识。</summary>
    [TestMethod]
    public void ResolveShell_PwshAndPowershell_MapToSameFamily()
    {
        Assert.AreEqual(HostShellResolver.PowerShellShell, HostShellResolver.ResolveShell("pwsh"));
        Assert.AreEqual(HostShellResolver.PowerShellShell, HostShellResolver.ResolveShell("POWERSHELL"));
    }

    /// <summary>判据 3：显式 cmd 不许被 auto 规则改写——调用方要 cmd 就得给 cmd。</summary>
    [TestMethod]
    public void ResolveShell_ExplicitCmd_IsPreserved()
    {
        Assert.AreEqual("cmd", HostShellResolver.ResolveShell("cmd"));
        Assert.AreEqual("cmd", HostShellResolver.ResolveShell(" CMD "));
    }

    /// <summary>
    /// 判据 4：PowerShell 家族必须优先 pwsh 7。若这里退化成 powershell.exe，
    /// <c>??</c>、三元赋值等 7.x 语法会在 5.1 下解析失败。
    /// </summary>
    [TestMethod]
    public void ResolvePowerShellExecutable_PrefersPwsh7()
    {
        if (!HostShellResolver.CommandExists("pwsh"))
            return;

        Assert.AreEqual("pwsh", HostShellResolver.ResolvePowerShellExecutable());
    }

    /// <summary>
    /// 判据 5：PowerShell 启动参数必须含 -NoProfile / -ExecutionPolicy Bypass / -Command，
    /// 且命令被 UTF-8 包装（否则 dotnet 的 UTF-8 输出会被按活动代码页解码成乱码）。
    /// </summary>
    [TestMethod]
    public void ConfigureStartInfo_PowerShell_CarriesUtf8WrapperAndNonInteractiveFlags()
    {
        if (!OperatingSystem.IsWindows() && !HostShellResolver.CommandExists("pwsh"))
            return;

        var psi = new ProcessStartInfo();
        var ok = HostShellResolver.ConfigureStartInfo(
            psi, HostShellResolver.PowerShellShell, "echo hi", ".", out var error);

        Assert.IsTrue(ok, "PowerShell 配置失败: " + error);
        Assert.AreEqual("pwsh", psi.FileName, "必须优先 pwsh 7");

        var args = psi.ArgumentList.ToArray();
        CollectionAssert.Contains(args, "-NoProfile", "必须禁止加载用户 profile");
        CollectionAssert.Contains(args, "-ExecutionPolicy");
        CollectionAssert.Contains(args, "Bypass");
        CollectionAssert.Contains(args, "-Command");

        var script = args[^1];
        StringAssert.Contains(script, "OutputEncoding", "缺少 UTF-8 包装");
        StringAssert.Contains(script, "echo hi", "原始命令必须保留在包装末尾");
        Assert.IsTrue(script.EndsWith("echo hi", StringComparison.Ordinal),
            "包装必须只前置环境设置，不得改写命令本身");
    }

    /// <summary>
    /// 判据 6：cmd 分支必须用 <c>Arguments</c> 原样拼接。
    /// 若改成 ArgumentList，.NET 会把内层 <c>"</c> 转义成 <c>\"</c>，
    /// cmd 不认 ⇒ 含引号命令 exit≠0 且输出全空（实测 2026-09-19）。
    /// </summary>
    [TestMethod]
    public void ConfigureStartInfo_Cmd_UsesRawArgumentsNotArgumentList()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var psi = new ProcessStartInfo();
        var ok = HostShellResolver.ConfigureStartInfo(psi, "cmd", "echo \"a b\"", ".", out var error);

        Assert.IsTrue(ok, "cmd 配置失败: " + error);
        Assert.AreEqual("cmd.exe", psi.FileName);
        Assert.AreEqual("/d /s /c \"echo \"a b\"\"", psi.Arguments);
        Assert.AreEqual(0, psi.ArgumentList.Count, "cmd 不得使用 ArgumentList");
    }

    /// <summary>判据 7：未知 shell 必须 fail-closed，且错误信息点名该 shell。</summary>
    [TestMethod]
    public void ConfigureStartInfo_UnknownShell_FailsClosed()
    {
        var psi = new ProcessStartInfo();
        var ok = HostShellResolver.ConfigureStartInfo(psi, "fish", "echo hi", ".", out var error);

        Assert.IsFalse(ok);
        StringAssert.Contains(error, "fish");
    }

    /// <summary>判据 8：包装是纯前置，命令原样保留在末尾。</summary>
    [TestMethod]
    public void WrapPowerShellCommand_PrefixesEnvironmentWithoutRewriting()
    {
        var wrapped = HostShellResolver.WrapPowerShellCommand("Get-ChildItem | Select-Object -First 1");

        StringAssert.Contains(wrapped, "OutputEncoding");
        StringAssert.Contains(wrapped, "$PSStyle.OutputRendering = 'PlainText'");
        Assert.IsTrue(wrapped.EndsWith("Get-ChildItem | Select-Object -First 1", StringComparison.Ordinal));
    }
}
