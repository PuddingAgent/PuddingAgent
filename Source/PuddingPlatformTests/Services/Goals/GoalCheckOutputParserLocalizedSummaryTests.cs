using PuddingPlatform.Services.Goals;

namespace PuddingPlatformTests.Services.Goals;

/// <summary>
/// 回归：受控检查的汇总行解析必须**语言无关**。
/// <para>
/// 实测背景（2026-09-15，本机 zh-CN）：`dotnet test` 输出为
/// <c>失败!  - 失败:    13，通过:  1031，已跳过:     0，总计:  1044，持续时间: 1 m 3 s</c>，
/// 而解析器原先只认英文 "Failed: / Passed:"，导致 Test 类受控检查在中文 locale 机器上
/// 永远解析不到摘要 ⇒ 永远无法通过（Goal 验收结构性不可达）。
/// 本用例直接锁定生产实测样本，防止回退。
/// </para>
/// </summary>
[TestClass]
public sealed class GoalCheckOutputParserLocalizedSummaryTests
{
    // 生产实测原文（zh-CN，失败 13 行）
    private const string ZhCnFailedLine =
        "失败!  - 失败:    13，通过:  1031，已跳过:     0，总计:  1044，持续时间: 1 m 3 s";

    // 生产实测原文（zh-CN，全通过形态）
    private const string ZhCnPassedLine =
        "已通过! - 失败:     0，通过:  1044，已跳过:     0，总计:  1044，持续时间: 1 m 3 s";

    // 英文对照（不得回退）
    private const string EnFailedLine =
        "Failed!  - Failed:    13, Passed:  1031, Skipped:     0, Total:  1044, Duration: 1 m 3 s";

    [TestMethod]
    public void ParseTestSummary_RecognizesChineseLocalizedFailedLine()
    {
        var summary = GoalCheckOutputParser.ParseTestSummary([ZhCnFailedLine]);

        Assert.IsNotNull(summary, "zh-CN 汇总行必须可解析（否则 Test 类检查在中文 locale 永远不通过）");
        Assert.AreEqual(13, summary!.FailedTestCount);
        Assert.AreEqual(1031, summary.PassedTestCount);
        Assert.AreEqual(1044, summary.ExecutedTestCount);
    }

    [TestMethod]
    public void ParseTestSummary_RecognizesChineseLocalizedPassedLine()
    {
        var summary = GoalCheckOutputParser.ParseTestSummary([ZhCnPassedLine]);

        Assert.IsNotNull(summary, "zh-CN 全通过汇总行必须可解析");
        Assert.AreEqual(0, summary!.FailedTestCount);
        Assert.AreEqual(1044, summary.PassedTestCount);
        Assert.AreEqual(1044, summary.ExecutedTestCount);
    }

    [TestMethod]
    public void ParseTestSummary_StillRecognizesEnglishLine()
    {
        var summary = GoalCheckOutputParser.ParseTestSummary([EnFailedLine]);

        Assert.IsNotNull(summary, "英文汇总行必须继续可解析（不得为支持本地化而回退）");
        Assert.AreEqual(13, summary!.FailedTestCount);
        Assert.AreEqual(1031, summary.PassedTestCount);
    }

    [TestMethod]
    public void ParseTestSummary_PrefersLastSummaryLine()
    {
        // 多项目输出：前面的中间汇总不得覆盖最后一条真实汇总
        var summary = GoalCheckOutputParser.ParseTestSummary(
            [ZhCnFailedLine, ZhCnPassedLine]);

        Assert.IsNotNull(summary);
        Assert.AreEqual(0, summary!.FailedTestCount);
        Assert.AreEqual(1044, summary.PassedTestCount);
    }

    [TestMethod]
    public void HasSuccessMarker_RecognizesChinesePassedMarker()
    {
        Assert.IsTrue(GoalCheckOutputParser.HasSuccessMarker([ZhCnPassedLine]));
        Assert.IsTrue(GoalCheckOutputParser.HasSuccessMarker(["Passed!  - Failed: 0, Passed: 1"]));
        Assert.IsFalse(GoalCheckOutputParser.HasSuccessMarker([ZhCnFailedLine]));
    }
}
