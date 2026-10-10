using Pudding.Contracts;

namespace Pudding.ContractsTests;

public sealed class ProtocolVersionTests
{
    [Fact]
    public void CurrentVersion_IsTheOnlySupportedVersionToday()
    {
        // v2：能力通道携带类型化调用者身份（下一阶段实施方案 §3.2）。
        Assert.Equal(1, DesktopProtocolVersion.Current);
        Assert.Equal(1, DesktopProtocolVersion.Minimum);
        Assert.True(DesktopProtocolVersion.IsSupported(DesktopProtocolVersion.Current));
        Assert.False(DesktopProtocolVersion.IsSupported(0));
        Assert.False(DesktopProtocolVersion.IsSupported(DesktopProtocolVersion.Current + 1));
    }

    [Fact]
    public void TryNegotiate_TakesIntersection()
    {
        Assert.True(DesktopProtocolVersion.TryNegotiate(1, 1, out var same));
        Assert.Equal(1, same);

        Assert.True(DesktopProtocolVersion.TryNegotiate(0, 99, out var widened));
        Assert.Equal(DesktopProtocolVersion.Current, widened);

        // 对端最低版本高于本端当前版本 ⇒ 谈不拢（不能"就地降级"或回退另一条通道）。
        Assert.False(DesktopProtocolVersion.TryNegotiate(2, 3, out _));
    }

    [Fact]
    public void TryNegotiate_FailsWhenRangesDoNotOverlap()
    {
        Assert.False(DesktopProtocolVersion.TryNegotiate(5, 9, out var negotiated));
        Assert.Equal(0, negotiated);

        Assert.False(DesktopProtocolVersion.TryNegotiate(0, 0, out _));
    }
}
