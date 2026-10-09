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
    public void EveryNumberOfThePlanIsTraceableToItsSourceOfTruth()
    {
        // 取红用例（round-136）：清单里的**数字**也是对外承诺。原先 S-09 写着"low 基准图 ≤512px/≤40 KiB"，
        // 而浏览器截图层根本没有这个策略（唯一存在的 512 低保证属于 **vision 管线**的 detail=low），
        // "≤40 KiB"更是查无出处 ⇒ 对外承诺了不存在的东西。已改写成代码保证的形式（按调用方上限判定）。
        // 这条把**留存的数字**逐一对到真源：数字改了或真源改了，两侧都会红。
        var expected = new (string PlanText, string SourceFile, string SourceText)[]
        {
            ("前 10 条标题", "Source/PuddingBrowser.AutomationTests/SerpBudgetFixtureTests.cs", "应有 10 条结果"),
            ("1536 字节", "Source/PuddingBrowser.AutomationTests/SerpBudgetFixtureTests.cs", "G5BudgetBytes = 1_536"),
            ("父结果 ≤2 KiB", "Source/PuddingBrowser.Automation/SubAgentDelegation.cs", "DefaultResultBudgetBytes = 2 * 1024"),
        };

        var plan = File.ReadAllText(Path.Combine(
            RepoLayout.Root, "Source", "Pudding.DesktopService", "BrowserSmokePlan.cs"));

        var problems = new List<string>();
        foreach (var (planText, sourceFile, sourceText) in expected)
        {
            if (!plan.Contains(planText, StringComparison.Ordinal))
            {
                problems.Add($"清单里找不到「{planText}」（数字被改写/删除后必须同步本表）");
            }

            var path = Path.Combine(RepoLayout.Root, sourceFile.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(path))
            {
                problems.Add($"找不到真源文件：{sourceFile}");
                continue;
            }

            if (!File.ReadAllText(path).Contains(sourceText, StringComparison.Ordinal))
            {
                problems.Add($"{sourceFile} 里找不到「{sourceText}」（真源改了，清单里的数字要跟着改）");
            }
        }

        // S-13 的上限是从代码常量插值来的（不是手写数字），这里确认它确实出现在清单文本里。
        if (!plan.Contains("PageEventLog.DefaultCapacity", StringComparison.Ordinal))
        {
            problems.Add("S-13 的容量应继续用 PageEventLog.DefaultCapacity 插值，而不是手写数字");
        }

        Assert.True(problems.Count == 0, "清单数字与真源不一致：\n  - " + string.Join("\n  - ", problems));
    }
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
