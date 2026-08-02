using System.Net;
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
}
