using PuddingBrowser.Automation;

namespace PuddingBrowser.AutomationTests;

/// <summary>
/// 阅读面收敛（设计方案 §6.1/§6.2、门禁 G5）：
/// 默认只给「能点什么、能填什么」，输出预算与**内部扫描预算**分开，
/// 截断必须如实区分 <c>output_budget</c>（可用游标续读）与 <c>incomplete_scan</c>（可能缺整片区域）。
/// </summary>
[TestClass]
public sealed class BrowserReadingFormatTests
{
    private const string Tree = """
        document name="Example"
        banner ref=p0-v1-n1 name="Site"
        navigation
          link ref=p0-v1-n2 name="Home"
          link ref=p0-v1-n3 name="Docs"
        main
          heading name="Welcome"
          list
            listitem ref=p0-v1-n4 name="Item one"
          textbox ref=p0-v1-n5 name="Search"
          button ref=p0-v1-n6 name="Go"
        contentinfo
          link ref=p0-v1-n7 name="Privacy"
        """;

    private static BrowserReadingRequest Request(
        BrowserReadingMode mode = BrowserReadingMode.Outline,
        int limit = BrowserReadingFormat.DefaultLineLimit,
        int maxBytes = BrowserReadingFormat.DefaultMaxBytes,
        int cursor = 0) => new(mode, limit, maxBytes, cursor);

    [TestMethod]
    public void Outline_Stays_Within_The_Design_Budget_On_A_Large_Tree()
    {
        // ⚠️ 合成树，**不是**真实页面测量：真实页面（TestSite / 目标站点）的字节数
        // 需要在外部进程内 smoke 里复核。这条只钉住「默认模式确实把体积收敛到设计预算内」。
        var lines = new List<string> { "document name=\"App\"" };
        for (var i = 0; i < 200; i++)
        {
            lines.Add($"  generic name=\"row {i}\"");
            lines.Add($"    button ref=p0-v1-n{i} name=\"Action {i} with a reasonably long label\"");
        }

        var tree = string.Join('\n', lines);
        var full = BrowserReadingFormat.Compose(tree, Request(BrowserReadingMode.Full));
        var outline = BrowserReadingFormat.Compose(tree, Request());

        Assert.IsTrue(outline.Bytes <= BrowserReadingFormat.DefaultMaxBytes,
            $"outline bytes={outline.Bytes} 应不超过 {BrowserReadingFormat.DefaultMaxBytes}");
        Assert.IsTrue(outline.Truncated, "大页面下默认模式必须如实标注截断");
        Assert.AreEqual(BrowserReadingTruncation.OutputBudget, outline.Truncation);
        Assert.IsNotNull(outline.NextCursor, "超预算时应给出续读游标");
        Assert.IsTrue(full.Bytes > outline.Bytes * 3,
            $"full={full.Bytes} 应远大于 outline={outline.Bytes}");
    }

    [TestMethod]
    public void Outline_Keeps_Interactive_And_Structural_Lines_Only()
    {
        var result = BrowserReadingFormat.Compose(Tree, Request());

        Assert.IsNotNull(result.Text);
        var lines = result.Text!.Split('\n');

        // 结构行（banner/navigation/main/heading/list/listitem/contentinfo）与可交互行保留；
        // 纯容器行（document 有 name，无 ref 且非结构角色）被丢弃。
        Assert.IsFalse(lines.Any(line => line.TrimStart().StartsWith("document", StringComparison.Ordinal)));
        Assert.IsTrue(lines.Any(line => line.Contains("ref=p0-v1-n6", StringComparison.Ordinal)));
        Assert.IsTrue(lines.Any(line => line.TrimStart().StartsWith("heading", StringComparison.Ordinal)));

        Assert.IsFalse(result.Truncated);
        Assert.AreEqual(BrowserReadingTruncation.None, result.Truncation);
        Assert.IsNull(result.NextCursor);
    }

