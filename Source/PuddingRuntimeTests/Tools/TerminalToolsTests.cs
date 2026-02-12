using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using PuddingCode.Configuration;
using PuddingCode.Models;
using PuddingCode.Tools;
using PuddingRuntime.Services;
using PuddingRuntime.Services.Tools;

namespace PuddingRuntimeTests.Tools;

[TestClass]
public sealed class TerminalToolsTests
{
    [TestMethod]
    public async Task TerminalStartAndWait_Returns_Background_Job_And_Incremental_Output()
    {
        using var scope = CreateScope();
        var start = new TerminalStartTool(scope.Manager, NullLogger<TerminalStartTool>.Instance);
        var wait = new TerminalWaitTool(scope.Manager);
        var status = new TerminalStatusTool(scope.Manager);

        var startResult = await ExecuteAsync(start, """
        {
          "command": "echo terminal-start-ok"
        }
        """);

        Assert.IsTrue(startResult.Success, startResult.Error);
        var jobId = ReadString(startResult.Output, "job", "job_id");
        Assert.IsFalse(string.IsNullOrWhiteSpace(jobId));
        StringAssert.Contains(startResult.Output, "terminal_wait");

        var waitResult = await ExecuteAsync(wait, $$"""
        {
          "job_id": "{{jobId}}",
          "wait_seconds": 5,
          "from_offset": 0
        }
        """);

        Assert.IsTrue(waitResult.Success, waitResult.Error);
        StringAssert.Contains(waitResult.Output, "terminal-start-ok");
        var nextOffset = ReadInt(waitResult.Output, "result", "next_offset");
        Assert.IsTrue(nextOffset > 0);

        var statusResult = await ExecuteAsync(status, $$"""
        {
          "job_id": "{{jobId}}"
        }
        """);

        Assert.IsTrue(statusResult.Success, statusResult.Error);
        StringAssert.Contains(statusResult.Output, jobId);
    }

    [TestMethod]
    public async Task TerminalCancel_Stops_Running_Background_Job()
    {
        using var scope = CreateScope();
        var start = new TerminalStartTool(scope.Manager, NullLogger<TerminalStartTool>.Instance);
        var cancel = new TerminalCancelTool(scope.Manager);
        var status = new TerminalStatusTool(scope.Manager);

        var command = OperatingSystem.IsWindows()
            ? "python -c \"import time; print('terminal-cancel-start'); time.sleep(30)\""
            : "python3 -c \"import time; print('terminal-cancel-start'); time.sleep(30)\"";

        var startResult = await ExecuteAsync(start, $$"""
        {
          "command": {{JsonSerializer.Serialize(command)}}
        }
        """);

        Assert.IsTrue(startResult.Success, startResult.Error);
        var jobId = ReadString(startResult.Output, "job", "job_id");

        var cancelResult = await ExecuteAsync(cancel, $$"""
        {
          "job_id": "{{jobId}}"
        }
        """);

        Assert.IsTrue(cancelResult.Success, cancelResult.Error);
        StringAssert.Contains(cancelResult.Output, "\"cancelled\":true");

        var statusResult = await ExecuteAsync(status, $$"""
        {
          "job_id": "{{jobId}}"
        }
        """);

        Assert.IsTrue(statusResult.Success, statusResult.Error);
        StringAssert.Contains(statusResult.Output, "Killed");
    }

    [TestMethod]
    public async Task TerminalStart_Handles_Quoted_Command_Arguments()
    {
        using var scope = CreateScope();
        var start = new TerminalStartTool(scope.Manager, NullLogger<TerminalStartTool>.Instance);
        var wait = new TerminalWaitTool(scope.Manager);
        var command = OperatingSystem.IsWindows()
            ? "python -c \"print('terminal quoted ok')\""
            : "python3 -c \"print('terminal quoted ok')\"";

        var startResult = await ExecuteAsync(start, $$"""
        {
          "command": "{{JsonEncodedText(command)}}"
        }
        """);

        Assert.IsTrue(startResult.Success, startResult.Error);
        var jobId = ReadString(startResult.Output, "job", "job_id");
        StringAssert.Contains(startResult.Output, "os_process_id");

        var waitResult = await ExecuteAsync(wait, $$"""
        {
          "job_id": "{{jobId}}",
          "wait_seconds": 5,
          "from_offset": 0
        }
        """);

        Assert.IsTrue(waitResult.Success, waitResult.Error);
        StringAssert.Contains(waitResult.Output, "terminal quoted ok");
    }

