using PuddingChat;
namespace PuddingChatTests;

public class TextPageWindowTests
{
    [Fact]
    public void PagesReconstructLargeSourceWithoutSplittingUnicodeOrCrLf()
    {
        var window = new TextPageWindow();
        var source = new string('字', TextPageWindow.PageSize - 1) + "😀"
            + new string('a', TextPageWindow.PageSize - 1) + "\r\nend";
        window.Update(source); var pages = new List<string>();
        for (var i = 0; i < window.Count; i++)
        {
            window.Move(i); pages.Add(window.Text);
            Assert.InRange(window.Text.Length, 1, TextPageWindow.PageSize + 1);
            Assert.False(char.IsLowSurrogate(window.Text[0]));
            Assert.False(char.IsHighSurrogate(window.Text[^1]));
            Assert.False(window.Text.EndsWith('\r'));
        }
        Assert.Equal(source, string.Concat(pages));
    }
    [Fact]
    public void StreamingPreservesReadingPageAndReplacementClampsIt()
    {
        var window = new TextPageWindow(); var initial = new string('a', TextPageWindow.PageSize * 4);
        window.Update(initial); window.Move(1); var reading = window.Text;
        window.Update(initial + new string('b', TextPageWindow.PageSize * 2));
        Assert.Equal(1, window.Page); Assert.Equal(reading, window.Text); Assert.Equal(6, window.Count);
        window.Update("short"); Assert.Equal(0, window.Page); Assert.Equal("short", window.Text);
        window.Update(""); window.Move(int.MaxValue); Assert.Equal("", window.Text); Assert.Equal(1, window.Count);
    }
}
