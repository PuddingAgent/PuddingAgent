using PuddingDeepSeekWeb;
using PuddingRuntime.Services.DeepSeekWeb;
using PuddingRuntime.Services.Skills;
using PuddingRuntimeTests.Tools;

namespace PuddingRuntimeTests.Services;

/// <summary>
/// <see cref="WebClientDeepSeekTransport"/> 的边界测试：请求/响应<b>逐字段原样透传</b>，
/// 不做错误分类、不重试、不吞异常（这些属于组件职责）。
/// </summary>
[TestClass]
public sealed class WebClientDeepSeekTransportTests
{
    [TestMethod]
    public async Task SendAsync_Maps_Request_Faithfully_And_Returns_Raw_Response()
    {
        var webClientResponse = new WebClientResponse
        {
            StatusCode = 429,
            ReasonPhrase = "Too Many Requests",
            ContentType = "application/json",
            Body = """{"error":{"message":"rate limited"}}""",
            Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Retry-After"] = "3",
            },
            FinalUrl = "https://api.deepseek.com/v1/tools/search",
        };
        var webClient = new RecordingWebClient(webClientResponse);
        var transport = new WebClientDeepSeekTransport(webClient);

        var requestHeaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Authorization"] = "Bearer secret-key",
        };
        var response = await transport.SendAsync(new DeepSeekWebTransportRequest
        {
            Url = "https://api.deepseek.com/v1/tools/search",
            Method = "POST",
            Headers = requestHeaders,
            Body = """{"query":"deepseek"}""",
            ContentType = "application/json",
            TimeoutSeconds = 45,
        });

        var mapped = webClient.LastRequest;
        Assert.IsNotNull(mapped);
        Assert.AreEqual("https://api.deepseek.com/v1/tools/search", mapped.Url);
        Assert.AreEqual("POST", mapped.Method);
        Assert.AreEqual("Bearer secret-key", mapped.Headers["Authorization"]);
        Assert.AreEqual("""{"query":"deepseek"}""", mapped.Body);
        Assert.AreEqual("application/json", mapped.ContentType);
        Assert.AreEqual(45, mapped.TimeoutSeconds);

        // 响应逐字段原样：429 不会被适配器改写/分类。
        Assert.AreEqual(429, response.StatusCode);
        Assert.AreEqual("Too Many Requests", response.ReasonPhrase);
        Assert.AreEqual(webClientResponse.Body, response.Body);
        Assert.AreEqual("3", response.GetHeader("Retry-After"));
    }

    [TestMethod]
    public async Task SendAsync_Does_Not_Retry_Or_Classify_NonSuccess_Status()
    {
        var webClient = new RecordingWebClient(
            DeepSeekWebTestTools.JsonResponse(500, """{"error":"boom"}""", "Internal Server Error"),
            DeepSeekWebTestTools.JsonResponse(200, """{"results":[]}"""));
        var transport = new WebClientDeepSeekTransport(webClient);

        var response = await transport.SendAsync(new DeepSeekWebTransportRequest
        {
            Url = "https://api.deepseek.com/v1/tools/search",
            Body = "{}",
        });

        Assert.AreEqual(1, webClient.Requests.Count, "适配器不得重试（重试是组件职责）。");
        Assert.AreEqual(500, response.StatusCode, "适配器不得把 5xx 改写为异常或分类结果。");
    }

    [TestMethod]
    public async Task SendAsync_Propagates_Transport_Exceptions()
    {
        var webClient = new RecordingWebClient
        {
            ExceptionToThrow = new InvalidOperationException("network down"),
        };
        var transport = new WebClientDeepSeekTransport(webClient);

        var exception = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => transport.SendAsync(new DeepSeekWebTransportRequest
            {
                Url = "https://api.deepseek.com/v1/tools/fetch",
                Body = """{"url":"https://example.com/"}""",
            }));

        StringAssert.Contains(exception.Message, "network down");
    }
}
