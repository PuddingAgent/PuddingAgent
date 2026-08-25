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

        Assert.IsTrue(SystemCommandParser.TryParse("/whoami", out var whoAmI));
        Assert.AreEqual(SystemCommandKind.WhoAmI, whoAmI.CommandKind);
        Assert.AreEqual("whoami", whoAmI.TargetId);

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
        Assert.IsFalse(SystemCommandParser.TryParse("/whoami all", out _));
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

    [TestMethod]
    public void RequiresPrivilege_LeavesOnlyHelpAndStatusReadOnly()
    {
        Assert.IsTrue(SystemCommandParser.TryParse("/help", out var help));
        Assert.IsFalse(SystemCommandParser.RequiresPrivilege(help));

        Assert.IsTrue(SystemCommandParser.TryParse("/status", out var status));
        Assert.IsFalse(SystemCommandParser.RequiresPrivilege(status));

        Assert.IsTrue(SystemCommandParser.TryParse("/whoami", out var whoAmI));
        Assert.IsFalse(SystemCommandParser.RequiresPrivilege(whoAmI));

        Assert.IsTrue(SystemCommandParser.TryParse("/yolo", out var yolo));
        Assert.IsTrue(SystemCommandParser.RequiresPrivilege(yolo));

        Assert.IsTrue(SystemCommandParser.TryParse("/authorize shell", out var authorize));
        Assert.IsTrue(SystemCommandParser.RequiresPrivilege(authorize));
    }

    [TestMethod]
    public void TryParse_Goal_Commands_Keep_Raw_Text_For_Goal_Service()
    {
        // ADR-074：/goal 的 objective 是自由文本，SystemCommandParser 只负责识别，
        // RawText 原样透传给 Goal 应用服务做完整 grammar 解析。
        var parsed = SystemCommandParser.TryParse(
            "/goal 修复全部失败测试 --rounds 32", out var command);

        Assert.IsTrue(parsed);
        Assert.AreEqual(SystemCommandAction.Run, command.Action);
        Assert.AreEqual(SystemCommandKind.Goal, command.CommandKind);
        Assert.AreEqual("goal", command.TargetId);
        Assert.AreEqual("/goal 修复全部失败测试 --rounds 32", command.RawText);
    }

    [TestMethod]
    public void TryParse_Goal_Multiline_Objective_Is_Preserved()
    {
        var raw = "/goal set 第一行\n第二行 --rounds 8";
        Assert.IsTrue(SystemCommandParser.TryParse(raw, out var command));
        Assert.AreEqual(SystemCommandKind.Goal, command.CommandKind);
        Assert.AreEqual(raw, command.RawText);
    }

    [TestMethod]
    public void TryParse_Goal_Like_Commands_Do_Not_LeakInto_Other_Kinds()
    {
        Assert.IsFalse(SystemCommandParser.TryParse("/goals", out _));
        Assert.IsFalse(SystemCommandParser.TryParse("/goalkeeper save", out _));
    }

    [TestMethod]
    public void RequiresPrivilege_Goal_Is_Privileged()
    {
        // 保守裁决：外部 Connector 必须在白名单内才能使用 /goal（含 status），
        // 防止未经授权的渠道建立后台执行权限。
        Assert.IsTrue(SystemCommandParser.TryParse("/goal", out var goal));
        Assert.IsTrue(SystemCommandParser.RequiresPrivilege(goal));
    }
}
