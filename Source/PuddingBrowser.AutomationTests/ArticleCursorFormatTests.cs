using Microsoft.VisualStudio.TestTools.UnitTesting;
using PuddingBrowser.Automation;

namespace PuddingBrowser.AutomationTests;

/// <summary>
/// 不透明游标（§6.1）：带**表示标识**与**页面/版本身份**，跨表示/跨页面/跨版本误用都可被拒绝。
/// 解析必须 fail closed —— 畸形游标绝不能当成"从头开始"（那同样会静默错位）。
/// </summary>
[TestClass]
public sealed class ArticleCursorTests
{
    private const string PageIdentity = "p0123abcd-v7";

    [TestMethod]
    public void EncodeAndParseRoundTrip()
    {
        Assert.AreEqual("md:3@p0123abcd-v7", ArticleCursor.Encode(ArticleCursor.MarkdownKind, 3, PageIdentity));
        Assert.AreEqual(
            "json:5@p0123abcd-v7", ArticleCursor.Encode(ArticleCursor.CompactJsonKind, 5, PageIdentity));
        Assert.AreEqual(
            "lines:50@p0123abcd-v7", ArticleCursor.Encode(ArticleCursor.LinesKind, 50, PageIdentity));

        Assert.IsTrue(ArticleCursor.TryParse(
            "json:5@p0123abcd-v7", out var kind, out var index, out var identity));
        Assert.AreEqual(ArticleCursor.CompactJsonKind, kind);
        Assert.AreEqual(5, index);
        Assert.AreEqual(PageIdentity, identity);
    }

    [TestMethod]
    public void PageIdentityCarriesAShortHashNotTheRawPageId()
    {
        // 游标要对调用方保持不透明：页面标识只取短哈希（8 位），不是完整页面 id。
        var identity = ArticleCursor.PageIdentityOf("page-with-a-long-identifier", pageVersion: 12);

        Assert.StartsWith("p", identity, StringComparison.Ordinal);
        Assert.EndsWith("-v12", identity, StringComparison.Ordinal);
        Assert.IsFalse(identity.Contains("page-with-a-long-identifier", StringComparison.Ordinal));

        // 同样的输入必须给出同样的身份（否则游标无法复核）。
        Assert.AreEqual(identity, ArticleCursor.PageIdentityOf("page-with-a-long-identifier", 12));

        // 页面或版本不同 ⇒ 身份不同。
        Assert.AreNotEqual(identity, ArticleCursor.PageIdentityOf("page-with-a-long-identifier", 13));
        Assert.AreNotEqual(identity, ArticleCursor.PageIdentityOf("another-page", 12));
    }

    [TestMethod]
    public void MatchesPageOnlyForTheSamePageAndVersion()
    {
        var identity = ArticleCursor.PageIdentityOf("p-1", 7);

        Assert.IsTrue(ArticleCursor.MatchesPage(identity, "p-1", 7));
        Assert.IsFalse(ArticleCursor.MatchesPage(identity, "p-1", 8), "版本变了就不能续读");
        Assert.IsFalse(ArticleCursor.MatchesPage(identity, "p-2", 7), "换了页面就不能续读");
    }

    [TestMethod]
    public void MalformedCursorsAreRejectedInsteadOfTreatedAsZero()
    {
        foreach (var value in new[] { null, "", "   ", "3", "md", "md:", ":3", "md:-1", "md:1.5", "md:1:2",
            "yaml:3@p0123abcd-v7", "MD:3@p0123abcd-v7", "md: 3@p0123abcd-v7",
            "md:3", "md:3@", "md:3@p0123abcd", "md:3@p0123abcd-v", "md:3@x0123abcd-v7",
            "md:3@p0123abc-v7", "md:3@p0123abcde-v7", "md:3@p0123abcd-vx", "md:3@p0123abcd-v-1",
            "md:3@p0123abcd-v7-extra" })
        {
            Assert.IsFalse(
                ArticleCursor.TryParse(value, out _, out _, out _), $"'{value}' 不应被解析成功");
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
            ArticleCursor.Encode(ArticleCursor.MarkdownKind, 3, PageIdentity),
            ArticleCursor.Encode(ArticleCursor.CompactJsonKind, 3, PageIdentity));
    }

    [TestMethod]
    public void EncodeRejectsNonsense()
    {
        Assert.ThrowsExactly<ArgumentException>(() => ArticleCursor.Encode("", 0, PageIdentity));
        Assert.ThrowsExactly<ArgumentException>(() => ArticleCursor.Encode(ArticleCursor.MarkdownKind, 0, ""));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => ArticleCursor.Encode(ArticleCursor.MarkdownKind, -1, PageIdentity));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => ArticleCursor.PageIdentityOf("p-1", -1));
    }
}
