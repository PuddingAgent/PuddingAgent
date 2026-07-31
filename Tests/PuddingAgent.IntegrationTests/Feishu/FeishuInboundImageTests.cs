using System.Net;
using System.Text;
using HarnessAgent.Core.Connectors.Feishu;
using Microsoft.Extensions.Logging.Abstractions;
using PuddingAgent.Connectors;
using PuddingCode.Configuration;
using PuddingPlatform.Services;

namespace PuddingAgent.IntegrationTests.Feishu;

[TestClass]
public sealed class FeishuInboundImageTests
{
    [TestMethod]
    public async Task ImageEvent_DownloadsOnceAndMapsToCanonicalVisionArtifactMetadata()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "pudding-feishu-image-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var storage = new VisionArtifactStorageService(
                PuddingDataPaths.FromRoot(root),
                NullLogger<VisionArtifactStorageService>.Instance);
            var mapper = new FeishuInboundMessageMapper(
                storage,
                NullLogger<FeishuInboundMessageMapper>.Instance);
            var handler = new FeishuImageHandler();
            using var http = new HttpClient(handler);
            using var client = new FeishuClient(
                new FeishuConfig
                {
                    AppId = "app_test",
                    AppSecret = "secret_test",
                },
                http);
            var binding = new FeishuConnectorBinding(
                "agent-test",
                "default",
                "app_test",
                "secret_test",
                null,
                TtsRepliesEnabled: true,
                TtsVoice: "Stella");
            var evt = CreateImageEvent();

            var envelope = await mapper.MapAsync(
                binding,
                "feishu:agent-test",
                evt,
                client);
            var retried = await mapper.MapAsync(
                binding,
                "feishu:agent-test",
                evt,
                client);

            Assert.AreEqual("image", envelope.MessageType);
            Assert.AreEqual("用户从飞书发送了一张图片。", envelope.MessageText);
            Assert.AreEqual("image", envelope.Metadata["inputMode"]);
            Assert.AreEqual(
                "true",
                envelope.Metadata["gateway_tts_replies_enabled"]);
            Assert.AreEqual(
                "Stella",
                envelope.Metadata["gateway_tts_voice"]);
            var artifactId = envelope.Metadata["visionArtifactId"];
            Assert.AreEqual(artifactId, envelope.Metadata["visionArtifactIds"]);
            Assert.AreEqual(artifactId, retried.Metadata["visionArtifactId"]);
            StringAssert.Matches(artifactId, new("^vision-[a-f0-9]{32}$"));
            Assert.HasCount(2, handler.Requests);
            StringAssert.Contains(handler.Requests[1], "/resources/img_v3_test?type=image");

            var resolved = await storage.ResolveAsync("default", artifactId);
            Assert.IsNotNull(resolved);
            Assert.AreEqual("image/png", resolved.MimeType);
            Assert.AreEqual(
                "data:image/png;base64,iVBORw0KGgo=",
                resolved.Uri);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static FeishuEvent CreateImageEvent() => new()
    {
        Header = new FeishuEventHeader
        {
            EventId = "evt_image",
            EventType = "im.message.receive_v1",
        },
        Event = new FeishuEventV2
        {
            Sender = new FeishuEventSender
            {
                SenderId = new FeishuSenderId { OpenId = "ou_sender" },
            },
            Message = new FeishuMessageEvent
            {
                MessageId = "om_image",
                ChatId = "oc_chat",
                MessageType = "image",
                Content = "{\"image_key\":\"img_v3_test\"}",
                CreateTime = "1720000000000",
            },
        },
    };

    private sealed class FeishuImageHandler : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri?.AbsoluteUri ?? "");
            if (request.RequestUri?.AbsolutePath.EndsWith(
                    "/tenant_access_token/internal",
                    StringComparison.Ordinal) == true)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        "{\"code\":0,\"msg\":\"ok\",\"tenant_access_token\":\"token\",\"expire\":7200}",
                        Encoding.UTF8,
                        "application/json"),
                });
            }

            var content = new ByteArrayContent(
                [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
            content.Headers.ContentType = new("image/png");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = content,
            });
        }
    }
}
