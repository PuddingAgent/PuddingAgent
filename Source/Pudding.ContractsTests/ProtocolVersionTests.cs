using Pudding.Contracts;

namespace Pudding.ContractsTests;

public sealed class ProtocolVersionTests
{
    [Fact]
    public void CurrentVersion_IsTheOnlySupportedVersionToday()
    {
        // v2：能力通道携带类型化调用者身份（下一阶段实施方案 §3.2）。
        Assert.Equal(2, DesktopProtocolVersion.Current);
        Assert.Equal(2, DesktopProtocolVersion.Minimum);
        Assert.True(DesktopProtocolVersion.IsSupported(DesktopProtocolVersion.Current));
        Assert.False(DesktopProtocolVersion.IsSupported(1)); // v1 与本切片不兼容
        Assert.False(DesktopProtocolVersion.IsSupported(0));
        Assert.False(DesktopProtocolVersion.IsSupported(DesktopProtocolVersion.Current + 1));
    }

    [Fact]
    public void TryNegotiate_TakesIntersection()
    {
        Assert.True(DesktopProtocolVersion.TryNegotiate(2, 2, out var same));
        Assert.Equal(2, same);

        Assert.True(DesktopProtocolVersion.TryNegotiate(0, 99, out var widened));
        Assert.Equal(DesktopProtocolVersion.Current, widened);

        // 对端最低版本高于本端当前版本 ⇒ 谈不拢（不能"就地降级"或回退另一条通道）。
        Assert.False(DesktopProtocolVersion.TryNegotiate(1, 1, out _));   // v1 配对必须被拒（不回退旧通道）
    }

    [Fact]
    public void TryNegotiate_FailsWhenRangesDoNotOverlap()
    {
        Assert.False(DesktopProtocolVersion.TryNegotiate(5, 9, out var negotiated));
        Assert.Equal(0, negotiated);

        Assert.False(DesktopProtocolVersion.TryNegotiate(0, 0, out _));
    }
}
