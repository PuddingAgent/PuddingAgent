using PuddingChat;

namespace PuddingChatTests;

public class FlowWindowTests
{
    private static FlowBlock[] Blocks(int count) => Enumerable.Range(0, count)
        .Select(i => new FlowBlock($"b{i}", "tool", "", "done")).ToArray();
    [Fact]
    public void InitialWindowAndExplicitPagesReachAllRecords()
    {
        var window = new FlowWindow(); var blocks = Blocks(100);
        Assert.Equal(60, window.Start(blocks));
        window.RevealEarlier(blocks); Assert.Equal(36, window.Start(blocks));
        window.RevealEarlier(blocks); Assert.Equal(12, window.Start(blocks));
        window.RevealEarlier(blocks); Assert.Equal(0, window.Start(blocks));
    }
    [Fact]
    public void StreamingKeepsExplicitlyRevealedAnchorAndResetReturnsToTail()
    {
        var window = new FlowWindow(); window.RevealEarlier(Blocks(100));
        Assert.Equal(36, window.Start(Blocks(120)));
        window.Reset(); Assert.Equal(80, window.Start(Blocks(120)));
    }
    [Fact]
    public void EmptyShortAndMissingAnchorAreBounded()
    {
        var window = new FlowWindow(); window.RevealEarlier([]);
        Assert.Equal(0, window.Start([])); Assert.Equal(0, window.Start(Blocks(12)));
        window.RevealEarlier(Blocks(100)); Assert.Equal(0, window.Start(Blocks(10)));
    }
}
