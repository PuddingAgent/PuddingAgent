using Pudding.CapabilityBroker.AspNetCore;
using Pudding.Contracts.Desktop;

namespace Pudding.CapabilityBroker.AspNetCoreTests;

/// <summary>
/// Core→Desktop 之间唯一的字符串契约：就绪信号里的端点描述。
///
/// 这批测试存在的理由是一次真实缺陷：Core 曾把它打成 <c>kind:address|v1|core-x</c>
/// （版本带 <c>v</c> 前缀），而 Desktop 侧的严格解析器要求整数，于是
/// <b>Enabled=true 时通道永远不启动</b>，且只在打开开关后才暴露。
/// 因此这里钉住三件事：格式可解析、版本是整数、描述不含凭据。
/// </summary>
public class CapabilityChannelReadySignalTests
{
    [Fact]
    public void Describe_NamedPipe_RoundTripsThroughStrictParser()
    {
        var endpoint = DesktopCapabilityEndpoint.NamedPipe("pudding-capability-abc", 1, "core-7");

        var text = CapabilityChannelReadySignal.Describe(endpoint);

        Assert.Equal("named-pipe:pudding-capability-abc|1|core-7", text);
        Assert.True(DesktopCapabilityEndpoint.TryParse(text, out var parsed));
        Assert.Equal(endpoint, parsed);
    }

    [Fact]
    public void Describe_Loopback_RoundTripsThroughStrictParser()
    {
        var endpoint = DesktopCapabilityEndpoint.LoopbackHttp2(new Uri("http://127.0.0.1:5099"), 1, null);

        var text = CapabilityChannelReadySignal.Describe(endpoint);

        Assert.Equal("loopback-h2c:http://127.0.0.1:5099|1|", text);
        Assert.True(DesktopCapabilityEndpoint.TryParse(text, out var parsed));
        Assert.Equal(DesktopCapabilityEndpointKind.LoopbackHttp2, parsed!.Kind);
    }

    [Fact]
    public void Describe_ProtocolVersion_SegmentIsInteger_NotDecorated()
    {
        var endpoint = DesktopCapabilityEndpoint.NamedPipe("pipe-a", 1, "core-1");

        var versionSegment = CapabilityChannelReadySignal.Describe(endpoint).Split('|')[1];

        // 这就是那次缺陷的回归守卫：任何 v/前缀/空格的「美化」都会让 Desktop 侧解析失败。
        Assert.Equal("1", versionSegment);
        Assert.True(int.TryParse(versionSegment, out _));
    }

    [Fact]
    public void Describe_VPrefixedDescription_IsRejectedByTheSameStrictParser()
    {
        // 反例固定住「为什么必须这么写」：旧格式无法被对端的解析器接受。
        Assert.False(DesktopCapabilityEndpoint.TryParse("named-pipe:pipe-a|v1|core-1", out _));
    }

    [Fact]
    public void Describe_LogLine_ContainsExactlyTheSameDescription()
    {
        var endpoint = DesktopCapabilityEndpoint.NamedPipe("pipe-b", 1, "core-2");

        var description = CapabilityChannelReadySignal.Describe(endpoint);
        var logLine = CapabilityChannelReadySignal.DescribeLogLine(endpoint);

        // 日志与就绪信号共用同一个描述，避免「两套格式各自漂移」。
        Assert.Contains(description, logLine, StringComparison.Ordinal);
    }

    [Fact]
    public void Describe_Description_CarriesNoCredentials()
    {
        var endpoint = DesktopCapabilityEndpoint.NamedPipe("pipe-c", 1, "core-3");

        var text = CapabilityChannelReadySignal.Describe(endpoint);

        // 结构性保证：描述里没有凭据字段，也不允许出现令牌形态的片段。
        Assert.DoesNotContain("token", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("=", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Describe_NullEndpoint_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => CapabilityChannelReadySignal.Describe(null!));
    }

    [Fact]
    public void ReadySignalFieldName_MatchesDesktopParserExpectation()
    {
        // 对端（PuddingDesktop.Core.CoreReadyMessageParser）按大小写不敏感读取 JSON 字段，
        // 名字必须是这一个；改名会让通道静默失效（字段读不到 ⇒ 预检判为「描述缺失」）。
        Assert.Equal("capabilityEndpoint", CapabilityChannelReadySignal.FieldName);
    }
}
