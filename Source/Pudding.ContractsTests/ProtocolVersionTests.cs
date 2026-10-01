using Pudding.Contracts;

namespace Pudding.ContractsTests;

public sealed class ProtocolVersionTests
{
    [Fact]
    public void CurrentVersion_IsTheOnlySupportedVersionToday()
    {
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
    }

    [Fact]
    public void TryNegotiate_FailsWhenRangesDoNotOverlap()
    {
        Assert.False(DesktopProtocolVersion.TryNegotiate(5, 9, out var negotiated));
        Assert.Equal(0, negotiated);

        Assert.False(DesktopProtocolVersion.TryNegotiate(0, 0, out _));
    }
}
