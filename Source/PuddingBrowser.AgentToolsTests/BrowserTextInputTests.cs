using PuddingBrowser.Abstractions;

namespace PuddingBrowser.AgentToolsTests;

/// <summary>
/// 输入形态判定：哪些文本没有物理键位、必须走「剪贴板 + Ctrl+V」。
///
/// 这条判据是插入路径的岔路口，判错会直接导致「页面有字、框架没字」：
/// 2026-10-26 抖音评论框（Draft.js）实测，只带 text 的 keyDown 能让 DOM 出现文字，
/// 但 EditorState 仍为空 —— 发送按钮不出现、回车也不提交。
/// </summary>
public sealed class BrowserTextInputTests
{
    [Theory]
    [InlineData("hello world 123 !?")]
    [InlineData("")]
    public void AsciiPrintable_DoesNotNeedClipboard(string text)
    {
        Assert.False(BrowserTextInput.RequiresClipboard(text));
    }

    [Fact]
    public void NullDoesNotNeedClipboard()
    {
        Assert.False(BrowserTextInput.RequiresClipboard(null));
    }

    [Theory]
    [InlineData("大肥鱼")]
    [InlineData("abc白饭")]
    [InlineData("emoji 🙂")]
    public void CjkAndEmoji_NeedClipboard(string text)
    {
        Assert.True(BrowserTextInput.RequiresClipboard(text));
    }

    /// <summary>
    /// 边界必须**包含**可打印区间的两端（0x20 空格、0x7E ~）：
    /// 多算一个字符就会白白走剪贴板，少算一个就会丢字。
    /// </summary>
    [Fact]
    public void PrintableBoundariesAreInclusive()
    {
        Assert.False(BrowserTextInput.RequiresClipboard(" "));
        Assert.False(BrowserTextInput.RequiresClipboard("~"));
        Assert.True(BrowserTextInput.RequiresClipboard("\t"));
        Assert.True(BrowserTextInput.RequiresClipboard("\u007F"));
    }
}
