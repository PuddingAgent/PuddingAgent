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

    [Fact]
    public async Task WorkspaceFile_OverTheLimit_IsSplitIntoPartsWithDistinctIdempotencyKeys()
    {
        var (connector, handler, file) = CreateConnector(openId: "ou_explicit", sizeBytes: 31 * 1024 * 1024);

        try
        {
            await connector.SendAsync(WorkspaceFileMessage(file, openId: "ou_explicit", target: "ou_explicit"));

            var uploads = handler.Requests.Where(r => r.Uri.Contains("/im/v1/files", StringComparison.Ordinal)).ToList();
            Assert.Equal(2, uploads.Count);
            Assert.Contains("part01of02", uploads[0].Body, StringComparison.Ordinal);
            Assert.Contains("part02of02", uploads[1].Body, StringComparison.Ordinal);

            var sends = handler.Requests.Where(r => r.Uri.Contains("/im/v1/messages", StringComparison.Ordinal)).ToList();
            // 两段文件 + 一条重组提示
            Assert.Equal(3, sends.Count);

            // 三份幂等键必须互不相同，否则飞书会把后两段当重复请求丢掉。
            var uuids = sends.Select(r => ReadUuid(r.Body)).ToList();
            Assert.All(uuids, u => Assert.False(string.IsNullOrWhiteSpace(u)));
            Assert.Equal(3, uuids.Distinct().Count());

            var hintText = sends
                .Select(r => ReadTextContent(r.Body))
                .SingleOrDefault(t => t is not null && t.Contains("part*of02", StringComparison.Ordinal));
            Assert.NotNull(hintText);
            Assert.Contains("part01of02", uploads[0].Body, StringComparison.Ordinal);
            Assert.Contains("part02of02", uploads[1].Body, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public async Task WorkspaceFile_UnderTheLimit_StaysASingleUploadWithoutHint()
    {
        var (connector, handler, file) = CreateConnector(openId: "ou_explicit");

        try
        {
            await connector.SendAsync(WorkspaceFileMessage(file, openId: "ou_explicit", target: "ou_explicit"));

            Assert.Single(handler.Requests.Where(r => r.Uri.Contains("/im/v1/files", StringComparison.Ordinal)));
            var send = Single(handler, "/im/v1/messages");
            Assert.DoesNotContain("part01of", send.Body, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(file);
        }
    }

    private static string? ReadUuid(string body)
    {
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.TryGetProperty("uuid", out var uuid) ? uuid.GetString() : null;
    }

    /// <summary>把 msg_type=text 的 content 内层 JSON 取出去（外层是字符串，内层才是 {text}）。</summary>
    private static string? ReadTextContent(string body)
    {
        using var doc = JsonDocument.Parse(body);
        if (!doc.RootElement.TryGetProperty("content", out var content))
            return null;
        var raw = content.GetString();
        if (string.IsNullOrWhiteSpace(raw) || !raw.StartsWith('{'))
            return null;
        using var inner = JsonDocument.Parse(raw);
        return inner.RootElement.TryGetProperty("text", out var text) ? text.GetString() : null;
    }

    private static (FeishuConnector Connector, OutboundRecordingHandler Handler, string File) CreateConnector(
        string? openId,
        int sizeBytes = 8)
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
        return (connector, handler, CreateTempPdf(sizeBytes));
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

    private static string CreateTempPdf(int sizeBytes = 8)
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            $"pudding-feishu-outbound-{Guid.NewGuid():N}.pdf");
        var payload = new byte[Math.Max(sizeBytes, 8)];
        new byte[] { 0x25, 0x50, 0x44, 0x46, 0x2D, 0x31, 0x2E, 0x34 }.CopyTo(payload, 0);
        File.WriteAllBytes(path, payload);
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
