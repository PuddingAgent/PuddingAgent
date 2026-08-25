using PuddingCode.Goals;

namespace PuddingCoreTests.Goals;

[TestClass]
public sealed class GoalCommandTextParserTests
{
    [TestMethod]
    public void Bare_Goal_Is_Status_Not_Implicit_Create()
    {
        Assert.IsTrue(GoalCommandTextParser.TryParse("/goal", out var command, out _, out _));
        Assert.AreEqual(GoalCommandKind.Status, command.Kind);
        Assert.IsNull(command.Objective);

        Assert.IsTrue(GoalCommandTextParser.TryParse("/goal status", out command, out _, out _));
        Assert.AreEqual(GoalCommandKind.Status, command.Kind);
    }

    [TestMethod]
    public void Plain_Objective_Is_Set_Shorthand()
    {
        Assert.IsTrue(GoalCommandTextParser.TryParse(
            "/goal 修复全部失败测试并保持公开 API 不变",
            out var command, out _, out _));
        Assert.AreEqual(GoalCommandKind.Set, command.Kind);
        Assert.AreEqual("修复全部失败测试并保持公开 API 不变", command.Objective);
        Assert.IsNull(command.Rounds);
    }

    [TestMethod]
    public void Explicit_Set_Parses_Objective_And_Rounds()
    {
        Assert.IsTrue(GoalCommandTextParser.TryParse(
            "/goal set 修复全部失败测试 --rounds 32",
            out var command, out _, out _));
        Assert.AreEqual(GoalCommandKind.Set, command.Kind);
        Assert.AreEqual("修复全部失败测试", command.Objective);
        Assert.AreEqual(32, command.Rounds);
    }

    [TestMethod]
    public void Objective_May_Be_Multiline_And_Contain_Quotes()
    {
        var text = "/goal set 目标第一行\n\"第二行\" 带引号 'third' line\n--rounds 8";
        Assert.IsTrue(GoalCommandTextParser.TryParse(text, out var command, out _, out _));
        Assert.AreEqual(GoalCommandKind.Set, command.Kind);
        Assert.AreEqual("目标第一行\n\"第二行\" 带引号 'third' line", command.Objective);
        Assert.AreEqual(8, command.Rounds);
    }

    [TestMethod]
    public void Rounds_Out_Of_Range_Is_Rejected_Explicitly()
    {
        Assert.IsFalse(GoalCommandTextParser.TryParse(
            "/goal x --rounds 257", out _, out var errorCode, out var errorMessage));
        Assert.AreEqual(GoalErrorCodes.InvalidRounds, errorCode);
        Assert.IsTrue(errorMessage!.Contains("257"));

        Assert.IsFalse(GoalCommandTextParser.TryParse(
            "/goal x --rounds 0", out _, out errorCode, out _));
        Assert.AreEqual(GoalErrorCodes.InvalidRounds, errorCode);

        Assert.IsFalse(GoalCommandTextParser.TryParse(
            "/goal x --rounds abc", out _, out errorCode, out _));
        Assert.AreEqual(GoalErrorCodes.InvalidRounds, errorCode);
    }

    [TestMethod]
    public void Rounds_Boundaries_Are_Inclusive()
    {
        Assert.IsTrue(GoalCommandTextParser.TryParse("/goal x --rounds 1", out var one, out _, out _));
        Assert.AreEqual(1, one.Rounds);
        Assert.IsTrue(GoalCommandTextParser.TryParse("/goal x --rounds 256", out var max, out _, out _));
        Assert.AreEqual(256, max.Rounds);
    }

    [TestMethod]
    public void Objective_Length_Is_Bounded_At_4000()
    {
        var ok = new string('好', 4000);
        Assert.IsTrue(GoalCommandTextParser.TryParse($"/goal {ok}", out var command, out _, out _));
        Assert.AreEqual(4000, command.Objective!.Length);

        var tooLong = new string('好', 4001);
        Assert.IsFalse(GoalCommandTextParser.TryParse($"/goal {tooLong}", out _, out var errorCode, out _));
        Assert.AreEqual(GoalErrorCodes.InvalidObjective, errorCode);
    }

    [TestMethod]
    public void Whitespace_Only_Objective_Is_Rejected()
    {
        // 尾随空白被 trim 后等价于裸 /goal → status，绝不隐式创建目标。
        Assert.IsTrue(GoalCommandTextParser.TryParse("/goal    ", out var status, out _, out _));
        Assert.AreEqual(GoalCommandKind.Status, status.Kind);

        // 显式 set 但 objective 为空 → invalid_objective。
        Assert.IsFalse(GoalCommandTextParser.TryParse("/goal set ", out _, out var errorCode, out _));
        Assert.AreEqual(GoalErrorCodes.InvalidObjective, errorCode);
    }

