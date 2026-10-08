using Pudding.Contracts.Desktop;

namespace Pudding.ContractsTests;

/// <summary>
/// 分页游标（设计方案 §6.2）：对模型不透明、绑定「页面版本 + 查询指纹 + 偏移」。
/// 过期（内容/查询变了）与非法（格式坏了）必须分开 —— 前者要求重新读取，后者是参数错误。
/// </summary>
public sealed class LocateCursorTests
{
    private static readonly DesktopLocator Css = new(DesktopLocatorKind.Css, ".item");

    [Fact]
    public void Cursor_RoundTripsThroughTheOpaqueWireFormat()
    {
        var cursor = BrowserLocateCursor.Create(pageVersion: 7, fingerprint: "a1b2c3d4", offset: 10);

        var wire = cursor.Encode();
        Assert.Equal("c1-7-a1b2c3d4-10", wire);

        Assert.True(BrowserLocateCursor.TryParse(wire, out var parsed));
        Assert.Equal(7, parsed.PageVersion);
        Assert.Equal("a1b2c3d4", parsed.Fingerprint);
        Assert.Equal(10, parsed.Offset);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("c1-7-a1b2c3d4")]            // 少一段
    [InlineData("c1-7-a1b2c3d4-10-extra")]   // 多一段
    [InlineData("c2-7-a1b2c3d4-10")]         // 未知方案版本
    [InlineData("c1-0-a1b2c3d4-10")]         // 版本必须为正
    [InlineData("c1-7-a1b2c3d4--1")]         // 偏移不能为负
    [InlineData("c1-7-nothex!!-10")]         // 指纹必须是 8 位十六进制
    [InlineData("c1-7-a1b2c3d-10")]          // 指纹长度不对
    [InlineData("c1-abc-a1b2c3d4-10")]       // 版本不是数字
    [InlineData("c1-7-a1b2c3d4-x")]          // 偏移不是数字
    public void MalformedCursor_IsRejected_InsteadOfBeingGuessed(string? wire)
    {
        Assert.False(BrowserLocateCursor.TryParse(wire, out _));
    }

    [Fact]
    public void Cursor_MatchesOnlyTheSameVersionAndTheSameQuery()
    {
        var cursor = BrowserLocateCursor.Create(7, "a1b2c3d4", 10);

        Assert.True(cursor.Matches(pageVersion: 7, fingerprint: "a1b2c3d4"));

        // 页面变了 ⇒ 过期（不能把旧游标套在新内容上）。
        Assert.False(cursor.Matches(pageVersion: 8, fingerprint: "a1b2c3d4"));

        // 换了查询条件 ⇒ 同样不适用。
        Assert.False(cursor.Matches(pageVersion: 7, fingerprint: "ffffffff"));
    }

    [Fact]
    public void Fingerprint_IsStableAcrossProcesses_AndCoversTheLocator()
    {
        // 不能用 string.GetHashCode（按进程加盐）：同一输入必须永远得到同一指纹。
        var first = BrowserLocateCursor.FingerprintOf(Css);
        var second = BrowserLocateCursor.FingerprintOf(new DesktopLocator(DesktopLocatorKind.Css, ".item"));

        Assert.Equal(first, second);
        Assert.Equal(8, first.Length);
        Assert.True(first.All(Uri.IsHexDigit));

        // 任一影响匹配集合的输入变化都必须改变指纹。
        Assert.NotEqual(first, BrowserLocateCursor.FingerprintOf(new DesktopLocator(DesktopLocatorKind.Css, ".other")));
        Assert.NotEqual(first, BrowserLocateCursor.FingerprintOf(new DesktopLocator(DesktopLocatorKind.Role, ".item")));
        Assert.NotEqual(first, BrowserLocateCursor.FingerprintOf(new DesktopLocator(DesktopLocatorKind.Css, ".item", exact: true)));
        Assert.NotEqual(first, BrowserLocateCursor.FingerprintOf(Css, includeHidden: true, inViewportOnly: false));
        Assert.NotEqual(first, BrowserLocateCursor.FingerprintOf(Css, includeHidden: false, inViewportOnly: true));
    }

    [Fact]
    public void Fingerprint_IgnoresPagingItself()
    {
        // 分页不改变"这是同一组结果"这一事实：limit/cursor 不参与指纹。
        var cursor = BrowserLocateCursor.Create(3, BrowserLocateCursor.FingerprintOf(Css), 5);
        Assert.Equal(BrowserLocateCursor.FingerprintOf(Css), cursor.Fingerprint);
    }

    [Fact]
    public void Create_RejectsNonsenseInputs()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => BrowserLocateCursor.Create(0, "a1b2c3d4", 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => BrowserLocateCursor.Create(1, "a1b2c3d4", -1));
        Assert.Throws<ArgumentException>(() => BrowserLocateCursor.Create(1, "  ", 0));
    }
}
