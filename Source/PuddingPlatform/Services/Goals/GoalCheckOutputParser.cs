using System.Globalization;
using System.Text.RegularExpressions;

namespace PuddingPlatform.Services.Goals;

/// <summary>
/// ADR-092 §6：从受控执行的终端输出中解析真实测试数量。
/// <para>
/// 只认 dotnet test / VSTest 的最终汇总行；解析不到就返回 null，由证据策略按
/// test_count_unknown 拒绝通过——绝不接受自我声明的"测试通过"。
/// </para>
/// </summary>
public static partial class GoalCheckOutputParser
{
    [GeneratedRegex(
        @"Failed:\s*(?<failed>\d+)\s*,\s*Passed:\s*(?<passed>\d+)(?:\s*,\s*Skipped:\s*(?<skipped>\d+))?(?:\s*,\s*Total:\s*(?<total>\d+))?",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TestSummaryRegex();

    /// <summary>
    /// VSTest 的**本地化**汇总行（如 zh-CN："失败!  - 失败: 13，通过: 1031，已跳过: 0，总计: 1044"）。
    /// 解析器只认汇总行事实、不依赖 UI 语言：英文与本地化两种形态都必须能识别，
    /// 否则中文 locale 机器上的 Test 类检查永远解析不到摘要（⇒ 永远无法通过）。
    /// </summary>
    [GeneratedRegex(
        @"失败:\s*(?<failed>\d+)\s*[，,]\s*通过:\s*(?<passed>\d+)(?:\s*[，,]\s*已跳过:\s*(?<skipped>\d+))?(?:\s*[，,]\s*总计:\s*(?<total>\d+))?",
        RegexOptions.CultureInvariant)]
    private static partial Regex LocalizedTestSummaryRegex();

    public sealed record TestSummary(int ExecutedTestCount, int PassedTestCount, int FailedTestCount);

    /// <summary>取最后一条汇总行（前面的行可能是逐项目中间汇总）。</summary>
    public static TestSummary? ParseTestSummary(IEnumerable<string>? lines)
    {
        if (lines is null)
            return null;

        TestSummary? summary = null;
        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;
            var match = TestSummaryRegex().Match(line);
            if (!match.Success)
                match = LocalizedTestSummaryRegex().Match(line);
            if (!match.Success)
                continue;

            var failed = ParseCount(match.Groups["failed"].Value);
            var passed = ParseCount(match.Groups["passed"].Value);
            if (failed is null || passed is null)
                continue;

            var executed = match.Groups["total"].Success
                ? ParseCount(match.Groups["total"].Value) ?? failed.Value + passed.Value
                : failed.Value + passed.Value;

            summary = new TestSummary(executed, passed.Value, failed.Value);
        }

        return summary;
    }

    /// <summary>dotnet build / test 在成功时会打印明确成功标记。</summary>
    public static bool HasSuccessMarker(IEnumerable<string>? lines)
    {
        if (lines is null)
            return false;
        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;
            if (line.Contains("Build succeeded", StringComparison.OrdinalIgnoreCase)
                || line.Contains("Passed!", StringComparison.Ordinal)
                || line.Contains("已通过", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>是否有未结束的后台/挂起进程迹象（如测试主机仍在运行）。</summary>
    public static bool HasUnfinishedProcessMarker(IEnumerable<string>? lines)
    {
        if (lines is null)
            return false;
        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;
            if (line.Contains("test host process crashed", StringComparison.OrdinalIgnoreCase)
                || line.Contains("process is still running", StringComparison.OrdinalIgnoreCase)
                || line.Contains("has not exited", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static int? ParseCount(string value)
        => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;
}
