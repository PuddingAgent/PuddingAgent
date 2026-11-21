using PuddingRuntime.Services;
using PuddingRuntime.Services.Skills;

namespace PuddingRuntimeTests.Services;

/// <summary>
/// 失败建议必须与失败原因匹配。
/// 实测背景：<c>shell</c> 是本代理失败率最高的工具（144 次调用 / 14 次失败），
/// 而超时后的建议长期是「检查参数约束」——那是方向性错误，会让人继续改参数，
/// 而正确动作是改用 terminal_start + terminal_wait 或传更大的 timeout_seconds。
/// 同理 <c>search_grep</c> 把文件路径当 directory 传时报
/// 「is a file, not a directory」，正确动作是传所在目录或用 pattern。
/// </summary>
[TestClass]
public sealed class FailureSuggestionTests
{
    private const string GenericSuggestion =
        "   💡 Suggestion: Check the tool's parameter constraints. " +
        "If the tool has access restrictions, try an alternative approach.";

    private const string TimedOutPrefix =
        "   💡 Suggestion: This tool enforces a hard timeout.";

    private const string DirectoryMisusePrefix =
        "   💡 Suggestion: 'directory' must be a folder, not a file.";

    private static SkillResult Failed(string? error) => new()
    {
        Success = false,
        Output = string.Empty,
        Error = error,
        ExitCode = 1,
    };

    [TestMethod]
    public void TimedOutError_PointsToTerminalTools_InsteadOfParameterTuning()
    {
        var suggestion = AgentExecutionService.BuildFailureSuggestion(
            "Command timed out after 30 seconds.");

        StringAssert.StartsWith(suggestion, TimedOutPrefix);
        StringAssert.Contains(suggestion, "terminal_start + terminal_wait");
        Assert.IsFalse(
            suggestion.Contains("parameter constraints", StringComparison.Ordinal),
            "超时后建议改参数是方向性错误：参数没错，是耗时超过了硬上限");
    }

    [TestMethod]
    public void TimedOutError_IsMatchedCaseInsensitively()
    {
        var suggestion = AgentExecutionService.BuildFailureSuggestion(
            "shell: Command TIMED OUT after 5 seconds.");

        StringAssert.StartsWith(suggestion, TimedOutPrefix);
    }

    [TestMethod]
    public void DirectoryMisuseError_PointsToContainingFolder()
    {
        var suggestion = AgentExecutionService.BuildFailureSuggestion(
            "'D:\\repo\\Source\\Foo.cs' is a file, not a directory.");

        StringAssert.StartsWith(suggestion, DirectoryMisusePrefix);
        StringAssert.Contains(suggestion, "'pattern'");
    }

    [TestMethod]
    public void UnknownError_FallsBackToGenericSuggestion_Verbatim()
    {
        var suggestion = AgentExecutionService.BuildFailureSuggestion("exit code 1");

        Assert.AreEqual(GenericSuggestion, suggestion, "无法归因的失败必须保持原通用建议，逐字不变");
    }

    [TestMethod]
    public void NullOrBlankError_FallsBackToGenericSuggestion()
    {
        Assert.AreEqual(GenericSuggestion, AgentExecutionService.BuildFailureSuggestion(null));
        Assert.AreEqual(GenericSuggestion, AgentExecutionService.BuildFailureSuggestion("   "));
    }

    /// <summary>
    /// 接线判据：新函数必须真的被失败回执调用，而不是只存在于源码里。
    /// </summary>
    [TestMethod]
    public void FailurePayload_EmbedsTimeoutSuggestion_EndToEnd()
    {
        var payload = AgentExecutionService.BuildFailurePayloadWithoutFuse(
            "shell",
            Failed("Command timed out after 30 seconds."));

        StringAssert.Contains(payload, "terminal_start + terminal_wait");
        Assert.IsFalse(
            payload.Contains("parameter constraints", StringComparison.Ordinal),
            "回执里的建议必须跟着失败原因走");
    }
}
