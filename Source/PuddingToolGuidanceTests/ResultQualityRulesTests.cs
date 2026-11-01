using PuddingToolGuidance;

namespace PuddingToolGuidanceTests;

/// <summary>
/// 结果质量规则（P2）：把「没搜到」和「没搜全」分开。
/// <para>
/// 这组规则的失败模式是**假阴性**：工具返回空/只有一部分，调用方读成「不存在」。
/// 因此正例用真实工具输出片段（scanned 0/2000、coverage: partial），
/// 反例用「完整的空结果」—— 那种情况下不该加任何提示。
/// </para>
/// </summary>
[TestClass]
public sealed class ResultQualityRulesTests
{
    private static GuidanceAdvice? Find(GuidanceInput input, string ruleId) =>
        new GuidanceEngine(maxAdvices: 5).Evaluate(input).Advices
            .FirstOrDefault(a => a.RuleId == ruleId);

    private static GuidanceInput Search(string? output) =>
        new("search_grep", null, null, Succeeded: true, Error: null, Output: output);

    // ── 搜索覆盖度 ────────────────────────────────────────────

    [DataTestMethod]
    [DataRow("(no matches)\ncoverage: partial — scanned 1121/2000 files")]
    [DataRow("文件枚举已达上限 2000 个，结果可能不完整")]
    [DataRow("scan_budget: complete=false limit_reason=max_enumerated_files")]
    public void Incomplete_search_output_should_advise(string output)
    {
        var advice = Find(Search(output), "search.coverage_incomplete_is_not_absence");

        Assert.IsNotNull(advice, output);
        StringAssert.Contains(advice!.Message, "不等于「不存在」");
    }

    [DataTestMethod]
    [DataRow("")]
    [DataRow("no matches")]
    [DataRow("PuddingToolGuidance/Rules/DefaultRules.cs:12: ...")]
    public void Clean_search_output_should_not_advise(string output)
    {
        Assert.IsNull(Find(Search(output), "search.coverage_incomplete_is_not_absence"), output);
    }

    [TestMethod]
    public void Null_output_should_not_advise()
    {
        Assert.IsNull(Find(Search(null), "search.coverage_incomplete_is_not_absence"));
    }

    // ── 测试过滤器没匹配到 ────────────────────────────────────

    [TestMethod]
    public void Filter_with_no_match_should_advise()
    {
        var advice = Find(
            new GuidanceInput("terminal_start", "cmd", "dotnet test x --filter FullyQualifiedName~Foo",
                Succeeded: false, Error: "No test matches the given testcase filter `FullyQualifiedName~Foo`"),
            "test.filter_matched_nothing");

        Assert.IsNotNull(advice);
        StringAssert.Contains(advice!.Message, "--list-tests", "提示要给出确认手段");
    }

    [TestMethod]
    public void Normal_test_output_should_not_advise()
    {
        Assert.IsNull(Find(
            new GuidanceInput("terminal_start", "cmd", "dotnet test x",
                Succeeded: true, Output: "Passed! - Failed: 0, Passed: 12"),
            "test.filter_matched_nothing"));
    }
}
