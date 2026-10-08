using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PuddingBrowser.Automation;

namespace PuddingBrowser.AutomationTests;

/// <summary>
/// 正文 markdown 组装（设计方案 §6.1 的 <c>mode=text</c>）。
/// G6 验收点在这里被逐条钉住：结构保真、**不丢段不重复**、块边界分页、超预算仍是合法输出。
/// </summary>
[TestClass]
public sealed class ArticleMarkdownFormatTests
{
    private static string Blocks(params string[] lines) => string.Join('\n', lines);

    [TestMethod]
    public void HeadingsKeepTheirLevel()
    {
        var result = ArticleMarkdown.Compose(Blocks(
            """{"k":"h1","t":"Title"}""",
            """{"k":"h3","t":"Sub"}"""));

        Assert.AreEqual("# Title\n\n### Sub", result.Text);
        Assert.AreEqual(2, result.ReturnedLines);
        Assert.IsFalse(result.Truncated);
    }

    [TestMethod]
    public void ListsKeepNestingByDepth()
    {
        var result = ArticleMarkdown.Compose(Blocks(
            """{"k":"li","t":"first","d":1}""",
            """{"k":"li","t":"nested","d":2}""",
            """{"k":"li","t":"third","d":1}"""));

        Assert.AreEqual("- first\n\n  - nested\n\n- third", result.Text);
    }

    [TestMethod]
    public void CodeBlocksPreserveWhitespaceAndUseALongerFenceWhenNeeded()
    {
        var result = ArticleMarkdown.Compose(Blocks(
            """{"k":"pre","t":"line1\n    indented\n"}"""));

        Assert.AreEqual("```\nline1\n    indented\n```", result.Text);

        var nested = ArticleMarkdown.Compose(Blocks("""{"k":"pre","t":"```\ninner\n```"}"""));
        StringAssert.StartsWith(nested.Text, "````");
    }

    [TestMethod]
    public void TablesBecomeValidMarkdown()
    {
        var result = ArticleMarkdown.Compose(Blocks(
            """{"k":"table","r":[["h1","h2"],["a|b","c"]]}"""));

        Assert.AreEqual("| h1 | h2 |\n| --- | --- |\n| a\\|b | c |", result.Text);
    }

    [TestMethod]
    public void LinksKeepTheirDestination()
    {
        // 驱动侧已把行内链接写成 [text](href)；这里钉住独立链接块与无文本链接。
        var result = ArticleMarkdown.Compose(Blocks(
            """{"k":"a","t":"Docs","h":"https://example.test/docs"}""",
            """{"k":"a","h":"https://example.test/raw"}"""));

        Assert.AreEqual("[Docs](https://example.test/docs)\n\n<https://example.test/raw>", result.Text);
    }

    [TestMethod]
    public void PagingStopsAtBlockBoundaries_AndContinuesWithoutGapsOrRepeats()
    {
        // 预算刻意只够前几块：分页必须落在块边界，续读既不能重复也不能跳过。
        var lines = Enumerable.Range(1, 8).Select(index => $$"""{"k":"p","t":"paragraph-{{index}}-xx"}""").ToArray();
        var all = Blocks(lines);

        var first = ArticleMarkdown.Compose(all, maxBytes: 96);
        Assert.IsTrue(first.Truncated);
        Assert.AreEqual(BrowserReadingTruncation.OutputBudget, first.Truncation);
        Assert.IsNotNull(first.NextCursor);

        var second = ArticleMarkdown.Compose(all, maxBytes: 5_000, cursor: first.NextCursor!.Value);
        Assert.IsFalse(second.Truncated);

        // 分页必须**真的提前停**：否则"第一页塞下全部"也会让下面的覆盖断言侥幸通过。
        Assert.IsTrue(first.ReturnedLines < 8, $"第一页不该塞下全部 8 段（实际 {first.ReturnedLines}）");
        Assert.IsTrue(second.ReturnedLines > 0, "第二页必须真的还有内容");
        Assert.IsTrue(first.Bytes <= 96 + 200, "第一页应受预算约束（单块整块给出的容差除外）");

        // 覆盖 100%：两段拼起来恰好是全部 8 段，且没有重复。
        var combined = (first.Text ?? string.Empty) + "\n\n" + (second.Text ?? string.Empty);
        for (var index = 1; index <= 8; index++)
        {
            var marker = $"paragraph-{index}-xx";
            Assert.AreEqual(1, combined.Split(marker).Length - 1, $"第 {index} 段应恰好出现一次");
        }

        Assert.AreEqual(8, first.ReturnedLines + second.ReturnedLines);
    }

    [TestMethod]
    public void OversizedSingleBlock_IsEmittedWholeInsteadOfSilentlyCut()
    {
        // 单块超预算：宁可整块给出（截断声明为 output_budget），也**不在块内切**——
        // 块内切分会造成续读重复或丢半段，而游标当前只能表达"从第几块开始"。
        var big = new string('x', 500);
        var result = ArticleMarkdown.Compose(Blocks($$"""{"k":"p","t":"{{big}}"}"""), maxBytes: 64);

        StringAssert.Contains(result.Text, big);
        Assert.IsTrue(result.Truncated);
        Assert.AreEqual(BrowserReadingTruncation.OutputBudget, result.Truncation);
    }

    [TestMethod]
    public void MalformedOrEmptyBlocks_AreCountedNotSilentlyDropped()
    {
        var result = ArticleMarkdown.Compose(Blocks(
            """{"k":"p","t":"kept"}""",
            "not json at all",
            """{"k":"p"}""",
            """{"t":"no kind"}"""));

        Assert.AreEqual("kept", result.Text);
        Assert.AreEqual(3, result.SkippedBlocks);          // 非法行 / 空段 / 无 kind
        Assert.IsTrue(result.Truncated);                    // 有丢弃就必须如实标注
        Assert.AreEqual(BrowserReadingTruncation.IncompleteScan, result.Truncation);
    }

    [TestMethod]
    public void UnknownKindWithText_DegradesToParagraph_SoContentIsNotLost()
    {
        var result = ArticleMarkdown.Compose(Blocks("""{"k":"figure","t":"caption text"}"""));

        Assert.AreEqual("caption text", result.Text);
        Assert.AreEqual(0, result.SkippedBlocks);
    }

    [TestMethod]
    public void QuoteAndRuleRenderAsMarkdown()
    {
        var result = ArticleMarkdown.Compose(Blocks(
            """{"k":"quote","t":"line1\nline2"}""",
            """{"k":"hr"}"""));

        Assert.AreEqual("> line1\n> line2\n\n---", result.Text);
    }

    [TestMethod]
    public void EmptyInput_ProducesEmptyResult()
    {
        var result = ArticleMarkdown.Compose(null);

        Assert.IsNull(result.Text);
        Assert.IsFalse(result.Truncated);
        Assert.AreEqual(0, result.ReturnedLines);
    }

    [TestMethod]
    public void BudgetIsMeasuredInUtf8Bytes()
    {
        // 中文一个字 3 字节：检查预算口径不是"字符数冒充字节数"。
        var chinese = new string('正', 40);            // 120 字节
        var result = ArticleMarkdown.Compose(Blocks($$"""{"k":"p","t":"{{chinese}}"}"""), maxBytes: 4_096);

        Assert.AreEqual(Encoding.UTF8.GetByteCount(result.Text!), result.Bytes);
        Assert.IsTrue(result.Bytes >= 120);
    }

    [TestMethod]
    public void Compose_RejectsNonsenseBudgets()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => ArticleMarkdown.Compose("", maxBytes: 1));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => ArticleMarkdown.Compose("", cursor: -1));
    }
}
