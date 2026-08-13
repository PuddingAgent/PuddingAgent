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
public sealed class FeishuInboundPostTests
{
    [TestMethod]
    public async Task PostEvent_MapsMarkdownAndPreservesGatewayMetadata()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "pudding-feishu-post-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var paths = PuddingDataPaths.FromRoot(root);
            var mapper = new FeishuInboundMessageMapper(
                new VisionArtifactStorageService(
                    paths,
                    NullLogger<VisionArtifactStorageService>.Instance),
                new AudioArtifactStorageService(
                    paths,
                    NullLogger<AudioArtifactStorageService>.Instance),
                new ManagedOggOpusTranscoder(),
                NullLogger<FeishuInboundMessageMapper>.Instance);
            var handler = new NoOpHttpHandler();
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
            var evt = CreatePostEvent();

            var envelope = await mapper.MapAsync(
                binding,
                "feishu:agent-test",
                evt,
                client);

            Assert.AreEqual("chat", envelope.MessageType);
            Assert.AreEqual(
                "文档已写入，未修改生产代码。\n"
                + "- [ADR-066](/E:/repo/ADR-066.md)",
                envelope.MessageText);
            Assert.AreEqual("post", envelope.Metadata["feishu_message_type"]);
            Assert.AreEqual("om_post", envelope.ExternalMessageId);
            Assert.AreEqual("oc_chat", envelope.ExternalConversationId);
            Assert.AreEqual("ou_sender", envelope.UserExternalId);
            Assert.IsEmpty(handler.Requests);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task PostEvent_WithImages_MaterializesArtifactsInOrder()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "pudding-feishu-post-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var paths = PuddingDataPaths.FromRoot(root);
            var storage = new VisionArtifactStorageService(
                paths,
                NullLogger<VisionArtifactStorageService>.Instance);
            var mapper = new FeishuInboundMessageMapper(
                storage,
                new AudioArtifactStorageService(
                    paths,
                    NullLogger<AudioArtifactStorageService>.Instance),
                new ManagedOggOpusTranscoder(),
                NullLogger<FeishuInboundMessageMapper>.Instance);
            var handler = new ImageHttpHandler();
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
            var evt = CreatePostEventWithImages();

            var envelope = await mapper.MapAsync(
                binding,
                "feishu:agent-test",
                evt,
                client);

            // chat 路径保持，文本保留 [图片] 占位。
            Assert.AreEqual("chat", envelope.MessageType);
            StringAssert.Contains(envelope.MessageText, "第一张图");
            StringAssert.Contains(envelope.MessageText, "[图片]");

            var ids = envelope.Metadata["visionArtifactIds"]
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            Assert.AreEqual(2, ids.Length);
            StringAssert.Matches(ids[0], new("^vision-[a-f0-9]{32}$"));
            StringAssert.Matches(ids[1], new("^vision-[a-f0-9]{32}$"));
            // 顺序与 post 内元素顺序一致（先 img_v2_first 后 img_v2_second）。
            Assert.AreEqual(
                StableImageArtifactIdForTest("om_post_img", "img_v2_first"),
                ids[0]);
            Assert.AreEqual(
                StableImageArtifactIdForTest("om_post_img", "img_v2_second"),
                ids[1]);

            var first = await storage.ResolveAsync("default", ids[0]);
            var second = await storage.ResolveAsync("default", ids[1]);
            Assert.IsNotNull(first);
            Assert.IsNotNull(second);
            Assert.AreEqual("image/png", first.MimeType);
            Assert.AreEqual("image/png", second.MimeType);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task PostEvent_WithImage_DownloadFailure_KeepsTextAndSkipsImage()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "pudding-feishu-post-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var paths = PuddingDataPaths.FromRoot(root);
            var mapper = new FeishuInboundMessageMapper(
                new VisionArtifactStorageService(
                    paths,
                    NullLogger<VisionArtifactStorageService>.Instance),
                new AudioArtifactStorageService(
                    paths,
                    NullLogger<AudioArtifactStorageService>.Instance),
                new ManagedOggOpusTranscoder(),
                NullLogger<FeishuInboundMessageMapper>.Instance);
            // 下载资源返回 500：丢图不丢文。
            var handler = new FailingImageDownloadHandler();
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
            var evt = CreatePostEventWithImages();

            var envelope = await mapper.MapAsync(
                binding,
                "feishu:agent-test",
                evt,
                client);

            Assert.AreEqual("chat", envelope.MessageType);
            StringAssert.Contains(envelope.MessageText, "第一张图");
            Assert.IsFalse(envelope.Metadata.ContainsKey("visionArtifactIds"));
            Assert.IsFalse(envelope.Metadata.ContainsKey("visionArtifactId"));
            // 2 张图各触发一次资源下载请求（token 请求之外的资源请求）。
            Assert.AreEqual(3, handler.Requests.Count);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static FeishuEvent CreatePostEvent() => new()
    {
        Header = new FeishuEventHeader
        {
            EventId = "evt_post",
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
                MessageId = "om_post",
                ChatId = "oc_chat",
                MessageType = "post",
                Content =
                    """
                    {
                      "title":"",
                      "content":[
                        [
                          {
                            "tag":"text",
                            "text":"文档已写入，未修改生产代码。"
                          }
                        ],
                        [
                          {"tag":"text","text":"- "},
                          {
                            "tag":"a",
                            "href":"/E:/repo/ADR-066.md",
                            "text":"ADR-066"
                          }
                        ]
                      ]
                    }
                    """,
                CreateTime = "1785600949939",
            },
        },
    };

    private static FeishuEvent CreatePostEventWithImages() => new()
    {
        Header = new FeishuEventHeader
        {
            EventId = "evt_post_img",
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
                MessageId = "om_post_img",
                ChatId = "oc_chat",
                MessageType = "post",
                Content =
                    """
                    {
                      "title":"",
                      "content_v2":[
                        [
                          {"tag":"text","text":"第一张图："},
                          {"tag":"img","image_key":"img_v2_first","width":100,"height":100}
                        ],
                        [
                          {"tag":"img","image_key":"img_v2_second","width":200,"height":200}
                        ]
                      ]
                    }
                    """,
                CreateTime = "1785600949939",
            },
        },
    };

    private static string StableImageArtifactIdForTest(
        string messageId,
        string imageKey)
    {
        var raw = Encoding.UTF8.GetBytes(
            $"feishu-image\nfeishu:agent-test\n{messageId}\n{imageKey}");
        using var sha = System.Security.Cryptography.SHA256.Create();
        var hash = sha.ComputeHash(raw);
        return $"vision-{Convert.ToHexString(hash.AsSpan(0, 16)).ToLowerInvariant()}";
    }

    private sealed class NoOpHttpHandler : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri?.AbsoluteUri ?? string.Empty);
            return Task.FromResult(
                new HttpResponseMessage(HttpStatusCode.InternalServerError));
        }
    }

    private sealed class ImageHttpHandler : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri?.AbsoluteUri ?? string.Empty);
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

    private sealed class FailingImageDownloadHandler : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri?.AbsoluteUri ?? string.Empty);
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

            return Task.FromResult(
                new HttpResponseMessage(HttpStatusCode.InternalServerError));
        }
    }
}