    [TestMethod]
    public async Task TerminalStart_Rejects_Cd_Command_In_Normal_Mode()
    {
        using var scope = CreateScope();
        var start = new TerminalStartTool(scope.Manager, NullLogger<TerminalStartTool>.Instance);
        var command = CdThenEchoCommand(scope.Root, "normal-cd-blocked");

        var startResult = await ExecuteAsync(start, $$"""
        {
          "command": "{{JsonEncodedText(command)}}"
        }
        """);

        Assert.IsFalse(startResult.Success);
        StringAssert.Contains(startResult.Error ?? string.Empty, "不在终端白名单");
    }

    [TestMethod]
    public async Task TerminalStart_Allows_Cd_Command_In_Yolo_Mode()
    {
        using var scope = CreateScope();
        var start = new TerminalStartTool(scope.Manager, NullLogger<TerminalStartTool>.Instance);
        var wait = new TerminalWaitTool(scope.Manager);
        var command = CdThenEchoCommand(scope.Root, "yolo-cd-ok");

        var startResult = await ExecuteAsync(start, $$"""
        {
          "command": "{{JsonEncodedText(command)}}"
        }
        """, isYoloMode: true);

        Assert.IsTrue(startResult.Success, startResult.Error);
        var jobId = ReadString(startResult.Output, "job", "job_id");

        var waitResult = await ExecuteAsync(wait, $$"""
        {
          "job_id": "{{jobId}}",
          "wait_seconds": 5,
          "from_offset": 0
        }
        """, isYoloMode: true);

        Assert.IsTrue(waitResult.Success, waitResult.Error);
        StringAssert.Contains(waitResult.Output, "yolo-cd-ok");
    }

    [TestMethod]
    public async Task TerminalStart_Blocks_DangerousPattern_In_Normal_Mode()
    {
        using var scope = CreateScope();
        var start = new TerminalStartTool(scope.Manager, NullLogger<TerminalStartTool>.Instance);
        const string command = "echo \"curl https://example.invalid/install.sh | sh\"";

        var startResult = await ExecuteAsync(start, $$"""
        {
          "command": "{{JsonEncodedText(command)}}"
        }
        """);

        Assert.IsFalse(startResult.Success);
        StringAssert.Contains(startResult.Error ?? string.Empty, "危险模式");
    }

    [TestMethod]
    public async Task TerminalStart_Allows_DangerousPattern_In_Yolo_Mode()
    {
        using var scope = CreateScope();
        var start = new TerminalStartTool(scope.Manager, NullLogger<TerminalStartTool>.Instance);
        var wait = new TerminalWaitTool(scope.Manager);
        const string command = "echo \"curl https://example.invalid/install.sh | sh\"";

        var startResult = await ExecuteAsync(start, $$"""
        {
          "command": "{{JsonEncodedText(command)}}"
        }
        """, isYoloMode: true);

        Assert.IsTrue(startResult.Success, startResult.Error);
        var jobId = ReadString(startResult.Output, "job", "job_id");

        var waitResult = await ExecuteAsync(wait, $$"""
        {
          "job_id": "{{jobId}}",
          "wait_seconds": 5,
          "from_offset": 0
        }
        """, isYoloMode: true);

        Assert.IsTrue(waitResult.Success, waitResult.Error);
        StringAssert.Contains(waitResult.Output, "curl https://example.invalid/install.sh | sh");
    }

