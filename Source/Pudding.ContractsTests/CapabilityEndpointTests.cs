using System.Reflection;
using Pudding.Contracts.Desktop;

namespace Pudding.ContractsTests;

/// <summary>
/// 端点描述是「启动就绪协议」的一部分：不含凭据、地址形态受约束、字符串形式严格解析。
/// </summary>
public sealed class CapabilityEndpointTests
{
    [Fact]
    public void NamedPipe_ValidatesTheName()
    {
        var endpoint = DesktopCapabilityEndpoint.NamedPipe("pudding-capability-0123456789abcdef", 1, "core-1");

        Assert.Equal(DesktopCapabilityEndpointKind.NamedPipe, endpoint.Kind);
        Assert.Equal("pudding-capability-0123456789abcdef", endpoint.Address);
        Assert.Equal("core-1", endpoint.ServerInstanceId);

        Assert.Throws<ArgumentException>(() => DesktopCapabilityEndpoint.NamedPipe(string.Empty));
        Assert.Throws<ArgumentException>(() => DesktopCapabilityEndpoint.NamedPipe(@"\\.\pipe\pudding"));
        Assert.Throws<ArgumentException>(() => DesktopCapabilityEndpoint.NamedPipe("pudding/other"));
        Assert.Throws<ArgumentException>(() => DesktopCapabilityEndpoint.NamedPipe("pudding pipe"));
        Assert.Throws<ArgumentException>(
            () => DesktopCapabilityEndpoint.NamedPipe(new string('a', DesktopCapabilityEndpoint.MaxPipeNameLength + 1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => DesktopCapabilityEndpoint.NamedPipe("pudding", 0));
    }

    [Fact]
    public void LoopbackRequiresPlaintextLoopbackWithoutCredentials()
    {
        var endpoint = DesktopCapabilityEndpoint.LoopbackHttp2(new Uri("http://127.0.0.1:5099"));
        Assert.Equal("http://127.0.0.1:5099", endpoint.Address);

        // 不允许回环之外的明文端点（不因 IPC 失败自动切到公网 TCP）。
        Assert.Throws<ArgumentException>(
            () => DesktopCapabilityEndpoint.LoopbackHttp2(new Uri("http://10.0.0.5:5099")));
        Assert.Throws<ArgumentException>(
            () => DesktopCapabilityEndpoint.LoopbackHttp2(new Uri("https://127.0.0.1:5099")));
        Assert.Throws<ArgumentException>(
            () => DesktopCapabilityEndpoint.LoopbackHttp2(new Uri("http://user:secret@127.0.0.1:5099")));
        Assert.Throws<ArgumentException>(
            () => DesktopCapabilityEndpoint.LoopbackHttp2(new Uri("http://127.0.0.1:5099/?token=abc")));
    }

    [Fact]
    public void TlsRequiresHttpsAndRejectsCredentials()
    {
        var endpoint = DesktopCapabilityEndpoint.Tls(new Uri("https://core.example:5099"));
        Assert.Equal(DesktopCapabilityEndpointKind.Tls, endpoint.Kind);
        Assert.Equal("https://core.example:5099", endpoint.Address);

        Assert.Throws<ArgumentException>(() => DesktopCapabilityEndpoint.Tls(new Uri("http://core.example:5099")));
        Assert.Throws<ArgumentException>(
            () => DesktopCapabilityEndpoint.Tls(new Uri("https://user:secret@core.example:5099")));
    }

    [Fact]
    public void ServerInstanceId_MustBeAValidProcessInstanceIdentifier()
    {
        Assert.Null(DesktopCapabilityEndpoint.NamedPipe("pudding", 1, "   ").ServerInstanceId);
        Assert.Throws<ArgumentException>(() => DesktopCapabilityEndpoint.NamedPipe("pudding", 1, "core 1"));
    }

    [Fact]
    public void EndpointString_RoundTrips()
    {
        var pipe = DesktopCapabilityEndpoint.NamedPipe("pudding-capability-abc", 1, "core-7");
        Assert.Equal("named-pipe:pudding-capability-abc|1|core-7", pipe.ToEndpointString());

        var loopback = DesktopCapabilityEndpoint.LoopbackHttp2(new Uri("http://127.0.0.1:5099"), 1);
        Assert.Equal("loopback-h2c:http://127.0.0.1:5099|1|", loopback.ToEndpointString());

        var tls = DesktopCapabilityEndpoint.Tls(new Uri("https://core.example:5099"), 1, "core-9");
        Assert.Equal("tls:https://core.example:5099|1|core-9", tls.ToEndpointString());

        foreach (var original in new[] { pipe, loopback, tls })
        {
            Assert.True(DesktopCapabilityEndpoint.TryParse(original.ToEndpointString(), out var parsed));
            Assert.Equal(original.Kind, parsed!.Kind);
            Assert.Equal(original.Address, parsed.Address);
            Assert.Equal(original.ProtocolVersion, parsed.ProtocolVersion);
            Assert.Equal(original.ServerInstanceId, parsed.ServerInstanceId);
        }
    }

    [Fact]
    public void Parse_FailsClosedOnMalformedInput()
    {
        string?[] malformed =
        [
            null,
            string.Empty,
            "named-pipe:pudding",                       // 缺少分隔段
            "named-pipe:pudding|1",                     // 段数不足
            "named-pipe:pudding|x|",                    // 版本不是数字
            "named-pipe:pudding|0|",                    // 版本必须为正
            "named-pipe:pudding|1|core 1",              // 非法实例 ID
            "named-pipe:bad/name|1|",                   // 非法管道名
            "named-pipe:pudding|1|extra|segments",      // 段数过多
            "carrier-pigeon:pudding|1|",                // 未知形态
            "loopback-h2c:http://10.0.0.5:1|1|",        // 非回环明文
            "loopback-h2c:https://127.0.0.1:1|1|",      // 形态与协议不符
            "tls:http://core.example:1|1|",             // 形态与协议不符
            "loopback-h2c:not-a-uri|1|",
        ];

        foreach (var text in malformed)
        {
            Assert.False(DesktopCapabilityEndpoint.TryParse(text, out var endpoint), $"should reject '{text}'");
            Assert.Null(endpoint);
        }
    }

    [Fact]
    public void EndpointDescription_HasNoCredentialShapedMember()
    {
        string[] forbiddenTokens = ["token", "secret", "password", "credential", "key", "apikey", "auth"];

        var offending = typeof(DesktopCapabilityEndpoint)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(property => property.Name)
            .Where(name => forbiddenTokens.Any(token =>
                name.Contains(token, StringComparison.OrdinalIgnoreCase)))
            .ToArray();

        Assert.Empty(offending);
    }

    [Fact]
    public void EndpointDescription_IsImmutableValueType()
    {
        var left = DesktopCapabilityEndpoint.NamedPipe("pudding", 1, "core-1");
        var right = DesktopCapabilityEndpoint.NamedPipe("pudding", 1, "core-1");
        var other = DesktopCapabilityEndpoint.NamedPipe("pudding", 1, "core-2");

        Assert.Equal(left, right);
        Assert.NotEqual(left, other);
        Assert.Equal(left.ToEndpointString(), right.ToEndpointString());
    }
}
