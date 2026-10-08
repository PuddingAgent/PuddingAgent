using Pudding.DesktopService;

namespace DesktopServiceTests;

/// <summary>
/// 交付报告渲染（把清单变成留痕文档）：Front Matter 必须齐全（否则 Docs 门禁会拦），
/// 且**逐字带上**清单里的未验证项 —— 不留"摘要时被删掉"的口子。
/// </summary>
public sealed class DeliveryManifestReportTests
{
    private static DeliveryManifest Manifest(
        IReadOnlyList<string>? knownUnverified = null,
        int passed = 276,
        int failed = 0) =>
        new(
            "round-64",
            "9b97418",
            ["PuddingDesktop.exe", "Pudding.DesktopService.dll"],
            [new DeliveryCheck("PuddingBrowser.AutomationTests", passed, failed)],
            knownUnverified ?? ["真机未验证：CDP 行为需外部 smoke。"],
            DeliveryManifest.RequiredLifecycleSteps);

    private static string Render(DeliveryManifest manifest) =>
        DeliveryManifestReport.Render(
            manifest,
            "交付清单（测试）",
            new DateOnly(2026, 10, 8),
            "reports-delivery-test");

    private static readonly string[] RequiredFrontMatterKeys =
    [
        "title:", "author:", "date:", "last_reviewed:", "description:", "status:",
        "categories:", "tags:", "related_docs:", "related_files:", "slug:", "draft:",
    ];

    [Fact]
    public void ReportCarriesACompleteFrontMatterBlock()
    {
        var markdown = Render(Manifest());

        Assert.StartsWith("---\n", markdown);
        foreach (var key in RequiredFrontMatterKeys)
        {
            Assert.Contains($"\n{key}", markdown);
        }

        Assert.Contains("status: active", markdown);
        Assert.Contains("slug: reports-delivery-test", markdown);
        Assert.Contains("draft: false", markdown);

        // 第二个 --- 关闭 Front Matter，正文从 H1 开始。
        var closing = markdown.IndexOf("\n---\n", 4, StringComparison.Ordinal);
        Assert.True(closing > 0, "Front Matter 未闭合");
        Assert.Contains("# 交付清单（测试）", markdown);
    }

    [Fact]
    public void ReportCarriesEveryUnverifiedItemVerbatim()
    {
        var items = new[]
        {
            "真机未验证：本环境没有 WebView2。",
            "真机未验证：注入的策略在 WinUI 真实运行时是否被调用无法自证。",
        };

        var markdown = Render(Manifest(items));

        foreach (var item in items)
        {
            Assert.Contains($"- {item}", markdown);
        }
    }

    [Fact]
    public void ReportStatesExplicitlyWhenThereAreNoUnverifiedItems()
    {
        var markdown = Render(Manifest(knownUnverified: []));
        Assert.Contains("没有已知的未验证项", markdown);
    }

    [Fact]
    public void ReportCarriesTheFactsAndTheStage()
    {
        var markdown = Render(Manifest());

        Assert.Contains("| 提交 | `9b97418` |", markdown);
        Assert.Contains("| 通过 / 失败 | **276 / 0** |", markdown);
        Assert.Contains("| PuddingBrowser.AutomationTests | 276 | 0 |", markdown);
        Assert.Contains("`PuddingDesktop.exe`", markdown);
        Assert.Contains("ready-for-external-deploy", markdown);
        Assert.Contains("`exit-reclamation`", markdown);
        Assert.Contains("拒绝", markdown);   // 明确写出"内部不能判定生命周期"
    }

    [Fact]
    public void ReportLinksTheDesignDocRelativeToTheReportsDirectory()
    {
        var markdown = Render(Manifest());

        // Front Matter 里用仓库相对路径，正文里的链接相对 Docs/14_reports/。
        Assert.Contains("related_docs: [Docs/12_features/", markdown);
        Assert.Contains("](../12_features/", markdown);
    }

    [Fact]
    public void RendererRefusesBlankTitleOrSlug()
    {
        Assert.Throws<ArgumentNullException>(() => DeliveryManifestReport.Render(null!, "t", new DateOnly(2026, 10, 8), "s"));
        Assert.Throws<ArgumentException>(() => DeliveryManifestReport.Render(Manifest(), "  ", new DateOnly(2026, 10, 8), "s"));
        Assert.Throws<ArgumentException>(() => DeliveryManifestReport.Render(Manifest(), "t", new DateOnly(2026, 10, 8), " "));
    }
}
