using PuddingCode.Tools;

namespace PuddingCoreTests.Tools;

[TestClass]
public sealed class SystemCommandParserTests
{
    [TestMethod]
    public void TryParse_Authorize_Defaults_To_Ten_Minutes()
    {
        var parsed = SystemCommandParser.TryParse("/authorize shell", out var command);

        Assert.IsTrue(parsed);
        Assert.AreEqual(SystemCommandAction.Authorize, command.Action);
        Assert.AreEqual("shell", command.TargetId);
        Assert.AreEqual(ToolAuthorizationScope.Timed, command.Scope);
        Assert.AreEqual(TimeSpan.FromMinutes(10), command.Duration);
    }

    [TestMethod]
    public void TryParse_Authorize_Session_Accepts_Underscore_Tool_Id()
    {
        var parsed = SystemCommandParser.TryParse("/authorize file_patch session", out var command);

        Assert.IsTrue(parsed);
        Assert.AreEqual("file_patch", command.TargetId);
        Assert.AreEqual(ToolAuthorizationScope.Session, command.Scope);
    }

    [TestMethod]
    public void TryParse_Rejects_Dotted_Tool_Id()
    {
        Assert.IsFalse(SystemCommandParser.TryParse("/authorize file.patch session", out _));
    }

    [TestMethod]
    public void TryParse_Help_Commands_Do_Not_Require_Tool()
    {
        Assert.IsTrue(SystemCommandParser.TryParse("/help", out var help));
        Assert.AreEqual(SystemCommandAction.Help, help.Action);
        Assert.AreEqual(SystemCommandKind.Help, help.CommandKind);

        Assert.IsTrue(SystemCommandParser.TryParse("/authorize -help", out var commandHelp));
        Assert.AreEqual(SystemCommandAction.Help, commandHelp.Action);
        Assert.AreEqual("authorize", commandHelp.TargetId);
    }

    [TestMethod]
    public void TryParse_System_Workflow_Commands_Are_Recognized()
    {
        Assert.IsTrue(SystemCommandParser.TryParse("/compact", out var compact));
        Assert.AreEqual(SystemCommandAction.Run, compact.Action);
        Assert.AreEqual(SystemCommandKind.Compact, compact.CommandKind);
        Assert.AreEqual("compact", compact.TargetId);

        Assert.IsTrue(SystemCommandParser.TryParse("/memory", out var memory));
        Assert.AreEqual(SystemCommandAction.Run, memory.Action);
        Assert.AreEqual(SystemCommandKind.Memory, memory.CommandKind);
        Assert.AreEqual("memory", memory.TargetId);

        Assert.IsTrue(SystemCommandParser.TryParse("/status", out var status));
        Assert.AreEqual(SystemCommandKind.Status, status.CommandKind);

        Assert.IsTrue(SystemCommandParser.TryParse("/stop all", out var stopAll));
        Assert.AreEqual(SystemCommandKind.Stop, stopAll.CommandKind);
        Assert.AreEqual("all", stopAll.TargetId);

        Assert.IsTrue(SystemCommandParser.TryParse("/mode safe", out var modeSafe));
        Assert.AreEqual(SystemCommandKind.Mode, modeSafe.CommandKind);
        Assert.AreEqual("safe", modeSafe.TargetId);

        Assert.IsTrue(SystemCommandParser.TryParse("/estop", out var estop));
        Assert.AreEqual(SystemCommandKind.EmergencyStop, estop.CommandKind);
    }

    [TestMethod]
    public void TryParse_Rejects_Invalid_Runtime_Command_Arguments()
    {
        Assert.IsFalse(SystemCommandParser.TryParse("/status all", out _));
        Assert.IsFalse(SystemCommandParser.TryParse("/stop session", out _));
        Assert.IsFalse(SystemCommandParser.TryParse("/mode turbo", out _));
        Assert.IsFalse(SystemCommandParser.TryParse("/estop now", out _));
    }

    [TestMethod]
    public void BuildHelpMessage_Uses_Markdown_List_For_Chat_Rendering()
    {
        var help = ToolAuthorizationDefaults.BuildHelpMessage();

        StringAssert.Contains(help, "- `/help`");
        StringAssert.Contains(help, "- `/compact`");
        StringAssert.Contains(help, Environment.NewLine + Environment.NewLine);
    }

    [TestMethod]
    public void BuildRequiredMessage_Tells_Agent_To_Request_Automatic_Approval()
    {
        var message = ToolAuthorizationDefaults.BuildRequiredMessage("shell");

        StringAssert.Contains(message, "request_tool_approval");
        StringAssert.Contains(message, "tool_id='shell'");
        StringAssert.Contains(message, "/authorize shell 10m");
    }

    [TestMethod]
    public void ComputeArgumentsHash_Normalizes_Json_Formatting_And_Object_Order()
    {
        var compact = """{"command":"pwd","shell":"auto","timeout_seconds":10}""";
        var spaced = """{"command": "pwd", "shell": "auto", "timeout_seconds": 10}""";
        var reordered = """{"timeout_seconds":10,"shell":"auto","command":"pwd"}""";

        var hash = ToolAuthorizationDefaults.ComputeArgumentsHash(compact);

        Assert.AreEqual(hash, ToolAuthorizationDefaults.ComputeArgumentsHash(spaced));
        Assert.AreEqual(hash, ToolAuthorizationDefaults.ComputeArgumentsHash(reordered));
    }

    [TestMethod]
    public void TryParse_Rejects_NonSlash_And_Unknown_Slash_Commands()
    {
        Assert.IsFalse(SystemCommandParser.TryParse("authorize shell", out _));
        Assert.IsFalse(SystemCommandParser.TryParse("/unknown shell", out _));
    }
}
