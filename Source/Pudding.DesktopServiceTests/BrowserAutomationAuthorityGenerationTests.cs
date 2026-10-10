using Pudding.Contracts;
using Pudding.Contracts.Desktop;
using PuddingBrowser.Automation;

namespace DesktopServiceTests;

/// <summary>
/// 连接世代的可读来源（签发与准入都必须用它，不能各自记一份）：
/// 未握手时为 <see cref="ConnectionGeneration.None"/>，握手/换代后与推入值一致。
/// </summary>
public sealed class BrowserAutomationAuthorityGenerationTests
{
    [Fact]
    public void TheLiveGenerationIsReadableAndFollowsTheNotifiedHandshake()
    {
        var authority = new BrowserAutomationAuthority(
            new DesktopInstanceId("desk-1"),
            new DesktopProcessInstanceId("proc-1"),
            new TestTimeProvider());

        // 未握手：必须是 None（fail-closed 的起点，不得伪造一个"看起来活着"的世代）。
        Assert.Equal(ConnectionGeneration.None, authority.ConnectionGeneration);
        Assert.False(authority.ConnectionGeneration.IsLive);

        var generation = ConnectionGeneration.Require(9);
        authority.NotifyConnectionGeneration(generation);

        Assert.Equal(generation, authority.ConnectionGeneration);
        Assert.True(authority.ConnectionGeneration.IsLive);
    }
}