using System.Text.RegularExpressions;
using Pudding.DesktopService;
using PuddingBrowser.Automation;

namespace DesktopServiceTests;

/// <summary>
/// 外部 smoke 清单（把散落的"真机未验证"收拢成可执行观察项）：
/// 覆盖全部门禁、Id 唯一、每条都有观察与期望、不许把进程内能测的混进来冒充真机。
/// </summary>
public sealed class BrowserSmokePlanTests
{
    [Fact]
    public void TheCanonicalPlanIsValidAndCoversEveryGate()
    {
        Assert.Empty(BrowserSmokePlan.Validate(BrowserSmokePlan.Checks));

        var gates = BrowserSmokePlan.Checks.Select(check => check.Gate).ToHashSet(StringComparer.Ordinal);
        foreach (var required in BrowserSmokePlan.RequiredGates)
        {
            Assert.Contains(required, gates);
        }
    }

    [Fact]
    public void EveryCheckStatesAnObservationAndAnExpectation()
    {
        Assert.All(BrowserSmokePlan.Checks, check =>
        {
            Assert.False(string.IsNullOrWhiteSpace(check.Id));
            Assert.False(string.IsNullOrWhiteSpace(check.Action));
            Assert.False(string.IsNullOrWhiteSpace(check.Expected));
            Assert.True(check.RequiresRealDevice, $"{check.Id} 不是真机观察项，不该放进 smoke 清单");
        });
    }

    [Fact]
    public void AMissingGateMakesThePlanIncomplete()
    {
        // 去掉 G8（滚动）⇒ 必须报"未覆盖"。
        var withoutScroll = BrowserSmokePlan.Checks.Where(check => check.Gate != "G8").ToArray();
        var problems = BrowserSmokePlan.Validate(withoutScroll);

        Assert.Contains(problems, problem => problem.Contains("G8", StringComparison.Ordinal));
    }

    [Fact]
    public void DuplicateIdsAndMissingFieldsAreRefused()
    {
        var duplicated = new List<BrowserSmokeCheck>(BrowserSmokePlan.Checks)
        {
            BrowserSmokePlan.Checks[0],
        };

        Assert.Contains(
            BrowserSmokePlan.Validate(duplicated),
            problem => problem.Contains("duplicate", StringComparison.Ordinal));

        var blank = new List<BrowserSmokeCheck>
        {
            new("S-X", "G1", "区域", "  ", "期望"),
        };
        Assert.Contains(
            BrowserSmokePlan.Validate(blank),
            problem => problem.Contains("must state", StringComparison.Ordinal));

        Assert.Contains(
            BrowserSmokePlan.Validate([]),
            problem => problem.Contains("at least one", StringComparison.Ordinal));
        Assert.NotEmpty(BrowserSmokePlan.Validate(null));
    }

    [Fact]
    public void ACheckThatNeedsNoRealDeviceMustNameItsAutomatedCoverage()
    {
        // 不许"既不需要真机、又说不清被什么覆盖" —— 那是最容易蒙混的一种。
        var sneaky = new List<BrowserSmokeCheck>
        {
            new("S-Y", "G1", "区域", "动作", "期望", RequiresRealDevice: false),
        };

        Assert.Contains(
            BrowserSmokePlan.Validate(sneaky),
            problem => problem.Contains("automated coverage", StringComparison.Ordinal));

        var honest = new List<BrowserSmokeCheck>
        {
            new("S-Z", "G1", "区域", "动作", "期望", RequiresRealDevice: false, AutomatedCoverage: "SomeTests.Case"),
        };

        // 单条清单当然覆盖不全门禁（那是另一条规则），但**不该**再报"没写清自动化覆盖"。
        Assert.DoesNotContain(
            BrowserSmokePlan.Validate(honest),
            problem => problem.Contains("automated coverage", StringComparison.Ordinal));
    }

    [Fact]
    public void TheEventReportingCheckStatesTheRealBoundFromTheCode()
    {
        // S-13 的期望必须**写出真实的上界**：外部一方只能照文字判定，写"有界"等于让人猜。
        // 这里把清单文字与 `PageEventLog.DefaultCapacity` 绑死：容量改了而清单没改 ⇒ 立刻变红。
        var check = BrowserSmokePlan.Checks.Single(item => item.Id == "S-13");

        Assert.Contains(
            PageEventLog.DefaultCapacity.ToString(System.Globalization.CultureInfo.InvariantCulture),
            check.Expected,
            StringComparison.Ordinal);
        Assert.Contains("(xN)", check.Expected, StringComparison.Ordinal);
        Assert.Contains("pageEvents", check.Expected, StringComparison.Ordinal);
    }

