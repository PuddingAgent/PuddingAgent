using System.Net;
using System.Text;
using HarnessAgent.Core.Connectors.Feishu;
using Microsoft.Extensions.Logging.Abstractions;
using PuddingAgent.Connectors;
using PuddingCode.Configuration;
using PuddingPlatform.Services;
using PuddingRuntime.Services;

namespace PuddingAgent.IntegrationTests.Feishu;

[TestClass]
public sealed class FeishuInboundImageTests
{
    /// <summary>
    /// ADR-077 夹具：合法最小 PNG（1×1 透明，67 字节，IHDR/IDAT/IEND 完整）。
    /// 保存路径以 <c>ImagePreprocessing.Inspect</c>（<c>SKCodec.Create</c> 真解码）做嗅探，
    /// 只含 8 字节 PNG 签名的伪夹具必然抛 MediaInvalid。
    /// </summary>
    private static readonly byte[] ValidPngBytes = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==");

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
            var audioStorage = new AudioArtifactStorageService(
                PuddingDataPaths.FromRoot(root),
                NullLogger<AudioArtifactStorageService>.Instance);
            var mapper = new FeishuInboundMessageMapper(
                storage,
                audioStorage,
                new ManagedOggOpusTranscoder(),
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
            // 存储不重编码：VisionArtifactStorageService.SaveCoreAsync 只对临时字节执行
            // Inspect 校验，落盘的是输入流原字节（VisionArtifactStorageService.cs:159-175、:178），
            // 因此 data URI 仍是「输入夹具字节的 base64」——字节级等值断言强度不变。
            Assert.AreEqual(
                $"data:image/png;base64,{Convert.ToBase64String(ValidPngBytes)}",
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

            var content = new ByteArrayContent(ValidPngBytes);
            content.Headers.ContentType = new("image/png");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = content,
            });
        }
    }
}
