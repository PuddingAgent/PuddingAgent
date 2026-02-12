using Microsoft.Extensions.Logging.Abstractions;
using PuddingCode.Configuration;
using PuddingCode.Models;
using PuddingCode.Tools;
using PuddingRuntime.Services.Tools;

namespace PuddingRuntimeTests.Tools;

[TestClass]
public sealed class HostShellToolTests
{
    [TestMethod]
    public async Task HostShellExecutor_AutoMode_Executes_On_Host()
    {
        var result = await HostShellExecutor.ExecuteAsync(new HostShellRequest
        {
            Command = OperatingSystem.IsWindows() ? "echo pudding-host-shell" : "printf pudding-host-shell",
            Shell = "auto",
            TimeoutSeconds = 10,
        }, NullLogger.Instance);

        Assert.IsTrue(result.Success, result.Error);
        StringAssert.Contains(result.Output, "pudding-host-shell");
        Assert.AreEqual(0, result.ExitCode);
        Assert.IsFalse(string.IsNullOrWhiteSpace(result.Shell));
    }

    [TestMethod]
    public async Task HostShellExecutor_Rejects_Unsupported_Shell()
    {
        var result = await HostShellExecutor.ExecuteAsync(new HostShellRequest
        {
            Command = "echo no",
            Shell = "docker",
            TimeoutSeconds = 10,
        }, NullLogger.Instance);

        Assert.IsFalse(result.Success);
        StringAssert.Contains(result.Error, "Unsupported shell");
    }

    [TestMethod]
    public async Task HostShellExecutor_Rejects_Invalid_Timeout()
    {
        var result = await HostShellExecutor.ExecuteAsync(new HostShellRequest
        {
            Command = "echo no",
            Shell = "auto",
            TimeoutSeconds = 0,
        }, NullLogger.Instance);

        Assert.IsFalse(result.Success);
        StringAssert.Contains(result.Error, "timeout_seconds");
    }

    [TestMethod]
    public async Task HostShellExecutor_Sets_Utf8_Python_Output_Environment()
    {
        if (!OperatingSystem.IsWindows())
            Assert.Inconclusive("Windows console encoding regression test.");

        var result = await HostShellExecutor.ExecuteAsync(new HostShellRequest
        {
            Command = "python -c \"print('✅ shell utf8 ok')\"",
            Shell = "powershell",
            TimeoutSeconds = 10,
        }, NullLogger.Instance);

        Assert.IsTrue(result.Success, result.Error);
        StringAssert.Contains(result.Output, "shell utf8 ok");
        StringAssert.Contains(result.Output, "shell=powershell");
        StringAssert.Contains(result.Output, "cwd=");
    }

    [TestMethod]
    public async Task HostShellTool_Executes_With_Host_Shell_Parameters()
    {
        var tool = CreateHostShellTool();

        var result = await tool.ExecuteAsync(new ToolExecutionRequest
        {
            ToolCallId = "call-1",
            ArgumentsJson = $$"""
            {
                "command": "{{(OperatingSystem.IsWindows() ? "echo shell-tool-ok" : "printf shell-tool-ok")}}",
                "shell": "auto",
                "timeout_seconds": 10
            }
            """,
            Context = new ToolExecutionContext
            {
                AgentInstanceId = "agent-1",
                WorkspaceId = "default",
                SessionId = "session-1",
            },
        });

        Assert.IsTrue(result.Success, result.Error);
        StringAssert.Contains(result.Output, "shell-tool-ok");
    }

    [TestMethod]
    public void HostShellTool_Descriptor_Uses_Shell_Tool_Id()
    {
        var descriptor = CreateHostShellTool().Descriptor;

        Assert.AreEqual("shell", descriptor.ToolId);
        Assert.AreEqual(ToolCategory.Execute, descriptor.Category);
        Assert.AreEqual(ToolPermissionLevel.High, descriptor.PermissionLevel);
        Assert.IsTrue(descriptor.Safety.HasFlag(ToolSafetyFlags.RequiresShell));
        CollectionAssert.AreEqual(new[] { "command" }, descriptor.Parameters.Required.ToArray());
        CollectionAssert.AreEquivalent(
            new[] { "command", "shell", "working_directory", "timeout_seconds", "reason" },
            descriptor.Parameters.Properties.Select(p => p.Name).ToArray());
    }

    private static HostShellTool CreateHostShellTool()
    {
        var dataPaths = PuddingDataPaths.FromRoot(Path.Combine(Path.GetTempPath(), "pudding-host-shell-tests"));
        return new HostShellTool(dataPaths, new AuditLogger(dataPaths), NullLogger<HostShellTool>.Instance);
    }
}