    [TestMethod]
    public void Interactive_Keeps_Only_Lines_The_Driver_Marked_With_A_Ref()
    {
        var result = BrowserReadingFormat.Compose(Tree, Request(BrowserReadingMode.Interactive));

        Assert.IsNotNull(result.Text);
        foreach (var line in result.Text!.Split('\n'))
        {
            Assert.Contains("ref=", line);
        }

        // 标题是结构行但不是可交互行 ⇒ interactive 模式下不返回。
        Assert.DoesNotContain("heading", result.Text, StringComparison.Ordinal);
    }

    [TestMethod]
    public void Full_Keeps_Everything()
    {
        var result = BrowserReadingFormat.Compose(Tree, Request(BrowserReadingMode.Full));

        Assert.AreEqual(result.SourceLines, result.ReturnedLines);
        Assert.IsFalse(result.Truncated);
        Assert.IsNull(result.NextCursor);
    }

    [TestMethod]
    public void Full_Ignores_The_Output_Budget()
    {
        // full 是排障模式：必须真的给全量，否则「full」会静默变成另一个有预算的模式。
        var longTree = string.Join('\n', Enumerable.Range(0, 500)
            .Select(i => $"button ref=p0-v1-n{i} name=\"{new string('x', 40)}\""));

        var full = BrowserReadingFormat.Compose(longTree, Request(BrowserReadingMode.Full, limit: 1, maxBytes: 64));

        Assert.AreEqual(500, full.ReturnedLines);
        Assert.IsFalse(full.Truncated);
        Assert.IsTrue(full.Bytes > 4096);

        // 扫描侧截断仍然要如实标注（那是「可能缺整片区域」，与预算无关）。
        var scanTruncated = BrowserReadingFormat.Compose(longTree, Request(BrowserReadingMode.Full), scanTruncated: true);
        Assert.AreEqual(BrowserReadingTruncation.IncompleteScan, scanTruncated.Truncation);
    }

    [TestMethod]
    public void Output_Budget_Truncation_Is_Distinct_From_Incomplete_Scan()
    {
        // 扫描完整、只是行数收敛 ⇒ output_budget + 游标（可续读）。
        var limited = BrowserReadingFormat.Compose(Tree, Request(limit: 3));

        Assert.IsTrue(limited.Truncated);
        Assert.AreEqual(BrowserReadingTruncation.OutputBudget, limited.Truncation);
        Assert.AreEqual(3, limited.ReturnedLines);
        Assert.AreEqual(3, limited.NextCursor);

        // 扫描侧被截断 ⇒ incomplete_scan，且**不给游标**（续读也无法保证完整）。
        var scanTruncated = BrowserReadingFormat.Compose(Tree, Request(), scanTruncated: true);

        Assert.IsTrue(scanTruncated.Truncated);
        Assert.AreEqual(BrowserReadingTruncation.IncompleteScan, scanTruncated.Truncation);
        Assert.IsNull(scanTruncated.NextCursor);
    }

    [TestMethod]
    public void Cursor_Continues_Where_The_Previous_Read_Stopped()
    {
        var first = BrowserReadingFormat.Compose(Tree, Request(limit: 3));
        var second = BrowserReadingFormat.Compose(Tree, Request(limit: 3, cursor: first.NextCursor!.Value));

        Assert.IsNotNull(first.Text);
        Assert.IsNotNull(second.Text);
        Assert.AreNotEqual(first.Text, second.Text);
        Assert.IsFalse(first.Text!.Split('\n').Intersect(second.Text!.Split('\n')).Any(),
            "续读不得重复上一页的行");
    }

    [TestMethod]
    public void Byte_Budget_Stops_The_Outline()
    {
        var longTree = string.Join('\n', Enumerable.Range(0, 100)
            .Select(i => $"button ref=p0-v1-n{i} name=\"{new string('x', 60)}\""));

        var result = BrowserReadingFormat.Compose(
            longTree, Request(limit: 100, maxBytes: 256));

        Assert.IsTrue(result.Truncated);
        Assert.AreEqual(BrowserReadingTruncation.OutputBudget, result.Truncation);
        Assert.IsTrue(result.Bytes <= 256, $"bytes={result.Bytes} 应受 256 限制");
        Assert.IsTrue(result.ReturnedLines < 100);
    }