    [Fact]
    public void TheRenderedPlanListsEveryCheckAndFlagsAutomatedCoverage()
    {
        var markdown = BrowserSmokePlan.ToMarkdown();

        foreach (var check in BrowserSmokePlan.Checks)
        {
            Assert.Contains(check.Id, markdown);
            Assert.Contains(check.Expected.Split('；')[0], markdown);
        }

        Assert.Contains("| 项 | 门禁 | 区域 | 怎么触发 | 看到什么才算过 |", markdown);
        Assert.Contains("已有自动化覆盖", markdown);
        Assert.Contains("AutomationTests.SubAgentDelegation", markdown);
    }

    [Fact]
    public void TheInternalVerificationRunbookNamesEveryStepAndScript()
    {
        // 内部验证顺序是交接材料的一部分：改了共享契约就必须跑链外抽查（round-122 的教训）。
        // 这条把"runbook 必须写清四步与四个脚本"钉住，避免文档与工具各自漂移。
        var runbook = File.ReadAllText(Path.Combine(
            RepoLayout.Root, "Docs", "13_runbooks", "浏览器自动化内部验证-runbook.md"));

        foreach (var script in new[]
        {
            "check-round-gates.ps1",
            "delivery-manifest.ps1",
            "check-dependent-suites.ps1",
            "external-acceptance.ps1",
        })
        {
            Assert.Contains(script, runbook, StringComparison.Ordinal);
        }

        // 四个步骤的标题必须在（顺序即流程）；步骤 3 还必须带**触发条件**，
        // 否则"什么时候该跑链外抽查"就又回到靠人记（这正是 round-122 之前的状况）。
        Assert.Contains("步骤 1：文档门禁", runbook, StringComparison.Ordinal);
        Assert.Contains("步骤 2：链内 7 套件", runbook, StringComparison.Ordinal);
        Assert.Contains("步骤 4：外部验收排练", runbook, StringComparison.Ordinal);

        // 步骤 3 的**标题行本身**必须带触发条件（正文别处出现同样的词不算）：
        // 否则"什么时候该跑链外抽查"又回到靠人记 —— 这正是 round-122 之前的状况。
        Assert.Matches(
            new Regex(@"^##\s*步骤 3：链外抽查.*改了共享契约", RegexOptions.Multiline),
            runbook);

        // 必须写明"证明不了什么"（诚实性要求：runbook 不能只讲能证明的）。
        Assert.Contains("证明不了", runbook, StringComparison.Ordinal);
    }

    [Fact]
    public void TheExternalScriptDocumentsHowToCallItsSwitches()
    {
        // 实战教训（round-119）：把 `-Template` 当"模板输出路径"传（它其实是**开关**）时，
        // 后面的路径被静默绑到位置参数上 —— 输出看着像成功，参数却是错的。
        // 这条把"帮助块必须写清开关用法与观察文件参数名"钉住，免得下一个人重犯。
        var script = File.ReadAllText(Path.Combine(RepoLayout.Root, "TestScripts", "external-acceptance.ps1"));

        Assert.Contains("-Template -Facts", script, StringComparison.Ordinal);
        Assert.Contains("-SkipTemplate -Conclude -Facts", script, StringComparison.Ordinal);
        Assert.Contains("SWITCHES", script, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("positional", script, StringComparison.OrdinalIgnoreCase);

        // 帮助块提到的参数必须仍然存在（脚本改写时这条会提醒同步）。
        Assert.Contains("$Facts", script, StringComparison.Ordinal);
        Assert.Contains("[switch]$Template", script, StringComparison.Ordinal);
        Assert.Contains("[switch]$Conclude", script, StringComparison.Ordinal);
    }

    [Fact]
    public void TheExternalScriptMirrorsThePlan()
    {
        // round-76 登记的风险：外部验收脚本里的观察项编号是 BrowserSmokePlan 的**镜像**（跨语言无法共享常量），
        // 当时没有门禁。这条测试把镜像关系变成可验证的。
        var path = Path.Combine(RepoLayout.Root, "TestScripts", "external-acceptance.ps1");
        Assert.True(File.Exists(path), $"找不到外部验收脚本：{path}");

        var script = File.ReadAllText(path);

        var scriptIds = System.Text.RegularExpressions.Regex
            .Matches(script, @"Id = ""(S-\d+)""")
            .Select(match => match.Groups[1].Value)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            BrowserSmokePlan.Checks.Select(check => check.Id).OrderBy(id => id, StringComparer.Ordinal),
            scriptIds);

        // 生命周期步骤同样必须齐全（脚本据此写骨架）。
        foreach (var step in DeliveryManifest.RequiredLifecycleSteps)
        {
            Assert.Contains(step, script);
        }
    }

