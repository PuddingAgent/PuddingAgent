using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using PuddingCode.Abstractions;
using PuddingRuntime.Services;

namespace PuddingRuntimeTests.Services;

[TestClass]
public sealed class VolcengineArkImageGenerationProviderTests
{
    [TestMethod]
    public async Task GenerateAsync_PostsArkContractAndDownloadsBoundedImage()
    {
        var handler = new ArkHandler();
        var provider = new VolcengineArkImageGenerationProvider(
            new StaticHttpClientFactory(new HttpClient(handler)),
            NullLogger<VolcengineArkImageGenerationProvider>.Instance);

        var result = await provider.GenerateAsync(
            new ImageGenerationProviderRequest
            {
                Endpoint = "https://ark.cn-beijing.volces.com/api/v3",
                ApiKey = "test-key",
                ModelId = "doubao-seedream-5-0-260128",
                Prompt = "一只在月球上的布丁猫",
                Size = "2K",
                Watermark = true,
            });

        var image = result.Images.Single();
        Assert.AreEqual("image/png", image.MimeType);
        CollectionAssert.AreEqual(ArkHandler.PngBytes, image.Content);
        Assert.HasCount(2, handler.Requests);
        var generation = handler.Requests[0];
        Assert.AreEqual(
            "https://ark.cn-beijing.volces.com/api/v3/images/generations",
            generation.Uri);
        Assert.AreEqual("Bearer", generation.AuthorizationScheme);
        Assert.AreEqual("test-key", generation.AuthorizationParameter);
        StringAssert.Contains(
            generation.Body,
            "\"model\":\"doubao-seedream-5-0-260128\"");
        StringAssert.Contains(generation.Body, "\"response_format\":\"url\"");
        StringAssert.Contains(generation.Body, "\"size\":\"2K\"");
        StringAssert.Contains(generation.Body, "\"output_format\":\"png\"");
        StringAssert.Contains(
            generation.Body,
            "\"optimize_prompt_options\":{\"mode\":\"standard\"}");
        Assert.AreEqual(
            "https://ark-cdn.example/generated.png",
            handler.Requests[1].Uri);
    }

    [TestMethod]
    public async Task GenerateAsync_ProEdit_PostsReferenceCoordinatesAndFastMode()
    {
        var handler = new ArkHandler();
        var provider = new VolcengineArkImageGenerationProvider(
            new StaticHttpClientFactory(new HttpClient(handler)),
            NullLogger<VolcengineArkImageGenerationProvider>.Instance);

        var result = await provider.GenerateAsync(
            new ImageGenerationProviderRequest
            {
                Endpoint = "https://ark.cn-beijing.volces.com/api/v3",
                ApiKey = "test-key",
                ModelId = "doubao-seedream-5-0-pro-260628",
                Prompt =
                    "把图 1 <bbox>120 180 640 760</bbox> 区域替换成花园",
                Size = "2048x2048",
                Watermark = false,
                OutputFormat = "jpeg",
                OptimizePromptMode = "fast",
                InputImages = ["data:image/png;base64,iVBORw0KGgo="],
            });

        Assert.HasCount(1, result.Images);
        var generation = handler.Requests[0];
        StringAssert.Contains(
            generation.Body,
            "\"model\":\"doubao-seedream-5-0-pro-260628\"");
        StringAssert.Contains(
            generation.Body,
            "\"image\":\"data:image/png;base64,iVBORw0KGgo=\"");
        StringAssert.Contains(
            generation.Body,
            "\\u003Cbbox\\u003E120 180 640 760\\u003C/bbox\\u003E");
        StringAssert.Contains(
            generation.Body,
            "\"optimize_prompt_options\":{\"mode\":\"fast\"}");
        StringAssert.Contains(generation.Body, "\"output_format\":\"jpeg\"");
        Assert.IsFalse(
            generation.Body.Contains(
                "\"sequential_image_generation\"",
                StringComparison.Ordinal));
    }

    private sealed class StaticHttpClientFactory(HttpClient client)
        : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class ArkHandler : HttpMessageHandler
    {
        public static readonly byte[] PngBytes =
            [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

        public List<RecordedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(new RecordedRequest(
                request.RequestUri!.AbsoluteUri,
                request.Headers.Authorization?.Scheme,
                request.Headers.Authorization?.Parameter,
                request.Content is null
                    ? ""
                    : await request.Content.ReadAsStringAsync(cancellationToken)));
            if (request.Method == HttpMethod.Post)
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        """{"data":[{"url":"https://ark-cdn.example/generated.png"}]}""",
                        Encoding.UTF8,
                        "application/json"),
                };
            }

            var content = new ByteArrayContent(PngBytes);
            content.Headers.ContentType = new("image/png");
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = content,
            };
        }
    }

    private sealed record RecordedRequest(
        string Uri,
        string? AuthorizationScheme,
        string? AuthorizationParameter,
        string Body);
}
