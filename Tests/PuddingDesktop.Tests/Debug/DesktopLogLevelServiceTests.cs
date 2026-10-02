using System.Net;
using System.Net.Http;
using System.Text;
using PuddingBrowser.Protocol;
using PuddingDesktop.Debug;
using Xunit;

namespace PuddingDesktop.Tests.Debug;

/// <summary>
/// Shell 侧 Debug 按钮的服务（读/切 Core 日志级别）。
///
/// 用桩 HttpMessageHandler 钉住三件事：**打到哪个端点**、**带上 ControlToken 头**、
/// **失败不当成成功**（否则界面会显示"已开启"而实际没生效）。
/// </summary>
public sealed class DesktopLogLevelServiceTests
{
    private static readonly Uri CoreAddress = new("http://127.0.0.1:8123");

    [Fact]
    public async Task GetAsync_SendsControlToken_AndParsesSnapshot()
    {
        var handler = new StubHandler(HttpStatusCode.OK, """
            {"level":"Debug","isVerbose":true,"supportedLevels":["Verbose","Debug","Information"],"configFile":"D:/data/config/logging.json"}
            """);
        var service = new DesktopLogLevelService(new HttpClient(handler));

        var snapshot = await service.GetAsync(CoreAddress, _ => Task.FromResult("token-123"));

        Assert.Equal(HttpMethod.Get, handler.LastMethod);
        Assert.Equal(DesktopLogLevelService.RelativePath, handler.LastPath);
        Assert.Equal("token-123", handler.LastControlToken);
        Assert.Equal("Debug", snapshot.Level);
        Assert.True(snapshot.IsVerbose);
        Assert.Equal(3, snapshot.SupportedLevels.Count);
        Assert.Equal("D:/data/config/logging.json", snapshot.ConfigFile);
    }

    [Fact]
    public async Task SetAsync_PutsRequestedLevel_AsJsonBody()
    {
        var handler = new StubHandler(HttpStatusCode.OK, """
            {"level":"Information","isVerbose":false,"supportedLevels":["Debug","Information"],"configFile":"D:/data/config/logging.json"}
            """);
        var service = new DesktopLogLevelService(new HttpClient(handler));

        var snapshot = await service.SetAsync(
            CoreAddress, _ => Task.FromResult("token-123"), "Information");

        Assert.Equal(HttpMethod.Put, handler.LastMethod);
        Assert.Equal(DesktopLogLevelService.RelativePath, handler.LastPath);
        Assert.NotNull(handler.LastBody);
        Assert.Contains("\"level\":\"Information\"", handler.LastBody!, StringComparison.Ordinal);
        Assert.False(snapshot.IsVerbose);
    }

    [Fact]
    public async Task SetAsync_NonSuccess_ThrowsInsteadOfReturningAFakeSnapshot()
    {
        var handler = new StubHandler(HttpStatusCode.BadRequest, """{"title":"无法识别的日志级别"}""");
        var service = new DesktopLogLevelService(new HttpClient(handler));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.SetAsync(CoreAddress, _ => Task.FromResult("t"), "shout"));
    }

    [Fact]
    public void ToggleTarget_FlipsBetweenDebugAndDefault()
    {
        // 与前端 useServerLogLevel 同一套语义：Debug ⇄ Information，一键可回退。
        Assert.Equal(DesktopLogLevelService.DebugLevel, DesktopLogLevelService.ToggleTarget(isVerbose: false));
        Assert.Equal(DesktopLogLevelService.DefaultLevel, DesktopLogLevelService.ToggleTarget(isVerbose: true));
    }

    private sealed class StubHandler(HttpStatusCode status, string json) : HttpMessageHandler
    {
        public HttpMethod? LastMethod { get; private set; }

        public string? LastPath { get; private set; }

        public string? LastControlToken { get; private set; }

        public string? LastBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            LastMethod = request.Method;
            LastPath = request.RequestUri?.AbsolutePath;
            LastControlToken = request.Headers.TryGetValues(
                BrowserBridgeProtocol.ControlTokenHeader, out var values)
                ? values.FirstOrDefault()
                : null;
            if (request.Content is not null)
            {
                LastBody = await request.Content.ReadAsStringAsync(cancellationToken);
            }

            return new HttpResponseMessage(status)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            };
        }
    }
}
