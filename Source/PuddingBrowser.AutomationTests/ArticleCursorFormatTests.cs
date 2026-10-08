using Microsoft.VisualStudio.TestTools.UnitTesting;
using PuddingBrowser.Automation;

namespace PuddingBrowser.AutomationTests;

/// <summary>
/// 不透明游标（§6.1）：带表示标识，跨表示误用可被拒绝。
/// 解析必须 fail closed —— 畸形游标绝不能当成"从头开始"（那同样会静默跳段）。
/// </summary>
[TestClass]
public sealed class ArticleCursorTests
{
    [TestMethod]
    public void EncodeAndParseRoundTrip()
    {
        Assert.AreEqual("md:3", ArticleCursor.Encode(ArticleCursor.MarkdownKind, 3));
        Assert.AreEqual("json:5", ArticleCursor.Encode(ArticleCursor.CompactJsonKind, 5));
        Assert.AreEqual("lines:50", ArticleCursor.Encode(ArticleCursor.LinesKind, 50));

        Assert.IsTrue(ArticleCursor.TryParse("json:5", out var kind, out var index));
        Assert.AreEqual(ArticleCursor.CompactJsonKind, kind);
        Assert.AreEqual(5, index);
    }

    [TestMethod]
    public void MalformedCursorsAreRejectedInsteadOfTreatedAsZero()
    {
        foreach (var value in new[] { null, "", "   ", "3", "md", "md:", ":3", "md:-1", "md:1.5", "md:1:2",
            "yaml:3", "MD:3", "md: 3" })
        {
            Assert.IsFalse(ArticleCursor.TryParse(value, out _, out _), $"'{value}' 不应被解析成功");
        }
    }

    [TestMethod]
    public void KindAndFormatMapBothWays()
    {
        Assert.AreEqual(ArticleCursor.MarkdownKind, ArticleCursor.KindOfFormat("markdown"));
        Assert.AreEqual(ArticleCursor.CompactJsonKind, ArticleCursor.KindOfFormat("COMPACT_JSON"));
        Assert.IsNull(ArticleCursor.KindOfFormat(null));
        Assert.IsNull(ArticleCursor.KindOfFormat("yaml"));

        Assert.AreEqual("markdown", ArticleCursor.FormatOfKind(ArticleCursor.MarkdownKind));
        Assert.AreEqual("compact_json", ArticleCursor.FormatOfKind(ArticleCursor.CompactJsonKind));
    }

    [TestMethod]
    public void DistinctRepresentationsGetDistinctCursors()
    {
        // 页边界不同 ⇒ 同一索引在两种表示里不是"同一页"，游标必须能区分。
        Assert.AreNotEqual(
            ArticleCursor.Encode(ArticleCursor.MarkdownKind, 3),
            ArticleCursor.Encode(ArticleCursor.CompactJsonKind, 3));
    }

    [TestMethod]
    public void EncodeRejectsNonsense()
    {
        Assert.ThrowsExactly<ArgumentException>(() => ArticleCursor.Encode("", 0));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => ArticleCursor.Encode(ArticleCursor.MarkdownKind, -1));
    }
}
