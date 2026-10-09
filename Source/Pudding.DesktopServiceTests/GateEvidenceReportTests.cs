using System.Text.RegularExpressions;
using Pudding.DesktopService;

namespace DesktopServiceTests;

/// <summary>
/// **对账表里引用的用例名必须真实存在**。
///
/// `Docs/14_reports/2026-10-09-门禁证据对账.md` 的 G1–G4 一节把每条验收标准钉到了**具体用例名**——
/// 这正是"内部门禁证据"最有价值的形式，但也最容易腐烂：用例改名/删除后，表里仍写着旧名字，
/// 读的人会以为"这条有证据"。这条门禁把表与实际用例绑在一起（首次写表时就抓到过 1 个错名）。
/// </summary>
public sealed class GateEvidenceReportTests
{
    private const string ReportRelativePath = "Docs/14_reports/2026-10-09-门禁证据对账.md";

    [Fact]
    public void EveryTestMethodNamedInTheGateReportExists()
    {
        var reportPath = Path.Combine(
            RepoLayout.Root, ReportRelativePath.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(File.Exists(reportPath), $"找不到对账表：{ReportRelativePath}");

        var report = File.ReadAllText(reportPath);

        // 只核对"G1–G4 逐条证据"一节（其它节里引用的是类名，不是用例名）。
        var sectionStart = report.IndexOf("## G1–G4 逐条证据", StringComparison.Ordinal);
        Assert.True(sectionStart >= 0, "对账表里找不到「G1–G4 逐条证据」一节（被改名/删除？）");
        var sectionEnd = report.IndexOf("## 诚实边界", sectionStart, StringComparison.Ordinal);
        var section = sectionEnd > sectionStart ? report[sectionStart..sectionEnd] : report[sectionStart..];

        var cited = Regex.Matches(section, "`([A-Z][A-Za-z0-9_]{6,})`")
            .Select(match => match.Groups[1].Value)
            .Where(name => !name.EndsWith("Tests", StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        Assert.True(cited.Length >= 30, $"只解析到 {cited.Length} 个用例名，解析逻辑可能写错");

        var declared = Directory
            .EnumerateFiles(
                Path.Combine(RepoLayout.Root, "Source", "PuddingBrowser.AutomationTests"), "*.cs")
            .SelectMany(path => Regex.Matches(File.ReadAllText(path), @"public void (?<name>\w+)\(")
                .Select(match => match.Groups["name"].Value))
            .ToHashSet(StringComparer.Ordinal);

        var missing = cited.Where(name => !declared.Contains(name)).ToArray();
        Assert.True(
            missing.Length == 0,
            "对账表引用了不存在的用例（改名/删除后必须同步表格）：\n  - " + string.Join("\n  - ", missing));
    }
}
