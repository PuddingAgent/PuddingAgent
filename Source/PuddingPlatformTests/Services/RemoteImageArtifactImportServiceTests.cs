using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using PuddingCode.Configuration;
using PuddingPlatform.Services;

namespace PuddingPlatformTests.Services;

[TestClass]
public sealed class RemoteImageArtifactImportServiceTests
{
    [TestMethod]
    public async Task ImportAsync_SniffsStoresAndReusesPublicImage()
    {
        var handler = new RecordingHandler(
            HttpStatusCode.OK,
            VisionTestImages.PngHeader(32, 32),
            "text/plain");
        var service = CreateService(handler);

        var first = await service.ImportAsync(
            "default",
            "https://images.example/cat.png");
        var reused = await service.ImportAsync(
            "default",
            "https://images.example/cat.png");

        StringAssert.StartsWith(first.ArtifactId, "vision-");
        Assert.AreEqual("image/png", first.MimeType);
        Assert.IsTrue(Path.IsPathFullyQualified(first.LocalPath));
        Assert.IsTrue(File.Exists(first.LocalPath));
        Assert.IsFalse(first.Reused);
        Assert.IsTrue(reused.Reused);
        Assert.AreEqual(first.ArtifactId, reused.ArtifactId);
        Assert.AreEqual(1, handler.RequestCount);
    }

    [TestMethod]
    public async Task ImportAsync_RejectsHttpAndNonImageBytes()
    {
        var handler = new RecordingHandler(
            HttpStatusCode.OK,
            [1, 2, 3, 4],
            "image/png");
        var service = CreateService(handler);

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => service.ImportAsync(
                "default",
                "http://images.example/cat.png"));
        await Assert.ThrowsExactlyAsync<UnsupportedVisionArtifactMediaTypeException>(
            () => service.ImportAsync(
                "default",
                "https://images.example/not-image.png"));
    }

    private static RemoteImageArtifactImportService CreateService(
        HttpMessageHandler handler)
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"pudding-remote-image-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var storage = new VisionArtifactStorageService(
            PuddingDataPaths.FromRoot(root),
            NullLogger<VisionArtifactStorageService>.Instance);
        return new RemoteImageArtifactImportService(
            new StaticHttpClientFactory(new HttpClient(handler)),
            storage,
            NullLogger<RemoteImageArtifactImportService>.Instance);
    }

    private sealed class StaticHttpClientFactory(HttpClient client)
        : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class RecordingHandler(
        HttpStatusCode statusCode,
        byte[] body,
        string contentType)
        : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            var response = new HttpResponseMessage(statusCode)
            {
                RequestMessage = request,
                Content = new ByteArrayContent(body),
            };
            response.Content.Headers.ContentType =
                new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);
            return Task.FromResult(response);
        }
    }
}
