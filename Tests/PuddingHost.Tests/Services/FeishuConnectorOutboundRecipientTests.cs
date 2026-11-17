using System.Net;
using System.Text;
using System.Text.Json;
using HarnessAgent.Core.Connectors.Feishu;
using Microsoft.Extensions.Logging.Abstractions;
using PuddingAgent.Connectors;
using PuddingCode.Platform;

namespace PuddingHost.Tests.Services;

/// <summary>
/// 连接器出站收件人：用注入的假 HTTP handler 断言真实发出去的 URL。
///
/// <para>
/// 为什么需要这一层：`FeishuRecipientResolver` 只是纯函数，它能证明「判定正确」，
/// 证明不了「连接器真的用了它」。2026-11-17 之前语音 / 图片 / 文件三条路径各自
/// 硬编码 `receive_id_type = "chat_id"`，而 <c>send_message</c> 给显式收件人
/// （<c>user:ou_xxx</c>）时只写 <c>open_id</c>，于是 open_id 被当成 chat_id 提交。
/// </para>
///
/// <para>
/// 这里用构造函数里的 `clientFactory` 测试缝（此前从未被使用）注入
/// <see cref="FeishuClient"/>，因此不需要启动 WebSocket、不需要网络。
/// </para>
/// </summary>
public sealed class FeishuConnectorOutboundRecipientTests
{
    [Fact]
    public async Task WorkspaceFile_ToExplicitOpenId_UsesOpenIdReceiveIdType()
    {
        var (connector, handler, file) = CreateConnector(openId: "ou_explicit");

        try
        {
            await connector.SendAsync(WorkspaceFileMessage(file, openId: "ou_explicit", target: "ou_explicit"));

            var send = Single(handler, "/im/v1/messages");
            Assert.True(
                send.Uri.Contains("receive_id_type=open_id", StringComparison.Ordinal),
                $"显式收件人必须按 open_id 发送，实际 URL：{send.Uri}");
            Assert.Equal("ou_explicit", ReadReceiveId(send.Body));
            Assert.DoesNotContain(handler.Requests, r => r.Uri.Contains("/reply", StringComparison.Ordinal));
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public async Task WorkspaceFile_WithoutExplicitOpenId_FallsBackToChatIdType()
    {
        var (connector, handler, file) = CreateConnector(openId: null);

        try
        {
            await connector.SendAsync(WorkspaceFileMessage(file, openId: null, target: "oc_group_chat"));

            var send = Single(handler, "/im/v1/messages");
            Assert.True(
                send.Uri.Contains("receive_id_type=chat_id", StringComparison.Ordinal),
                $"没有显式 open_id 时按 chat_id 发送，实际 URL：{send.Uri}");
            Assert.Equal("oc_group_chat", ReadReceiveId(send.Body));
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public async Task WorkspaceFile_WithBlankOpenId_FallsBackToChatIdType()
    {
        // 空串 open_id 不得遮蔽真正的群聊目标，否则会把空目标发给飞书。
        var (connector, handler, file) = CreateConnector(openId: "   ");

        try
        {
            await connector.SendAsync(WorkspaceFileMessage(file, openId: "   ", target: "oc_group_chat"));

            var send = Single(handler, "/im/v1/messages");
            Assert.True(
                send.Uri.Contains("receive_id_type=chat_id", StringComparison.Ordinal),
                $"空 open_id 必须回落 chat_id，实际 URL：{send.Uri}");
            Assert.Equal("oc_group_chat", ReadReceiveId(send.Body));
        }
        finally
        {
            File.Delete(file);
        }
    }

    private static (FeishuConnector Connector, OutboundRecordingHandler Handler, string File) CreateConnector(
        string? openId)
    {
        _ = openId;
        var handler = new OutboundRecordingHandler();
        var http = new HttpClient(handler);
        // 四个注入服务在本路径上不被使用（文件投递只用 _client 与取材策略），
        // 因此传 null 以免为它们编造实现；一旦将来这些依赖进入本路径，测试会以 NRE 暴露。
        var connector = new FeishuConnector(
            new FeishuConnectorBinding(
                "agent",
                "default",
                "cli_test",
                "secret_test",
                Description: null,
                ChannelId: "channel",
                TtsRepliesEnabled: false,
                TtsVoice: "Cherry"),
            NullLogger<FeishuConnector>.Instance,
            inboundMessageMapper: null!,
            ttsDeliveryService: null!,
            imageUploadPreparation: null!,
            visionArtifacts: null!,
            clientFactory: config => new FeishuClient(config, http));
        return (connector, handler, CreateTempPdf());
    }

    private static ConnectorMessage WorkspaceFileMessage(string path, string? openId, string target)
    {
        var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [ConnectorPayloadMetadata.Kind] = ConnectorPayloadKinds.WorkspaceFile,
            [ConnectorPayloadMetadata.SourcePath] = path,
            ["uuid"] = "stable-file-uuid",
        };
        if (openId is not null)
            metadata["open_id"] = openId;
        return new ConnectorMessage
        {
            Target = target,
            Content = string.Empty,
            Metadata = metadata,
        };
    }

    private static string CreateTempPdf()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            $"pudding-feishu-outbound-{Guid.NewGuid():N}.pdf");
        File.WriteAllBytes(path, [0x25, 0x50, 0x44, 0x46, 0x2D, 0x31, 0x2E, 0x34]);
        return path;
    }

    private static RecordedRequest Single(OutboundRecordingHandler handler, string uriFragment)
        => Assert.Single(handler.Requests, r => r.Uri.Contains(uriFragment, StringComparison.Ordinal));

    private static string? ReadReceiveId(string body)
    {
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.GetProperty("receive_id").GetString();
    }

    private sealed record RecordedRequest(string Method, string Uri, string Body);

    private sealed class OutboundRecordingHandler : HttpMessageHandler
    {
        public List<RecordedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);
            var uri = request.RequestUri!.ToString();
            Requests.Add(new RecordedRequest(request.Method.Method, uri, body));

            // 三种端点：取 token、上传文件、发消息。
            string json;
            if (uri.Contains("/auth/v3/tenant_access_token", StringComparison.Ordinal))
                json = """{"code":0,"msg":"ok","tenant_access_token":"tenant-token","expire":7200}""";
            else if (uri.Contains("/im/v1/files", StringComparison.Ordinal))
                json = """{"code":0,"msg":"ok","data":{"file_key":"file_pdf_1"}}""";
            else
                json = """{"code":0,"msg":"ok","data":{"message_id":"om_sent"}}""";

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            };
        }
    }
}
