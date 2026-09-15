using System.Net;
using PuddingCode.Core;
using PuddingCode.Models;

namespace PuddingCoreTests;

[TestClass]
public sealed class DeepSeekImageRequestLimitTests
{
    [TestMethod]
    [DataRow(true)][DataRow(false)]
    public async Task MixedImagesShareOneTotalByteLimitRegardlessOfOrder(bool fileFirst)
    {
        var policy = new VisionRequestPolicy { FilesMaxTotalBytes = 100, InlineMaxTotalBytes = 1000, InlineMaxBytesPerImage = 55 };
        var budget = new VisualInputRequestBudget(policy);
        var images = fileFirst ? new[] { "file", "inline" } : new[] { "inline", "file" };
        var error = await Assert.ThrowsExactlyAsync<VisionPipelineException>(() => LlmVisualInputPlanner.PlanAsync("default",
            images.Select(id => new LlmImagePart(id)).ToList(), new SizedImages(), policy,
            fileUploader: new Upload(), budget: budget));
        StringAssert.Contains(error.Message,"combined image bytes");
    }

    private sealed class SizedImages : IVisualArtifactResolver
    {
        public Task<VisualArtifactResolveResult?> ResolveAsync(string workspaceId,string artifactId,CancellationToken ct = default,string detail = "original")
            => Task.FromResult<VisualArtifactResolveResult?>(new(artifactId,"data:image/png;base64," + Convert.ToBase64String(new byte[artifactId == "file" ? 60 : 50]),"image/png"));
    }
    private sealed class Upload : IDeepSeekFilesUploader
    {
        public Task<ProviderFileUploadResult> UploadAsync(byte[] imageBytes,string mimeType,long lifetimeSeconds,CancellationToken ct = default)
            => Task.FromResult(new ProviderFileUploadResult("file-test",mimeType,imageBytes.Length,lifetimeSeconds,DateTimeOffset.UtcNow.AddDays(7)));
    }

    [TestMethod]
    public async Task FinalBodyOver48MiBIsRejectedBeforeProviderHttp()
    {
        var handler = new NoSend();
        var gateway = new ResponsesLlmGateway(new HttpClient(handler), new LlmOptions("https://api.deepseek.com", "test", "deepseek-flash"));
        var error = await Assert.ThrowsExactlyAsync<VisionPipelineException>(() => gateway.ChatAsync(
            [new ChatMessage(ChatRole.User, new string('a',48 * 1024 * 1024))], []));
        Assert.AreEqual(VisionErrorCodes.RequestLimitExceeded,error.Code);
        Assert.AreEqual(0,handler.Calls);
    }
    private sealed class NoSend : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
        { Calls++; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)); }
    }
}
