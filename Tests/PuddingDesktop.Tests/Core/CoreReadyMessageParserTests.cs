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
}
