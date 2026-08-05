using System.Net;
using System.Text;
using System.Text.Json;
using HarnessAgent.Core.Connectors.Feishu;

namespace HarnessAgent.Core.Tests.Feishu;

[TestClass]
public sealed class FeishuClientReplyTests
{
    [TestMethod]
    public async Task ReplyTextAsync_UsesMessageReplyApiAndStableUuid()
    {
        var handler = new RecordingHandler();
        using var http = new HttpClient(handler);
        using var client = new FeishuClient(
            new FeishuConfig
            {
                AppId = "cli_test",
                AppSecret = "secret_test",
            },
            http);

        var result = await client.ReplyTextAsync(
            "om_test",
            "copy me",
            "echo-stable-uuid");

        Assert.AreEqual(0, result.Code);
        Assert.HasCount(2, handler.Requests);
        var reply = handler.Requests[1];
        Assert.AreEqual(
            "https://open.feishu.cn/open-apis/im/v1/messages/om_test/reply",
            reply.Uri);
        Assert.AreEqual("POST", reply.Method);
        Assert.AreEqual("Bearer", reply.AuthorizationScheme);
        Assert.AreEqual("tenant-token", reply.AuthorizationParameter);

        using var outer = JsonDocument.Parse(reply.Body);
        Assert.AreEqual(
            "text",
            outer.RootElement.GetProperty("msg_type").GetString());
        Assert.AreEqual(
            "echo-stable-uuid",
            outer.RootElement.GetProperty("uuid").GetString());
        var content = outer.RootElement.GetProperty("content").GetString();
        Assert.IsNotNull(content);
        using var inner = JsonDocument.Parse(content!);
        Assert.AreEqual(
            "copy me",
            inner.RootElement.GetProperty("text").GetString());
    }

    [TestMethod]
    public async Task SendMessageAsync_DefaultsToOpenIdType_AndSupportsChatIdType()
    {
        var handler = new RecordingHandler();
        using var http = new HttpClient(handler);
        using var client = new FeishuClient(
            new FeishuConfig
            {
                AppId = "cli_test",
                AppSecret = "secret_test",
            },
            http);

        // 私聊：默认 receive_id_type=open_id
        var dm = await client.SendMessageAsync(
            "ou_user",
            "text",
            """{"text":"hi"}""",
            "dm-uuid");
        Assert.AreEqual(0, dm.Code);

        // 群聊兜底：receive_id_type=chat_id，receive_id 必须是 chat_id
        var group = await client.SendMessageAsync(
            "oc_group_chat",
            "text",
            """{"text":"hello group"}""",
            "group-uuid",
            receiveIdType: "chat_id");
        Assert.AreEqual(0, group.Code);

        Assert.HasCount(3, handler.Requests);
        var dmSend = handler.Requests[1];
        var groupSend = handler.Requests[2];
        Assert.AreEqual(
            "https://open.feishu.cn/open-apis/im/v1/messages?receive_id_type=open_id",
            dmSend.Uri);
        Assert.AreEqual(
            "https://open.feishu.cn/open-apis/im/v1/messages?receive_id_type=chat_id",
            groupSend.Uri);

        using var dmBody = JsonDocument.Parse(dmSend.Body);
        Assert.AreEqual(
            "ou_user",
            dmBody.RootElement.GetProperty("receive_id").GetString());
        using var groupBody = JsonDocument.Parse(groupSend.Body);
        Assert.AreEqual(
            "oc_group_chat",
            groupBody.RootElement.GetProperty("receive_id").GetString());
        Assert.AreEqual(
            "group-uuid",
            groupBody.RootElement.GetProperty("uuid").GetString());
    }

