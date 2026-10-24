using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using PuddingCode.Abstractions;
using PuddingCode.Tools;
using PuddingRuntime.Services.Tools;

namespace PuddingRuntimeTests.Tools;

[TestClass]
public sealed class TerminalStartEfficiencyTests
{
    private static readonly TerminalProcessInfo Running = new()
    {
        ProcessId = "short-job", SessionId = "session", Command = "echo ok",
        Status = TerminalProcessStatus.Running,
    };

    [TestMethod]
    public async Task Start_Collects_Short_Job_Without_Another_Model_Call()
    {
        var manager = CreateManager();
        manager.SetupSequence(m => m.ReadOutputAsync("short-job", 0, 120, 4000, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Snapshot(Running))
            .ReturnsAsync(Snapshot(Running with { Status = TerminalProcessStatus.Exited, ExitCode = 0 }, "last-line"));

        var result = await Execute(manager, """{"command":"echo ok"}""");
        Assert.IsTrue(result.Success, result.Error);
        using var doc = JsonDocument.Parse(result.Output);
        Assert.AreEqual("Exited", doc.RootElement.GetProperty("job").GetProperty("status").GetString());
        Assert.AreEqual(0, doc.RootElement.GetProperty("job").GetProperty("exit_code").GetInt32());
        StringAssert.Contains(doc.RootElement.GetProperty("output").GetProperty("output").GetString()!, "last-line");
        StringAssert.Contains(doc.RootElement.GetProperty("next_action").GetString()!, "completed");
        manager.Verify(m => m.StartAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [TestMethod]
    public async Task Start_Uses_Completed_Snapshot_For_Failed_Job_Too()
    {
        var manager = CreateManager();
        manager.Setup(m => m.ReadOutputAsync("short-job", 0, 120, 4000, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Snapshot(Running with { Status = TerminalProcessStatus.Failed, ExitCode = 7 }, "failed-command"));
        var result = await Execute(manager, """{"command":"echo ok"}""");
        using var doc = JsonDocument.Parse(result.Output);
        Assert.AreEqual(7, doc.RootElement.GetProperty("job").GetProperty("exit_code").GetInt32());
        Assert.IsTrue(doc.RootElement.GetProperty("output").GetProperty("command_failed").GetBoolean());
    }

    [TestMethod]
    public async Task Start_ZeroYield_Returns_Running_Job_Immediately()
    {
        var manager = CreateManager();
        manager.Setup(m => m.ReadOutputAsync("short-job", 0, 120, 4000, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Snapshot(Running, "first-line"));
        var result = await Execute(manager, """{"command":"echo ok","yield_time_ms":0}""");
        Assert.IsTrue(result.Success, result.Error);
        manager.Verify(m => m.ReadOutputAsync("short-job", 0, 120, 4000, It.IsAny<CancellationToken>()), Times.Once);
        StringAssert.Contains(result.Output, "terminal_wait");
        StringAssert.Contains(result.Output, "from_offset");
    }

    [TestMethod]
    public async Task Start_Truncated_Output_Returns_Read_Handle_Without_Waiting()
    {
        var manager = CreateManager();
        manager.Setup(m => m.ReadOutputAsync("short-job", 0, 120, 4000, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Snapshot(Running, "first-line") with { Truncated = true, TotalLines = 2 });
        var result = await Execute(manager, """{"command":"echo ok","yield_time_ms":10000}""");
        Assert.IsTrue(result.Success, result.Error);
        StringAssert.Contains(result.Output, "terminal_read");
        manager.Verify(m => m.ReadOutputAsync("short-job", 0, 120, 4000, It.IsAny<CancellationToken>()), Times.Once);
    }

    [TestMethod]
    [DataRow(-1)]
    [DataRow(10001)]
    public async Task Start_Invalid_Yield_Does_Not_Launch_Command(int milliseconds)
    {
        var manager = CreateManager();
        var result = await Execute(manager, $$"""{"command":"echo ok","yield_time_ms":{{milliseconds}}}""");
        Assert.IsFalse(result.Success);
        StringAssert.Contains(result.Error!, "yield_time_ms");
        // 只报范围不够：调用方几乎总是想用它“等长任务”，必须被告知改用 terminal_wait。
        // 这条断言让「删掉那句指引」的回归必然取红（变异已验证）。
        StringAssert.Contains(result.Error!, "terminal_wait");
        manager.Verify(m => m.StartAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [TestMethod]
    public async Task Start_Deadline_Preserves_Running_Job()
    {
        var manager = CreateManager();
        manager.Setup(m => m.ReadOutputAsync("short-job", 0, 120, 4000, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Snapshot(Running));
        var result = await Execute(manager, """{"command":"echo ok","yield_time_ms":20}""");
        Assert.IsTrue(result.Success, result.Error);
        StringAssert.Contains(result.Output, "Running");
        manager.Verify(m => m.KillAsync(It.IsAny<string>()), Times.Never);
    }

    private static Mock<ITerminalProcessManager> CreateManager()
    {
        var manager = new Mock<ITerminalProcessManager>();
        manager.Setup(m => m.StartAsync("session", "echo ok", It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Running);
        return manager;
    }

    private static TerminalOutputSnapshot Snapshot(TerminalProcessInfo job, params string[] lines) => new()
    {
        Process = job, Lines = lines, NextOffset = lines.Length, TotalLines = lines.Length,
    };

    private static Task<ToolExecutionResult> Execute(Mock<ITerminalProcessManager> manager, string args) =>
        new TerminalStartTool(manager.Object, NullLogger<TerminalStartTool>.Instance).ExecuteAsync(new ToolExecutionRequest
        {
            ToolCallId = "start", ArgumentsJson = args,
            Context = new ToolExecutionContext { SessionId = "session", WorkspaceId = "test", AgentInstanceId = "test", WorkingDirectory = Path.GetTempPath() },
        });
}
