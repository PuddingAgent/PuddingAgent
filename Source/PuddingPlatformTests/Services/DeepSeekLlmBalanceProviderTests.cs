using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using PuddingCode.Configuration;
using PuddingPlatform.Data.Dtos;
using PuddingPlatform.Services;

namespace PuddingPlatformTests.Services;

/// <summary>
/// DeepSeek 余额适配器测试——FakeHttpMessageHandler 直构（项目无 mock 框架惯例）。
/// 覆盖：响应解析（字符串数字）、/v1 剥离、Bearer 头、非 2xx 优雅降级、网络错误抛出、CanHandle 矩阵。
/// </summary>
[TestClass]
public sealed class DeepSeekLlmBalanceProviderTests
{
    private sealed class FakeHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;
        public HttpRequestMessage? LastRequest { get; private set; }

        public FakeHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> respond)
            => _respond = respond;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            return Task.FromResult(_respond(request));
        }
    }

    private sealed class StubHttpClientFactory : IHttpClientFactory
    {
        private readonly HttpMessageHandler _handler;

        public StubHttpClientFactory(HttpMessageHandler handler) => _handler = handler;

        public HttpClient CreateClient(string name) => new(_handler, disposeHandler: false);
    }

    private static HttpResponseMessage Json(HttpStatusCode statusCode, string json) =>
        new(statusCode)
        {
            Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
        };

    private static PuddingLlmProviderConfig Provider(
        string providerId = "deepseek",
        string baseUrl = "https://api.deepseek.com") =>
        new() { ProviderId = providerId, Name = providerId, BaseUrl = baseUrl };

    private static DeepSeekLlmBalanceProvider CreateProvider(FakeHttpMessageHandler handler) =>
        new(
            new StubHttpClientFactory(handler),
            NullLogger<DeepSeekLlmBalanceProvider>.Instance);

    [TestMethod]
    public async Task QueryAsync_ParsesBalanceResponse_WithStringAmounts()
    {
        var handler = new FakeHttpMessageHandler(_ => Json(HttpStatusCode.OK,
            """
            {"is_available":true,"balance_infos":[{"currency":"CNY","total_balance":"110.00","granted_balance":"10.00","topped_up_balance":"100.00"}]}
            """));
        var provider = CreateProvider(handler);

        var dto = await provider.QueryAsync(Provider(), "sk-test");

        Assert.IsTrue(dto.IsAvailable);
        Assert.IsNull(dto.Error);
        Assert.AreEqual(1, dto.BalanceInfos.Count);
        var info = dto.BalanceInfos[0];
        Assert.AreEqual("CNY", info.Currency);
        Assert.AreEqual(110.00m, info.TotalBalance);
        Assert.AreEqual(10.00m, info.GrantedBalance);
        Assert.AreEqual(100.00m, info.ToppedUpBalance);
        Assert.AreEqual("https://api.deepseek.com/user/balance", dto.Endpoint);
    }

    [TestMethod]
    public async Task QueryAsync_StripsTrailingV1_FromBaseUrl()
    {
        var handler = new FakeHttpMessageHandler(_ =>
            Json(HttpStatusCode.OK, """{"is_available":true,"balance_infos":[]}"""));
        var provider = CreateProvider(handler);

        var dto = await provider.QueryAsync(Provider(baseUrl: "https://api.deepseek.com/v1/"), "sk-test");

        Assert.AreEqual(
            "https://api.deepseek.com/user/balance",
            handler.LastRequest!.RequestUri!.ToString());
        Assert.AreEqual("https://api.deepseek.com/user/balance", dto.Endpoint);
    }

    [TestMethod]
    public async Task QueryAsync_SendsBearerAuthorization()
    {
        var handler = new FakeHttpMessageHandler(_ =>
            Json(HttpStatusCode.OK, """{"is_available":true,"balance_infos":[]}"""));
        var provider = CreateProvider(handler);

        await provider.QueryAsync(Provider(), "sk-test");

        var authorization = handler.LastRequest!.Headers.Authorization;
        Assert.AreEqual("Bearer", authorization!.Scheme);
        Assert.AreEqual("sk-test", authorization.Parameter);
    }

    [TestMethod]
    public async Task QueryAsync_NonSuccess_ReturnsErrorDto_WithUpstreamMessage()
    {
        var handler = new FakeHttpMessageHandler(_ => Json(HttpStatusCode.Unauthorized,
            """
            {"error":{"message":"authentication_error","type":"authentication_error","param":null,"code":"invalid_request_error"}}
            """));
        var provider = CreateProvider(handler);

        var dto = await provider.QueryAsync(Provider(), "sk-bad");

        Assert.IsFalse(dto.IsAvailable);
        Assert.AreEqual("authentication_error", dto.Error);
        Assert.AreEqual(0, dto.BalanceInfos.Count);
    }

    [TestMethod]
    public async Task QueryAsync_NonSuccess_NonJsonBody_FallsBackToStatusCode()
    {
        var handler = new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound)
        {
            Content = new StringContent("Not Found", System.Text.Encoding.UTF8, "text/plain"),
        });
        var provider = CreateProvider(handler);

        var dto = await provider.QueryAsync(Provider(), "sk-test");

        Assert.IsFalse(dto.IsAvailable);
        Assert.AreEqual("余额 API 返回状态码 404", dto.Error);
    }

    [TestMethod]
    public async Task QueryAsync_NetworkError_ThrowsHttpRequestException()
    {
        var handler = new FakeHttpMessageHandler(_ =>
            throw new HttpRequestException("connection refused"));
        var provider = CreateProvider(handler);

        await Assert.ThrowsExactlyAsync<HttpRequestException>(
            () => provider.QueryAsync(Provider(), "sk-test"));
    }

    [TestMethod]
    public void CanHandle_MatchesDeepSeekProviderIdOrBaseUrl()
    {
        var provider = new DeepSeekLlmBalanceProvider();

        Assert.IsTrue(provider.CanHandle(Provider(
            providerId: "deepseek", baseUrl: "https://proxy.example.com/v1")));
        Assert.IsTrue(provider.CanHandle(Provider(
            providerId: "DeepSeek-CN", baseUrl: "https://proxy.example.com")));
        Assert.IsTrue(provider.CanHandle(Provider(
            providerId: "my-proxy", baseUrl: "https://api.deepseek.com/v1")));
        Assert.IsFalse(provider.CanHandle(Provider(
            providerId: "moonshot", baseUrl: "https://api.moonshot.cn/v1")));
    }

    [TestMethod]
    public void BuildBalanceUrl_AppendsUserBalance_AndStripsV1()
    {
        Assert.AreEqual(
            "https://api.deepseek.com/user/balance",
            DeepSeekLlmBalanceProvider.BuildBalanceUrl("https://api.deepseek.com"));
        Assert.AreEqual(
            "https://api.deepseek.com/user/balance",
            DeepSeekLlmBalanceProvider.BuildBalanceUrl("https://api.deepseek.com/"));
        Assert.AreEqual(
            "https://api.deepseek.com/user/balance",
            DeepSeekLlmBalanceProvider.BuildBalanceUrl("https://api.deepseek.com/v1"));
        Assert.AreEqual(
            "https://api.deepseek.com/user/balance",
            DeepSeekLlmBalanceProvider.BuildBalanceUrl("https://api.deepseek.com/v1/"));
        Assert.AreEqual(
            "https://api.deepseek.com/user/balance",
            DeepSeekLlmBalanceProvider.BuildBalanceUrl("https://api.deepseek.com/user/balance"));
    }
}
