using System.Text.RegularExpressions;
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
    public async Task Cmd_Quoted_Pipeline_Preserves_Arguments_And_Output()
    {
        if (!OperatingSystem.IsWindows()) Assert.Inconclusive("CMD requires Windows.");
        var result = await HostShellExecutor.ExecuteAsync(new HostShellRequest
        {
            Command = "echo \"alpha beta\" | findstr \"alpha\"", Shell = "cmd", TimeoutSeconds = 10,
        }, NullLogger.Instance);
        Assert.IsTrue(result.Success, result.Error + " " + result.Output);
        StringAssert.Contains(result.Output, "alpha beta");
    }

    [TestMethod]
    [DataRow("auto")]
    [DataRow("powershell")]
    public async Task PowerShell_Cmd_Prefix_Returns_Actionable_Error_Before_Execution(string shell)
    {
        if (!OperatingSystem.IsWindows()) Assert.Inconclusive("Windows shell defaults.");
        var result = await HostShellExecutor.ExecuteAsync(new HostShellRequest
        {
            Command = "cd /d . && echo should-not-run", Shell = shell, TimeoutSeconds = 10,
        }, NullLogger.Instance);
        Assert.IsFalse(result.Success);
        StringAssert.Contains(result.Error!, "shell_syntax_mismatch");
        StringAssert.Contains(result.Error!, "shell=cmd");
        StringAssert.Contains(result.Error!, "working_directory");
        Assert.IsFalse(result.Output.Contains("should-not-run", StringComparison.Ordinal));
    }

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
            Command = "py -3 -c \"print('✅ shell utf8 ok')\"",
            Shell = "powershell",
            TimeoutSeconds = 10,
        }, NullLogger.Instance);

        Assert.IsTrue(result.Success, result.Error);
        StringAssert.Contains(result.Output, "shell utf8 ok");
        StringAssert.Contains(result.Output, "shell=powershell");
        StringAssert.Contains(result.Output, "cwd=");
    }

    [TestMethod]
    public async Task HostShellExecutor_AcceptsPwshAsPowerShellAlias()
    {
        if (!OperatingSystem.IsWindows())
            Assert.Inconclusive("PowerShell alias regression test is Windows-specific.");

        var result = await HostShellExecutor.ExecuteAsync(new HostShellRequest
        {
            Command = "Write-Output 'pwsh-alias-ok'",
            Shell = "pwsh",
            TimeoutSeconds = 10,
        }, NullLogger.Instance);

        Assert.IsTrue(result.Success, result.Error);
        StringAssert.Contains(result.Output, "pwsh-alias-ok");
        Assert.AreEqual("powershell", result.Shell);
    }

    [TestMethod]
    public async Task HostShellExecutor_WslMode_UsesWindowsWorkingDirectoryMapping()
    {
        if (!OperatingSystem.IsWindows())
            Assert.Inconclusive("WSL regression test is Windows-specific.");

        var result = await HostShellExecutor.ExecuteAsync(new HostShellRequest
        {
            Command = "pwd",
            Shell = "wsl",
            WorkingDirectory = Directory.GetCurrentDirectory(),
            TimeoutSeconds = 30,
        }, NullLogger.Instance);

        if (!result.Success && result.Error?.Contains("wsl.exe is not available", StringComparison.OrdinalIgnoreCase) == true)
            Assert.Inconclusive("WSL is not installed on this test host.");

        Assert.IsTrue(result.Success, result.Error);
        Assert.AreEqual("wsl", result.Shell);
        StringAssert.Contains(result.Output.Replace('\\', '/'), "/mnt/");
    }

    [TestMethod]
    public async Task HostShellExecutor_Blocks_Process_Termination_At_Execution_Boundary()
    {
        var command = OperatingSystem.IsWindows()
            ? $"taskkill /PID {Environment.ProcessId} /F"
            : $"kill -9 {Environment.ProcessId}";

        var result = await HostShellExecutor.ExecuteAsync(new HostShellRequest
        {
            Command = command,
            Shell = "auto",
            TimeoutSeconds = 10,
        }, NullLogger.Instance);

        Assert.IsFalse(result.Success);
        StringAssert.Contains(result.Error ?? string.Empty, "terminal_cancel");
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
            new[] { "command", "shell", "working_directory", "timeout_seconds", "reason", "tail_lines" },
            descriptor.Parameters.Properties.Select(p => p.Name).ToArray());
    }

    // 2026-11-14 不变量：提示里只能点名子代理一定已加载的工具。
    // 子代理预加载集 = SubAgentTool.PreloadToolIds（file_read / file_search / search_grep）；
    // CoreToolIds 不含 list_dir，所以旧版对 dir 推荐 list_dir 的提示是拿不到的。
    [TestMethod]
    public void ShellTip_OnlyNamesToolsTheSubAgentAlwaysHas()
    {
        var alwaysLoaded = new[] { "file_read", "file_search", "search_grep" };
        var triggers = new[]
        {
            "select-string foo", "grep -n foo", "findstr foo", "rg foo",
            "get-childitem .", "gci", "dir /s /b", "ls",
            "get-content a.txt", "cat a.txt", "type a.txt",
            "test-path a.txt",
        };

        foreach (var command in triggers)
        {
            var tip = HostShellTool.BuildSpecializedToolTip(command);
            Assert.IsNotNull(tip, $"'{command}' 应产生提示");

            var named = Regex.Matches(tip, "`([a-z_]+)`").Select(m => m.Groups[1].Value).ToArray();
            Assert.IsTrue(named.Length > 0, $"'{command}' 的提示未点名任何工具：{tip}");
            foreach (var name in named)
            {
                CollectionAssert.Contains(
                    alwaysLoaded,
                    name,
                    $"'{command}' 的提示点名了子代理可能未加载的工具 '{name}'（提示无法执行）：{tip}");
            }
        }
    }

    // 反证：旧实现在 dir 上点名 list_dir。这条专门锁住那次回归。
    [TestMethod]
    public void ShellTip_Dir_DoesNotRecommendListDir()
    {
        var tip = HostShellTool.BuildSpecializedToolTip("dir /s /b Source\\*.csproj");

        Assert.IsNotNull(tip);
        StringAssert.Contains(tip, "file_search");
        Assert.IsFalse(tip.Contains("list_dir"), $"list_dir 不在子代理预加载集，不得点名：{tip}");
    }

    // 正向对照：不匹配任何专用工具的命令不发提示（不能为了覆盖率一律加）。
    [TestMethod]
    public void ShellTip_ReturnsNull_ForUnrelatedCommand()
    {
        Assert.IsNull(HostShellTool.BuildSpecializedToolTip("echo hello"));
        Assert.IsNull(HostShellTool.BuildSpecializedToolTip("dotnet build"));
        Assert.IsNull(HostShellTool.BuildSpecializedToolTip(""));
    }

    private static HostShellTool CreateHostShellTool()
    {
        var dataPaths = PuddingDataPaths.FromRoot(Path.Combine(Path.GetTempPath(), "pudding-host-shell-tests"));
        return new HostShellTool(dataPaths, new AuditLogger(dataPaths), NullLogger<HostShellTool>.Instance);
    }
}
