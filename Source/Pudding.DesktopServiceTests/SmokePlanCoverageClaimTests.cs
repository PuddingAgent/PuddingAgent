using Pudding.DesktopService;
using System.Text.RegularExpressions;

namespace DesktopServiceTests;

/// <summary>
/// **外部 smoke 清单里"自动覆盖"的说法必须是真的**。
///
/// 清单对每个"不需要真机"的检查都写明了它由哪些进程内用例覆盖（例如
/// <c>AutomationTests.BrowserControlAuthority*</c>）。这些名字是**对外的承诺**：
/// 外部一方会据此认为"这条已经被自动测试兜住了"。若用例改名/删除而清单没跟着改，
/// 承诺就变成**假话** —— 而原来的校验只要求"协了覆盖"，不要求"覆盖真的存在"。
/// </summary>
public sealed class SmokePlanCoverageClaimTests
{
    [Fact]
    public void EveryAutomatedCoverageClaimResolvesToARealTestClass()
    {
        var automationTestsDirectory = Path.Combine(
            RepoLayout.Root, "Source", "PuddingBrowser.AutomationTests");
        Assert.True(Directory.Exists(automationTestsDirectory), $"找不到自动化测试项目：{automationTestsDirectory}");

        var declaredClasses = Directory
            .EnumerateFiles(automationTestsDirectory, "*.cs", SearchOption.AllDirectories)
            .SelectMany(path => Regex.Matches(File.ReadAllText(path), @"\bclass\s+(?<name>[A-Za-z_][A-Za-z0-9_]*)")
                .Select(match => match.Groups["name"].Value))
            .ToHashSet(StringComparer.Ordinal);

        Assert.True(declaredClasses.Count > 20, $"扫到的测试类太少（{declaredClasses.Count}），路径或正则可能写错");

        var unresolved = new List<string>();
        var checkedClaims = 0;

        foreach (var check in BrowserSmokePlan.Checks)
        {
            if (string.IsNullOrWhiteSpace(check.AutomatedCoverage))
            {
                continue;
            }

            // 写法允许："AutomationTests.XxxYyy*"、"XxxTests"、"A / B"（多个目标用斜杠分开）。
            foreach (var rawTarget in check.AutomatedCoverage.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var target = rawTarget.StartsWith("AutomationTests.", StringComparison.Ordinal)
                    ? rawTarget["AutomationTests.".Length..]
                    : rawTarget;

                var wildcard = target.EndsWith('*');
                var name = wildcard ? target[..^1] : target;
                checkedClaims++;

                var matched = wildcard
                    ? declaredClasses.Any(declared => declared.StartsWith(name, StringComparison.Ordinal))
                    : declaredClasses.Contains(name);

                if (!matched)
                {
                    unresolved.Add($"{check.Id} ({check.Gate}) -> {rawTarget}");
                }
            }
        }

        Assert.True(checkedClaims >= 6, $"解析到的覆盖声明太少（{checkedClaims}），解析逻辑可能写错");
        Assert.True(
            unresolved.Count == 0,
            "清单承诺了这些自动化覆盖，但仓库里找不到对应的测试类（改名/删除后必须同步清单）：\n  - "
            + string.Join("\n  - ", unresolved));
    }
}
