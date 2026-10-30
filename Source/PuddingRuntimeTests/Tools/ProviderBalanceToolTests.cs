using System.Net;
using System.Net.Http.Headers;
using PuddingRuntime.Services.Tools;

namespace PuddingRuntimeTests.Tools;

/// <summary>
/// provider_balance 工具的契约测试：解析、端点拼接、错误上报，以及**凭据不外泄**。
/// <para>全程用假的 <see cref="HttpMessageHandler"/>，不联网、不用真 key。</para>
/// </summary>
[TestClass]
public sealed class ProviderBalanceToolTests
{
    private const string FakeKey = "TESTKEY-SHOULD-NOT-LEAK";
    private const string BaseUrl = "https://api.example.test";

    private static readonly string ConfigJson = $$"""
    {
      "providers": [
        { "providerId": "deepseek", "baseUrl": "{{BaseUrl}}", "apiKey": "{{FakeKey}}", "isEnabled": true },
        { "providerId": "disabled-one", "baseUrl": "{{BaseUrl}}", "apiKey": "{{FakeKey}}", "isEnabled": false },
        { "providerId": "no-key", "baseUrl": "{{BaseUrl}}", "isEnabled": true }
      ]
    }
    """;

    /// <summary>DeepSeek 形状的余额响应要能被解析成三个金额，且请求打到 /user/balance 并带 Bearer。</summary>
    [TestMethod]
    public async Task Query_ParsesBalance_And_PostsBearerToUserBalance()
    {
        HttpRequestMessage? seen = null;
        var http = new HttpClient(new StubHandler(request =>
        {
            seen = request;
            return Json("""
            {"is_available":true,"balance_infos":[
              {"currency":"CNY","total_balance":"254.92","granted_balance":"0.00","topped_up_balance":"254.92"}]}
            """);
        }));

        var endpoint = new ProviderBalanceEndpoint("deepseek", BaseUrl, FakeKey, "apiKey");
        var snapshot = await ProviderBalanceTool.QueryAsync(http, endpoint, CancellationToken.None);

        Assert.IsTrue(snapshot.IsAvailable);
        Assert.AreEqual("api.example.test", snapshot.Host);
        Assert.HasCount(1, snapshot.Balances);
        Assert.AreEqual("CNY", snapshot.Balances[0].Currency);
        Assert.AreEqual("254.92", snapshot.Balances[0].TotalBalance);
        Assert.AreEqual("0.00", snapshot.Balances[0].GrantedBalance);
        Assert.AreEqual("254.92", snapshot.Balances[0].ToppedUpBalance);

        Assert.IsNotNull(seen);
        Assert.AreEqual(BaseUrl + "/user/balance", seen!.RequestUri!.ToString());
        Assert.AreEqual("Bearer", seen.Headers.Authorization!.Scheme);
        Assert.AreEqual(FakeKey, seen.Headers.Authorization.Parameter, "凭据必须进请求头（否则等于没查）。");
    }

    /// <summary>回执里绝不能出现 apiKey —— 这是本工具最容易被一次 JsonSerializer 意外破坏的约束。</summary>
    [TestMethod]
    public async Task Json_Output_Never_Contains_ApiKey()
    {
        var http = new HttpClient(new StubHandler(_ => Json("""
            {"is_available":true,"balance_infos":[
              {"currency":"CNY","total_balance":"1.00","granted_balance":"1.00","topped_up_balance":"0.00"}]}
            """)));

        var endpoint = new ProviderBalanceEndpoint("deepseek", BaseUrl, FakeKey, "apiKey");
        var snapshot = await ProviderBalanceTool.QueryAsync(http, endpoint, CancellationToken.None);
        var json = ProviderBalanceTool.ToJson(snapshot);

        Assert.IsFalse(json.Contains(FakeKey, StringComparison.Ordinal), "回执不得包含 apiKey。");
        StringAssert.Contains(json, "totalBalance");
        StringAssert.Contains(json, "1.00");
    }

    /// <summary>HTTP 非 2xx 必须报「状态码 + 端点不支持该接口」这类可行动信息，而不是返回 0 余额。</summary>
    [TestMethod]
    public async Task Query_NonSuccess_ReportsStatusInsteadOfZeroBalance()
    {
        var http = new HttpClient(new StubHandler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.Unauthorized)
            {
                Content = new StringContent("""{"error":{"message":"invalid api key"}}"""),
            };
            return response;
        }));

        var endpoint = new ProviderBalanceEndpoint("deepseek", BaseUrl, FakeKey, "apiKey");

        var exception = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => ProviderBalanceTool.QueryAsync(http, endpoint, CancellationToken.None));

        StringAssert.Contains(exception.Message, "401");
        StringAssert.Contains(exception.Message, "端点不支持该接口");
        Assert.IsFalse(exception.Message.Contains(FakeKey, StringComparison.Ordinal), "异常信息也不得包含 apiKey。");
    }

    /// <summary>配置解析：命中 provider、禁用 provider、缺凭据三条分支都要给出可行动错误。</summary>
    [TestMethod]
    public void ResolveProvider_CoversFound_Disabled_MissingKey_AndUnknown()
    {
        Assert.IsTrue(
            ProviderBalanceTool.TryResolveProvider(ConfigJson, "deepseek", out var endpoint, out var error),
            error);
        Assert.AreEqual(FakeKey, endpoint.ApiKey);
        Assert.AreEqual(BaseUrl, endpoint.BaseUrl);

        Assert.IsFalse(ProviderBalanceTool.TryResolveProvider(ConfigJson, "disabled-one", out _, out var disabled));
        StringAssert.Contains(disabled, "isEnabled=false");

        Assert.IsFalse(ProviderBalanceTool.TryResolveProvider(ConfigJson, "no-key", out _, out var noKey));
        StringAssert.Contains(noKey, "没有可用凭据");

        Assert.IsFalse(ProviderBalanceTool.TryResolveProvider(ConfigJson, "nope", out _, out var unknown));
        StringAssert.Contains(unknown, "没有 provider");
    }

    /// <summary>工具必须能通过 [Tool] 特性被自动发现（否则「已实现但永远不被加载」）。</summary>
    [TestMethod]
    public void Tool_Is_Discoverable_By_Attribute()
    {
        var attribute = typeof(ProviderBalanceTool)
            .GetCustomAttributes(typeof(PuddingCode.Tools.ToolAttribute), inherit: false);

        Assert.HasCount(1, attribute, "provider_balance 需要 [Tool] 特性才会被 AddPuddingToolsFromAssembly 注册。");
    }

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
    };

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => Task.FromResult(respond(request));
    }
}
