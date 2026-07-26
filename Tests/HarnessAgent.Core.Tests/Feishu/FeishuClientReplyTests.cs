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

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public List<RecordedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(new RecordedRequest(
                request.RequestUri?.AbsoluteUri ?? "",
                request.Headers.Authorization?.Scheme,
                request.Headers.Authorization?.Parameter,
                request.Content is null
                    ? ""
                    : await request.Content.ReadAsStringAsync(cancellationToken)));

            var isToken = request.RequestUri?.AbsolutePath.EndsWith(
                "/tenant_access_token/internal",
                StringComparison.Ordinal) == true;
            var json = isToken
                ? """{"code":0,"msg":"ok","tenant_access_token":"tenant-token","expire":7200}"""
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
        string Uri,
        string? AuthorizationScheme,
        string? AuthorizationParameter,
        string Body);
}
