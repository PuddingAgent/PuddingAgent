using System.Net;
using System.Text;
using System.Text.Json;
using PuddingCode.Core;
using PuddingCode.Models;

namespace PuddingCoreTests;

/// <summary>
/// responses 协议的出站断言：思考强度必须真的出现在报文里。
/// 这一层是整条链的最后一跳——前端选择、快照透传、策略合成都可以正确，
/// 但如果网关没把它写进 JSON，用户看到的区别只是「多花钱」而不是「想得更深」。
/// </summary>
[TestClass]
public sealed class ResponsesLlmGatewayReasoningEffortTests
{
    [TestMethod]
    public async Task ChatAsync_WritesReasoningEffortIntoTheRequestBody()
    {
        var handler = new CapturingHandler();
        using var http = new HttpClient(handler);
        var options = new PuddingCode.Platform.Options.LlmOptions(
            "https://example.test/v1", "test-key", "deepseek-flash",
            ReasoningEffort: "max");
        var gateway = new ResponsesLlmGateway(http, options);

        await Assert.ThrowsExactlyAsync<HttpRequestException>(() => gateway.ChatAsync(
            [new ChatMessage(ChatRole.User, "hi")],
            [],
            CancellationToken.None));

        Assert.IsNotNull(handler.Body, "网关必须发出请求，否则本测试无意义。");
        using var document = JsonDocument.Parse(handler.Body!);
        Assert.IsTrue(
            document.RootElement.TryGetProperty("reasoning", out var reasoning),
            "报文里必须出现 reasoning 段，否则 provider 只会用默认强度。");
        Assert.AreEqual("max", reasoning.GetProperty("effort").GetString());
    }

    [TestMethod]
    public async Task ChatAsync_OmitsReasoningSectionWhenEffortIsAbsent()
    {
        var handler = new CapturingHandler();
        using var http = new HttpClient(handler);
        var options = new PuddingCode.Platform.Options.LlmOptions(
            "https://example.test/v1", "test-key", "deepseek-flash");
        var gateway = new ResponsesLlmGateway(http, options);

        await Assert.ThrowsExactlyAsync<HttpRequestException>(() => gateway.ChatAsync(
            [new ChatMessage(ChatRole.User, "hi")],
            [],
            CancellationToken.None));

        using var document = JsonDocument.Parse(handler.Body!);
        Assert.IsFalse(
            document.RootElement.TryGetProperty("reasoning", out _),
            "未指定强度时不得伪造 reasoning 段——留空与 high 是两回事。");
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Body = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json"),
            };
        }
    }
}
