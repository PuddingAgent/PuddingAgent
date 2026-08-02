using System.Net;
using PuddingBrowser.Protocol;
using PuddingHost.Hosting;

namespace PuddingHost.Tests.BrowserBridge;

public sealed class DesktopBrowserBridgeAuthenticationTests
{
    [Fact]
    public async Task ConsoleMode_DoesNotMapBrowserBridgeEndpoint()
    {
        await using var host = await BrowserBridgeTestHost.StartAsync(PuddingHostMode.Console);
        var response = await host.HttpClient.GetAsync(BrowserBridgeProtocol.EndpointPath);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task MissingToken_ReturnsUnauthorized()
    {
        await using var host = await BrowserBridgeTestHost.StartAsync();
        var response = await host.HttpClient.GetAsync(BrowserBridgeProtocol.EndpointPath);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task WrongToken_ReturnsUnauthorized()
    {
        await using var host = await BrowserBridgeTestHost.StartAsync();
        using var request = new HttpRequestMessage(HttpMethod.Get, BrowserBridgeProtocol.EndpointPath);
        request.Headers.Add(BrowserBridgeProtocol.ControlTokenHeader, "wrong-token");
        var response = await host.HttpClient.SendAsync(request);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task NonLoopback_ReturnsForbiddenBeforeTokenOrUpgrade()
    {
        await using var host = await BrowserBridgeTestHost.StartAsync();
        using var request = new HttpRequestMessage(HttpMethod.Get, BrowserBridgeProtocol.EndpointPath);
        request.Headers.Add("X-Test-Remote-IP", "192.0.2.10");
        request.Headers.Add(BrowserBridgeProtocol.ControlTokenHeader, BrowserBridgeTestHost.ValidToken);
        var response = await host.HttpClient.SendAsync(request);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task ValidTokenWithoutWebSocketUpgrade_ReturnsBadRequest()
    {
        await using var host = await BrowserBridgeTestHost.StartAsync();
        using var request = new HttpRequestMessage(HttpMethod.Get, BrowserBridgeProtocol.EndpointPath);
        request.Headers.Add(BrowserBridgeProtocol.ControlTokenHeader, BrowserBridgeTestHost.ValidToken);
        var response = await host.HttpClient.SendAsync(request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
