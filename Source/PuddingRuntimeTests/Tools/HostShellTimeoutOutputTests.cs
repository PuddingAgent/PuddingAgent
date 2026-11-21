using Microsoft.Extensions.Logging.Abstractions;
using PuddingRuntime.Services.Tools;

namespace PuddingRuntimeTests.Tools;

/// <summary>
/// 超时路径的输出保留契约。
/// 旧行为：超时一律 <c>Output = string.Empty</c>，进程被杀前已经打印的内容全部丢弃，
/// 调用方只能看到「timed out after N seconds」，无从判断命令跑到了哪一步。
/// </summary>
[TestClass]
public sealed class HostShellTimeoutOutputTests
{
    // ping 用来占满超时窗口；echo 负责在超时前产生可识别的输出。
    private const string SlowTail = "& ping -n 31 127.0.0.1 > nul";

    [TestMethod]
    public async Task Timeout_Preserves_Partial_StdOut_And_Says_So()
    {
        if (!OperatingSystem.IsWindows()) Assert.Inconclusive("CMD requires Windows.");

        var result = await HostShellExecutor.ExecuteAsync(new HostShellRequest
        {
            Command = "echo PARTIAL_MARKER_7a2 " + SlowTail,
            Shell = "cmd",
            TimeoutSeconds = 2,
        }, NullLogger.Instance);

        Assert.IsFalse(result.Success, "命令必须被超时终止");
        Assert.AreEqual(-1, result.ExitCode, "超时的退出码约定为 -1");
        StringAssert.Contains(result.Error!, "timed out after 2 seconds");
        StringAssert.Contains(result.Error!, "preserved in output",
            "超时时必须明确告知：超时前产生的输出被保留在 output 字段");
        StringAssert.Contains(result.Output, "PARTIAL_MARKER_7a2",
            "超时前已经打印的内容不得被丢弃");
        StringAssert.Contains(result.Output, "shell=cmd",
            "保留的输出仍应带执行元数据，格式与正常结束一致");
    }

    [TestMethod]
    public async Task Timeout_Preserves_Partial_StdErr()
    {
        if (!OperatingSystem.IsWindows()) Assert.Inconclusive("CMD requires Windows.");

        var result = await HostShellExecutor.ExecuteAsync(new HostShellRequest
        {
            Command = "echo STDERR_MARKER_9c1 1>&2 " + SlowTail,
            Shell = "cmd",
            TimeoutSeconds = 2,
        }, NullLogger.Instance);

        Assert.IsFalse(result.Success);
        StringAssert.Contains(result.Output, "[stderr]: STDERR_MARKER_9c1",
            "stderr 也要走与成功路径相同的合并口径");
    }

    [TestMethod]
    public async Task Timeout_Without_Any_Output_Does_Not_Claim_Preserved()
    {
        if (!OperatingSystem.IsWindows()) Assert.Inconclusive("CMD requires Windows.");

        var result = await HostShellExecutor.ExecuteAsync(new HostShellRequest
        {
            Command = "ping -n 31 127.0.0.1 > nul",
            Shell = "cmd",
            TimeoutSeconds = 2,
        }, NullLogger.Instance);

        Assert.IsFalse(result.Success);
        StringAssert.Contains(result.Error!, "timed out after 2 seconds");
        Assert.IsFalse(result.Error!.Contains("preserved in output", StringComparison.Ordinal),
            "确实没有任何输出时不得声称保留了输出——那会把「没来得及输出」和「我们主动保留」混为一谈");
        Assert.AreEqual(string.Empty, result.Output,
            "无输出时 output 必须为空，不能只留一行 [shell=...] 元数据冒充内容");
    }
}
