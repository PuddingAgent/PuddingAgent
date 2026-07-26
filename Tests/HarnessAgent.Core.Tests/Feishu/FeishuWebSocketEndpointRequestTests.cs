using System.Text.Json;
using HarnessAgent.Core.Connectors.Feishu;

namespace HarnessAgent.Core.Tests.Feishu;

[TestClass]
public sealed class FeishuWebSocketEndpointRequestTests
{
    [TestMethod]
    public async Task CreateEndpointRequest_MatchesOfficialDiscoveryContract()
    {
        var config = new FeishuConfig
        {
            AppId = "cli_test",
            AppSecret = "secret_test",
        };

        using var request = FeishuWebSocket.CreateEndpointRequest(config);

        Assert.AreEqual(HttpMethod.Post, request.Method);
        Assert.AreEqual(
            "https://open.feishu.cn/callback/ws/endpoint",
            request.RequestUri?.AbsoluteUri);
        Assert.IsTrue(
            request.Headers.TryGetValues("locale", out var localeValues));
        CollectionAssert.Contains(localeValues.ToArray(), "zh");
        Assert.IsFalse(
            string.IsNullOrWhiteSpace(request.Headers.UserAgent.ToString()));

        Assert.IsNotNull(request.Content);
        using var payload = JsonDocument.Parse(
            await request.Content.ReadAsStringAsync());
        Assert.AreEqual(
            config.AppId,
            payload.RootElement.GetProperty("AppID").GetString());
        Assert.AreEqual(
            config.AppSecret,
            payload.RootElement.GetProperty("AppSecret").GetString());
    }
}
