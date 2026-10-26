using System.Net;
using System.Security.Cryptography;
using System.Text;
using PuddingFinance.Collection;

namespace PuddingFinanceTests;

/// <summary>
/// 官方源抓取客户端的回归。
///
/// 全部使用假 <see cref="HttpMessageHandler"/>：单测**不得**真的去打官方站点，
/// 否则测试既慢又会因为对方限流/改版而不稳。
/// </summary>
[TestClass]
public sealed class OfficialSourceFetcherTests
{
    [TestMethod]
    public async Task NonWhitelistedHost_IsRejectedBeforeAnyRequest()
    {
        var handler = new StubHandler((_, _) => Text(200, "ok"));
        using var http = new HttpClient(handler);
        var fetcher = new OfficialSourceFetcher(http);

        var error = await Assert.ThrowsExactlyAsync<SourceFetchException>(
            () => fetcher.FetchAsync(new SourceFetchRequest("https://example.com/data")));

        StringAssert.Contains(error.Message, "白名单");
        Assert.IsFalse(error.IsRetryable, "白名单拒绝是确定态");
        Assert.AreEqual(0, handler.CallCount, "被拒绝的请求不得发出");
    }

    [TestMethod]
    public async Task LookalikeHost_IsRejected()
    {
        var handler = new StubHandler((_, _) => Text(200, "ok"));
        using var http = new HttpClient(handler);
        var fetcher = new OfficialSourceFetcher(http);

        // 后缀匹配若写成"包含"，这个域名会被放行——这里钉死它不会。
        await Assert.ThrowsExactlyAsync<SourceFetchException>(
            () => fetcher.FetchAsync(new SourceFetchRequest("https://www.cninfo.com.cn.attacker.com/x")));

        Assert.AreEqual(0, handler.CallCount);
    }

    [TestMethod]
    public async Task EmptyUserInfoInUrl_IsRejected()
    {
        var handler = new StubHandler((_, _) => Text(200, "ok"));
        using var http = new HttpClient(handler);
        var fetcher = new OfficialSourceFetcher(http);

        await Assert.ThrowsExactlyAsync<SourceFetchException>(
            () => fetcher.FetchAsync(new SourceFetchRequest("https://user:pass@www.cninfo.com.cn/x")));

        Assert.AreEqual(0, handler.CallCount);
    }

    [TestMethod]
    public async Task Post_SendsBodyWithDeclaredContentType()
    {
        var handler = new StubHandler((_, _) => Text(200, "{\"result\":1}"));
        using var http = new HttpClient(handler);
        var fetcher = new OfficialSourceFetcher(http);

        var result = await fetcher.FetchAsync(new SourceFetchRequest(
            "https://data.stats.gov.cn/easyquery.htm",
            Method: "post",
            Body: "id=zb&dbcode=hgnd",
            ContentType: "application/x-www-form-urlencoded"));

        Assert.AreEqual(200, result.StatusCode);
        Assert.AreEqual(1, handler.CallCount);
        Assert.AreEqual("POST", handler.Calls[0].Method, "方法名需归一化为大写");
        Assert.AreEqual("id=zb&dbcode=hgnd", handler.Calls[0].Body);
        Assert.AreEqual("application/x-www-form-urlencoded", handler.Calls[0].ContentType);
    }

    [TestMethod]
    public async Task RetryableStatus_RetriesThenSucceeds_AndRecordsStatus()
    {
        var handler = new StubHandler((_, call) => call == 1 ? Text(429, "slow down") : Text(200, "ok"));
        var delays = new List<TimeSpan>();
        using var http = new HttpClient(handler);
        var fetcher = new OfficialSourceFetcher(
            http,
            delay: (duration, _) =>
            {
                delays.Add(duration);
                return Task.CompletedTask;
            });

        var result = await fetcher.FetchAsync(new SourceFetchRequest(
            "https://www.cninfo.com.cn/api", MaxAttempts: 3));

        Assert.AreEqual(2, result.Attempts);
        CollectionAssert.AreEqual(new[] { 429 }, result.RetriedStatusCodes.ToArray());
        Assert.HasCount(1, delays, "只应退避一次");
        Assert.AreEqual(TimeSpan.FromMilliseconds(200), delays[0]);
        Assert.AreEqual("ok", result.Body);
    }

