using System.Net;
using System.Text;
using PuddingHomeAssistant.Contracts;
using PuddingHomeAssistant.Diagnostics;
using PuddingHomeAssistant.Rest;

namespace PuddingHomeAssistantTests;

/// <summary>可计数的假传输层：用于证明「策略拒绝时根本没有发出请求」。</summary>
internal sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
{
    public int CallCount { get; private set; }
    public HttpRequestMessage? LastRequest { get; private set; }
    public string? LastBody { get; private set; }
    public Uri? LastUri { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        CallCount++;
        LastRequest = request;
        LastUri = request.RequestUri;
        LastBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        return responder(request);
    }

    public static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) => new(status)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json"),
    };
}

[TestClass]
public sealed class HaRestClientTests
{
    private static readonly HaInstance Instance = new()
    {
        Alias = "test",
        BaseUrl = new Uri("http://127.0.0.1:8123/"),
        Token = "secret-token-value",
        OptInDomains = new HashSet<string>(StringComparer.Ordinal),
    };

    private static (HaRestClient Client, FakeHandler Handler) Create(Func<HttpRequestMessage, HttpResponseMessage> responder)
    {
        var handler = new FakeHandler(responder);
        var http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        return (new HaRestClient(http), handler);
    }

    [TestMethod]
    public async Task GetStates_ParsesObjects()
    {
        const string json = """
        [
          {"entity_id":"light.a","state":"on","last_changed":"2026-10-19T00:00:00+00:00","last_updated":"2026-10-19T00:00:00+00:00","attributes":{"brightness":128}},
          {"entity_id":"switch.b","state":"off","last_changed":"2026-10-19T00:00:00+00:00","last_updated":"2026-10-19T00:00:00+00:00"}
        ]
        """;
        var (client, handler) = Create(_ => FakeHandler.Json(json));

        var states = await client.GetStatesAsync(Instance, CancellationToken.None);

        Assert.AreEqual(2, states.Count);
        Assert.AreEqual("light.a", states[0].EntityId);
        Assert.AreEqual("light", states[0].Domain);
        Assert.AreEqual(1, handler.CallCount);
        Assert.AreEqual("/api/states", handler.LastUri!.AbsolutePath);
    }

    [TestMethod]
    public async Task AuthHeader_CarriesBearerToken()
    {
        var (client, handler) = Create(_ => FakeHandler.Json("{\"message\":\"API running.\"}"));
        await client.GetStatusAsync(Instance, CancellationToken.None);

        Assert.IsNotNull(handler.LastRequest!.Headers.Authorization);
        Assert.AreEqual("Bearer", handler.LastRequest.Headers.Authorization!.Scheme);
        Assert.AreEqual("secret-token-value", handler.LastRequest.Headers.Authorization.Parameter);
    }

    [DataTestMethod]
    [DataRow("shell_command", "restart")]
    [DataRow("hassio", "host_reboot")]
    [DataRow("lock", "unlock")]
    [DataRow("alarm_control_panel", "alarm_disarm")]
    public async Task CallService_BlockedDomain_SendsNoRequest(string domain, string service)
    {
        var (client, handler) = Create(_ => FakeHandler.Json("[]"));

        var ex = await Assert.ThrowsExactlyAsync<HaClientException>(
            () => client.CallServiceAsync(Instance, new HaServiceCall(domain, service, null, null, false), CancellationToken.None));

        Assert.AreEqual(HaErrorCodes.DomainBlocked, ex.Code);
        Assert.AreEqual(0, handler.CallCount, "被策略拒绝时不得发出任何 HTTP 请求");
    }

    [TestMethod]
    public async Task CallService_OptInDomainWithoutPermission_SendsNoRequest()
    {
        var (client, handler) = Create(_ => FakeHandler.Json("[]"));
        var ex = await Assert.ThrowsExactlyAsync<HaClientException>(
            () => client.CallServiceAsync(Instance, new HaServiceCall("media_player", "turn_on", null, null, false), CancellationToken.None));

        Assert.AreEqual(HaErrorCodes.OptInRequired, ex.Code);
        Assert.AreEqual(0, handler.CallCount);
    }

