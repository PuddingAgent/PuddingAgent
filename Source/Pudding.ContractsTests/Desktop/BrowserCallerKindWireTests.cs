using Pudding.Contracts.Desktop;

namespace Pudding.ContractsTests;

/// <summary>
/// 调用者类别的线缆编码（方案 §3.2）：Core 与 Desktop 共用一份真源；
/// 用字符串而非 proto enum，因为 proto3 的默认值会让"没填"与"填了第一个枚举值"无法区分。
/// </summary>
public sealed class BrowserCallerKindWireTests
{
    [Theory]
    [InlineData(BrowserCallerKind.Agent, "agent")]
    [InlineData(BrowserCallerKind.SubAgent, "sub_agent")]
    [InlineData(BrowserCallerKind.User, "user")]
    public void KindsRoundTripThroughTheWireName(BrowserCallerKind kind, string wire)
    {
        Assert.Equal(wire, BrowserCallerKindWire.NameOf(kind));
        Assert.True(BrowserCallerKindWire.TryParse(wire, out var parsed));
        Assert.Equal(kind, parsed);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Agent")]
    [InlineData("subagent")]
    [InlineData("0")]
    [InlineData("unknown")]
    public void UnknownWireValuesAreRefusedInsteadOfDefaultingToAKind(string? wire)
    {
        // 空/大小写不符/未知取值都**不得**被当成某个默认类别 —— 身份不能靠默认值补出来。
        Assert.False(BrowserCallerKindWire.TryParse(wire, out _));
    }

    [Fact]
    public void AnUnregisteredEnumValueCannotBeFormatted()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => BrowserCallerKindWire.NameOf((BrowserCallerKind)42));
    }
}