    [TestMethod]
    public void Subcommands_Are_Case_Insensitive()
    {
        Assert.IsTrue(GoalCommandTextParser.TryParse("/GOAL PAUSE too costly", out var pause, out _, out _));
        Assert.AreEqual(GoalCommandKind.Pause, pause.Kind);
        Assert.AreEqual("too costly", pause.Reason);

        Assert.IsTrue(GoalCommandTextParser.TryParse("/Goal Resume", out var resume, out _, out _));
        Assert.AreEqual(GoalCommandKind.Resume, resume.Kind);
    }

    [TestMethod]
    public void Pause_And_Cancel_Accept_Optional_Reason()
    {
        Assert.IsTrue(GoalCommandTextParser.TryParse("/goal pause", out var bare, out _, out _));
        Assert.AreEqual(GoalCommandKind.Pause, bare.Kind);
        Assert.IsNull(bare.Reason);

        Assert.IsTrue(GoalCommandTextParser.TryParse(
            "/goal cancel 用户要求停止：成本过高", out var cancel, out _, out _));
        Assert.AreEqual(GoalCommandKind.Cancel, cancel.Kind);
        Assert.AreEqual("用户要求停止：成本过高", cancel.Reason);
    }

    [TestMethod]
    public void Edit_And_Replace_Require_Objective()
    {
        Assert.IsTrue(GoalCommandTextParser.TryParse(
            "/goal edit 新目标", out var edit, out _, out _));
        Assert.AreEqual(GoalCommandKind.Edit, edit.Kind);
        Assert.AreEqual("新目标", edit.Objective);

        Assert.IsTrue(GoalCommandTextParser.TryParse(
            "/goal replace 另一个目标 --rounds 16", out var replace, out _, out _));
        Assert.AreEqual(GoalCommandKind.Replace, replace.Kind);
        Assert.AreEqual(16, replace.Rounds);

        Assert.IsFalse(GoalCommandTextParser.TryParse("/goal edit", out _, out var errorCode, out _));
        Assert.AreEqual(GoalErrorCodes.InvalidObjective, errorCode);
    }

    [TestMethod]
    public void Strict_Subcommands_Reject_Trailing_Text()
    {
        Assert.IsFalse(GoalCommandTextParser.TryParse("/goal status extra", out _, out var errorCode, out _));
        Assert.AreEqual(GoalErrorCodes.InvalidCommand, errorCode);

        Assert.IsFalse(GoalCommandTextParser.TryParse("/goal resume now", out _, out errorCode, out _));
        Assert.AreEqual(GoalErrorCodes.InvalidCommand, errorCode);

        Assert.IsFalse(GoalCommandTextParser.TryParse("/goal clear everything", out _, out errorCode, out _));
        Assert.AreEqual(GoalErrorCodes.InvalidCommand, errorCode);
    }

    [TestMethod]
    public void Objective_Starting_With_Reserved_Word_Uses_Subcommand_Semantics()
    {
        // 消歧规则：首 token 命中保留字即子命令 —— "status report" 不是 objective。
        Assert.IsFalse(GoalCommandTextParser.TryParse(
            "/goal status report", out _, out var errorCode, out _));
        Assert.AreEqual(GoalErrorCodes.InvalidCommand, errorCode);

        // 首词是保留字前缀（非完全匹配）时按 objective 处理。
        Assert.IsTrue(GoalCommandTextParser.TryParse(
            "/goal statuses need cleanup", out var command, out _, out _));
        Assert.AreEqual(GoalCommandKind.Set, command.Kind);
        Assert.AreEqual("statuses need cleanup", command.Objective);
    }

    [TestMethod]
    public void Non_Goal_Text_Is_Rejected()
    {
        Assert.IsFalse(GoalCommandTextParser.TryParse("/compact", out _, out _, out _));
        Assert.IsFalse(GoalCommandTextParser.TryParse("/goals x", out _, out _, out _));
        Assert.IsFalse(GoalCommandTextParser.TryParse("goal x", out _, out _, out _));
        Assert.IsFalse(GoalCommandTextParser.TryParse(null, out _, out _, out _));
        Assert.IsFalse(GoalCommandTextParser.TryParse("", out _, out _, out _));
    }

    [TestMethod]
    public void TryCreate_Builds_Request_From_Text()
    {
        var created = GoalCommandRequest.TryCreate(
            "ws", "conv", "agent", "user", "req-1",
            "/goal 测试目标 --rounds 4", "web",
            out var request, out _, out _);

        Assert.IsTrue(created);
        Assert.AreEqual("ws", request!.WorkspaceId);
        Assert.AreEqual("conv", request.ConversationId);
        Assert.AreEqual(GoalCommandKind.Set, request.Command.Kind);
        Assert.AreEqual(4, request.Command.Rounds);
        Assert.AreEqual("web", request.SourceChannel);
    }
}
