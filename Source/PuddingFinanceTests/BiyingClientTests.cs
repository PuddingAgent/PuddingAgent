using System.Net;
using System.Text;
using PuddingFinance.Biying;

namespace PuddingFinanceTests;

/// <summary>
/// 客户端行为的回归。全部用假 HttpMessageHandler：单测**不应该**花掉真实的
/// 10,000 次/天配额——那正是考核里最容易犯的错。
/// </summary>
[TestClass]
public sealed class BiyingClientTests
{
    private const string Licence = "08AF0000-0000-0000-0000-000000000000";

    [TestMethod]
    public async Task SecondIdenticalCall_HitsCache_AndDoesNotConsumeQuotaAgain()
    {
        var handler = new StubHandler(_ => Json(200, """[{"t":"2026-10-23 15:35:12","p":11.2}]"""));
        using var http = new HttpClient(handler);
        var client = Build(http, dailyQuota: 10);

        var first = await client.CallAsync("54", ["000001"]);
        var second = await client.CallAsync("54", ["000001"]);

        Assert.IsFalse(first.FromCache);
        Assert.IsTrue(second.FromCache, "同样的 URL 第二次应命中缓存");
        Assert.AreEqual(1, handler.CallCount, "缓存命中不得再发请求");
        Assert.AreEqual(1, client.Quota.Used, "配额只应消耗一次");
    }

    [TestMethod]
    public async Task QuotaExhausted_ThrowsBeforeSendingRequest()
    {
        var handler = new StubHandler(_ => Json(200, "[]"));
        using var http = new HttpClient(handler);
        var client = Build(http, dailyQuota: 1);

        await client.CallAsync("54", ["000001"]);

        var error = await Assert.ThrowsExactlyAsync<BiyingCallException>(
            () => client.CallAsync("54", ["000002"]));

        StringAssert.Contains(error.Message, "配额已用尽");
        Assert.IsFalse(error.IsRetryable, "配额耗尽是确定态，重试只会更糟");
        Assert.AreEqual(1, handler.CallCount, "配额耗尽后不得再发请求");
    }

    [TestMethod]
    public async Task LicenceRejection_IsNotRetryable()
    {
        var handler = new StubHandler(_ => Json(404, """{"code":404,"message":"Licence证书不存在","licence":"000001"}"""));
        using var http = new HttpClient(handler);
        var client = Build(http);

        var error = await Assert.ThrowsExactlyAsync<BiyingCallException>(
            () => client.CallAsync("54", ["000001"]));

        Assert.IsFalse(error.IsRetryable);
        StringAssert.Contains(error.Message, "证书无效");
    }

    [TestMethod]
    public async Task BusinessErrorInsideHttp200_IsDetected()
    {
        // 实测：必盈失败时 HTTP 仍是 200，错误藏在 body 的 code 字段里
        var handler = new StubHandler(_ => Json(200, """{"code":502,"message":"bypass miss; FastAPI removed"}"""));
        using var http = new HttpClient(handler);
        var client = Build(http);

        var error = await Assert.ThrowsExactlyAsync<BiyingCallException>(
            () => client.CallAsync("54", ["000001"]));

        Assert.AreEqual(502, error.ProviderCode);
        Assert.IsTrue(error.IsRetryable, "5xx 类业务错误可退避重试");
    }

