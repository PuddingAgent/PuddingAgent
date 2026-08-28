using PuddingDesktop.Core;

namespace PuddingDesktop.Tests.Core;

public class CoreStartupProgressMessageParserTests
{
    [Fact]
    public void TryParse_ValidProgress_ReturnsMessage()
    {
        var line = """PUDDING_DESKTOP_STARTING {"protocolVersion":1,"processId":1234,"sequence":7,"phase":"initializing","elapsedMilliseconds":30000}""";

        var result = CoreStartupProgressMessageParser.TryParse(line);

        Assert.NotNull(result);
        Assert.Equal(1, result.ProtocolVersion);
        Assert.Equal(1234, result.ProcessId);
        Assert.Equal(7, result.Sequence);
        Assert.Equal("initializing", result.Phase);
        Assert.Equal(30000, result.ElapsedMilliseconds);
    }

    [Fact]
    public void TryParse_UnrelatedLine_ReturnsNull()
    {
        Assert.Null(CoreStartupProgressMessageParser.TryParse("[Startup] Ensuring tables"));
    }

    [Theory]
    [InlineData("""PUDDING_DESKTOP_STARTING {"protocolVersion":1,"processId":1234,"sequence":0,"phase":"initializing"}""")]
    [InlineData("""PUDDING_DESKTOP_STARTING {"protocolVersion":1,"processId":1234,"sequence":1}""")]
    public void TryParse_InvalidProgress_Throws(string line)
    {
        Assert.Throws<InvalidOperationException>(() =>
            CoreStartupProgressMessageParser.TryParse(line));
    }
}
