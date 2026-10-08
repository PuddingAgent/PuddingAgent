using System.Text;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PuddingBrowser.Automation;

namespace PuddingBrowser.AutomationTests;

/// <summary>
/// 正文的 compact_json 表示（§151 的 <c>format</c>）。
/// 与 markdown 共用分页语义，但输出是给程序读的；**截断后仍必须是合法 JSON**。
/// </summary>
[TestClass]
public sealed class ArticleCompactJsonTests
{
    private static string Blocks(params string[] lines) => string.Join('\n', lines);

    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

    [TestMethod]
    public void BlocksKeepKindTextDepthHrefAndRows()
    {
        var result = ArticleCompactJson.Compose(Blocks(
            """{"k":"h2","t":"Title"}""",
            """{"k":"li","t":"Item","d":2}""",
            """{"k":"a","t":"Docs","h":"https://example.test/"}""",
            """{"k":"table","r":[["h1","h2"],["c1","c2"]]}"""));

        var root = Parse(result.Text!);
        var blocks = root.GetProperty("blocks");
        Assert.AreEqual(4, blocks.GetArrayLength());

        Assert.AreEqual("h2", blocks[0].GetProperty("k").GetString());
        Assert.AreEqual("Title", blocks[0].GetProperty("t").GetString());

        // 列表层级只对 li 有意义，其他块不写 d（少一个字段就少一份歧义）。
        Assert.AreEqual(2, blocks[1].GetProperty("d").GetInt32());
        Assert.IsFalse(blocks[0].TryGetProperty("d", out _));

        Assert.AreEqual("https://example.test/", blocks[2].GetProperty("h").GetString());

        var rows = blocks[3].GetProperty("r");
        Assert.AreEqual(2, rows.GetArrayLength());
        Assert.AreEqual("c2", rows[1][1].GetString());

        Assert.IsFalse(root.GetProperty("truncated").GetBoolean());
        Assert.AreEqual(JsonValueKind.Null, root.GetProperty("nextCursor").ValueKind);
    }

    [TestMethod]
    public void TruncatedOutputIsStillValidJson_AndSaysSoExplicitly()
    {
        // 边写边截会产出半截 JSON；这里必须"先选块再序列化"，因此括号永远成对。
        var lines = Enumerable.Range(1, 8).Select(index => $$"""{"k":"p","t":"paragraph-{{index}}-xxxxxxxx"}""").ToArray();
        var result = ArticleCompactJson.Compose(Blocks(lines), maxBytes: 96);

        Assert.IsTrue(result.Truncated);
        var root = Parse(result.Text!);                       // 能解析 ⇒ 合法 JSON
        Assert.IsTrue(root.GetProperty("truncated").GetBoolean());
        Assert.IsTrue(root.GetProperty("nextCursor").GetInt32() > 0);
        Assert.IsTrue(root.GetProperty("blocks").GetArrayLength() < 8);
    }

    [TestMethod]
    public void EachRepresentationPagesByItsOwnBytes_AndStillCoversEverything()
    {
        // 两种形状的块成本不同（JSON 有结构/转义/包装开销），因此**页边界本来就该不同**：
        // 强行共用 markdown 的分页会让 JSON 超出调用方请求的预算。
        // 真正要保的是：各自的输出都不超预算（单块超预算除外），且各自续读能覆盖全部、不重复。
        var lines = Enumerable.Range(1, 8).Select(index => $$"""{"k":"p","t":"paragraph-{{index}}-xxxxxxxx"}""").ToArray();
        var all = Blocks(lines);

        var markdown = ArticleMarkdown.Compose(all, maxBytes: 96);
        var compact = ArticleCompactJson.Compose(all, maxBytes: 96);

        Assert.IsTrue(markdown.Bytes <= 96, $"markdown 超预算：{markdown.Bytes}");
        Assert.IsTrue(compact.Bytes <= 96, $"compact_json 超预算：{compact.Bytes}");

        // 各自续读：拼接后每段**恰好出现一次**（不重复、不丢段）。
        var second = ArticleCompactJson.Compose(all, maxBytes: 5_000, cursor: compact.NextCursor!.Value);
        var combined = compact.Text! + second.Text!;
        for (var index = 1; index <= 8; index++)
        {
            Assert.AreEqual(1, combined.Split($"paragraph-{index}-xxxxxxxx").Length - 1, $"第 {index} 段应恰好出现一次");
        }

        Assert.AreEqual(8, compact.ReturnedLines + second.ReturnedLines);
    }

    [TestMethod]
    public void SingleBlockOverBudget_IsEmittedWholeAndFlagged()
    {
        // 与 markdown 同一条规则：宁可整块给出（并如实标注），也不在块内切。
        var big = new string('x', 400);
        var result = ArticleCompactJson.Compose(Blocks($$"""{"k":"p","t":"{{big}}"}"""), maxBytes: 64);

        Assert.AreEqual(1, result.ReturnedLines);
        Assert.IsTrue(result.Bytes > 64);
        Assert.IsTrue(result.Truncated);
        Assert.AreEqual(BrowserReadingTruncation.OutputBudget, result.Truncation);
        Parse(result.Text!);   // 仍然是合法 JSON
    }

    [TestMethod]
    public void BytesAreMeasuredInUtf8_NotCharacters()
    {
        var chinese = new string('正', 40);
        var result = ArticleCompactJson.Compose(Blocks($$"""{"k":"p","t":"{{chinese}}"}"""));

        Assert.AreEqual(Encoding.UTF8.GetByteCount(result.Text!), result.Bytes);
        Assert.IsTrue(result.Bytes > 120);
    }

    [TestMethod]
    public void MalformedBlocksAreCountedNotSilentlyDropped()
    {
        var result = ArticleCompactJson.Compose(Blocks(
            """{"k":"p","t":"kept"}""",
            "not json at all",
            """{"k":"p"}"""));

        Assert.AreEqual(1, result.ReturnedLines);
        Assert.AreEqual(2, result.SkippedBlocks);
        Assert.IsTrue(result.Truncated);
        Assert.AreEqual(BrowserReadingTruncation.IncompleteScan, result.Truncation);
    }

    [TestMethod]
    public void EmptyInputStaysEmptyRatherThanEmittingAFakeObject()
    {
        var result = ArticleCompactJson.Compose(null);

        Assert.IsNull(result.Text);
        Assert.AreEqual(0, result.ReturnedLines);
    }

    [TestMethod]
    public void RejectsNonsenseBudgets()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => ArticleCompactJson.Compose("", maxBytes: 1));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => ArticleCompactJson.Compose("", cursor: -1));
    }
}