    [Fact]
    public void TheChecklistRowsDoNotPromiseMoreThanThePlan()
    {
        // 取红用例（round-142）：原来的漂移门禁只核对"编号与门禁"出现在文档里，
        // 于是文档可以继续承诺代码/清单里**没有**的东西 —— 实测 S-09 一行还留着已被判定为
        // 无出处的「≤40 KiB」（round-136 已从清单里删掉，文档没跟上）。
        // 现在把**动作**逐字核对，并要求文档行里的**每个数字**都能在清单的对应项里找到出处。
        var path = Path.Combine(
            RepoLayout.Root, "Docs", "14_reports", "2026-10-09-浏览器自动化外部smoke清单.md");
        var rows = File.ReadAllLines(path)
            .Where(line => line.StartsWith("| S-", StringComparison.Ordinal))
            .Select(line => line.Split('|').Select(cell => cell.Trim()).ToArray())
            .ToDictionary(cells => cells[1], cells => cells, StringComparer.Ordinal);

        var problems = new List<string>();
        foreach (var check in BrowserSmokePlan.Checks)
        {
            if (!rows.TryGetValue(check.Id, out var cells) || cells.Length < 6)
            {
                problems.Add($"{check.Id} 在清单文档里找不到整行");
                continue;
            }

            var documentedAction = cells[4];
            var documentedExpectation = cells[5];
            if (!string.Equals(documentedAction, check.Action, StringComparison.Ordinal))
            {
                problems.Add($"{check.Id} 的动作与清单不一致：\n      文档：{documentedAction}\n      代码：{check.Action}");
            }

            var planText = check.Action + " " + check.Expected;
            foreach (System.Text.RegularExpressions.Match number in System.Text.RegularExpressions.Regex.Matches(
                documentedExpectation, @"\d+"))
            {
                if (!planText.Contains(number.Value, StringComparison.Ordinal))
                {
                    problems.Add(
                        $"{check.Id} 的期望里写了数字 {number.Value}，但清单里找不到出处"
                        + "（文档承诺了清单没有的东西）");
                }
            }
        }

        Assert.True(problems.Count == 0, "清单文档与代码真源不一致：\n  - " + string.Join("\n  - ", problems));
    }
    [Fact]
    public void TheCommittedChecklistDoesNotDriftFromThePlan()
    {
        // 留痕清单（Docs/14_reports）必须与代码里的唯一真源一致：
        // 每条观察项都要出现在文档里（防止"代码加了项、文档没跟上"）。
        var path = Path.Combine(
            RepoLayout.Root, "Docs", "14_reports", "2026-10-09-浏览器自动化外部smoke清单.md");
        Assert.True(File.Exists(path), $"找不到 smoke 清单文档：{path}");

        var markdown = File.ReadAllText(path);

        foreach (var check in BrowserSmokePlan.Checks)
        {
            Assert.Contains($"| {check.Id} |", markdown);
            Assert.Contains(check.Gate, markdown);
        }

        // 反向：文档里不该出现代码里没有的观察项编号。
        var documentedIds = System.Text.RegularExpressions.Regex
            .Matches(markdown, @"^\| (S-\d+) \|", System.Text.RegularExpressions.RegexOptions.Multiline)
            .Select(match => match.Groups[1].Value)
            .ToArray();

        Assert.Equal(
            BrowserSmokePlan.Checks.Select(check => check.Id).OrderBy(id => id, StringComparer.Ordinal),
            documentedIds.OrderBy(id => id, StringComparer.Ordinal));
    }
}