    [TestMethod]
    public async Task NonRetryableStatus_DoesNotRetry()
    {
        var handler = new StubHandler((_, _) => Text(404, "nope"));
        var delays = new List<TimeSpan>();
        using var http = new HttpClient(handler);
        var fetcher = new OfficialSourceFetcher(
            http,
            delay: (duration, _) =>
            {
                delays.Add(duration);
                return Task.CompletedTask;
            });

        var error = await Assert.ThrowsExactlyAsync<SourceFetchException>(
            () => fetcher.FetchAsync(new SourceFetchRequest("https://www.sse.com.cn/x")));

        Assert.AreEqual(404, error.StatusCode);
        Assert.IsFalse(error.IsRetryable);
        Assert.AreEqual(1, handler.CallCount, "4xx（除 408/425/429）不该重试");
        Assert.IsEmpty(delays);
    }

    [TestMethod]
    public async Task RetryExhausted_ThrowsWithLastStatus()
    {
        var handler = new StubHandler((_, _) => Text(503, "maintenance"));
        using var http = new HttpClient(handler);
        var fetcher = new OfficialSourceFetcher(http, delay: (_, _) => Task.CompletedTask);

        var error = await Assert.ThrowsExactlyAsync<SourceFetchException>(
            () => fetcher.FetchAsync(new SourceFetchRequest("https://www.szse.cn/x", MaxAttempts: 3)));

        Assert.AreEqual(3, handler.CallCount);
        Assert.AreEqual(503, error.StatusCode);
        Assert.IsTrue(error.IsRetryable, "5xx 属于临时态，应标记可重试");
    }

    [TestMethod]
    public async Task OversizedBody_IsTruncatedAndFlagged()
    {
        var payload = new string('x', 4096);
        var handler = new StubHandler((_, _) => Text(200, payload));
        using var http = new HttpClient(handler);
        var fetcher = new OfficialSourceFetcher(http);

        var result = await fetcher.FetchAsync(new SourceFetchRequest(
            "https://www.bse.cn/big", MaxBytes: 100));

        Assert.IsTrue(result.Truncated, "超限必须如实置位，不能假装是完整内容");
        Assert.AreEqual(100, result.Bytes);
        Assert.AreEqual(100, result.Body.Length);
    }

    [TestMethod]
    public async Task Sha256_MatchesReturnedBytes()
    {
        const string payload = "{\"t\":\"2026-10-26\",\"p\":11.2}";
        var handler = new StubHandler((_, _) => Text(200, payload));
        using var http = new HttpClient(handler);
        var fetcher = new OfficialSourceFetcher(http);

        var result = await fetcher.FetchAsync(new SourceFetchRequest("https://www.csindex.com.cn/x"));

        var expected = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();
        Assert.AreEqual(expected, result.Sha256, "指纹必须对「实际返回的字节」计算");
        Assert.AreEqual(Encoding.UTF8.GetByteCount(payload), result.Bytes);
    }

    private static HttpResponseMessage Text(int status, string body)
    {
        return new HttpResponseMessage((HttpStatusCode)status)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
    }

    private sealed class StubHandler(
        Func<HttpRequestMessage, int, HttpResponseMessage> responder) : HttpMessageHandler
    {
        public int CallCount { get; private set; }

        public List<(string Method, string? Body, string? ContentType)> Calls { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            CallCount++;
            var body = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            Calls.Add((request.Method.Method, body, request.Content?.Headers.ContentType?.MediaType));
            return responder(request, CallCount);
        }
    }
}