    [TestMethod]
    public async Task CallService_OptInDomainWhenEnabled_Succeeds()
    {
        var instance = new HaInstance
        {
            Alias = "test",
            BaseUrl = new Uri("http://127.0.0.1:8123/"),
            Token = "t",
            OptInDomains = new HashSet<string>(["media_player"], StringComparer.Ordinal),
        };
        var (client, handler) = Create(_ => FakeHandler.Json("[]"));

        var result = await client.CallServiceAsync(
            instance, new HaServiceCall("media_player", "turn_on", ["media_player.tv"], null, false), CancellationToken.None);

        Assert.AreEqual(1, handler.CallCount);
        Assert.AreEqual(0, result.ChangedStateCount);
    }

    [TestMethod]
    public async Task GetState_NotFound_MapsToStableCode()
    {
        var (client, _) = Create(_ => FakeHandler.Json("{\"message\":\"Entity not found.\"}", HttpStatusCode.NotFound));

        var ex = await Assert.ThrowsExactlyAsync<HaClientException>(
            () => client.GetStateAsync(Instance, "light.missing", CancellationToken.None));

        Assert.AreEqual(HaErrorCodes.NotFound, ex.Code);
        Assert.AreEqual(404, ex.HttpStatus);
    }

    [TestMethod]
    public async Task Unauthorized_DoesNotLeakTokenInMessage()
    {
        var (client, _) = Create(_ => FakeHandler.Json("401: Unauthorized", HttpStatusCode.Unauthorized));

        var ex = await Assert.ThrowsExactlyAsync<HaClientException>(
            () => client.GetStatesAsync(Instance, CancellationToken.None));

        Assert.AreEqual(HaErrorCodes.AuthFailed, ex.Code);
        Assert.IsFalse(ex.Message.Contains("secret-token-value", StringComparison.Ordinal), "异常消息泄露了令牌");
    }

    [TestMethod]
    public async Task CallService_ArrayResult_CountsChangedStates()
    {
        var (client, _) = Create(_ => FakeHandler.Json("""[{"entity_id":"light.a"},{"entity_id":"light.b"}]"""));

        var result = await client.CallServiceAsync(
            Instance, new HaServiceCall("light", "turn_on", ["light.a", "light.b"], null, false), CancellationToken.None);

        Assert.AreEqual(2, result.ChangedStateCount);
        Assert.IsNull(result.Response);
    }

    [TestMethod]
    public async Task CallService_ReturnResponse_ExposesResponse()
    {
        const string json = """{"changed_states":[{"entity_id":"light.a"}],"response":{"ok":true}}""";
        var (client, handler) = Create(_ => FakeHandler.Json(json));

        var result = await client.CallServiceAsync(
            Instance, new HaServiceCall("light", "turn_on", ["light.a"], null, true), CancellationToken.None);

        Assert.AreEqual(1, result.ChangedStateCount);
        Assert.IsNotNull(result.Response);
        Assert.AreEqual("?return_response", handler.LastUri!.Query);
    }

    [TestMethod]
    public async Task CallService_MalformedDomain_IsRejectedBeforeHttp()
    {
        var (client, handler) = Create(_ => FakeHandler.Json("[]"));

        var ex = await Assert.ThrowsExactlyAsync<HaClientException>(
            () => client.CallServiceAsync(Instance, new HaServiceCall("light/../x", "turn_on", null, null, false), CancellationToken.None));

        Assert.AreEqual(HaErrorCodes.InvalidRequest, ex.Code);
        Assert.AreEqual(0, handler.CallCount);
    }

    [TestMethod]
    public async Task GetConfig_ReturnsVersionAndComponentCount()
    {
        const string json = """{"version":"2026.2.1","location_name":"Home","time_zone":"Asia/Shanghai","components":["light","switch","fan"]}""";
        var (client, _) = Create(_ => FakeHandler.Json(json));

        var config = await client.GetConfigAsync(Instance, CancellationToken.None);

        Assert.AreEqual("2026.2.1", config.Version);
        Assert.AreEqual("Asia/Shanghai", config.TimeZone);
        Assert.AreEqual(3, config.ComponentCount);
    }
}