    [TestMethod]
    public async Task DownloadMessageResourceAsync_UsesAuthenticatedBoundedResourceApi()
    {
        var handler = new RecordingHandler();
        using var http = new HttpClient(handler);
        using var client = new FeishuClient(
            new FeishuConfig
            {
                AppId = "cli_test",
                AppSecret = "secret_test",
            },
            http);

        var result = await client.DownloadMessageResourceAsync(
            "om/image id",
            "img/key",
            "image");

        Assert.HasCount(2, handler.Requests);
        var download = handler.Requests[1];
        Assert.AreEqual("GET", download.Method);
        Assert.AreEqual(
            "https://open.feishu.cn/open-apis/im/v1/messages/om%2Fimage%20id/resources/img%2Fkey?type=image",
            download.Uri);
        Assert.AreEqual("Bearer", download.AuthorizationScheme);
        Assert.AreEqual("tenant-token", download.AuthorizationParameter);
        CollectionAssert.AreEqual(
            new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A },
            result.Content);
        Assert.AreEqual("image/png", result.ContentType);
    }

    [TestMethod]
    public async Task AudioReply_UploadsOpusThenRepliesWithFileKeyAndStableUuid()
    {
        var handler = new RecordingHandler();
        using var http = new HttpClient(handler);
        using var client = new FeishuClient(
            new FeishuConfig
            {
                AppId = "cli_test",
                AppSecret = "secret_test",
            },
            http);

        var uploaded = await client.UploadAudioAsync(
            [0x4F, 0x67, 0x67, 0x53],
            "pudding.opus",
            durationMs: 250);
        Assert.AreEqual("file_opus_123", uploaded.Data?.FileKey);
        var replied = await client.ReplyAudioAsync(
            "om/audio id",
            uploaded.Data!.FileKey!,
            "audio-stable-uuid");
        Assert.AreEqual(0, replied.Code);

        Assert.HasCount(3, handler.Requests);
        var upload = handler.Requests[1];
        Assert.AreEqual(
            "https://open.feishu.cn/open-apis/im/v1/files",
            upload.Uri);
        Assert.AreEqual("Bearer", upload.AuthorizationScheme);
        StringAssert.Contains(upload.Body, "name=file_type");
        StringAssert.Contains(upload.Body, "opus");
        StringAssert.Contains(upload.Body, "name=duration");
        StringAssert.Contains(upload.Body, "250");
        StringAssert.Contains(upload.Body, "audio/ogg");

        var reply = handler.Requests[2];
        Assert.AreEqual(
            "https://open.feishu.cn/open-apis/im/v1/messages/om%2Faudio%20id/reply",
            reply.Uri);
        using var outer = JsonDocument.Parse(reply.Body);
        Assert.AreEqual(
            "audio",
            outer.RootElement.GetProperty("msg_type").GetString());
        Assert.AreEqual(
            "audio-stable-uuid",
            outer.RootElement.GetProperty("uuid").GetString());
        using var content = JsonDocument.Parse(
            outer.RootElement.GetProperty("content").GetString()!);
        Assert.AreEqual(
            "file_opus_123",
            content.RootElement.GetProperty("file_key").GetString());
    }

    [TestMethod]
    public async Task ImageReply_UploadsMessageImageThenRepliesWithImageKey()
    {
        var handler = new RecordingHandler();
        using var http = new HttpClient(handler);
        using var client = new FeishuClient(
            new FeishuConfig
            {
                AppId = "cli_test",
                AppSecret = "secret_test",
            },
            http);

        var uploaded = await client.UploadImageAsync(
            [0x89, 0x50, 0x4E, 0x47],
            "pudding.png",
            "image/png");
        Assert.AreEqual("img_generated_123", uploaded.Data?.ImageKey);
        var replied = await client.ReplyImageAsync(
            "om/image id",
            uploaded.Data!.ImageKey!,
            "image-stable-uuid");
        Assert.AreEqual(0, replied.Code);

        Assert.HasCount(3, handler.Requests);
        var upload = handler.Requests[1];
        Assert.AreEqual(
            "https://open.feishu.cn/open-apis/im/v1/images",
            upload.Uri);
        StringAssert.Contains(upload.Body, "name=image_type");
        StringAssert.Contains(upload.Body, "message");
        StringAssert.Contains(upload.Body, "name=image");
        StringAssert.Contains(upload.Body, "image/png");

        var reply = handler.Requests[2];
        Assert.AreEqual(
            "https://open.feishu.cn/open-apis/im/v1/messages/om%2Fimage%20id/reply",
            reply.Uri);
        using var outer = JsonDocument.Parse(reply.Body);
        Assert.AreEqual(
            "image",
            outer.RootElement.GetProperty("msg_type").GetString());
        Assert.AreEqual(
            "image-stable-uuid",
            outer.RootElement.GetProperty("uuid").GetString());
        using var content = JsonDocument.Parse(
            outer.RootElement.GetProperty("content").GetString()!);
        Assert.AreEqual(
            "img_generated_123",
            content.RootElement.GetProperty("image_key").GetString());
    }

    [TestMethod]
    public async Task CardKitStreaming_UsesCardEntityReferenceAndOrderedUpdates()
    {
        var handler = new RecordingHandler();
        using var http = new HttpClient(handler);
        using var client = new FeishuClient(
            new FeishuConfig
            {
                AppId = "cli_test",
                AppSecret = "secret_test",
            },
            http);

        var created = await client.CreateCardAsync(
            """{"schema":"2.0","config":{"streaming_mode":true}}""");
        Assert.AreEqual("card_123", created.Data?.CardId);

        var published = await client.ReplyCardAsync(
            "om_source",
            "card_123",
            "publish-uuid");
        Assert.AreEqual("om_card_reply", published.Data?.MessageId);

        var updated = await client.UpdateCardElementContentAsync(
            "card_123",
            "stream_md",
            "完整累计内容",
            1,
            "update-uuid");
        Assert.AreEqual(0, updated.Code);

        var finished = await client.UpdateCardAsync(
            "card_123",
            """{"schema":"2.0","config":{"streaming_mode":false},"body":{"elements":[]}}""",
            sequence: 2,
            uuid: "finish-uuid");
        Assert.AreEqual(0, finished.Code);

        Assert.HasCount(5, handler.Requests);

        var create = handler.Requests[1];
        Assert.AreEqual("POST", create.Method);
        Assert.AreEqual(
            "https://open.feishu.cn/open-apis/cardkit/v1/cards",
            create.Uri);
        using (var body = JsonDocument.Parse(create.Body))
        {
            Assert.AreEqual("card_json", body.RootElement.GetProperty("type").GetString());
            StringAssert.Contains(
                body.RootElement.GetProperty("data").GetString(),
                "streaming_mode");
        }

        var publish = handler.Requests[2];
        Assert.AreEqual("POST", publish.Method);
        using (var body = JsonDocument.Parse(publish.Body))
        {
            Assert.AreEqual("interactive", body.RootElement.GetProperty("msg_type").GetString());
            using var content = JsonDocument.Parse(
                body.RootElement.GetProperty("content").GetString()!);
            Assert.AreEqual(
                "card_123",
                content.RootElement.GetProperty("data").GetProperty("card_id").GetString());
        }

        var update = handler.Requests[3];
        Assert.AreEqual("PUT", update.Method);
        Assert.AreEqual(
            "https://open.feishu.cn/open-apis/cardkit/v1/cards/card_123/elements/stream_md/content",
            update.Uri);
        using (var body = JsonDocument.Parse(update.Body))
        {
            Assert.AreEqual(1, body.RootElement.GetProperty("sequence").GetInt32());
            Assert.AreEqual("完整累计内容", body.RootElement.GetProperty("content").GetString());
        }

        var finish = handler.Requests[4];
        Assert.AreEqual("PUT", finish.Method);
        Assert.AreEqual(
            "https://open.feishu.cn/open-apis/cardkit/v1/cards/card_123",
            finish.Uri);
        using (var body = JsonDocument.Parse(finish.Body))
        {
            Assert.AreEqual(2, body.RootElement.GetProperty("sequence").GetInt32());
            using var settings = JsonDocument.Parse(body.RootElement
                .GetProperty("card")
                .GetProperty("data")
                .GetString()!);
            Assert.IsFalse(
                settings.RootElement.GetProperty("config").GetProperty("streaming_mode").GetBoolean());
        }
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public List<RecordedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(new RecordedRequest(
                request.Method.Method,
                request.RequestUri?.AbsoluteUri ?? "",
                request.Headers.Authorization?.Scheme,
                request.Headers.Authorization?.Parameter,
                request.Content is null
                    ? ""
                    : await request.Content.ReadAsStringAsync(cancellationToken)));

            var isToken = request.RequestUri?.AbsolutePath.EndsWith(
                "/tenant_access_token/internal",
                StringComparison.Ordinal) == true;
            var path = request.RequestUri?.AbsolutePath ?? "";
            if (path.Contains("/resources/", StringComparison.Ordinal))
            {
                var binary = new ByteArrayContent(
                    [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
                binary.Headers.ContentType = new("image/png");
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = binary,
                };
            }

            var json = isToken
                ? """{"code":0,"msg":"ok","tenant_access_token":"tenant-token","expire":7200}"""
                : path.EndsWith("/im/v1/files", StringComparison.Ordinal)
                    ? """{"code":0,"msg":"ok","data":{"file_key":"file_opus_123"}}"""
                : path.EndsWith("/im/v1/images", StringComparison.Ordinal)
                    ? """{"code":0,"msg":"ok","data":{"image_key":"img_generated_123"}}"""
                : path.EndsWith("/cardkit/v1/cards", StringComparison.Ordinal)
                    ? """{"code":0,"msg":"ok","data":{"card_id":"card_123"}}"""
                    : path.EndsWith("/reply", StringComparison.Ordinal)
                        ? """{"code":0,"msg":"ok","data":{"message_id":"om_card_reply"}}"""
                        : """{"code":0,"msg":"ok","data":{}}""";
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    json,
                    Encoding.UTF8,
                    "application/json"),
            };
        }
    }

    private sealed record RecordedRequest(
        string Method,
        string Uri,
        string? AuthorizationScheme,
        string? AuthorizationParameter,
        string Body);
}