    [TestMethod]
    public async Task ServerError_IsRetryable()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.BadGateway)
        {
            Content = new StringContent("upstream boom", Encoding.UTF8),
        });
        using var http = new HttpClient(handler);
        var client = Build(http);

        var error = await Assert.ThrowsExactlyAsync<BiyingCallException>(
            () => client.CallAsync("54", ["000001"]));

        Assert.IsTrue(error.IsRetryable);
        StringAssert.Contains(error.Message, "502");
    }

    [TestMethod]
    public async Task CacheExpires_AfterRealtimeTtl()
    {
        var clock = new FakeClock(DateTimeOffset.Parse("2026-10-25T06:00:00Z"));
        var handler = new StubHandler(_ => Json(200, "[]"));
        using var http = new HttpClient(handler);
        var client = Build(http, clock: clock);

        await client.CallAsync("54", ["000001"]);
        clock.Advance(TimeSpan.FromSeconds(5));
        var cachedResult = await client.CallAsync("54", ["000001"]);
        clock.Advance(TimeSpan.FromSeconds(20));
        var refreshed = await client.CallAsync("54", ["000001"]);

        Assert.IsTrue(cachedResult.FromCache, "10 秒内应命中缓存");
        Assert.IsFalse(refreshed.FromCache, "超过 TTL 应重新请求");
        Assert.AreEqual(2, handler.CallCount);
    }

    [TestMethod]
    public void CacheTtl_IsClassifiedByEndpointKind()
    {
        var options = new BiyingOptions { Licence = Licence };
        var catalog = BiyingEndpointCatalog.LoadEmbedded();

        Assert.AreEqual(options.RealtimeCacheTtl, BiyingClient.ResolveTtl(catalog.Get("54"), options), "实时行情");
        Assert.AreEqual(options.DailyCacheTtl, BiyingClient.ResolveTtl(catalog.Get("314"), options), "历史分时");
        Assert.AreEqual(options.StaticCacheTtl, BiyingClient.ResolveTtl(catalog.Get("255"), options), "资产负债表");
    }

    [TestMethod]
    public async Task ResponseExceedingByteCap_IsRejected()
    {
        var handler = new StubHandler(_ => Json(200, new string('x', 4096)));
        using var http = new HttpClient(handler);
        var client = Build(http, maxBytes: 1024);

        var error = await Assert.ThrowsExactlyAsync<BiyingCallException>(
            () => client.CallAsync("54", ["000001"]));

        Assert.IsFalse(error.IsRetryable);
        StringAssert.Contains(error.Message, "超过本地上限");
    }

    [TestMethod]
    public async Task ErrorMessages_NeverLeakLicence()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError)
        {
            Content = new StringContent("boom", Encoding.UTF8),
        });
        using var http = new HttpClient(handler);
        var client = Build(http);

        var error = await Assert.ThrowsExactlyAsync<BiyingCallException>(
            () => client.CallAsync("54", ["000001"]));

        Assert.DoesNotContain(Licence, error.Message, "异常消息里不得出现证书原文");
        StringAssert.Contains(error.Message, "<licence>");
    }

    [TestMethod]
    public void Quota_RollsOverOnNextUtcDay()
    {
        var clock = new FakeClock(DateTimeOffset.Parse("2026-10-25T23:50:00Z"));
        var quota = new BiyingQuota(2, 0.5, clock);

        Assert.IsTrue(quota.TryConsume(out _));
        Assert.IsTrue(quota.TryConsume(out _));
        Assert.IsFalse(quota.TryConsume(out _));
        Assert.IsTrue(quota.IsWarning);

        clock.Advance(TimeSpan.FromMinutes(20)); // 跨 UTC 日

        Assert.AreEqual(0, quota.Used, "跨日应重置");
        Assert.IsTrue(quota.TryConsume(out var remaining));
        Assert.AreEqual(1, remaining);
    }

    private static BiyingClient Build(
        HttpClient http,
        int dailyQuota = 10_000,
        IBiyingClock? clock = null,
        int maxBytes = 8 * 1024 * 1024)
    {
        var options = new BiyingOptions
        {
            Licence = Licence,
            DailyQuota = dailyQuota,
            MaxResponseBytes = maxBytes,
        };
        return new BiyingClient(http, BiyingEndpointCatalog.LoadEmbedded(), options, clock);
    }

    private static HttpResponseMessage Json(int status, string body) => new((HttpStatusCode)status)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public int CallCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult(respond(request));
        }
    }

    private sealed class FakeClock(DateTimeOffset start) : IBiyingClock
    {
        private DateTimeOffset _now = start;

        public DateTimeOffset UtcNow => _now;

        public void Advance(TimeSpan delta) => _now += delta;
    }
}
