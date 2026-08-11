using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using PuddingBrowser.Protocol;
using PuddingCode.Storage;
using PuddingDesktop.Storage;

namespace PuddingDesktop.Tests.Storage;

public sealed class CoreStorageManagementClientTests
{
    [Fact]
    public async Task Analyze_Uses_Core_Admin_Route_And_Desktop_Control_Token()
    {
        HttpRequestMessage? captured = null;
        using var client = new CoreStorageManagementClient(new StubHandler(request =>
        {
            captured = request;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new StorageDatabaseAnalysis
                {
                    CapturedAt = DateTimeOffset.UtcNow,
                    TotalBytes = 123,
                    Databases = [],
                    Items = [],
                    Warnings = [],
                }),
            };
        }));
        client.Configure(
            new Uri("http://127.0.0.1:8123"),
            _ => Task.FromResult("control-token"));

        var analysis = await client.AnalyzeAsync(CancellationToken.None);

        Assert.Equal(123, analysis.TotalBytes);
        Assert.NotNull(captured);
        Assert.Equal(HttpMethod.Get, captured.Method);
        Assert.Equal(
            "http://127.0.0.1:8123/api/admin/storage/databases",
            captured.RequestUri!.AbsoluteUri);
        Assert.Equal(
            "control-token",
            captured.Headers.GetValues(BrowserBridgeProtocol.ControlTokenHeader).Single());
    }

    private sealed class StubHandler(
        Func<HttpRequestMessage, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => Task.FromResult(responseFactory(request));
    }
}
