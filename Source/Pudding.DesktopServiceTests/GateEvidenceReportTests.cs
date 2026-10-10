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
    public void TheBrowserAutomationComponentKeepsItsCompileTimeBoundary()
    {
        // 设计 §2/§10：Automation 是**独立组件**，S1–S4 期间只增独立工程与测试；B1 首段才把它接进 Host。
        // 边界必须可核对（而不是靠文档约定）：**Core 侧不得引用它**，Host 侧**确有一个**消费者
        // （Host 与 DesktopService 共用同一份后置条件判据，见 §4.2/§1.1）。
        var root = RepoLayout.Root;
        var coreProjects = new[]
        {
            Path.Combine("Source", "PuddingAgent", "PuddingAgent.csproj"),
            Path.Combine("Source", "PuddingRuntime", "PuddingRuntime.csproj"),
        };

        foreach (var relative in coreProjects)
        {
            var path = Path.Combine(root, relative);
            Assert.True(File.Exists(path), $"找不到 Core 工程：{relative}");
            Assert.DoesNotContain(
                "PuddingBrowser.Automation",
                File.ReadAllText(path),
                StringComparison.Ordinal);
        }

        // Host 侧：**必须**引用（否则 Host 与 DesktopService 会各自验出相反结论）。若有意取消这条接线，
        // 就请同时更新本条与设计文档 —— 不许"悄悄不接"。
        var hostPath = Path.Combine(root, "Source", "PuddingHost", "PuddingHost.csproj");
        Assert.True(File.Exists(hostPath), "找不到 PuddingHost.csproj");
        Assert.Contains("PuddingBrowser.Automation", File.ReadAllText(hostPath), StringComparison.Ordinal);

        // 该引用存在的理由要能在代码里看见（共用后置条件判据）。
        var surface = Path.Combine(root, "Source", "PuddingHost", "BrowserBridge", "BridgeBrowserCapabilitySurface.cs");
        Assert.True(File.Exists(surface), "找不到 Host 侧浏览器桥面");
        Assert.Contains("BrowserMutationPostcondition.Validate", File.ReadAllText(surface), StringComparison.Ordinal);
    }
    [Fact]
    public void TheKnownFlakyRegistryIsConsultedByTheRetryPath()
    {
        // 重跑后变绿的套件**不能**一律被当成"没事"：脚本必须拿失败用例名去查已知 flake 注册表，
        // 不在表里的要写成 NEW INSTABILITY（否则任何未来的真问题都会被重跑悄悄吸收）。
        var root = RepoLayout.Root;
        var registryPath = Path.Combine(root, "TestScripts", "known-flaky.txt");
        Assert.True(File.Exists(registryPath), $"找不到已知 flake 注册表：{registryPath}");

        var registry = File.ReadAllText(registryPath);

        // 仍在册的时序 flake（round-164 就因漏登记第二条而触发过一次拒绝）。
        foreach (var knownFlake in new[]
        {
            "PuddingDesktop.Tests.Browser.WebView2PresentationGateTests.HiddenSurface_ParksLateSdkFramesAndStopsObservingAfterDisposal",
        })
        {
            Assert.Contains(knownFlake, registry, StringComparison.Ordinal);
        }

        // **已修好**的那条必须只是注释里的历史记录，不能仍是"条目" —— 否则它会把未来的真失败悄悄吸收。
        // （修复证据：确定性判据取代墙钟，见 Docs/00_changelog 的 flake 修复记录。）
        var bareEntries = registry
            .Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.Length > 0 && !line.StartsWith("#", StringComparison.Ordinal))
            .ToArray();
        // 两条**已修好**的用例都必须只是注释里的历史记录，不能仍是"条目"。
        foreach (var fixedFlake in new[]
        {
            "PuddingHost.Tests.Hosting.DesktopConfigurationLoadingTests.LlmConfigurationLoad_DoesNotDeadlockOnDesktopSynchronizationContext",
            "PuddingHost.Tests.BrowserBridge.DesktopBrowserBridgeHandshakeTests.SecondConnection_CannotReplaceAwaitingHelloConnection",
        })
        {
            Assert.DoesNotContain(fixedFlake, bareEntries);
        }
        Assert.Contains("FIXED", registry, StringComparison.Ordinal);

        var script = File.ReadAllText(Path.Combine(root, "TestScripts", "delivery-manifest.ps1"));
        Assert.Contains("known-flaky.txt", script, StringComparison.Ordinal);
        Assert.Contains("NEW INSTABILITY", script, StringComparison.Ordinal);
        Assert.Contains("known-flaky registry", script, StringComparison.Ordinal);
        // round-162：新不稳定**默认拒绝出清单**，必须显式 -AllowNewInstability 才能接受。
        Assert.Contains("[switch]$AllowNewInstability", script, StringComparison.Ordinal);
        Assert.Contains("REFUSING to build a manifest: a retry absorbed a failure", script, StringComparison.Ordinal);
        // round-163：吸收的失败要留下**频次**数据（append-only 日志 + 只读统计脚本），否则"它偶尔失败"无法行动。
        Assert.Contains("flake-observations.txt", script, StringComparison.Ordinal);
        Assert.Contains("flake-stats.ps1", script, StringComparison.Ordinal);
        foreach (var required in new[] { "flake-observations.txt", "flake-stats.ps1" })
        {
            Assert.True(
                File.Exists(Path.Combine(root, "TestScripts", required)),
                $"缺少交付链的频次数据文件：TestScripts/{required}");
        }
    }
    [Fact]
    public void DocumentedPlanDeviationsStillDescribeTheActualCode()
    {
        // §6.1 的预算数字与代码**不一致**（文档 10,000 节点/深度 64；代码 5,000/24），这份偏差已写进对账表。
        // 这条门禁保证：① 对账表还写着这段偏差；② 代码里的值确实等于表里记录的值。
        // 任何人改了默认预算（无论向文档靠拢还是反向），都必须同步更新对账表 —— 偏差不会悄悄消失或悄悄扩大。
        var reportPath = Path.Combine(RepoLayout.Root, ReportRelativePath.Replace('/', Path.DirectorySeparatorChar));
        var report = File.ReadAllText(reportPath);
        Assert.Contains("§6.1 预算数字对账", report, StringComparison.Ordinal);

        var options = new Pudding.Contracts.Desktop.DesktopSnapshotOptions();
        // round-155：节点/深度已按文档对齐（10_000/64）；250ms 与 full 模式预算仍是登记的偏差。
        Assert.Equal(10_000, Pudding.Contracts.Desktop.DesktopSnapshotOptions.DefaultMaxNodes);
        Assert.Equal(64, options.MaxDepth);
        Assert.Contains("5_000", report, StringComparison.Ordinal);
        Assert.Contains("10,000", report, StringComparison.Ordinal);
        Assert.Contains("maxDepth = 64", report, StringComparison.Ordinal);
        Assert.Contains("250ms", report, StringComparison.Ordinal);

        // §6.1 各模式预算里"一致的那四条"也要钉住；full 模式的偏差必须仍在记录里。
        Assert.Equal(4_096, PuddingBrowser.Automation.ArticleMarkdown.DefaultMaxBytes);
        Assert.Equal(4_096, PuddingBrowser.Automation.ArticleCompactJson.DefaultMaxBytes);
        // round-156：full 模式的 16 KiB/64 KiB 预算已按文档实现；仅"无续读游标"作为缺口登记。
        Assert.Contains("续读游标已实现", report, StringComparison.Ordinal);
        Assert.Contains("16 KiB、硬上限 64 KiB", report, StringComparison.Ordinal);
    }
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

        // 只核对**标准行**里的用例名：同一节里还有"真机观察步骤"这类叙述行，里面的行内代码（如 CoreReady）
        // 不是用例名，若一并核对会变成噪音。
        var criterionLines = section
            .Split('\n')
            .Where(line => line.StartsWith("|", StringComparison.Ordinal))
            .Where(line => !line.Contains("真机观察步骤", StringComparison.Ordinal))
            .ToArray();

        var cited = criterionLines
            .SelectMany(line => Regex.Matches(line, "`([A-Z][A-Za-z0-9_]{6,})`").Select(match => match.Groups[1].Value))
            .Where(name => !name.EndsWith("Tests", StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        Assert.True(cited.Length >= 30, $"只解析到 {cited.Length} 个用例名，解析逻辑可能写错");

        // 表里会引用多个测试工程的用例名（自动化组件 / 桌面浏览器面 / Agent 工具面），逐个工程都扫。
        var projects = new[]
        {
            Path.Combine("Source", "PuddingBrowser.AutomationTests"),
            Path.Combine("Source", "Pudding.DesktopSurface.BrowserTests"),
            Path.Combine("Tests", "PuddingBrowser.AgentTools.Tests"),
            Path.Combine("Tests", "PuddingDesktop.Tests"),
        };

        var declared = projects
            .Select(relative => Path.Combine(RepoLayout.Root, relative))
            .Where(Directory.Exists)
            .SelectMany(directory => Directory.EnumerateFiles(directory, "*.cs", SearchOption.AllDirectories))
            .SelectMany(path => Regex.Matches(File.ReadAllText(path), @"public\s+(?:async\s+)?(?:Task|void|Task<[^>]+>)\s+(?<name>\w+)\s*\(")
                .Select(match => match.Groups["name"].Value))
            .ToHashSet(StringComparer.Ordinal);

        var missing = cited.Where(name => !declared.Contains(name)).ToArray();
        Assert.True(
            missing.Length == 0,
            "对账表引用了不存在的用例（改名/删除后必须同步表格）：\n  - " + string.Join("\n  - ", missing));
    }
}
