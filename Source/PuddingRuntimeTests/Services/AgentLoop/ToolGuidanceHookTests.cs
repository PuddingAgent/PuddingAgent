using PuddingRuntime.Services.AgentLoop;
using PuddingRuntime.Services.Skills;

namespace PuddingRuntimeTests.Services.AgentLoop;

/// <summary>
/// ToolGuidanceHook 的契约：只追加文本、不改成功位与退出码、解析失败必静默。
/// <para>
/// 这个 Hook 会对**每一次** shell 调用执行，所以「不该提示时确实不提示」和
/// 「该提示时确实提示了」同等重要 —— 前者防止噪声，后者防止它是摆设。
/// </para>
/// </summary>
[TestClass]
public sealed class ToolGuidanceHookTests
{
    private static AgentLoopContext Ctx() => new()
    {
        SessionId = "s",
        AgentInstanceId = "a",
        WorkspaceId = "w",
        AgentTemplateId = "t",
        UserMessage = "hi",
        MaxRounds = 10,
    };

    private static SkillResult Ok(string output, int exitCode = 0) => new()
    {
        Success = true,
        Output = output,
        ExitCode = exitCode,
    };

    [TestMethod]
    public async Task Cmd_unix_command_gets_note_appended()
    {
        var hook = new ToolGuidanceHook();
        var result = Ok("file1\r\nfile2");

        var transformed = await hook.OnToolResultTransformingAsync(
            Ctx(), 0, "shell",
            """{"command":"dir /b | tail -5","shell":"cmd"}""",
            result);

        StringAssert.Contains(transformed.Output, "NOTE:");
        StringAssert.Contains(transformed.Output, "tail");
        StringAssert.StartsWith(transformed.Output, "file1", "原有输出必须留在最前面");
    }

    [TestMethod]
    public async Task Success_and_exit_code_are_never_changed()
    {
        var hook = new ToolGuidanceHook();
        var result = Ok("out", exitCode: 3);

        var transformed = await hook.OnToolResultTransformingAsync(
            Ctx(), 0, "shell",
            """{"command":"findstr /c:\"静态上下文\" a.cs","shell":"cmd"}""",
            result);

        Assert.AreEqual(result.Success, transformed.Success, "引导层不得改成功位");
        Assert.AreEqual(result.ExitCode, transformed.ExitCode, "引导层不得改退出码");
        Assert.AreEqual(result.Error, transformed.Error);
        StringAssert.Contains(transformed.Output, "NOTE:", "该命中的规则仍要命中");
    }

    [TestMethod]
    public async Task Non_shell_tool_is_untouched()
    {
        var hook = new ToolGuidanceHook();
        var result = Ok("content");

        var transformed = await hook.OnToolResultTransformingAsync(
            Ctx(), 0, "file_read", """{"command":"dir | tail"}""", result);

        Assert.AreSame(result, transformed, "非 shell 工具应原样返回（短路，不解析参数）");
    }

    [TestMethod]
    public async Task Malformed_json_is_swallowed()
    {
        var hook = new ToolGuidanceHook();
        var result = Ok("content");

        var transformed = await hook.OnToolResultTransformingAsync(
            Ctx(), 0, "shell", "this-is-not-json", result);

        Assert.AreSame(result, transformed, "参数解析失败必须原样返回，不得抛进主执行链");
    }

    [TestMethod]
    public async Task Clean_command_produces_no_note()
    {
        var hook = new ToolGuidanceHook();
        var result = Ok("out");

        var transformed = await hook.OnToolResultTransformingAsync(
            Ctx(), 0, "shell", """{"command":"cd /d repo && dotnet build","shell":"cmd"}""", result);

        Assert.AreSame(result, transformed, "正常命令不得被加提示");
    }

    [TestMethod]
    public async Task Terminal_tools_default_to_cmd_and_shell_tool_defaults_to_powershell()
    {
        var hook = new ToolGuidanceHook();

        // terminal_* 默认 cmd ⇒ tail 命中
        var terminal = await hook.OnToolResultTransformingAsync(
            Ctx(), 0, "terminal_start", """{"command":"dir | tail -3"}""", Ok("o"));
        StringAssert.Contains(terminal.Output, "NOTE:");

        // shell 工具默认 pwsh（不为 cmd 规则命中，说明默认值确实按工具分流）
        var shell = await hook.OnToolResultTransformingAsync(
            Ctx(), 0, "shell", """{"command":"dir | tail -3"}""", Ok("o"));
        Assert.IsFalse(shell.Output.Contains("NOTE:"), "shell 工具默认 pwsh，不应命中 cmd 专属规则");
    }
}
