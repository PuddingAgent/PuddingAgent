using System.Net;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PuddingDingTalk;

namespace PuddingDingTalkTests;

[TestClass]
public class MediaTransportTests
{
    private sealed class MediaEndpoint(Func<HttpRequestMessage, HttpResponseMessage> reply) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(reply(request));
    }
    private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK)
        { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    [TestMethod]
    public async Task MediaReadsProviderCapabilityAndSendsToBoundRoomUsingSeparateReceipt()
    {
        var posts = new List<string>();
        using var http = new HttpClient(new MediaEndpoint(req =>
        {
            var uri = req.RequestUri!;
            switch (uri.AbsolutePath)
            {
                case "/v1.0/oauth2/accessToken": return Json("""{"accessToken":"test-access","expireIn":7200}""");
                case "/v1.0/robot/messageFiles/download":
                    return Json("""{"downloadUrl":"https://media.dingtalk.com/sample-image"}""");
                case "/sample-image":
                    Assert.IsFalse(req.Headers.Contains("x-acs-dingtalk-access-token"));
                    return new(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) };
                case "/media/upload": return Json("""{"errcode":0,"media_id":"test-image-id"}""");
                case "/v1.0/robot/groupMessages/send":
                    posts.Add(req.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
                    return Json("""{"processQueryKey":"receipt"}""");
                default: throw new AssertFailedException(uri.AbsolutePath);
            }
        }));
        var media = new DingTalkMediaClient(http, "robot", "test-secret");
        var bytes = await media.DownloadAsync("provider-code", default);
        CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, bytes);
        Assert.IsTrue(await media.SendImageAsync("bound-room", new(bytes, "image/png"), default));
        Assert.Contains("bound-room", posts.Single());
        Assert.Contains("test-image-id", posts.Single());
    }

    [TestMethod]
    public async Task UnapprovedDownloadHostCannotReceiveARequest()
    {
        var count = 0;
        using var http = new HttpClient(new MediaEndpoint(req => ++count == 1
            ? Json("""{"accessToken":"test","expireIn":600}""")
            : Json("""{"downloadUrl":"https://unrelated.example/file"}""")));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
            new DingTalkMediaClient(http, "robot", "test-secret").DownloadAsync("code", default));
        Assert.AreEqual(2, count);
    }

    [TestMethod]
    public void NavigationProjectsOnlyMatchingUiFields()
    {
        var text = """
            registry.Register(new FeatureDefinition
            {
                Title = "图像标注",
                PanelType = FeaturePanelType.Project,
                Category = "证据整理",
                IconImageSource = "private-canary"
            });
            """;
        Assert.AreEqual("项目面板 → 证据整理 → 图像标注", MpcalNavigationEvidence.Search(text, "如何使用图像标注？").Single());
        Assert.IsEmpty(MpcalNavigationEvidence.Search(text, "private-canary"));
    }
}
