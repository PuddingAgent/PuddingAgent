using PuddingRuntime.Services;

namespace PuddingRuntimeTests.Services;

/// <summary>
/// 锁定失败工具摘要的构成规则：error 在前、output 尾部在后。
///
/// 背景（见 Docs/07_architecture/子代理错误载荷根因-2026-11-21.md）：shell 工具的
/// Error 只有裸退出码，真正的 stderr 被合并在 Output 里。摘要若只取 error，父代理
/// 拿到的就只有 "shell: exit code 1"，无从判断失败原因。
/// 这里同时锁住「output 为空时与改动前逐字相同」，确保补上的只是信息，不是行为变化。
/// </summary>
[TestClass]
public class ToolFailureSummaryTests
{
    /// <summary>
    /// 判据 1：error 与 output 都有时，摘要必须同时携带失败原因与 output 尾部，
    /// 且 error 出现在前（保留「失败原因」的语义顺序）。
    /// </summary>
    [TestMethod]
    public void ErrorAndOutput_SummaryCarriesReasonThenOutputTail()
    {
        var summary = AgentExecutionService.BuildToolFailureSummary(
            "shell",
            "exit code 1",
            "line1\nERR: 文件不存在\n");

        StringAssert.Contains(summary, "exit code 1");
        StringAssert.Contains(summary, "文件不存在");
        // MSTest 的签名为 IsLessThan(upperBound, value)，断言 value < upperBound，
        // 所以「error 在前」要写成 upperBound = output 的位置。
        Assert.IsLessThan(
            summary.IndexOf("文件不存在", StringComparison.Ordinal),
            summary.IndexOf("exit code 1", StringComparison.Ordinal),
            $"error 必须在 output 之前。实际: {summary}");
        Assert.IsTrue(summary.StartsWith("shell: ", StringComparison.Ordinal), summary);
    }

    /// <summary>
    /// 判据 2：output 为空时，输出必须与改动前逐字相同 —— 这是零行为变化的分支。
    /// null 与纯空白都算「空」。
    /// </summary>
    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow("   \n\t ")]
    public void ErrorWithoutOutput_IsByteIdenticalToLegacyFormat(string? output)
    {
        var summary = AgentExecutionService.BuildToolFailureSummary("shell", "exit code 1", output);

        Assert.AreEqual("shell: exit code 1", summary);
    }

    /// <summary>
    /// 判据 3：没有 error 时不制造空占位 —— 退化为旧行为里「只取 output」的分支；
    /// 两者都缺时才用 unknown tool failure。
    /// </summary>
    [TestMethod]
    public void OutputWithoutError_FallsBackToOutputAlone()
    {
        var summary = AgentExecutionService.BuildToolFailureSummary("shell", null, "boom");

        Assert.AreEqual("shell: boom", summary);
        Assert.IsFalse(summary.Contains("unknown tool failure", StringComparison.Ordinal), summary);
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow(" ")]
    public void NoErrorAndNoOutput_UsesUnknownFailurePlaceholder(string? error)
    {
        var summary = AgentExecutionService.BuildToolFailureSummary(null, error, null);

        Assert.AreEqual("tool: unknown tool failure", summary);
    }

    /// <summary>
    /// 判据 4 的一半：总长必须被既有 Truncate 约束住。
    ///
    /// 注意上限是 512 + 1：<c>Truncate</c> 截断后在尾部追加一个省略号，
    /// 因此产出 maxLen+1 个字符 —— 改动前的旧实现同样如此（文档 §7 判据 4 写的 512 未计入该省略号）。
    ///
    /// 判据 4 的另一半（取尾部而非头部）见 <see cref="LongOutput_KeepsTailNotHead"/>。
    /// </summary>
    [TestMethod]
    public void LongErrorAndOutput_SummaryStaysWithinTruncationBound()
    {
        var summary = AgentExecutionService.BuildToolFailureSummary(
            "shell",
            new string('e', 900),
            "boom");

        Assert.IsLessThanOrEqualTo(513, summary.Length, $"摘要长度 {summary.Length} 超出自定义上限 513");
    }

    /// <summary>
    /// 判据 4：截取的必须是 output 的**尾部**。命令行错误的现场在末尾，
    /// 保留头部等于把唯一有用的信息剪掉。
    /// </summary>
    [TestMethod]
    public void LongOutput_KeepsTailNotHead()
    {
        var output = "HEAD-MARKER" + new string('x', 1000) + "TAIL-MARKER";

        var summary = AgentExecutionService.BuildToolFailureSummary("shell", "exit code 1", output);

        StringAssert.Contains(summary, "TAIL-MARKER");
        Assert.IsFalse(summary.Contains("HEAD-MARKER", StringComparison.Ordinal), summary);
        Assert.IsLessThanOrEqualTo(513, summary.Length, $"摘要长度 {summary.Length} 超出自定义上限 513");
    }

    /// <summary>
    /// output 短于尾部预算时不能被加省略号或裁掉内容 —— 完整保留便于父代理直接阅读。
    /// </summary>
    [TestMethod]
    public void ShortOutput_IsKeptVerbatim()
    {
        var summary = AgentExecutionService.BuildToolFailureSummary(
            "shell",
            "exit code 1",
            "error: 权限不足");

        StringAssert.Contains(summary, "输出尾部: error: 权限不足");
    }
}
