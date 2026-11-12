using PuddingAgent.Services;
using PuddingDingTalk;

namespace PuddingHost.Tests;

/// <summary>
/// An unbound caller in a private DingTalk chat must learn which identifiers the administrator
/// needs to create the channel binding. The notice is the only place the platform echoes an
/// external user id, so it is asserted here rather than through a full turn round-trip.
/// </summary>
public sealed class DingTalkUnboundNoticeTests
{
    private static GroupSource Private(string corp, string user) =>
        new(corp, "private-conversation", user, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow) { Group = false };

    [Fact]
    public void UnboundPrivateNoticeEchoesTheCallersOwnIdentifiers()
    {
        var text = DingTalkConversationIngress.UnboundPrivateNotice(Private("corp-1", "staff-42"));
        Assert.Contains("staff-42", text);
        Assert.Contains("corp-1", text);
        Assert.Contains("绑定", text);
    }

    [Fact]
    public void UnboundPrivateNoticeOfAnotherCallerCarriesNoSharedSecret()
    {
        // The notice is built from the source alone: two different callers never see each other's ids.
        var first = DingTalkConversationIngress.UnboundPrivateNotice(Private("corp-1", "staff-42"));
        var second = DingTalkConversationIngress.UnboundPrivateNotice(Private("corp-1", "staff-99"));
        Assert.DoesNotContain("staff-99", first);
        Assert.DoesNotContain("staff-42", second);
    }
}
