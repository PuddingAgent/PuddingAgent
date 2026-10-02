using PuddingDesktop.Core;

namespace PuddingDesktop.Tests.Core;

public class CoreReadyMessageParserTests
{
    [Fact]
    public void TryParse_ValidReadyLine_ReturnsMessage()
    {
        var line = """PUDDING_DESKTOP_READY {"protocolVersion":1,"processId":1234,"baseAddress":"http://127.0.0.1:52137"}""";

        var result = CoreReadyMessageParser.TryParse(line);

        Assert.NotNull(result);
        Assert.Equal(1, result.ProtocolVersion);
        Assert.Equal(1234, result.ProcessId);
        Assert.Equal("http://127.0.0.1:52137/", result.BaseAddress.ToString());
    }

    [Fact]
    public void TryParse_NoPrefix_ReturnsNull()
    {
        var result = CoreReadyMessageParser.TryParse("Some other log line");

        Assert.Null(result);
    }

    [Fact]
    public void TryParse_EmptyLine_ReturnsNull()
    {
        Assert.Null(CoreReadyMessageParser.TryParse(""));
        Assert.Null(CoreReadyMessageParser.TryParse("   "));
    }

    [Fact]
    public void TryParse_NonLoopbackAddress_Throws()
    {
        var line = """PUDDING_DESKTOP_READY {"protocolVersion":1,"processId":1234,"baseAddress":"http://192.168.1.1:52137"}""";

        Assert.Throws<InvalidOperationException>(() => CoreReadyMessageParser.TryParse(line));
    }

    [Fact]
    public void TryParse_NonHttpScheme_Throws()
    {
        var line = """PUDDING_DESKTOP_READY {"protocolVersion":1,"processId":1234,"baseAddress":"https://127.0.0.1:52137"}""";

        Assert.Throws<InvalidOperationException>(() => CoreReadyMessageParser.TryParse(line));
    }

    [Fact]
    public void TryParse_MissingBaseAddress_Throws()
    {
        var line = """PUDDING_DESKTOP_READY {"protocolVersion":1,"processId":1234}""";

        Assert.Throws<InvalidOperationException>(() => CoreReadyMessageParser.TryParse(line));
    }

    [Fact]
    public void TryParse_MalformedJson_Throws()
    {
        var line = """PUDDING_DESKTOP_READY {not json}""";

        Assert.ThrowsAny<Exception>(() => CoreReadyMessageParser.TryParse(line));
    }

    [Fact]
    public void TryParse_EmbeddedInOtherOutput_StillParses()
    {
        var line = """[INFO] Starting... PUDDING_DESKTOP_READY {"protocolVersion":1,"processId":5678,"baseAddress":"http://127.0.0.1:9000"} trailing""";

        var result = CoreReadyMessageParser.TryParse(line);

        Assert.NotNull(result);
        Assert.Equal(5678, result.ProcessId);
        Assert.Equal(9000, result.BaseAddress.Port);
    }

    [Fact]
    public void TryParse_WithoutCapabilityEndpoint_LeavesItNull()
    {
        // 能力通道默认关闭：Core 不输出该字段，Desktop 侧必须是 null 而不是空串
        // （空串会被下游当成「有个描述可以解析」）。
        var line = """PUDDING_DESKTOP_READY {"protocolVersion":1,"processId":1,"baseAddress":"http://127.0.0.1:9001"}""";

        var result = CoreReadyMessageParser.TryParse(line);

        Assert.NotNull(result);
        Assert.Null(result.CapabilityEndpoint);
    }

    [Fact]
    public void TryParse_WithCapabilityEndpoint_CarriesDescriptionVerbatim()
    {
        // 字段名与 Core 侧 CapabilityChannelReadySignal.FieldName 一致；
        // 描述原文必须逐字搬运（是否可用由 DesktopCapabilityChannelPreflight 判定）。
        var line = """PUDDING_DESKTOP_READY {"protocolVersion":1,"processId":2,"baseAddress":"http://127.0.0.1:9002","capabilityEndpoint":"named-pipe:pudding-capability-abc|1|core-7"}""";

        var result = CoreReadyMessageParser.TryParse(line);

        Assert.NotNull(result);
        Assert.Equal("named-pipe:pudding-capability-abc|1|core-7", result.CapabilityEndpoint);
    }

    [Fact]
    public void TryParse_BlankCapabilityEndpoint_TreatedAsAbsent()
    {
        var line = """PUDDING_DESKTOP_READY {"protocolVersion":1,"processId":3,"baseAddress":"http://127.0.0.1:9003","capabilityEndpoint":"  "}""";

        var result = CoreReadyMessageParser.TryParse(line);

        Assert.NotNull(result);
        Assert.Null(result.CapabilityEndpoint);
    }

    [Fact]
    public void TryParse_UnknownExtraField_IsIgnored()
    {
        // 就绪协议必须能向前兼容：旧 Desktop 读到新字段不能失败。
        var line = """PUDDING_DESKTOP_READY {"protocolVersion":1,"processId":4,"baseAddress":"http://127.0.0.1:9004","somethingNew":42}""";

        var result = CoreReadyMessageParser.TryParse(line);

        Assert.NotNull(result);
        Assert.Equal(4, result.ProcessId);
    }
}
