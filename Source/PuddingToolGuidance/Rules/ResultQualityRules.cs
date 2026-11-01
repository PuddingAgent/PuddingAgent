namespace PuddingToolGuidance.Rules;

/// <summary>
/// 结果质量规则（P2）：把「没搜到」和「没搜全」分开。
/// <para>
/// 这是最容易被误读的一类失败：搜索工具返回空，人（和 Agent）的第一反应是「不存在」，
/// 而真实原因往往是**范围被截断**（文件枚举到上限、只扫了一部分、根本没指定目录）。
/// 2026-11-01 实测：未传 <c>directory</c> 的 <c>search_grep</c> 返回
/// <c>scanned 0/2000 + coverage: partial</c>，被当成「仓库里没有」；另一次命中
/// 「文件枚举已达上限」，也被当成「只找到这些」。
/// </para>
/// <para>
/// 判据全部来自工具**自己的输出文本**，不靠调用方推断 —— 所以对任何 Agent 都生效，
/// 与它有没有读过这个工具的文档无关。
/// </para>
/// </summary>
public static class ResultQualityRules
{
    /// <summary>搜索范围被截断/未生效的标记（工具输出里的原文）。</summary>
    private static readonly string[] TruncationMarkers =
    [
        "coverage: partial",
        "complete=false",
        "scanned 0/",
        "limit_reason=",
        "文件枚举已达上限",
        "扫描已达预算上限",
        "结果可能不完整"
    ];

    /// <summary>测试筛选器没匹配到任何用例的标记。</summary>
    private static readonly string[] NoTestMatchMarkers =
    [
        "No test matches the given testcase filter",
        "没有与给定",
        "no test matches"
    ];

    public static IReadOnlyList<IGuidanceRule> All { get; } =
    [
        new SearchCoverageIncompleteRule(),
        new TestFilterMatchedNothingRule()
    ];

    /// <summary>搜索输出显示范围不完整 —— 空结果不等于「不存在」。</summary>
    private sealed class SearchCoverageIncompleteRule : IGuidanceRule
    {
        public string Id => "search.coverage_incomplete_is_not_absence";

        public GuidanceAdvice? Evaluate(GuidanceInput input)
        {
            if (string.IsNullOrWhiteSpace(input.Output))
                return null;

            foreach (var marker in TruncationMarkers)
            {
                if (!input.Output.Contains(marker, StringComparison.OrdinalIgnoreCase))
                    continue;

                return new GuidanceAdvice(Id,
                    "搜索结果不完整（" + marker + "），**不等于「不存在」**。" +
                    "缩小 directory、改用 code_symbol_search / file_search 等索引工具，或换成本更低的分源检索。");
            }

            return null;
        }
    }

    /// <summary><c>--filter</c> 没匹配到任何用例 —— 通常是筛选表达式写错，不是「没有这些测试」。</summary>
    private sealed class TestFilterMatchedNothingRule : IGuidanceRule
    {
        public string Id => "test.filter_matched_nothing";

        public GuidanceAdvice? Evaluate(GuidanceInput input)
        {
            var text = input.Output;
            if (string.IsNullOrWhiteSpace(text))
                text = input.Error;

            if (string.IsNullOrWhiteSpace(text))
                return null;

            foreach (var marker in NoTestMatchMarkers)
            {
                if (!text.Contains(marker, StringComparison.OrdinalIgnoreCase))
                    continue;

                return new GuidanceAdvice(Id,
                    "过滤器没匹配到任何用例 —— 这不代表「测试不存在」。检查 FullyQualifiedName 是否拼全、" +
                    "多个条件是否用 `|` 连接；不确定时先用一次不带 --filter 的 `--list-tests` 确认名字。");
            }

            return null;
        }
    }
}
