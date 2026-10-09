using System.Text.RegularExpressions;
using Pudding.DesktopService;

namespace DesktopServiceTests;

/// <summary>
/// **运行手册里的命令必须真的能跑**。
///
/// 手册是给人照抄的：它写着 <c>&amp; .\TestScripts\delivery-manifest.ps1 -BuildId …</c>，
/// 若脚本后来改名了开关（或手册写错一个字母），照抄的人会得到"找不到参数"这类错误 ——
/// 而手册本身**看不出来**。这条把两者钉在一起：手册里出现的每个开关，都必须在脚本的参数表里存在。
/// </summary>
public sealed class RunbookCommandTests
{
    [Theory]
    [InlineData("Docs/13_runbooks/浏览器自动化内部验证-runbook.md")]
    [InlineData("Docs/13_runbooks/浏览器自动化外部smoke-runbook.md")]
    public void EverySwitchMentionedInTheRunbooksExistsInItsScript(string runbookRelativePath)
    {
        var runbookPath = Path.Combine(RepoLayout.Root, runbookRelativePath.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(File.Exists(runbookPath), $"找不到运行手册：{runbookRelativePath}");

        var runbook = File.ReadAllText(runbookPath);
        var problems = new List<string>();
        var checkedSwitches = 0;

        // 只核对"调用 TestScripts 脚本"的同一行（跨行的反引号续行只覆盖首行 —— 见诚实边界）。
        foreach (Match invocation in Regex.Matches(runbook, @"TestScripts[\\/](?<script>[\w.\-]+\.ps1)(?<args>[^\r\n]*)"))
        {
            var scriptName = invocation.Groups["script"].Value;
            var args = invocation.Groups["args"].Value;

            var scriptPath = Path.Combine(RepoLayout.Root, "TestScripts", scriptName);
            if (!File.Exists(scriptPath))
            {
                // 手册引用的脚本不存在 —— 这本身就是要报的问题。
                problems.Add($"{runbookRelativePath} 引用了不存在的脚本：TestScripts/{scriptName}");
                continue;
            }

            var script = File.ReadAllText(scriptPath);
            foreach (Match switchMatch in Regex.Matches(args, @"(?<![\w-])-(?<name>[A-Za-z][A-Za-z0-9]*)"))
            {
                var name = switchMatch.Groups["name"].Value;
                checkedSwitches++;

                // PowerShell 参数引用形如 $Name（大小写不敏感）。
                if (!Regex.IsMatch(script, @"\$" + Regex.Escape(name) + @"\b", RegexOptions.IgnoreCase))
                {
                    problems.Add(
                        $"{runbookRelativePath} 用了 -{name}，但 TestScripts/{scriptName} 里没有该参数");
                }
            }
        }

        Assert.True(
            problems.Count == 0,
            "运行手册与脚本不一致（照抄会失败）：\n  - " + string.Join("\n  - ", problems));

        // 防止"正则写坏导致一条都没查"的假绿。
        Assert.True(checkedSwitches >= 5, $"只核对到 {checkedSwitches} 个开关，解析逻辑可能写错");
    }
}
