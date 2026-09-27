using PuddingChat;
using Xunit;

public class ReadingPositionTests
{
    [Fact] public void GrowingEarlierMessagePreservesVisibleMessage()
    {
        var anchor = ReadingPosition.Capture([new("first", 0, 200), new("second", 200, 400)], 240, 900);
        Assert.Equal(390, anchor.Restore([new("first", 0, 350), new("second", 350, 400)], 1050));
    }
    [Fact] public void LatestFollowsNewOutput() => Assert.Equal(1500, ReadingPosition.Capture([], 880, 900).Restore([], 1500));
    [Fact] public void MissingTrimmedAnchorClampsToAvailableRange() => Assert.Equal(100, new ReadingPosition("old", 20, 500, false).Restore([], 100));
}
