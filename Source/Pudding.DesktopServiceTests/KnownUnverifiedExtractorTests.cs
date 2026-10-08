using Pudding.DesktopService;

namespace DesktopServiceTests;

/// <summary>
/// 从修改日志提取"内部证明不了的结论"（交付清单 knownUnverified 的来源）。
/// 规则刻意保守：宁可少提取（人工能补），也不要把"已验证"的句子误判成未验证。
/// </summary>
public sealed class KnownUnverifiedExtractorTests
{
    [Fact]
    public void ExtractsTheHonestBoundariesFromChangelogText()
    {
        const string changelog = """
            ---
            title: B3 原生拖拽
            ---

            ## 验证

            - `AutomationTests` 272/272。
            - **真机未验证**：CDP 序列有纯函数测试，但"分步拖拽在真实页面能否生效"必须由外部进程内 smoke 复核。
            - 已注册的检查全部通过。

            ## 已知边界

            - DOM 模式不支持拖拽（明确拒绝）。
            - 只支持 input[type=file]，拖放区不适用。  <!-- 与"未验证"无关，不应被提取 -->
            """;

        var items = KnownUnverifiedExtractor.Extract([changelog]);

        Assert.Single(items);
        Assert.Contains("真机未验证", items[0]);
        Assert.Contains("外部进程内 smoke", items[0]);

        // markdown 装饰被清掉（清单是给人读的纯文本）。
        Assert.DoesNotContain("**", items[0]);
        Assert.DoesNotContain("-", items[0][..1]);
    }

    [Fact]
    public void DeduplicatesAndPreservesOrder()
    {
        var items = KnownUnverifiedExtractor.Extract(
        [
            "- 真机未验证：CDP 行为需外部 smoke。\n- 其它检查通过。",
            "- 真机未验证：CDP 行为需外部 smoke。\n- 环境限制：本机无 WebView2。",
        ]);

        Assert.Equal(2, items.Count);
        Assert.Contains("CDP 行为", items[0]);
        Assert.Contains("环境限制", items[1]);
    }

    [Fact]
    public void ReturnsEmptyListRatherThanNull()
    {
        // 交付清单要求该字段显式给出：空列表 = "确实没有"，null = "没想过"。
        var items = KnownUnverifiedExtractor.Extract(["## 验证\n- 全部通过，已真机验证完毕。"]);
        Assert.NotNull(items);
        Assert.Empty(items);

        Assert.Empty(KnownUnverifiedExtractor.Extract([]));
        Assert.Empty(KnownUnverifiedExtractor.Extract([null, "   "]));
    }

    [Fact]
    public void IgnoresTextThatMerelyMentionsVerificationAsDone()
    {
        var items = KnownUnverifiedExtractor.Extract(
        [
            "- 已真机验证：起停与回收均通过。",
            "- 验证方式：外部控制器重启到新构建。",
            "- 全量套件 276/276 通过。",
        ]);

        Assert.Empty(items);
    }

    [Fact]
    public void ItemsAreBoundedInLengthAndCount()
    {
        var longLine = "- 真机未验证：" + new string('x', 500);
        var many = string.Join('\n', Enumerable.Range(0, KnownUnverifiedExtractor.MaxItems + 10)
            .Select(index => $"- 真机未验证：第 {index} 项"));

        var items = KnownUnverifiedExtractor.Extract([longLine]);
        Assert.Single(items);
        Assert.True(items[0].Length <= KnownUnverifiedExtractor.MaxItemChars + 1, $"过长：{items[0].Length}");
        Assert.EndsWith("…", items[0]);

        Assert.Equal(KnownUnverifiedExtractor.MaxItems, KnownUnverifiedExtractor.Extract([many]).Count);
    }

    [Fact]
    public void ExtractsFromTheRealChangelogCorpus()
    {
        // 用**真实日志**跑一遍：这些边界是我每轮如实登记的，提取器必须能把它们找出来
        // （否则"交付清单不遗漏未验证项"就只是设计意图）。
        var changelogDirectory = Path.Combine(RepoLayout.Root, "Docs", "00_changelog", "2026Year", "10");
        Assert.True(Directory.Exists(changelogDirectory), $"找不到日志目录：{changelogDirectory}");

        var documents = Directory.EnumerateFiles(changelogDirectory, "*.md")
            .Where(path => Path.GetFileName(path).Contains("B3原生拖拽", StringComparison.Ordinal)
                || Path.GetFileName(path).Contains("B4截图", StringComparison.Ordinal)
                || Path.GetFileName(path).Contains("B7交付清单", StringComparison.Ordinal))
            .Select(File.ReadAllText)
            .ToArray();
        Assert.NotEmpty(documents);

        var items = KnownUnverifiedExtractor.Extract(documents);

        Assert.NotEmpty(items);
        Assert.Contains(items, item => item.Contains("真机", StringComparison.Ordinal));
        Assert.All(items, item => Assert.True(item.Length > 0));
    }

    [Fact]
    public void ExtractedItemsSatisfyTheDeliveryManifestRequirement()
    {
        // 端到端的一小步：提取出来的东西直接放进清单，且清单能通过校验。
        var items = KnownUnverifiedExtractor.Extract(
            ["- 真机未验证：CDP 行为需外部 smoke。"]);

        var manifest = new DeliveryManifest(
            "build-2026-10-08",
            "3eddd34",
            ["PuddingDesktop.exe"],
            [new DeliveryCheck("全部套件", 1_845, 0)],
            items,
            ["startup", "restart", "crash-recovery", "exit-reclamation"]);

        Assert.Empty(DeliveryManifestValidator.Validate(manifest));
        Assert.Single(manifest.KnownUnverified!);
    }
}
