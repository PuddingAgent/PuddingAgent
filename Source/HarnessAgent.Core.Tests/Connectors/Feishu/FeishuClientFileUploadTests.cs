using System.Net;
using System.Text;
using HarnessAgent.Core.Connectors.Feishu;

namespace HarnessAgent.Core.Tests.Connectors.Feishu;

/// <summary>
/// 2026-11-16：飞书文件上传通道。
///
/// <para>
/// 这条通道原本写死为音频（file_type=opus、msg_type=audio）。泛化之后必须同时钉住两件事：
/// ① 任意文件能按传入的 file_type 上传并以 msg_type=file 投递；
/// ② 音频那条老路径的 file_type 仍然是 opus、Content-Type 仍然是 audio/ogg ——
/// 否则「泛化」就是悄悄改了语音的行为，而语音是每天在用的。
/// </para>
/// </summary>
[TestClass]
public sealed class FeishuClientFileUploadTests
{
    private const string TokenBody =
        "{\"code\":0,\"msg\":\"ok\",\"tenant_access_token\":\"t-test\",\"expire\":7200}";

    [TestMethod]
    public async Task UploadFileAsync_Sends_Pdf_As_Declared_FileType()
    {
        var handler = new RecordingHandler(
            TokenBody,
            "{\"code\":0,\"msg\":\"ok\",\"data\":{\"file_key\":\"file_v2_abc\"}}");
        using var client = CreateClient(handler);

        var result = await client.UploadFileAsync(
            Encoding.UTF8.GetBytes("%PDF-1.4 test"),
            "how-to-live-better.pdf",
            "pdf",
            durationMs: null,
            contentType: "application/pdf");

        Assert.AreEqual(0, result.Code);
        Assert.AreEqual("file_v2_abc", result.Data?.FileKey);

        var upload = handler.Requests.Single(r => r.Url.Contains("/im/v1/files", StringComparison.Ordinal));
        Assert.AreEqual("pdf", MultipartField(upload.Body, "file_type"), "file_type 必须取调用方给的值");
        Assert.AreEqual("how-to-live-better.pdf", MultipartField(upload.Body, "file_name"));
        Assert.IsNotNull(MultipartField(upload.Body, "file"), "必须真的把文件字节放进 file 段");
        Assert.AreEqual("Bearer t-test", upload.Authorization);
    }

    [TestMethod]
    public async Task UploadAudioAsync_Still_Uses_Opus_FileType()
    {
        var handler = new RecordingHandler(
            TokenBody,
            "{\"code\":0,\"msg\":\"ok\",\"data\":{\"file_key\":\"file_v2_voice\"}}");
        using var client = CreateClient(handler);

        await client.UploadAudioAsync(Encoding.UTF8.GetBytes("OggS test"), "pudding-1.opus", durationMs: 1234);

        var upload = handler.Requests.Single(r => r.Url.Contains("/im/v1/files", StringComparison.Ordinal));
        Assert.AreEqual("opus", MultipartField(upload.Body, "file_type"), "音频老路径的 file_type 不得被泛化改掉");
        Assert.AreEqual("1234", MultipartField(upload.Body, "duration"), "音频仍需带 duration");
        StringAssert.Contains(upload.Body, "audio/ogg", "音频段的 Content-Type 必须仍是 audio/ogg");
    }

    [TestMethod]
    public async Task ReplyFileAsync_Uses_File_Message_Type()
    {
        var handler = new RecordingHandler(
            TokenBody,
            "{\"code\":0,\"msg\":\"ok\",\"data\":{\"message_id\":\"om_reply\"}}");
        using var client = CreateClient(handler);

        var result = await client.ReplyFileAsync("om_inbound", "file_v2_abc", uuid: "u-1");

        Assert.AreEqual(0, result.Code);
        var reply = handler.Requests.Single(r => r.Url.Contains("/reply", StringComparison.Ordinal));
        StringAssert.Contains(reply.Url, "/im/v1/messages/om_inbound/reply");
        StringAssert.Contains(reply.Body, "\"msg_type\":\"file\"", "file_key 必须以 file 类型投递");
        StringAssert.Contains(reply.Body, "file_v2_abc");
    }

    private static FeishuClient CreateClient(RecordingHandler handler)
        => new(
            new FeishuConfig { AppId = "cli_test", AppSecret = "secret_test" },
            new HttpClient(handler));

    /// <summary>从 multipart 原文里取某个表单项的值（不含边界与头）。</summary>
    private static string? MultipartField(string raw, string name)
    {
        var marker = "name=" + name;
        var idx = raw.IndexOf(marker, StringComparison.Ordinal);
        if (idx < 0)
            return null;

        var start = raw.IndexOf("\r\n\r\n", idx, StringComparison.Ordinal);
        if (start < 0)
            return null;
        start += 4;

        var end = raw.IndexOf("\r\n", start, StringComparison.Ordinal);
        return end < 0 ? raw[start..] : raw[start..end];
    }

    private sealed class RecordingHandler(params string[] responses) : HttpMessageHandler
    {
        private int _cursor;

        public List<(string Url, string Body, string? Authorization)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add((
                request.RequestUri?.ToString() ?? string.Empty,
                body,
                request.Headers.Authorization?.ToString()));

            var payload = _cursor < responses.Length
                ? responses[_cursor++]
                : "{\"code\":0,\"msg\":\"ok\",\"data\":{}}";
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json"),
            };
        }
    }
}
