using Microsoft.Extensions.Logging.Abstractions;
using PuddingHost.Hosting;

namespace PuddingHost.Tests.Hosting;

public sealed class PuddingControllerAddressRewriteHandlerTests
{
    [Fact]
    public async Task DesktopChild_RewritesConfiguredControllerAddressToBoundLoopback()
    {
        var accessor = new PuddingServerAddressAccessor();
        accessor.SetBoundAddresses(["http://127.0.0.1:43127"]);
        var recorder = new RecordingHandler();
        using var client = CreateClient(PuddingHostMode.DesktopChild, accessor, recorder);

        using var response = await client.GetAsync("/api/session/workspace/default?limit=20");

        Assert.Equal(
            new Uri("http://127.0.0.1:43127/api/session/workspace/default?limit=20"),
            recorder.RequestUri);
    }

    [Fact]
    public async Task Console_KeepsConfiguredControllerAddress()
    {
        var accessor = new PuddingServerAddressAccessor();
        accessor.SetBoundAddresses(["http://127.0.0.1:43127"]);
        var recorder = new RecordingHandler();
        using var client = CreateClient(PuddingHostMode.Console, accessor, recorder);

        using var response = await client.GetAsync("/api/session");

        Assert.Equal(new Uri("http://localhost:5000/api/session"), recorder.RequestUri);
    }

    [Fact]
    public async Task DesktopChild_BeforeAddressCapture_FailsWithActionableMessage()
    {
        var recorder = new RecordingHandler();
        using var client = CreateClient(
            PuddingHostMode.DesktopChild,
            new PuddingServerAddressAccessor(),
            recorder);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.GetAsync("/api/session"));

        Assert.Contains("after the Host has started", exception.Message, StringComparison.Ordinal);
        Assert.Null(recorder.RequestUri);
    }

    private static HttpClient CreateClient(
        PuddingHostMode mode,
        IPuddingServerAddressAccessor accessor,
        HttpMessageHandler innerHandler)
    {
        var options = new PuddingHostOptions
        {
            Mode = mode,
            DataRoot = "C:\\pudding-test-data",
        };
        var handler = new PuddingControllerAddressRewriteHandler(
            options,
            accessor,
            NullLogger<PuddingControllerAddressRewriteHandler>.Instance)
        {
            InnerHandler = innerHandler,
        };

        return new HttpClient(handler)
        {
            BaseAddress = new Uri("http://localhost:5000"),
        };
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public Uri? RequestUri { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestUri = request.RequestUri;
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK));
        }
    }
}
