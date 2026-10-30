using System.Net;
using System.Text;
using PuddingCode.Configuration;
using PuddingLlmUsage.Contracts;
using PuddingRuntime.Services.Llm;

namespace PuddingRuntimeTests.Tools;

/// <summary>
/// <see cref="LlmProviderBalanceProbe"/> 的契约测试。
/// <para>
/// 守两条不能被放宽的规则：
/// <list type="number">
/// <item><description><b>失败必须变成 Failure 观测，不能抛</b>——抛出会让采样循环中断，
/// 曲线上留下「看起来没变化」的假象。</description></item>
/// <item><description><b>字段缺失记 null，不能记 0</b>——「不知道赠送额度」与「没有赠送额度」是两件事。</description></item>
/// </list>
/// </para>
/// </summary>
[TestClass]
public sealed class LlmProviderBalanceProbeTests
{
    private const string FakeKey = "TESTKEY-SHOULD-NOT-LEAK";
    private const string BaseUrl = "https://api.example.test";

    private static readonly DateTimeOffset Now = new(2026, 10, 30, 10, 0, 0, TimeSpan.Zero);

    private static readonly string ConfigJson = $$"""
    {
      "providers": [
        { "providerId": "deepseek", "baseUrl": "{{BaseUrl}}", "apiKey": "{{FakeKey}}", "isEnabled": true },
        { "providerId": "disabled-one", "baseUrl": "{{BaseUrl}}", "apiKey": "{{FakeKey}}", "isEnabled": false }
      ]
    }
    """;

    private static (LlmProviderBalanceProbe Probe, string Root) CreateProbe(
        Func<HttpRequestMessage, HttpResponseMessage> respond,
        string? configJson = null)
    {
        var root = Path.Combine(Path.GetTempPath(), "pudding-llm-usage-probe-" + Guid.NewGuid().ToString("N"));
        var configDirectory = Path.Combine(root, "config");
        Directory.CreateDirectory(configDirectory);
        File.WriteAllText(
            Path.Combine(configDirectory, "llm.providers.json"),
            configJson ?? ConfigJson,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        var paths = PuddingDataPaths.FromRoot(root);
        var probe = new LlmProviderBalanceProbe(paths, new HttpClient(new StubHandler(respond)));
        return (probe, root);
    }

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };

    [TestMethod]
    public async Task Success_ParsesAmountsIntoDecimals()
    {
        var (probe, root) = CreateProbe(_ => Json("""
            {"is_available":true,"balance_infos":[
              {"currency":"CNY","total_balance":"248.55","granted_balance":"0.00","topped_up_balance":"248.55"}]}
            """));
        try
        {
            var observations = await probe.QueryAsync("deepseek", Now);

            Assert.AreEqual(1, observations.Count);
            var observation = observations[0];
            Assert.IsTrue(observation.Succeeded);
            Assert.AreEqual("deepseek", observation.ProviderId);
            Assert.AreEqual("CNY", observation.Currency);
            Assert.AreEqual(248.55m, observation.TotalBalance);
            Assert.AreEqual(0.00m, observation.GrantedBalance);
            Assert.AreEqual(248.55m, observation.ToppedUpBalance);
            Assert.AreEqual(Now, observation.ObservedAtUtc);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task MissingGrantedAndToppedUp_BecomeNull_NotZero()
    {
        var (probe, root) = CreateProbe(_ => Json("""
            {"is_available":true,"balance_infos":[{"currency":"CNY","total_balance":"10.00"}]}
            """));
        try
        {
            var observation = (await probe.QueryAsync("deepseek", Now)).Single();

            Assert.IsTrue(observation.Succeeded);
            Assert.AreEqual(10.00m, observation.TotalBalance);
            Assert.IsNull(observation.GrantedBalance, "服务商没给赠送额度时必须是 null，不能写成 0。");
            Assert.IsNull(observation.ToppedUpBalance, "同上。");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task HttpFailure_ReturnsFailureObservation_AndDoesNotThrow()
    {
        var (probe, root) = CreateProbe(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized)
        {
            Content = new StringContent("""{"error":{"message":"invalid api key"}}"""),
        });
        try
        {
            var observations = await probe.QueryAsync("deepseek", Now);

            Assert.AreEqual(1, observations.Count);
            var observation = observations[0];
            Assert.IsFalse(observation.Succeeded, "采样失败必须是 Failure 观测，不能抛异常。");
            Assert.IsNull(observation.TotalBalance);
            Assert.IsNotNull(observation.Error);
            StringAssert.Contains(observation.Error!, "401");
            Assert.IsFalse(observation.Error!.Contains(FakeKey, StringComparison.Ordinal), "错误信息不得包含 apiKey。");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task EmptyBalanceInfos_IsFailure_NotAnEmptyList()
    {
        // 返回空列表会让调用方以为「这个 provider 没有余额」，而事实是「没查到」。
        var (probe, root) = CreateProbe(_ => Json("""{"is_available":true,"balance_infos":[]}"""));
        try
        {
            var observations = await probe.QueryAsync("deepseek", Now);

            Assert.AreEqual(1, observations.Count);
            Assert.IsFalse(observations[0].Succeeded);
            StringAssert.Contains(observations[0].Error!, "balance_infos");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task UnparsableAmount_IsFailureForThatCurrency()
    {
        var (probe, root) = CreateProbe(_ => Json("""
            {"is_available":true,"balance_infos":[{"currency":"CNY","total_balance":"n/a"}]}
            """));
        try
        {
            var observation = (await probe.QueryAsync("deepseek", Now)).Single();

            Assert.IsFalse(observation.Succeeded);
            Assert.AreEqual("CNY", observation.Currency, "失败的观测仍要保留币种，否则无法归入曲线。");
            StringAssert.Contains(observation.Error!, "n/a");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task DisabledProvider_AndUnknownProvider_AreFailures()
    {
        var (probe, root) = CreateProbe(_ => Json("""{"is_available":true,"balance_infos":[]}"""));
        try
        {
            var disabled = (await probe.QueryAsync("disabled-one", Now)).Single();
            Assert.IsFalse(disabled.Succeeded);
            StringAssert.Contains(disabled.Error!, "isEnabled=false");

            var unknown = (await probe.QueryAsync("nope", Now)).Single();
            Assert.IsFalse(unknown.Succeeded);
            StringAssert.Contains(unknown.Error!, "没有 provider");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task MissingConfigFile_IsFailure_NotException()
    {
        var root = Path.Combine(Path.GetTempPath(), "pudding-llm-usage-nocfg-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var probe = new LlmProviderBalanceProbe(
                PuddingDataPaths.FromRoot(root),
                new HttpClient(new StubHandler(_ => Json("{}"))));

            var observation = (await probe.QueryAsync("deepseek", Now)).Single();

            Assert.IsFalse(observation.Succeeded);
            StringAssert.Contains(observation.Error!, "LLM 配置不存在");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task EmptyProviderId_IsFailure()
    {
        var (probe, root) = CreateProbe(_ => Json("{}"));
        try
        {
            var observation = (await probe.QueryAsync("  ", Now)).Single();

            Assert.IsFalse(observation.Succeeded);
            StringAssert.Contains(observation.Error!, "providerId 为空");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => Task.FromResult(respond(request));
    }
}
