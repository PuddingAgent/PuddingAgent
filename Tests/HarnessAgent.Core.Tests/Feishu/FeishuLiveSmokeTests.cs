using System.Text.Json;
using HarnessAgent.Core.Connectors.Feishu;

namespace HarnessAgent.Core.Tests.Feishu;

[TestClass]
public sealed class FeishuLiveSmokeTests
{
    private const string EnableEnvironmentVariable =
        "PUDDING_RUN_FEISHU_LIVE_TESTS";
    private const string ConfigPathEnvironmentVariable =
        "PUDDING_FEISHU_CONFIG";

    [TestMethod]
    [TestCategory("Live")]
    public async Task CredentialsAndWebSocketEndpoint_AreReachable()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable(
                    EnableEnvironmentVariable),
                "1",
                StringComparison.Ordinal))
        {
            Assert.Inconclusive(
                $"Set {EnableEnvironmentVariable}=1 to run the live Feishu smoke test.");
        }

        var configPath =
            Environment.GetEnvironmentVariable(
                ConfigPathEnvironmentVariable)
            ?? @"D:\data\config\feishu.json";
        Assert.IsTrue(
            File.Exists(configPath),
            $"Feishu config does not exist: {configPath}");

        var config = JsonSerializer.Deserialize<FeishuConfig>(
            await File.ReadAllTextAsync(configPath),
            new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
            });
        Assert.IsNotNull(config);
        Assert.IsFalse(string.IsNullOrWhiteSpace(config!.AppId));
        Assert.IsFalse(string.IsNullOrWhiteSpace(config.AppSecret));

        using var timeout =
            new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using (var client = new FeishuClient(config))
        {
            var token = await client.GetAccessTokenAsync(timeout.Token);
            Assert.IsFalse(string.IsNullOrWhiteSpace(token));
        }

        using var webSocket = new FeishuWebSocket(config);
        await webSocket.ConnectAsync(timeout.Token);
        Assert.IsTrue(webSocket.IsConnected);
        await webSocket.DisconnectAsync();
        Assert.IsFalse(webSocket.IsConnected);
    }
}