    [TestMethod]
    public void A_Single_Oversized_Line_Is_Still_Returned()
    {
        // 宁可超一点预算也要给出一行，否则调用方永远读不到东西；但仍如实标注截断。
        var huge = "button ref=p0-v1-n1 name=\"" + new string('y', 400) + "\"";

        var result = BrowserReadingFormat.Compose(huge, Request(maxBytes: 64));

        Assert.IsNotNull(result.Text);
        Assert.AreEqual(1, result.ReturnedLines);
    }

    [TestMethod]
    public void Empty_Tree_Yields_No_Text_And_No_Truncation()
    {
        foreach (var input in new string?[] { null, "", "   " })
        {
            var result = BrowserReadingFormat.Compose(input, Request());

            Assert.IsNull(result.Text);
            Assert.AreEqual(0, result.SourceLines);
            Assert.AreEqual(0, result.ReturnedLines);
            Assert.IsFalse(result.Truncated);
        }
    }

    [TestMethod]
    public void Byte_Count_Is_Utf8()
    {
        // 用非 ASCII 名字验证字节口径（模型看到的是字节预算，不是字符数）。
        var tree = "button ref=p0-v1-n1 name=\"提交订单\"";

        var result = BrowserReadingFormat.Compose(tree, Request());

        Assert.AreEqual(System.Text.Encoding.UTF8.GetByteCount(tree), result.Bytes);
        Assert.IsTrue(result.Bytes > tree.Length, "UTF-8 下中文占多字节");
    }

    [TestMethod]
    public void Invalid_Budgets_Are_Rejected()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => BrowserReadingFormat.Compose(Tree, Request(limit: 0)));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => BrowserReadingFormat.Compose(Tree, Request(maxBytes: 8)));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => BrowserReadingFormat.Compose(Tree, Request(cursor: -1)));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => BrowserReadingFormat.Compose(Tree, Request((BrowserReadingMode)99)));
        Assert.ThrowsExactly<ArgumentNullException>(
            () => BrowserReadingFormat.Compose(Tree, null!));
    }

    [TestMethod]
    public void Defaults_Match_The_Design_Budget()
    {
        Assert.AreEqual(50, BrowserReadingFormat.DefaultLineLimit);
        Assert.AreEqual(4096, BrowserReadingFormat.DefaultMaxBytes);

        // 内部扫描预算与输出预算分开，且明显更大。
        Assert.IsTrue(BrowserReadingFormat.ScanMaxNodes > BrowserReadingFormat.DefaultLineLimit);
        Assert.AreEqual(10_000, BrowserReadingFormat.ScanMaxNodes);
        Assert.AreEqual(64, BrowserReadingFormat.ScanMaxDepth);
    }

    [TestMethod]
    public void Wire_Names_Are_Stable()
    {
        Assert.AreEqual("outline", BrowserReadingFormatWire.NameOf(BrowserReadingMode.Outline));
        Assert.AreEqual("interactive", BrowserReadingFormatWire.NameOf(BrowserReadingMode.Interactive));
        Assert.AreEqual("text", BrowserReadingFormatWire.NameOf(BrowserReadingMode.Text));
        Assert.AreEqual("full", BrowserReadingFormatWire.NameOf(BrowserReadingMode.Full));
        Assert.AreEqual("none", BrowserReadingFormatWire.TruncationNameOf(BrowserReadingTruncation.None));
        Assert.AreEqual("output_budget", BrowserReadingFormatWire.TruncationNameOf(BrowserReadingTruncation.OutputBudget));
        Assert.AreEqual("incomplete_scan", BrowserReadingFormatWire.TruncationNameOf(BrowserReadingTruncation.IncompleteScan));

        foreach (var mode in Enum.GetValues<BrowserReadingMode>())
        {
            Assert.IsTrue(BrowserReadingFormatWire.TryParse(BrowserReadingFormatWire.NameOf(mode), out var parsed));
            Assert.AreEqual(mode, parsed);
        }

        Assert.IsFalse(BrowserReadingFormatWire.TryParse("brand_new_mode", out _));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => BrowserReadingFormatWire.NameOf((BrowserReadingMode)1234));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => BrowserReadingFormatWire.TruncationNameOf((BrowserReadingTruncation)1234));
    }
}
