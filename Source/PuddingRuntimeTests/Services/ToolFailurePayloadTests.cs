using System.Text;
using PuddingRuntime.Services;
using PuddingRuntime.Services.Skills;

namespace PuddingRuntimeTests.Services;

/// <summary>
/// 失败回执必须携带工具自身的输出。
/// 失败时 <c>Error</c> 往往只有退出码，真正的原因在 stdout/stderr 里；
/// 丢掉它会让调用方为了看到原因而重跑一次命令——同一份信息付两次成本，
/// 而且第二次未必能复现（例如脚本已产生副作用）。
/// </summary>
[TestClass]
public sealed class ToolFailurePayloadTests
{
    private static SkillResult Failed(string output, string? error = "exit code 1") => new()
    {
        Success = false,
        Output = output,
        Error = error,
        ExitCode = 1,
    };

    [TestMethod]
    public void FailurePayload_IncludesToolOutput_BetweenErrorAndSuggestion()
    {
        var payload = AgentExecutionService.BuildFailurePayloadWithoutFuse(
            "shell", Failed("boom: file not found\nsecond diagnostic line"));

        StringAssert.Contains(payload, "boom: file not found");
        StringAssert.Contains(payload, "second diagnostic line");
        StringAssert.Contains(payload, "   Output:");

        var errorIndex = payload.IndexOf("   Error:", StringComparison.Ordinal);
        var outputIndex = payload.IndexOf("   Output:", StringComparison.Ordinal);
        var suggestionIndex = payload.IndexOf("Suggestion:", StringComparison.Ordinal);

        Assert.IsGreaterThanOrEqualTo(0, errorIndex - 0, "Error 行必须存在");
        Assert.IsTrue(outputIndex > errorIndex, "输出必须紧跟 Error 之后，先给原因再给建议");
        Assert.IsTrue(suggestionIndex > outputIndex, "建议行必须排在诊断输出之后");
    }

    [TestMethod]
    public void FailurePayload_OmitsOutputBlock_WhenToolProducedNothing()
    {
        var payload = AgentExecutionService.BuildFailurePayloadWithoutFuse("file_read", Failed("   "));

        Assert.IsFalse(
            payload.Contains("   Output:", StringComparison.Ordinal),
            "空输出不应产出空的 Output 块——那只是噪声");
        StringAssert.Contains(payload, "   Error: exit code 1");
    }

    [TestMethod]
    public void AppendFailureOutput_LeavesBuilderUntouched_ForBlankOutput()
    {
        var sb = new StringBuilder("HEAD");
        AgentExecutionService.AppendFailureOutput(sb, " \r\n\t ");
        Assert.AreEqual("HEAD", sb.ToString());
    }

    [TestMethod]
    public void AppendFailureOutput_PreservesMultiLineDiagnosticsVerbatim()
    {
        var sb = new StringBuilder();
        AgentExecutionService.AppendFailureOutput(sb, "line-1\nline-2\n");

        StringAssert.Contains(sb.ToString(), "line-1\nline-2\n");
    }
}