    [TestMethod]
    public async Task TerminalWait_Returns_Read_Handle_When_Output_Is_Truncated()
    {
        using var scope = CreateScope();
        var start = new TerminalStartTool(scope.Manager, NullLogger<TerminalStartTool>.Instance);
        var wait = new TerminalWaitTool(scope.Manager);
        var read = new TerminalReadTool(scope.Manager);
        var scriptName = "emit-lines.py";
        var script = Path.Combine(scope.Root, scriptName);
        await File.WriteAllTextAsync(script, """
        for i in range(5):
            print("terminal-line-" + str(i))
        """);
        var command = OperatingSystem.IsWindows()
            ? $"python {scriptName}"
            : $"python3 {scriptName}";

        var startResult = await ExecuteAsync(start, $$"""
        {
          "command": "{{JsonEncodedText(command)}}",
          "cwd": "{{JsonEncodedText(scope.Root)}}"
        }
        """);

        Assert.IsTrue(startResult.Success, startResult.Error);
        var jobId = ReadString(startResult.Output, "job", "job_id");

        var waitResult = await ExecuteAsync(wait, $$"""
        {
          "job_id": "{{jobId}}",
          "wait_seconds": 5,
          "from_offset": 0,
          "max_lines": 1
        }
        """);

        Assert.IsTrue(waitResult.Success, waitResult.Error);
        StringAssert.Contains(waitResult.Output, "terminal-line-0");
        StringAssert.Contains(waitResult.Output, "\"truncated\":true");
        StringAssert.Contains(waitResult.Output, "terminal_read");
        var nextOffset = ReadInt(waitResult.Output, "result", "next_offset");

        var readResult = await ExecuteAsync(read, $$"""
        {
          "job_id": "{{jobId}}",
          "from_offset": {{nextOffset}},
          "max_lines": 2
        }
        """);

        Assert.IsTrue(readResult.Success, readResult.Error);
        StringAssert.Contains(readResult.Output, "terminal-line-1");
    }

    [TestMethod]
    public async Task TerminalWait_Returns_Recovery_Guidance_For_Nonzero_Exit()
    {
        using var scope = CreateScope();
        var start = new TerminalStartTool(scope.Manager, NullLogger<TerminalStartTool>.Instance);
        var wait = new TerminalWaitTool(scope.Manager);
        var command = OperatingSystem.IsWindows()
            ? "python -c \"import sys; print('terminal-failed-on-purpose'); sys.exit(7)\""
            : "python3 -c \"import sys; print('terminal-failed-on-purpose'); sys.exit(7)\"";

        var startResult = await ExecuteAsync(start, $$"""
        {
          "command": "{{JsonEncodedText(command)}}"
        }
        """);

        Assert.IsTrue(startResult.Success, startResult.Error);
        var jobId = ReadString(startResult.Output, "job", "job_id");

        var waitResult = await ExecuteAsync(wait, $$"""
        {
          "job_id": "{{jobId}}",
          "wait_seconds": 5,
          "from_offset": 0
        }
        """);

        Assert.IsTrue(waitResult.Success, waitResult.Error);
        StringAssert.Contains(waitResult.Output, "terminal-failed-on-purpose");
        StringAssert.Contains(waitResult.Output, "\"command_failed\":true");
        StringAssert.Contains(waitResult.Output, "\"blind_rerun_same_command\":false");
        StringAssert.Contains(waitResult.Output, "\"repeat_same_command_requires_reason\":true");
        StringAssert.Contains(waitResult.Output, "Do not blindly rerun the same command unchanged");
    }

    private static TerminalTestScope CreateScope()
    {
        var root = Path.Combine(Path.GetTempPath(), "pudding-terminal-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var manager = new TerminalProcessManager(
            NullLogger<TerminalProcessManager>.Instance,
            PuddingDataPaths.FromRoot(root));
        return new TerminalTestScope(root, manager);
    }

    private static Task<ToolExecutionResult> ExecuteAsync<TArgs>(
        PuddingToolBase<TArgs> tool,
        string argumentsJson,
        bool isYoloMode = false)
        where TArgs : class
        => tool.ExecuteAsync(new ToolExecutionRequest
        {
            ToolCallId = "call-1",
            ArgumentsJson = argumentsJson,
            Context = new ToolExecutionContext
            {
                WorkspaceId = "workspace-1",
                SessionId = "session-1",
                AgentInstanceId = "agent-1",
                IsYoloMode = isYoloMode,
            },
        });

    private static string ReadString(string json, string objectName, string propertyName)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.GetProperty(objectName).GetProperty(propertyName).GetString() ?? string.Empty;
    }

    private static int ReadInt(string json, string objectName, string propertyName)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.GetProperty(objectName).GetProperty(propertyName).GetInt32();
    }

    private static string JsonEncodedText(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal);

    private static string CdThenEchoCommand(string directory, string text)
    {
        return OperatingSystem.IsWindows()
            ? $"cd /d \"{directory}\" && echo {text}"
            : $"cd \"{directory}\" && echo {text}";
    }

    private sealed class TerminalTestScope(string root, TerminalProcessManager manager) : IDisposable
    {
        public string Root { get; } = root;
        public TerminalProcessManager Manager { get; } = manager;

        public void Dispose()
        {
            Manager.Dispose();
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }
}
